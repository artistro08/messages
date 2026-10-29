using System.Net.Http.Headers;
using GMessages.Protocol.Http;
using GMessages.Protocol.Session;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Auth;

/// <summary>
/// Keeps Google's short-lived <c>__Secure-1PSIDTS</c> sign-in cookie fresh, the way Google pages do in a browser.
/// </summary>
/// <remarks>
/// Not part of LibGM: an owner-approved exception (spec, "Session lifetime"). Google refuses a session about 2 hours
/// after sign-in when this cookie is never renewed, and no Messages response renews it. Browsers renew it by posting
/// to <c>https://accounts.google.com/RotateCookies</c>, which answers with a new <c>__Secure-1PSIDTS</c> in
/// <c>Set-Cookie</c>. The request shape (POST, <c>Content-Type: application/json</c>,
/// <c>Origin: https://accounts.google.com</c>, body <c>[000,"-0000000000000000000"]</c>, the account's cookies) and
/// the 10 minute cadence follow the Python <c>gemini-webapi</c> project:
/// <see href="https://github.com/HanaokaYuzu/Gemini-API/blob/master/src/gemini_webapi/utils/rotate_1psidts.py">rotate_1psidts.py</see>,
/// <see href="https://github.com/HanaokaYuzu/Gemini-API/blob/master/src/gemini_webapi/constants.py">constants.py</see>
/// and the 600 second <c>refresh_interval</c> in
/// <see href="https://github.com/HanaokaYuzu/Gemini-API/blob/master/src/gemini_webapi/client.py">client.py</see>.
/// <para>
/// A rotation is due when none was tried yet or the last try is <see cref="Interval"/> old, so it happens at most
/// every 10 minutes, whether the last try worked or not. A failure is logged with its error type and HTTP status and
/// keeps the session; the next interval tries again. Only paired Google accounts (a browser device and a cookie map)
/// are rotated. Every wait uses the <see cref="TimeProvider"/>. Cookie values are never logged, only names and expiry.
/// </para>
/// </remarks>
/// <param name="http">Sends the request with the session's cookie jar; built on a client with <see cref="RequestTimeout"/> and no automatic redirects (see <c>GmClient.Create</c>).</param>
/// <param name="auth">The account whose cookies are rotated.</param>
/// <param name="endpoints">The server addresses.</param>
/// <param name="time">The clock for the cadence.</param>
/// <param name="log">The logger.</param>
public sealed partial class CookieRotator(GmHttpClient http, AuthData auth, GmEndpoints endpoints, TimeProvider time, ILogger log)
{
    /// <summary>The time between rotations (gemini-webapi's 600 second default; browsers rotate about this often).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long one rotation request may take (C#-only; the shared client's 100 s would stall a connect).
    /// </summary>
    /// <remarks>
    /// The rotation client's <see cref="HttpClient.Timeout"/>, so it runs on real time, like every
    /// <see cref="HttpClient"/> timeout. A timeout is a failed rotation, tried again at the next interval.
    /// </remarks>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // The Body Google's Pages Post (gemini-webapi: rotate_1psidts)
    private const string RotateBody = "[000,\"-0000000000000000000\"]";

    // Guards _lastAttempt And _inFlight: The Check, The Stamp And The Start Are One Step
    private readonly Lock _lock = new();
    private DateTimeOffset? _lastAttempt;
    private Task? _inFlight;

    /// <summary>
    /// Rotates the cookie when the last try is <see cref="Interval"/> old or there was none.
    /// </summary>
    /// <remarks>
    /// Called before the first request after a start or a resume, and by <see cref="RunAsync"/>. Does nothing for an
    /// account that isn't paired or has no cookies. A caller that arrives while a rotation is in flight waits for it
    /// (with its own <paramref name="ct"/>) instead of sending another, so the request it guards goes out with the new
    /// cookie. A rotation canceled midway clears its stamp, so the next caller rotates; if that cancel came from
    /// another caller, a waiting caller rotates itself. Never throws for a failed rotation, only when canceled.
    /// </remarks>
    /// <param name="ct">Cancels the wait and the request.</param>
    /// <returns>When the next rotation is due.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<DateTimeOffset> RotateIfDueAsync(CancellationToken ct)
    {
        while (true)
        {
            DateTimeOffset now = time.GetUtcNow();

            // Not Paired Or Not A Google Account: Nothing To Keep Fresh
            if (auth.Browser is null || !auth.Cookies.HasCookies)
            {
                return now + Interval;
            }

            // Wait For The One In Flight, Return When Not Due, Or Start One
            Task? running = null;
            TaskCompletionSource? mine = null;
            lock (_lock)
            {
                if (_inFlight is { IsCompleted: false } in_flight)
                {
                    running = in_flight;
                }
                else if (_lastAttempt is { } last && now - last < Interval)
                {
                    return last + Interval;
                }
                else
                {
                    mine         = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlight    = mine.Task;
                    _lastAttempt = now;
                }
            }

            // Someone Else's Rotation: Wait, Then Check Again (A Canceled One Leaves It Due)
            if (running is not null)
            {
                try
                {
                    await running.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Their Cancel, Not Ours
                }

                continue;
            }

            // Ours
            try
            {
                await RotateAsync(ct).ConfigureAwait(false);
                mine!.SetResult();
                return now + Interval;
            }
            catch (Exception)
            {
                // Canceled Midway (The Only Way Out Of RotateAsync): Not A Try, So The Next Caller (A Reconnect) Rotates
                lock (_lock)
                {
                    _lastAttempt = null;
                }

                mine!.SetCanceled(CancellationToken.None);
                throw;
            }
        }
    }

    /// <summary>
    /// Rotates now if due, then every <see cref="Interval"/>, until <paramref name="ct"/> is canceled.
    /// </summary>
    /// <remarks>The long poll runs this while paired and cancels it on disconnect, sign-out or unpair.</remarks>
    /// <param name="ct">Stops the loop and any rotation in flight.</param>
    /// <returns>A task that completes when stopped; stopping is not an error.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                DateTimeOffset next = await RotateIfDueAsync(ct).ConfigureAwait(false);
                TimeSpan wait       = next - time.GetUtcNow();
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped: Not A Failure
        }
    }

    // One Rotation: Post Like The Browser, Keep Whatever Cookies Come Back, Log The Outcome
    private async Task RotateAsync(CancellationToken ct)
    {
        // Build Request
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.RotateCookies) { Content = new StringContent(RotateBody) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("origin", endpoints.AccountsBase.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("user-agent", GmConstants.UserAgent);

        // Send; The Jar Takes The New Cookies Either Way, Only A 2xx Counts As Rotated
        try
        {
            using HttpResponseMessage res = await http.SendWithCookiesAsync(request, IsSharedGoogleCookie, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            string names = GmHttpClient.DescribeSetCookies(res, time.GetUtcNow(), IsSharedGoogleCookie);
            LogRotated(log, names.Length > 0 ? names : "none");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Diagnosis: Which Failure, And The HTTP Status When There Was One (0 Otherwise)
            int status        = ex is HttpRequestException { StatusCode: { } code } ? (int)code : 0;
            string error_type = ex.GetType().Name;
            LogRotationFailed(log, error_type, status);
        }
    }

    /// <summary>
    /// Tells whether a rotation answer's <c>Set-Cookie</c> line is a cookie for all of <c>google.com</c>.
    /// </summary>
    /// <remarks>
    /// The jar sends every cookie it holds to messages.google.com and the googleapis.com hosts, so only cookies a
    /// browser would send there too are kept: <c>Domain=.google.com</c> (or <c>google.com</c>). Host-only cookies,
    /// such as <c>__Host-*</c> and other accounts.google.com-only ones, are dropped.
    /// </remarks>
    /// <param name="line">The raw <c>Set-Cookie</c> value.</param>
    /// <returns>True to keep it.</returns>
    internal static bool IsSharedGoogleCookie(string line)
    {
        string[] parts = line.Split(';');
        if (parts[0].TrimStart().StartsWith("__Host-", StringComparison.Ordinal))
        {
            return false;
        }

        return parts.Skip(1)
            .Select(a => a.Trim())
            .Any(a => a.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase) && a[7..].TrimStart('.').Equals("google.com", StringComparison.OrdinalIgnoreCase));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cookies rotated: {Names}")]
    private static partial void LogRotated(ILogger logger, string names);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cookie rotation failed: {ErrorType} HTTP {Status}")]
    private static partial void LogRotationFailed(ILogger logger, string errorType, int status);
}

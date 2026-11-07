using System.Globalization;
using System.Text;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Auth;

/// <summary>
/// Refreshes the Tachyon auth token before it expires.
/// </summary>
/// <remarks>
/// Ported from <c>refreshAuthToken</c>, <c>updateTachyonAuthToken</c> and <c>isFatalRefreshError</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// The refresher gets a new token when less than 1 hour is left. The refresh key of the account signs the request
/// over <c>"{requestId}:{timestampMicros}"</c>. The request never holds push keys, because this app has no web push.
/// Thus the Go <c>MoreParameters</c> block stays empty. The token never goes in a log.
/// </remarks>
/// <param name="http">The HTTP client.</param>
/// <param name="auth">The account that owns the token.</param>
/// <param name="endpoints">The server addresses.</param>
/// <param name="time">The clock for the expiry check and the new expiry.</param>
/// <param name="log">The logger.</param>
public sealed partial class TokenRefresher(GmHttpClient http, AuthData auth, GmEndpoints endpoints, TimeProvider time, ILogger log)
{
    /// <summary>The refresher gets a new token when less than this time is left. Go calls it <c>RefreshTachyonBuffer</c>.</summary>
    public static readonly TimeSpan RefreshBuffer = TimeSpan.FromHours(1);

    /// <summary>The life of a token when the server gives no TTL.</summary>
    public static readonly TimeSpan DefaultValidFor = TimeSpan.FromHours(24);

    // These Google errors tell that the credentials are gone. Go names them ErrInvalidCredentials and
    // ErrRequestedEntityNotFound.
    private const string InvalidCredentialsMessage = "Request had invalid authentication credentials. Expected OAuth 2 access token, login cookie or other valid authentication credential. See https://developers.google.com/identity/sign-in/web/devconsole-project.";
    private const string EntityNotFoundMessage     = "Requested entity was not found.";

    /// <summary>
    /// Occurs with <see cref="AuthTokenRefreshed"/> after the refresher stores a new token.
    /// </summary>
    public event Action<object>? EventRaised;

    /// <summary>
    /// Refreshes the token when less than 1 hour is left.
    /// </summary>
    /// <remarks>
    /// The method does nothing when the account has no browser device, or when more than 1 hour is left. An account
    /// that is not paired has no browser device. On success, the method stores the new token, expiry and TTL on the
    /// <see cref="AuthData"/>. Then it raises <see cref="AuthTokenRefreshed"/>.
    /// </remarks>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the token is fresh.</returns>
    /// <exception cref="GmRequestException">The server refused the refresh; see <see cref="IsFatal"/>.</exception>
    /// <exception cref="InvalidDataException">The response had no token.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task RefreshIfNeededAsync(CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (auth.Browser is null || auth.TachyonExpiry - now > RefreshBuffer)
        {
            return;
        }

        // The signed text is "{id}:{micros}".
        string requestId  = Guid.NewGuid().ToString();
        long timestamp    = now.ToUnixTimeMilliseconds() * 1000;
        byte[] signature  = auth.RefreshKey.SignSha256Der(Encoding.UTF8.GetBytes(requestId + ":" + timestamp.ToString(CultureInfo.InvariantCulture)));

        var payload = new RegisterRefreshRequest
        {
            MessageAuth = new AuthMessage
            {
                RequestID        = requestId,
                TachyonAuthToken = ByteString.CopyFrom(auth.TachyonAuthToken ?? []),
                Network          = auth.AuthNetwork,
                ConfigVersion    = GmConstants.ConfigVersion,
            },
            CurrBrowserDevice = auth.Browser,
            UnixTimestamp     = timestamp,
            Signature         = ByteString.CopyFrom(signature),
            Parameters        = new RegisterRefreshRequest.Types.Parameters { EmptyArr = new EmptyArr() },
            MessageType       = 2,
        };

        LogRefreshing(log, auth.TachyonExpiry);
        TokenData token;
        try
        {
            RegisterRefreshResponse res = await http.PostTypedAsync<RegisterRefreshResponse>(endpoints.RegisterRefresh, payload, GmContentType.PbLite, ct).ConfigureAwait(false);

            // Go checks the token for nil. This port checks for Length > 0 instead, as in other places.
            if (res.TokenData is not { TachyonAuthToken.Length: > 0 } data)
            {
                throw new InvalidDataException("No tachyon auth token in refresh response.");
            }

            token = data;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // For diagnosis, the log tells the failure type and the HTTP status. The status is 0 when no response arrived.
            int status = ex is GmRequestException req ? req.StatusCode : 0;
            string errorType = ex.GetType().Name;
            LogRefreshFailed(log, errorType, status);
            throw;
        }

        Apply(auth, token, time);
        TimeSpan validFor = TimeSpan.FromMicroseconds(auth.TachyonTtlMicros);
        LogUpdated(log, auth.TachyonExpiry, validFor);
        EventRaised?.Invoke(new AuthTokenRefreshed());
    }

    /// <summary>
    /// Stores a token with its expiry and TTL on an account.
    /// </summary>
    /// <remarks>
    /// Ported from <c>updateTachyonAuthToken</c>. A TTL of 0 means 24 hours. The method is static, so the sign-in can
    /// store its first token without a refresher. It stores the token, expiry and TTL as 1 <see cref="TachyonToken"/>
    /// in 1 write, so they never tear.
    /// </remarks>
    /// <param name="auth">The account to update.</param>
    /// <param name="data">The token and its TTL in microseconds.</param>
    /// <param name="time">The clock for the new expiry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
    internal static void Apply(AuthData auth, TokenData data, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(data);

        TimeSpan validFor = data.TTL == 0 ? DefaultValidFor : TimeSpan.FromMicroseconds(data.TTL);

        // 1 swap, so a concurrent save never sees a new token with an old expiry.
        auth.Tachyon = new TachyonToken(data.TachyonAuthToken.ToByteArray(), time.GetUtcNow() + validFor, (long)validFor.TotalMicroseconds);
    }

    /// <summary>
    /// Tells whether a refresh error means that the session is gone permanently.
    /// </summary>
    /// <remarks>
    /// Ported from <c>isFatalRefreshError</c>. HTTP 401, 403 and 404 are fatal. The Google "invalid credentials" and
    /// "entity not found" errors are fatal with any status. The method matches them on type and message, as Go
    /// <c>RequestError.Is</c> does. All other errors can clear on a retry, also network errors and 5xx. The method
    /// also checks the inner exceptions, in place of the Go error wrapping.
    /// </remarks>
    /// <param name="e">The error from <see cref="RefreshIfNeededAsync"/>.</param>
    /// <returns>True when a retry cannot help.</returns>
    public static bool IsFatal(Exception e)
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
        {
            if (current is not GmRequestException req)
            {
                continue;
            }

            bool knownError = req.Error is { Type: 16, Message: InvalidCredentialsMessage } or { Type: 5, Message: EntityNotFoundMessage };
            if (knownError || req.StatusCode is 401 or 403 or 404)
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshing tachyon token, expiry {Expiry:o}")]
    private static partial void LogRefreshing(ILogger logger, DateTimeOffset expiry);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refresh failed: {ErrorType} HTTP {Status}")]
    private static partial void LogRefreshFailed(ILogger logger, string errorType, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tachyon token valid until {Expiry:o} (TTL {ValidFor})")]
    private static partial void LogUpdated(ILogger logger, DateTimeOffset expiry, TimeSpan validFor);
}

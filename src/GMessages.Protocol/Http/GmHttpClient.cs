using System.Globalization;
using System.Net.Http.Headers;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Http;

/// <summary>
/// How a request body is encoded.
/// </summary>
public enum GmContentType
{
    /// <summary>Binary protobuf, <c>application/x-protobuf</c>.</summary>
    Protobuf,

    /// <summary>PBLite (protobuf as a JSON array), <c>application/json+protobuf</c>.</summary>
    PbLite,
}

/// <summary>
/// Sends requests to Google the way the web app does: relay headers, cookies, SAPISIDHASH, retries and error decoding.
/// </summary>
/// <remarks>
/// Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/http.go">http.go</see>,
/// <c>BuildRelayHeaders</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/func.go">util/func.go</see>
/// and <c>fetchConfig</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// Every POST gets the relay headers plus the jar's cookies. A 5xx answer is retried up to three attempts in total,
/// waiting 1 s then 2 s through the <see cref="TimeProvider"/>; long polls and network failures are never retried.
/// The jar should be the only cookie store: build the <see cref="HttpClient"/> handlers with <c>UseCookies = false</c>
/// so a handler-level cookie container does not add a second copy. Cookie values and tokens are never logged.
/// </remarks>
/// <param name="http">The client for normal requests.</param>
/// <param name="longPollHttp">The client for long polls; see <see cref="CreateLongPollClient"/>.</param>
/// <param name="cookies">The cookie jar shared with the session.</param>
/// <param name="time">The clock used for SAPISIDHASH and retry delays.</param>
/// <param name="log">The logger.</param>
public sealed partial class GmHttpClient(HttpClient http, HttpClient longPollHttp, CookieJar cookies, TimeProvider time, ILogger log)
{
    /// <summary>Total attempts for a request that keeps getting 5xx answers.</summary>
    public const int ServerErrorMaxAttempts = 3;

    /// <summary>The wait before the first retry; attempt N waits N times this.</summary>
    public static readonly TimeSpan ServerErrorRetryInterval = TimeSpan.FromSeconds(1);

    /// <summary>The long-poll client timeout (Go: <c>WithGlobalTimeout(30 * time.Minute)</c>).</summary>
    public static readonly TimeSpan LongPollTimeout = TimeSpan.FromMinutes(30);

    private const string ProtobufMime = "application/x-protobuf";
    private const string PbLiteMime   = "application/json+protobuf";

    // The Last Cookie Line Logged Per Set Of Cookie Names, So An Unchanged Response Stays Quiet
    private readonly Dictionary<string, string> _loggedCookies = [];

    /// <summary>
    /// Creates the client used for long polls.
    /// </summary>
    /// <remarks>It only differs from a normal client by its 30 minute timeout.</remarks>
    /// <param name="handler">The message handler to send through.</param>
    /// <returns>A new client; the caller owns it.</returns>
    public static HttpClient CreateLongPollClient(HttpMessageHandler handler) => new(handler) { Timeout = LongPollTimeout };

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Sends a POST and returns the raw response, retrying 5xx answers.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the headers arrive so long polls can stream the body. The caller owns and must dispose the
    /// response. A final 5xx response (after the last attempt) is returned, not thrown. Cookies from every response,
    /// including ones that are retried, are merged into the jar.
    /// </remarks>
    /// <param name="url">The target URL.</param>
    /// <param name="body">The message to send.</param>
    /// <param name="type">How to encode the body.</param>
    /// <param name="longPoll">True to use the long-poll client and never retry.</param>
    /// <param name="ct">Cancels the request or a retry wait.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a known content type.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<HttpResponseMessage> PostAsync(Uri url, IMessage body, GmContentType type, bool longPoll, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(body);

        // Encode Body Once
        (byte[] bytes, string mime) = type switch
        {
            GmContentType.Protobuf => (body.ToByteArray(), ProtobufMime),
            GmContentType.PbLite   => (PbLiteSerializer.Serialize(body), PbLiteMime),
            _                      => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown request content type."),
        };
        HttpClient client = longPoll ? longPollHttp : http;

        for (int attempt = 1; ; attempt++)
        {
            // Build Request (A Fresh One Per Attempt); Nothing Leaves Once Canceled
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(bytes) };
            AddRelayHeaders(request, "*/*");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", mime);
            cookies.ApplyTo(request, time.GetUtcNow().ToUnixTimeSeconds());

            // Send
            HttpResponseMessage res = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            cookies.UpdateFrom(res);
            LogSetCookies(res, url.AbsolutePath);

            if (longPoll || (int)res.StatusCode < 500 || attempt >= ServerErrorMaxAttempts)
            {
                return res;
            }

            // Wait And Retry
            TimeSpan retry_in = attempt * ServerErrorRetryInterval;
            LogServerErrorRetry(log, (int)res.StatusCode, url.AbsolutePath, attempt, retry_in);
            res.Dispose();
            await Task.Delay(retry_in, time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a POST and decodes the response into a message.
    /// </summary>
    /// <remarks>
    /// Mirrors Go's <c>typedHTTPResponse</c>: any non-2xx status becomes a <see cref="GmRequestException"/>, with
    /// Google's <see cref="ErrorResponse"/> attached when the body decodes and has a message.
    /// </remarks>
    /// <typeparam name="T">The response message type.</typeparam>
    /// <param name="url">The target URL.</param>
    /// <param name="body">The message to send.</param>
    /// <param name="type">How to encode the request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The decoded response.</returns>
    /// <exception cref="GmRequestException">The status was not 2xx.</exception>
    /// <exception cref="InvalidDataException">The response content type is missing or unknown.</exception>
    /// <exception cref="PbLiteException">A PBLite response body is invalid.</exception>
    /// <exception cref="InvalidProtocolBufferException">A protobuf response body is invalid.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    public async Task<T> PostTypedAsync<T>(Uri url, IMessage body, GmContentType type, CancellationToken ct)
        where T : IMessage<T>, new()
    {
        HttpResponseMessage res = await PostAsync(url, body, type, false, ct).ConfigureAwait(false);
        return await ReadTypedAsync<T>(res, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the web app's config-style GET and decodes the response into a message.
    /// </summary>
    /// <remarks>
    /// The headers match Go's <c>fetchConfig</c>: relay headers with <c>sec-fetch-site: same-origin</c> and without
    /// <c>x-user-agent</c> and <c>origin</c>. Not retried, as in Go.
    /// </remarks>
    /// <typeparam name="T">The response message type.</typeparam>
    /// <param name="url">The target URL.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The decoded response.</returns>
    /// <exception cref="GmRequestException">The status was not 2xx.</exception>
    /// <exception cref="InvalidDataException">The response content type is missing or unknown.</exception>
    /// <exception cref="PbLiteException">A PBLite response body is invalid.</exception>
    /// <exception cref="InvalidProtocolBufferException">A protobuf response body is invalid.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    public async Task<T> GetTypedAsync<T>(Uri url, CancellationToken ct)
        where T : IMessage<T>, new()
    {
        ArgumentNullException.ThrowIfNull(url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddRelayHeaders(request, "*/*");
        request.Headers.Remove("sec-fetch-site");
        request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
        request.Headers.Remove("x-user-agent");
        request.Headers.Remove("origin");
        cookies.ApplyTo(request, time.GetUtcNow().ToUnixTimeSeconds());

        HttpResponseMessage res = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        cookies.UpdateFrom(res);
        LogSetCookies(res, url.AbsolutePath);
        return await ReadTypedAsync<T>(res, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a raw media request (upload start, upload finalize or download) with only the caller's headers.
    /// </summary>
    /// <remarks>
    /// Media requests carry LibGM's upload headers alone (Go: <c>NewMediaUploadHeaders</c> and <c>BuildUploadHeaders</c>
    /// in util/func.go): no api key, no <c>x-user-agent</c> and no cookies, so the jar is neither read nor updated. A
    /// header that belongs on the content, such as <c>content-type</c>, goes there. Unlike Go's download, the status is
    /// always checked. Sent on the long-poll client (30 minute timeout), since a big file can outlast the normal 100 s. Not
    /// retried, as in Go. Bodies and headers are never logged.
    /// </remarks>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The target URL.</param>
    /// <param name="headers">Every header to send.</param>
    /// <param name="content">The body, or null for none; the request disposes it.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The status, the response headers (case-insensitive, content headers included) and the body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/>, <paramref name="url"/> or <paramref name="headers"/> is null.</exception>
    /// <exception cref="GmRequestException">The status was not 2xx.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<GmMediaResponse> SendMediaAsync(HttpMethod method, Uri url, IEnumerable<KeyValuePair<string, string>> headers, HttpContent? content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(headers);

        // Build Request: Caller Headers Only
        using var request = new HttpRequestMessage(method, url) { Content = content };
        foreach ((string name, string value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        // Send And Read On The Long-Poll Client: A Big File Can Take Longer Than The Normal 100 s Timeout
        using HttpResponseMessage res = await longPollHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        byte[] body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        int status  = (int)res.StatusCode;
        if (status is < 200 or >= 300)
        {
            throw new GmRequestException(status, null, body);
        }

        var response_headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IEnumerable<string> values) in res.Headers.Concat(res.Content.Headers))
        {
            response_headers[name] = string.Join(",", values);
        }

        return new GmMediaResponse(status, response_headers, body);
    }

    /// <summary>
    /// Tells whether an HTTP header belongs to Device Bound Session Credentials (DBSC).
    /// </summary>
    /// <remarks>
    /// The W3C draft's <c>Secure-Session-Registration</c>, <c>-Challenge</c>, <c>-Response</c>, <c>-Skipped</c> and
    /// <c>Sec-Secure-Session-Id</c> (<see href="https://w3c.github.io/webappsec-dbsc/"/>), and the <c>Sec-Session-*</c>
    /// names of earlier drafts and Chrome's Google-only prototype (<c>Sec-Session-Google-Registration</c>). A bound
    /// session's <c>__Secure-1PSIDTS</c> can only be renewed with the browser's private key, which this app never has.
    /// </remarks>
    /// <param name="name">The header name.</param>
    /// <returns>True for a DBSC header.</returns>
    public static bool IsBoundSessionHeader(string name) =>
        name.StartsWith("Secure-Session-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Sec-Secure-Session-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Sec-Session-", StringComparison.OrdinalIgnoreCase);

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Port Of util.BuildRelayHeaders (Content-Type Is Added By The Caller)
    private static void AddRelayHeaders(HttpRequestMessage req, string accept)
    {
        HttpRequestHeaders h = req.Headers;
        h.TryAddWithoutValidation("sec-ch-ua", GmConstants.SecUA);
        h.TryAddWithoutValidation("x-user-agent", GmConstants.XUserAgent);
        h.TryAddWithoutValidation("x-goog-api-key", GmConstants.GoogleApiKey);
        h.TryAddWithoutValidation("sec-ch-ua-mobile", GmConstants.SecUAMobile);
        h.TryAddWithoutValidation("user-agent", GmConstants.UserAgent);
        h.TryAddWithoutValidation("sec-ch-ua-platform", "\"" + GmConstants.UAPlatform + "\"");
        h.TryAddWithoutValidation("accept", accept);
        h.TryAddWithoutValidation("origin", "https://messages.google.com");
        h.TryAddWithoutValidation("sec-fetch-site", "cross-site");
        h.TryAddWithoutValidation("sec-fetch-mode", "cors");
        h.TryAddWithoutValidation("sec-fetch-dest", "empty");
        h.TryAddWithoutValidation("referer", "https://messages.google.com/");
        h.TryAddWithoutValidation("accept-language", "en-US,en;q=0.9");
    }

    // Diagnosis: Logs The Names And Expiry Times Of Cookies A Response Sets, Never The Values; Logging Must Never Fail A Request
    private void LogSetCookies(HttpResponseMessage res, string path)
    {
        try
        {
            List<(string Name, string When, string Stamp)> set = ParseSetCookies(res, time.GetUtcNow());
            if (set.Count == 0)
            {
                return;
            }

            // Only A Change Is News: The Same Cookie Names With The Same Expiry Attributes As Last Time Stay Quiet
            string name_set = string.Join(",", set.Select(c => c.Name).Order(StringComparer.Ordinal));
            string current  = string.Join(",", set.Select(c => $"{c.Name}|{c.Stamp}").Order(StringComparer.Ordinal));
            lock (_loggedCookies)
            {
                if (_loggedCookies.TryGetValue(name_set, out string? last) && last == current)
                {
                    return;
                }

                _loggedCookies[name_set] = current;
            }

            string line_text = string.Join(", ", set.Select(c => $"{c.Name} ({c.When})"));
            LogCookiesSet(log, path, line_text);
        }
        catch (Exception ex)
        {
            string error_type = ex.GetType().Name;
            LogCookiesNotLogged(log, error_type);
        }
    }

    // The Name, Readable Expiry And Raw Expiry Attribute Of Each Set-Cookie Line; Values Are Never Read
    private static List<(string Name, string When, string Stamp)> ParseSetCookies(HttpResponseMessage res, DateTimeOffset now)
    {
        var set = new List<(string Name, string When, string Stamp)>();
        if (!res.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? lines))
        {
            return set;
        }

        foreach (string line in lines)
        {
            string[] parts = line.Split(';');
            int eq         = parts[0].IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            // Max-Age Wins Over Expires, As In RFC 6265; The Raw Attribute Is What Tells Two Responses Apart
            string when  = "session";
            string stamp = "session";
            foreach (string attr in parts.Skip(1).Select(a => a.Trim()))
            {
                if (attr.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase) && long.TryParse(attr[8..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
                {
                    when  = FormatExpiry(() => now.AddSeconds(seconds));
                    stamp = attr;
                    break;
                }

                if (attr.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(attr[8..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
                {
                    when  = FormatExpiry(() => date);
                    stamp = attr;
                }
            }

            set.Add((parts[0][..eq].Trim(), when, stamp));
        }

        return set;
    }

    // An Absurd Expiry (Out Of Range) Is Shown As Unknown Instead Of Throwing
    private static string FormatExpiry(Func<DateTimeOffset> expires)
    {
        try
        {
            return "expires " + expires().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "expires ?";
        }
    }

    // Port Of typedHTTPResponse
    private async Task<T> ReadTypedAsync<T>(HttpResponseMessage res, CancellationToken ct)
        where T : IMessage<T>, new()
    {
        using (res)
        {
            byte[] body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            string? mime = res.Content.Headers.ContentType?.MediaType;
            int status = (int)res.StatusCode;

            // Success
            if (status is >= 200 and < 300)
            {
                return DecodeBody<T>(body, mime);
            }

            // Failure: Attach Google's Error When It Decodes
            ErrorResponse? error = null;
            try
            {
                var decoded = DecodeBody<ErrorResponse>(body, mime);
                error = decoded.Message.Length > 0 ? decoded : null;
            }
            catch (Exception ex) when (ex is InvalidDataException or PbLiteException or InvalidProtocolBufferException)
            {
                LogErrorBodyNotDecoded(log, status, ex);
            }

            throw new GmRequestException(status, error, body);
        }
    }

    // Port Of decodeProtoResp (Media Type Without Parameters, Case-Insensitive)
    private static T DecodeBody<T>(byte[] body, string? mime) where T : IMessage<T>, new()
    {
        if (string.IsNullOrEmpty(mime))
        {
            throw new InvalidDataException("Response has no parsable content type.");
        }

        if (mime.Equals(ProtobufMime, StringComparison.OrdinalIgnoreCase))
        {
            var message = new T();
            message.MergeFrom(body);
            return message;
        }

        if (mime.Equals(PbLiteMime, StringComparison.OrdinalIgnoreCase) || mime.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return PbLiteSerializer.Deserialize<T>(body);
        }

        throw new InvalidDataException($"Unknown content type {mime} in response.");
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Server error from Google Messages, retrying. Status {StatusCode}, path {Path}, attempt {Attempt}, retry in {RetryIn}")]
    private static partial void LogServerErrorRetry(ILogger logger, int statusCode, string path, int attempt, TimeSpan retryIn);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cookies set by {Path}: {Names}")]
    private static partial void LogCookiesSet(ILogger logger, string path, string names);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cookie diagnostics skipped: {ErrorType}")]
    private static partial void LogCookiesNotLogged(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error response body (status {StatusCode}) did not decode")]
    private static partial void LogErrorBodyNotDecoded(ILogger logger, int statusCode, Exception exception);
}

/// <summary>
/// The answer to a raw media request sent with <see cref="GmHttpClient.SendMediaAsync"/>.
/// </summary>
/// <param name="StatusCode">The HTTP status, always 2xx.</param>
/// <param name="Headers">The response and content headers by name, case-insensitive; repeated values are joined with commas.</param>
/// <param name="Body">The response body.</param>
public sealed record GmMediaResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Body);

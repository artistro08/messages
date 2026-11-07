using System.Globalization;
using System.Net.Http.Headers;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Http;

/// <summary>
/// Tells how the client encodes a request body.
/// </summary>
public enum GmContentType
{
    /// <summary>Binary protobuf, <c>application/x-protobuf</c>.</summary>
    Protobuf,

    /// <summary>PBLite, a protobuf as a JSON array, <c>application/json+protobuf</c>.</summary>
    PbLite,
}

/// <summary>
/// Sends requests to Google in the same way as the web app: relay headers, cookies, SAPISIDHASH, retries and error decoding.
/// </summary>
/// <remarks>
/// Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/http.go">http.go</see>,
/// <c>BuildRelayHeaders</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/func.go">util/func.go</see>
/// and <c>fetchConfig</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// Each POST gets the relay headers and the cookies of the jar. The client sends a request that gets a 5xx answer
/// up to 3 times in total. Between the attempts, it waits 1 s, then 2 s, on the <see cref="TimeProvider"/>. It never
/// retries a long poll or a network failure. The jar must be the only cookie store. Thus build the
/// <see cref="HttpClient"/> handlers with <c>UseCookies = false</c>, so a handler cookie container adds no second copy.
/// Cookie values and tokens never go in a log.
/// </remarks>
/// <param name="http">The client for normal requests.</param>
/// <param name="longPollHttp">The client for long polls. See <see cref="CreateLongPollClient"/>.</param>
/// <param name="cookies">The cookie jar that the session also uses.</param>
/// <param name="time">The clock for the SAPISIDHASH and the retry waits.</param>
/// <param name="log">The logger.</param>
public sealed partial class GmHttpClient(HttpClient http, HttpClient longPollHttp, CookieJar cookies, TimeProvider time, ILogger log)
{
    /// <summary>The total number of attempts for a request that gets only 5xx answers.</summary>
    public const int ServerErrorMaxAttempts = 3;

    /// <summary>The wait before the first retry. Attempt N waits N times this value.</summary>
    public static readonly TimeSpan ServerErrorRetryInterval = TimeSpan.FromSeconds(1);

    /// <summary>The timeout of the long-poll client. Go sets it with <c>WithGlobalTimeout(30 * time.Minute)</c>.</summary>
    public static readonly TimeSpan LongPollTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The largest media response body that the client reads. It is the 100 MiB LibGM file limit plus 4 MiB for the encryption overhead.</summary>
    public const long MaxMediaBodyBytes = (100L + 4L) * 1024 * 1024;

    /// <summary>The largest API response body that the client reads. 16 MiB is far more than any real page of conversations or messages.</summary>
    public const long MaxApiBodyBytes = 16L * 1024 * 1024;

    private const string ProtobufMime = "application/x-protobuf";
    private const string PbLiteMime   = "application/json+protobuf";

    // This map keeps the last logged cookie line for each set of cookie names. Thus an unchanged response writes no new line.
    private readonly Dictionary<string, string> _loggedCookies = [];

    /// <summary>
    /// Creates the client for long polls.
    /// </summary>
    /// <remarks>The only difference from a normal client is the 30 minute timeout.</remarks>
    /// <param name="handler">The message handler to send through.</param>
    /// <returns>A new client. The caller owns it.</returns>
    public static HttpClient CreateLongPollClient(HttpMessageHandler handler) => new(handler) { Timeout = LongPollTimeout };

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Sends a POST and returns the raw response. The method retries 5xx answers.
    /// </summary>
    /// <remarks>
    /// The method returns when the headers arrive, so a long poll can stream the body. The caller owns the response
    /// and must dispose it. After the last attempt, the method returns a 5xx response and does not throw. The cookies
    /// of each response go into the jar, also from a response that the method retries.
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

        (byte[] bytes, string mime) = type switch
        {
            GmContentType.Protobuf => (body.ToByteArray(), ProtobufMime),
            GmContentType.PbLite   => (PbLiteSerializer.Serialize(body), PbLiteMime),
            _                      => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown request content type."),
        };
        HttpClient client = longPoll ? longPollHttp : http;

        for (int attempt = 1; ; attempt++)
        {
            // Each attempt gets a new request. After a cancel, no request goes out.
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(bytes) };
            AddRelayHeaders(request, "*/*");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", mime);
            cookies.ApplyTo(request, time.GetUtcNow().ToUnixTimeSeconds());

            HttpResponseMessage res = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            cookies.UpdateFrom(res);
            LogSetCookies(res, url.AbsolutePath);

            if (longPoll || (int)res.StatusCode < 500 || attempt >= ServerErrorMaxAttempts)
            {
                return res;
            }

            TimeSpan retryIn = attempt * ServerErrorRetryInterval;
            LogServerErrorRetry(log, (int)res.StatusCode, url.AbsolutePath, attempt, retryIn);
            res.Dispose();
            await Task.Delay(retryIn, time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a POST and decodes the response into a message.
    /// </summary>
    /// <remarks>
    /// Ported from Go <c>typedHTTPResponse</c>. Each non-2xx status throws a <see cref="GmRequestException"/>. The
    /// exception holds the Google <see cref="ErrorResponse"/> when the body decodes and has a message.
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
    /// Sends the config GET of the web app and decodes the response into a message.
    /// </summary>
    /// <remarks>
    /// The headers are the same as in Go <c>fetchConfig</c>: the relay headers with <c>sec-fetch-site: same-origin</c>,
    /// without <c>x-user-agent</c> and without <c>origin</c>. As in Go, the method does not retry.
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
    /// Sends a raw media request with only the headers of the caller: an upload start, an upload finalize or a download.
    /// </summary>
    /// <remarks>
    /// A media request has only the LibGM upload headers, from Go <c>NewMediaUploadHeaders</c> and
    /// <c>BuildUploadHeaders</c> in util/func.go. It has no API key, no <c>x-user-agent</c> and no cookies. Thus the
    /// method does not read or update the jar. A content header, such as <c>content-type</c>, goes on the content.
    /// Go does not check the status of a download, but this method always checks it. An upload start and finalize
    /// need exactly 200, as in Go. A download needs a 2xx. The method refuses a body over
    /// <see cref="MaxMediaBodyBytes"/> while it reads it. The request uses the long-poll client with its 30 minute
    /// timeout. A big file can take longer than the normal 100 s. As in Go, the method does not retry. Bodies and
    /// headers never go in a log.
    /// </remarks>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The target URL.</param>
    /// <param name="headers">Every header to send.</param>
    /// <param name="content">The body, or null for no body. The request disposes it.</param>
    /// <param name="requireOk">True when only HTTP 200 is a success, as for an upload start and finalize.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The status, the response headers and the body. The header names are case-insensitive and include the content headers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/>, <paramref name="url"/> or <paramref name="headers"/> is null.</exception>
    /// <exception cref="GmRequestException">The status was not 200 (<paramref name="requireOk"/>) or not 2xx.</exception>
    /// <exception cref="InvalidDataException">The body is larger than <see cref="MaxMediaBodyBytes"/>.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<GmMediaResponse> SendMediaAsync(HttpMethod method, Uri url, IEnumerable<KeyValuePair<string, string>> headers, HttpContent? content, bool requireOk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(headers);

        using var request = new HttpRequestMessage(method, url) { Content = content };
        foreach ((string name, string value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using HttpResponseMessage res = await longPollHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        byte[] body = await ReadCappedAsync(res.Content, MaxMediaBodyBytes, ct).ConfigureAwait(false);
        int status  = (int)res.StatusCode;
        if (requireOk ? status != 200 : status is < 200 or >= 300)
        {
            throw new GmRequestException(status, null, body);
        }

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IEnumerable<string> values) in res.Headers.Concat(res.Content.Headers))
        {
            responseHeaders[name] = string.Join(",", values);
        }

        return new GmMediaResponse(status, responseHeaders, body);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // The method reads a body up to a limit. A declared length over the limit fails at once. A streamed body fails
    // when it crosses the limit. A declared length goes into 1 array of that size, so a big video is in memory only 1 time.
    // A body shorter than its declared length throws EndOfStreamException.
    internal static async Task<byte[]> ReadCappedAsync(HttpContent content, long max, CancellationToken ct)
    {
        long? length = content.Headers.ContentLength;
        if (length > max)
        {
            throw new InvalidDataException("The response is too large.");
        }

        Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var streamScope = stream.ConfigureAwait(false);

        if (length is { } known)
        {
            byte[] body = new byte[known];
            await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
            return body;
        }

        using var buffer = new MemoryStream();
        byte[] chunk     = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new InvalidDataException("The response is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // This method ports util.BuildRelayHeaders. The caller adds the Content-Type.
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

    // For diagnosis, the method logs the names and expiry times of the cookies that a response sets. It never logs
    // the values. The log must never make a request fail.
    private void LogSetCookies(HttpResponseMessage res, string path)
    {
        try
        {
            List<(string Name, string When, string Stamp)> set = ParseSetCookies(res, time.GetUtcNow());
            if (set.Count == 0)
            {
                return;
            }

            // Only a change goes in the log. The same cookie names with the same expiry attributes write no new line.
            string nameSet  = string.Join(",", set.Select(c => c.Name).Order(StringComparer.Ordinal));
            string current  = string.Join(",", set.Select(c => $"{c.Name}|{c.Stamp}").Order(StringComparer.Ordinal));
            lock (_loggedCookies)
            {
                if (_loggedCookies.TryGetValue(nameSet, out string? last) && last == current)
                {
                    return;
                }

                _loggedCookies[nameSet] = current;
            }

            string lineText = string.Join(", ", set.Select(c => $"{c.Name} ({c.When})"));
            LogCookiesSet(log, path, lineText);
        }
        catch (Exception ex)
        {
            string errorType = ex.GetType().Name;
            LogCookiesNotLogged(log, errorType);
        }
    }

    // The method gets the name, the readable expiry and the raw expiry attribute of each Set-Cookie line. It never
    // reads a value.
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

            // As in RFC 6265, Max-Age has priority over Expires. The raw attribute tells 2 responses apart.
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

    // The method shows an expiry out of the DateTimeOffset range as unknown and does not throw.
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

    // This method ports typedHTTPResponse.
    private async Task<T> ReadTypedAsync<T>(HttpResponseMessage res, CancellationToken ct)
        where T : IMessage<T>, new()
    {
        using (res)
        {
            byte[] body  = await ReadCappedAsync(res.Content, MaxApiBodyBytes, ct).ConfigureAwait(false);
            string? mime = res.Content.Headers.ContentType?.MediaType;
            int status   = (int)res.StatusCode;

            if (status is >= 200 and < 300)
            {
                return DecodeBody<T>(body, mime);
            }

            // The exception holds the Google error when the error body decodes.
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

    // This method ports decodeProtoResp. It compares the media type without parameters and ignores case.
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
/// The answer to a raw media request from <see cref="GmHttpClient.SendMediaAsync"/>.
/// </summary>
/// <param name="StatusCode">The HTTP status, always 2xx.</param>
/// <param name="Headers">The response and content headers by name, case-insensitive. A comma joins repeated values.</param>
/// <param name="Body">The response body.</param>
public sealed record GmMediaResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Body);

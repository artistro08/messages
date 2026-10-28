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
/// The token is refreshed once less than an hour is left. The request is signed with the account's refresh key over
/// <c>"{requestId}:{timestampMicros}"</c>. Push keys are never sent: this app has no web push, so Go's
/// <c>MoreParameters</c> block stays empty. The token itself is never logged.
/// </remarks>
/// <param name="http">The HTTP client.</param>
/// <param name="auth">The account whose token is refreshed.</param>
/// <param name="endpoints">The server addresses.</param>
/// <param name="time">The clock for the expiry check and the new expiry.</param>
/// <param name="log">The logger.</param>
public sealed partial class TokenRefresher(GmHttpClient http, AuthData auth, GmEndpoints endpoints, TimeProvider time, ILogger log)
{
    /// <summary>Refresh once less than this is left (Go: <c>RefreshTachyonBuffer</c>).</summary>
    public static readonly TimeSpan RefreshBuffer = TimeSpan.FromHours(1);

    /// <summary>How long a token lasts when the server gives no TTL.</summary>
    public static readonly TimeSpan DefaultValidFor = TimeSpan.FromHours(24);

    // Google Errors That Mean The Credentials Are Gone (Go: ErrInvalidCredentials, ErrRequestedEntityNotFound)
    private const string InvalidCredentialsMessage = "Request had invalid authentication credentials. Expected OAuth 2 access token, login cookie or other valid authentication credential. See https://developers.google.com/identity/sign-in/web/devconsole-project.";
    private const string EntityNotFoundMessage     = "Requested entity was not found.";

    /// <summary>
    /// Raised with <see cref="AuthTokenRefreshed"/> after a new token is stored.
    /// </summary>
    public event Action<object>? EventRaised;

    /// <summary>
    /// Refreshes the token if less than an hour is left.
    /// </summary>
    /// <remarks>
    /// Does nothing when the account has no browser device (not paired) or more than an hour is left. On success the
    /// new token, expiry and TTL are stored on the <see cref="AuthData"/> and <see cref="AuthTokenRefreshed"/> is raised.
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

        // Sign "{id}:{micros}"
        string request_id = Guid.NewGuid().ToString();
        long timestamp    = now.ToUnixTimeMilliseconds() * 1000;
        byte[] signature  = auth.RefreshKey.SignSha256Der(Encoding.UTF8.GetBytes(request_id + ":" + timestamp.ToString(CultureInfo.InvariantCulture)));

        var payload = new RegisterRefreshRequest
        {
            MessageAuth = new AuthMessage
            {
                RequestID        = request_id,
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
        RegisterRefreshResponse res = await http.PostTypedAsync<RegisterRefreshResponse>(endpoints.RegisterRefresh, payload, GmContentType.PbLite, ct).ConfigureAwait(false);

        // Note: Length > 0 stands in for Go's != nil, as elsewhere in this port
        if (res.TokenData is not { TachyonAuthToken.Length: > 0 } token)
        {
            throw new InvalidDataException("No tachyon auth token in refresh response.");
        }

        Update(token);
        EventRaised?.Invoke(new AuthTokenRefreshed());
    }

    /// <summary>
    /// Stores a new token with its expiry and TTL.
    /// </summary>
    /// <remarks>
    /// Ported from <c>updateTachyonAuthToken</c>. A TTL of 0 means 24 hours. Also used by pairing, which gets its
    /// first token the same way.
    /// </remarks>
    /// <param name="data">The token and its TTL in microseconds.</param>
    public void Update(TokenData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        TimeSpan valid_for = data.TTL == 0 ? DefaultValidFor : TimeSpan.FromMicroseconds(data.TTL);

        auth.TachyonAuthToken = data.TachyonAuthToken.ToByteArray();
        auth.TachyonExpiry    = time.GetUtcNow() + valid_for;
        auth.TachyonTtlMicros = (long)valid_for.TotalMicroseconds;
        LogUpdated(log, auth.TachyonExpiry, data.TTL);
    }

    /// <summary>
    /// Tells whether a refresh error means the session is gone for good.
    /// </summary>
    /// <remarks>
    /// Ported from <c>isFatalRefreshError</c>: HTTP 401, 403 and 404 are fatal, and so are Google's "invalid
    /// credentials" and "entity not found" errors with any status (matched on type and message, like Go's
    /// <c>RequestError.Is</c>). Everything else, including network errors and 5xx, is worth retrying. Inner exceptions
    /// are checked too, standing in for Go's error wrapping.
    /// </remarks>
    /// <param name="e">The error from <see cref="RefreshIfNeededAsync"/>.</param>
    /// <returns>True when retrying cannot help.</returns>
    public static bool IsFatal(Exception e)
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
        {
            if (current is not GmRequestException req)
            {
                continue;
            }

            bool known_error = req.Error is { Type: 16, Message: InvalidCredentialsMessage } or { Type: 5, Message: EntityNotFoundMessage };
            if (known_error || req.StatusCode is 401 or 403 or 404)
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Refreshing auth token, current expiry {Expiry}")]
    private static partial void LogRefreshing(ILogger logger, DateTimeOffset expiry);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated tachyon token, expiry {Expiry}, valid for {ValidForMicros} us")]
    private static partial void LogUpdated(ILogger logger, DateTimeOffset expiry, long validForMicros);
}

using System.Globalization;
using GMessages.Protocol.Auth;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Pairing;

/// <summary>
/// A phone found on a Google account that could be paired.
/// </summary>
/// <remarks>
/// Ported from Go's <c>primaryDeviceID</c>. The unknown fields keep Go's names because their meaning is not known:
/// <see cref="UnknownTs"/> is what Go stores as the destination phone ID.
/// </remarks>
/// <param name="RegId">The registration ID (a UUID string) of the phone.</param>
/// <param name="UnknownInt">An unknown number the server sends for the phone.</param>
/// <param name="UnknownTs">An unknown timestamp-like number; 0 when the server sent none.</param>
/// <param name="LastSeen">When the phone was last seen, from the server's microsecond timestamp.</param>
public sealed record PrimaryDevice(string RegId, ulong UnknownInt, ulong UnknownTs, DateTimeOffset LastSeen);

/// <summary>
/// Signs in with a Google account: fetches the web config and asks the server for the Tachyon token and the account's phones.
/// </summary>
/// <remarks>
/// Ported from <c>baseSignInGaiaPayload</c>, <c>signInGaiaGetToken</c> and <c>getGaiaPrimaryDevices</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go">pair_google.go</see>,
/// and <c>FetchConfig</c> / <c>fetchConfig</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>
/// with <c>ParsedClientVersion</c> from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/gmproto/config-extra.go">config-extra.go</see>.
/// The config fetch sets the session ID, which the sign-in request needs for its device ID. The sign-in stores the
/// Tachyon token and the browser and phone identities on the <see cref="AuthData"/>. The token is never logged.
/// </remarks>
/// <param name="http">The HTTP client.</param>
/// <param name="auth">The account being signed in.</param>
/// <param name="endpoints">The server addresses.</param>
/// <param name="time">The clock for the token expiry.</param>
/// <param name="log">The logger.</param>
public sealed partial class GaiaSignIn(GmHttpClient http, AuthData auth, GmEndpoints endpoints, TimeProvider time, ILogger log)
{
    /// <summary>
    /// Fetches the web config and takes the session ID from it.
    /// </summary>
    /// <remarks>
    /// Ported from <c>FetchConfig</c>. The session ID is only replaced when the config has a device ID. A device ID
    /// that is not a UUID is logged and ignored, as in Go. An unparseable client version fails the fetch. Go meant to
    /// do the same but wraps the wrong variable (<c>err</c> instead of <c>parseErr</c>), so its error hides the real
    /// cause; this port reports it. Cookies from the response are merged into the jar.
    /// </remarks>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the config is processed.</returns>
    /// <exception cref="GmRequestException">The status was not 2xx.</exception>
    /// <exception cref="InvalidDataException">The client version cannot be parsed, or the response content type is unknown.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task FetchConfigAsync(CancellationToken ct)
    {
        Config config = await http.GetTypedAsync<Config>(endpoints.Config, ct).ConfigureAwait(false);

        // Fail On An Unparseable Version
        ConfigVersion version = ParseClientVersion(config.ClientVersion);
        ConfigVersion current = GmConstants.ConfigVersion;
        if (version.Year != current.Year || version.Month != current.Month || version.Day != current.Day)
        {
            LogVersionNotLatest(log, current.Year, current.Month, current.Day, version.Year, version.Month, version.Day);
        }

        // Take The Session ID From The Device Info
        string device_id = config.DeviceInfo?.DeviceID ?? "";
        if (device_id.Length == 0)
        {
            return;
        }

        if (Guid.TryParse(device_id, out Guid session_id))
        {
            auth.SessionId = session_id;
        }
        else
        {
            LogBadDeviceId(log, device_id);
        }
    }

    /// <summary>
    /// Signs in and returns the phones on the account that can be paired.
    /// </summary>
    /// <remarks>
    /// Ported from <c>signInGaiaGetToken</c> plus <c>getGaiaPrimaryDevices</c>. The request carries the refresh key's
    /// public key. On success the Tachyon token (TTL 0 means 24 hours) and the identities are stored: the browser is
    /// the device as sent, the phone is a copy with a lower-cased source ID. A phone is an <c>UnknownItems2</c> entry
    /// with <c>UnknownInt4 == 1</c>; its last-seen time and timestamp come from the <c>UnknownItems3</c> entry with
    /// the same UUID, if any. Order follows the server.
    /// </remarks>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The phones, possibly none.</returns>
    /// <exception cref="GmRequestException">The status was not 2xx.</exception>
    /// <exception cref="InvalidDataException">The response has no token or device, or an unknown content type.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<IReadOnlyList<PrimaryDevice>> GetPrimaryDevicesAsync(CancellationToken ct)
    {
        // Ask The Server
        SignInGaiaRequest payload = BaseRequest();
        payload.Inner.SomeData = new SignInGaiaRequest.Types.Inner.Types.Data { SomeData = ByteString.CopyFrom(auth.RefreshKey.PublicKeySpki()) };
        SignInGaiaResponse response = await http.PostTypedAsync<SignInGaiaResponse>(endpoints.SignInGaia, payload, GmContentType.PbLite, ct).ConfigureAwait(false);

        // Note: Go would store a nil token or device here; this port rejects them instead of saving a half sign-in
        if (response.TokenData is null)
        {
            throw new InvalidDataException("No token data in sign-in response.");
        }

        if (response.DeviceData?.DeviceWrapper?.Device is not { } device)
        {
            throw new InvalidDataException("No device in sign-in response.");
        }

        // Store The Token And Identities
        TokenRefresher.Apply(auth, response.TokenData, time);
        Device phone   = device.Clone();
        phone.SourceID = device.SourceID.ToLowerInvariant();
        auth.Mobile    = phone;
        auth.Browser   = device;
        LogSignedIn(log, auth.TachyonExpiry);

        // Find The Phones
        var devices = new List<PrimaryDevice>();
        var by_uuid = new Dictionary<string, int>();
        foreach (var item in response.DeviceData.UnknownItems2.Where(i => i.UnknownInt4 == 1))
        {
            // Note: Go keeps a map, so a repeated UUID points at the last entry
            by_uuid[item.DestOrSourceUUID] = devices.Count;
            devices.Add(new PrimaryDevice(item.DestOrSourceUUID, item.UnknownBigInt7, 0, default));
        }

        // Join Last-Seen Data By UUID
        foreach (var item in response.DeviceData.UnknownItems3)
        {
            if (!by_uuid.TryGetValue(item.DestOrSourceUUID, out int index))
            {
                continue;
            }

            devices[index] = devices[index] with
            {
                LastSeen  = DateTimeOffset.UnixEpoch.AddTicks(item.UnknownTimestampMicroseconds * TimeSpan.TicksPerMicrosecond),
                UnknownTs = item.Item8?.UnknownTimestamp ?? 0,
            };
        }

        return devices;
    }

    /// <summary>
    /// Builds the device ID the sign-in request sends.
    /// </summary>
    /// <remarks>
    /// <c>"messages-web-"</c> plus the lowercase hex of the session ID's 16 bytes in RFC 4122 order, which is what
    /// Go's <c>%x</c> of the UUID bytes gives.
    /// </remarks>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>The device ID.</returns>
    internal static string DeviceIdFor(Guid sessionId) => "messages-web-" + Convert.ToHexStringLower(sessionId.ToByteArray(bigEndian: true));

    /// <summary>
    /// Parses the web client version string (<c>YYYYMMDD</c>) the way Go's <c>ParsedClientVersion</c> does.
    /// </summary>
    /// <remarks>
    /// Year, month and day are read from fixed positions; V1 and V2 are always 4 and 6. Go would crash on a string
    /// shorter than 8 characters; this throws instead.
    /// </remarks>
    /// <param name="clientVersion">The version string from the config.</param>
    /// <returns>The parsed version.</returns>
    /// <exception cref="InvalidDataException">The string is too short or a part is not a number.</exception>
    internal static ConfigVersion ParseClientVersion(string clientVersion)
    {
        if (clientVersion.Length < 8
            || !int.TryParse(clientVersion.AsSpan(0, 4), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int year)
            || !int.TryParse(clientVersion.AsSpan(4, 2), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int month)
            || !int.TryParse(clientVersion.AsSpan(6, 2), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int day))
        {
            throw new InvalidDataException("Failed to parse client version.");
        }

        return new ConfigVersion { Year = year, Month = month, Day = day, V1 = 4, V2 = 6 };
    }

    // Port Of baseSignInGaiaPayload
    private SignInGaiaRequest BaseRequest() => new()
    {
        AuthMessage = new AuthMessage
        {
            RequestID     = Guid.NewGuid().ToString(),
            Network       = GmConstants.GoogleNetwork,
            ConfigVersion = GmConstants.ConfigVersion,
        },
        Inner = new SignInGaiaRequest.Types.Inner
        {
            DeviceID = new SignInGaiaRequest.Types.Inner.Types.DeviceID { UnknownInt1 = 3, DeviceID_ = DeviceIdFor(auth.SessionId) },
        },
        Network = GmConstants.GoogleNetwork,
    };

    [LoggerMessage(Level = LogLevel.Trace, Message = "Web client version is not the latest: {CurrentYear}.{CurrentMonth}.{CurrentDay} -> {LatestYear}.{LatestMonth}.{LatestDay}")]
    private static partial void LogVersionNotLatest(ILogger logger, int currentYear, int currentMonth, int currentDay, int latestYear, int latestMonth, int latestDay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to parse device ID {DeviceId}")]
    private static partial void LogBadDeviceId(ILogger logger, string deviceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Signed in with Google account, token expiry {Expiry}")]
    private static partial void LogSignedIn(ILogger logger, DateTimeOffset expiry);
}

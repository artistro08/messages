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
/// Ported from <c>primaryDeviceID</c> in Go. The unknown fields keep the Go names, because their meaning is not
/// known. Go stores <see cref="UnknownTs"/> as the destination phone ID.
/// </remarks>
/// <param name="RegId">The registration ID of the phone, as a UUID string.</param>
/// <param name="UnknownInt">An unknown number that the server sends for the phone.</param>
/// <param name="UnknownTs">An unknown number like a timestamp, or 0 when the server sent none.</param>
/// <param name="LastSeen">The last time that the server saw the phone, from its microsecond timestamp.</param>
public sealed record PrimaryDevice(string RegId, ulong UnknownInt, ulong UnknownTs, DateTimeOffset LastSeen);

/// <summary>
/// Signs in with a Google account. It fetches the web config and asks the server for the Tachyon token and the phones
/// of the account.
/// </summary>
/// <remarks>
/// Ported from <c>baseSignInGaiaPayload</c>, <c>signInGaiaGetToken</c> and <c>getGaiaPrimaryDevices</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go">pair_google.go</see>,
/// and <c>FetchConfig</c> / <c>fetchConfig</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>
/// with <c>ParsedClientVersion</c> from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/gmproto/config-extra.go">config-extra.go</see>.
/// The config fetch sets the session ID, which the sign-in request needs for its device ID. The sign-in stores the
/// Tachyon token and the browser and phone identities on the <see cref="AuthData"/>. The log never gets the token.
/// </remarks>
/// <param name="http">The HTTP client.</param>
/// <param name="auth">The account to sign in.</param>
/// <param name="endpoints">The server addresses.</param>
/// <param name="time">The clock for the token expiry.</param>
/// <param name="log">The logger.</param>
public sealed partial class GaiaSignIn(GmHttpClient http, AuthData auth, GmEndpoints endpoints, TimeProvider time, ILogger log)
{
    /// <summary>
    /// Fetches the web config and takes the session ID from it.
    /// </summary>
    /// <remarks>
    /// Ported from <c>FetchConfig</c>. The method replaces the session ID only when the config has a device ID. It logs
    /// a device ID that is not a UUID and keeps the old session ID. Go sets the zero UUID then. A client version that
    /// does not parse fails the fetch. Go has the same intent, but it wraps the wrong variable: <c>err</c>, not
    /// <c>parseErr</c>. Thus the error of Go hides the real cause. This port reports the cause. The method merges the
    /// cookies of the response into the jar.
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

        ConfigVersion version = ParseClientVersion(config.ClientVersion);
        ConfigVersion current = GmConstants.ConfigVersion;
        if (version.Year != current.Year || version.Month != current.Month || version.Day != current.Day)
        {
            LogVersionNotLatest(log, current.Year, current.Month, current.Day, version.Year, version.Month, version.Day);
        }

        string deviceId = config.DeviceInfo?.DeviceID ?? "";
        if (deviceId.Length == 0)
        {
            return;
        }

        if (Guid.TryParse(deviceId, out Guid sessionId))
        {
            auth.SessionId = sessionId;
        }
        else
        {
            LogBadDeviceId(log, deviceId);
        }
    }

    /// <summary>
    /// Signs in and returns the phones on the account that can be paired.
    /// </summary>
    /// <remarks>
    /// Ported from <c>signInGaiaGetToken</c> and <c>getGaiaPrimaryDevices</c>. The request carries the public key of
    /// the refresh key. On success, the method stores the Tachyon token and the identities. A token TTL of 0 means 24
    /// hours. The browser is the device as the server sent it. The phone is a copy with a lowercase source ID. A phone
    /// is an <c>UnknownItems2</c> entry with <c>UnknownInt4 == 1</c>. Its last-seen time and timestamp come from the
    /// <c>UnknownItems3</c> entry with the same UUID, when there is one. The order is the order of the server.
    /// </remarks>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The phones. The list can be empty.</returns>
    /// <exception cref="GmRequestException">The status was not 2xx.</exception>
    /// <exception cref="InvalidDataException">The response has no token or device, or an unknown content type.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<IReadOnlyList<PrimaryDevice>> GetPrimaryDevicesAsync(CancellationToken ct)
    {
        SignInGaiaRequest payload = BaseRequest();
        payload.Inner.SomeData = new SignInGaiaRequest.Types.Inner.Types.Data { SomeData = ByteString.CopyFrom(auth.RefreshKey.PublicKeySpki()) };
        SignInGaiaResponse response = await http.PostTypedAsync<SignInGaiaResponse>(endpoints.SignInGaia, payload, GmContentType.PbLite, ct).ConfigureAwait(false);

        // Go stores a nil token or device here. This port refuses them, so that it does not save a half sign-in.
        if (response.TokenData is null)
        {
            throw new InvalidDataException("No token data in sign-in response.");
        }

        if (response.DeviceData?.DeviceWrapper?.Device is not { } device)
        {
            throw new InvalidDataException("No device in sign-in response.");
        }

        TokenRefresher.Apply(auth, response.TokenData, time);
        Device phone   = device.Clone();
        phone.SourceID = device.SourceID.ToLowerInvariant();
        auth.Mobile    = phone;
        auth.Browser   = device;
        TimeSpan validFor  = TimeSpan.FromMicroseconds(auth.TachyonTtlMicros);
        LogSignedIn(log, auth.TachyonExpiry, validFor);

        var devices = new List<PrimaryDevice>();
        var byUuid  = new Dictionary<string, int>();
        foreach (RPCGaiaData.Types.UnknownContainer.Types.Item2.Types.Item1 item in response.DeviceData.UnknownItems2.Where(i => i.UnknownInt4 == 1))
        {
            // As in Go, a repeated UUID points at the last entry, because Go keeps a map.
            byUuid[item.DestOrSourceUUID] = devices.Count;
            devices.Add(new PrimaryDevice(item.DestOrSourceUUID, item.UnknownBigInt7, 0, default));
        }

        foreach (RPCGaiaData.Types.UnknownContainer.Types.Item4 item in response.DeviceData.UnknownItems3)
        {
            if (!byUuid.TryGetValue(item.DestOrSourceUUID, out int index))
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
    /// The ID is <c>"messages-web-"</c> and the lowercase hex of the 16 bytes of the session ID in RFC 4122 order. The
    /// <c>%x</c> format of Go gives the same hex for the UUID bytes.
    /// </remarks>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>The device ID.</returns>
    internal static string DeviceIdFor(Guid sessionId) => "messages-web-" + Convert.ToHexStringLower(sessionId.ToByteArray(bigEndian: true));

    /// <summary>
    /// Parses the web client version string <c>YYYYMMDD</c>, as <c>ParsedClientVersion</c> in Go does.
    /// </summary>
    /// <remarks>
    /// The method reads the year, the month and the day from fixed positions. V1 is always 4 and V2 is always 6. Go
    /// crashes on a string shorter than 8 characters. This port throws an exception then.
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

    // Port of baseSignInGaiaPayload.
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Tachyon token valid until {Expiry:o} (TTL {ValidFor})")]
    private static partial void LogSignedIn(ILogger logger, DateTimeOffset expiry, TimeSpan validFor);
}

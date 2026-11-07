using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Http;

/// <summary>
/// Holds the fixed values that make requests look like the Google web app.
/// </summary>
/// <remarks>
/// These values change together when Google ships a new web client: the browser version, the config version and the
/// API key. Each protobuf property returns a new copy, so a caller cannot change the shared defaults. Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/constants.go">util/constants.go</see>
/// and
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/config.go">util/config.go</see>.
/// </remarks>
public static class GmConstants
{
    /// <summary>The public Google API key that the web app sends as <c>x-goog-api-key</c>.</summary>
    public const string GoogleApiKey = "AIzaSyCA4RsOZUFrm9whhtGosPlJLmVPnfSHKz8";

    /// <summary>The browser user agent string.</summary>
    public const string UserAgent = "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

    /// <summary>The <c>sec-ch-ua</c> client hint.</summary>
    public const string SecUA = "\"Google Chrome\";v=\"152\", \"Chromium\";v=\"152\", \"Not-A.Brand\";v=\"24\"";

    /// <summary>The platform name. The client sends it in quotes as <c>sec-ch-ua-platform</c>.</summary>
    public const string UAPlatform = "Android";

    /// <summary>The <c>x-user-agent</c> value.</summary>
    public const string XUserAgent = "grpc-web-javascript/0.1";

    /// <summary>The <c>sec-ch-ua-mobile</c> client hint.</summary>
    public const string SecUAMobile = "?1";

    /// <summary>The network name for a sign-in with a Google account.</summary>
    public const string GoogleNetwork = "GDitto";

    /// <summary>
    /// Gets the web client version that the client sends with authenticated calls.
    /// </summary>
    /// <remarks>Each access returns a new instance.</remarks>
    public static ConfigVersion ConfigVersion => new() { Year = 2026, Month = 9, Day = 2, V1 = 4, V2 = 6 };

    /// <summary>
    /// Gets the browser description that the client sends when it pairs.
    /// </summary>
    /// <remarks>Each access returns a new instance.</remarks>
    public static BrowserDetails BrowserDetails => new()
    {
        UserAgent   = UserAgent,
        BrowserType = BrowserType.Other,
        OS          = "libgm",
        DeviceType  = DeviceType.Tablet,
    };
}

using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Http;

/// <summary>
/// The fixed values that make requests look like Google's own web app.
/// </summary>
/// <remarks>
/// Kept in one file because they change together when Google ships a new web client (browser version, config
/// version, API key). The protobuf messages are returned as fresh copies on each access so callers can never change
/// the shared defaults. Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/constants.go">util/constants.go</see>
/// and
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/config.go">util/config.go</see>.
/// </remarks>
public static class GmConstants
{
    /// <summary>The public Google API key the web app sends as <c>x-goog-api-key</c>.</summary>
    public const string GoogleApiKey = "AIzaSyCA4RsOZUFrm9whhtGosPlJLmVPnfSHKz8";

    /// <summary>The browser user agent string.</summary>
    public const string UserAgent = "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

    /// <summary>The <c>sec-ch-ua</c> client hint.</summary>
    public const string SecUA = "\"Google Chrome\";v=\"152\", \"Chromium\";v=\"152\", \"Not-A.Brand\";v=\"24\"";

    /// <summary>The platform name, sent quoted as <c>sec-ch-ua-platform</c>.</summary>
    public const string UAPlatform = "Android";

    /// <summary>The <c>x-user-agent</c> value.</summary>
    public const string XUserAgent = "grpc-web-javascript/0.1";

    /// <summary>The <c>sec-ch-ua-mobile</c> client hint.</summary>
    public const string SecUAMobile = "?1";

    /// <summary>The network name used when signing in with a Google account.</summary>
    public const string GoogleNetwork = "GDitto";

    /// <summary>
    /// The web client version sent with authenticated calls.
    /// </summary>
    /// <remarks>A new instance on every access.</remarks>
    public static ConfigVersion ConfigVersion => new() { Year = 2026, Month = 9, Day = 2, V1 = 4, V2 = 6 };

    /// <summary>
    /// The browser description sent when pairing.
    /// </summary>
    /// <remarks>A new instance on every access.</remarks>
    public static BrowserDetails BrowserDetails => new()
    {
        UserAgent   = UserAgent,
        BrowserType = BrowserType.Other,
        OS          = "libgm",
        DeviceType  = DeviceType.Tablet,
    };
}

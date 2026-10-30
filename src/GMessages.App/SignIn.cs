namespace GMessages.App;

/// <summary>
/// Google sign-in values for the pairing page's WebView2, proven by the sign-in spike.
/// </summary>
/// <remarks>
/// See <c>docs/superpowers/notes/sign-in-spike.md</c>. Google blocks WebView2's own user agent ("This browser or app
/// may not be secure"), so a desktop Chrome one is set before navigating. Keep its Chrome version current, because
/// Google may start rejecting old versions.
/// </remarks>
internal static class SignIn
{
    /// <summary>The desktop Chrome user agent set on the sign-in WebView2.</summary>
    public const string ChromeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

    /// <summary>The browser arguments for the sign-in WebView2: hides client hints, turns off device-bound sessions, and sets the user agent at browser level.</summary>
    /// <remarks>
    /// <c>DeviceBoundSessions</c> is Chromium's feature name for Device Bound Session Credentials (DBSC), on by default
    /// on Windows since Chromium 145 (<c>net/base/features.cc</c>; Edge's "Device Bound Session Credentials (Standard)"
    /// flag). With it on, Google binds the sign-in to a key in this throwaway profile, so the app's copy of
    /// <c>__Secure-1PSIDTS</c> can't be renewed and Google refuses it about 2 hours later. The earlier
    /// <c>DeviceBoundSessionCredentials</c> was the origin trial's name, not a feature, so it turned nothing off.
    /// </remarks>
    public const string BrowserArguments = "--disable-features=UserAgentClientHint,DeviceBoundSessions --user-agent=\"" + ChromeUserAgent + "\"";

    /// <summary>The Google origin that also holds the account cookies in production.</summary>
    public const string GoogleOrigin = "https://google.com";

    /// <summary>The cookie that means the user is signed in.</summary>
    public const string SignedInCookie = "SAPISID";

    /// <summary>The fresh-session cookie waited for (up to <see cref="SessionCookieWait"/>) once signed in.</summary>
    public const string SessionCookie = "__Secure-1PSIDTS";

    /// <summary>The main sign-in cookie, captured alongside <see cref="SessionCookie"/> (<c>__Secure-1PSID</c> + <c>__Secure-1PSIDTS</c>).</summary>
    public const string MainSessionCookie = "__Secure-1PSID";

    /// <summary>How long to wait for <see cref="SessionCookie"/> after <see cref="SignedInCookie"/> appears.</summary>
    public static readonly TimeSpan SessionCookieWait = TimeSpan.FromSeconds(5);

    /// <summary>The cookies pairing needs; every other cookie is ignored.</summary>
    public static readonly string[] WantedNames = ["SID", "HSID", "SSID", "OSID", "APISID", SignedInCookie, SessionCookie, MainSessionCookie];
}

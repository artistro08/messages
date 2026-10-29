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

    /// <summary>The browser arguments for the sign-in WebView2: hides client hints and device-bound sessions, and sets the user agent at browser level.</summary>
    public const string BrowserArguments = "--disable-features=UserAgentClientHint,DeviceBoundSessionCredentials --user-agent=\"" + ChromeUserAgent + "\"";

    /// <summary>The Google origin that also holds the account cookies in production.</summary>
    public const string GoogleOrigin = "https://google.com";

    /// <summary>The cookie that means the user is signed in.</summary>
    public const string SignedInCookie = "SAPISID";

    /// <summary>The fresh-session cookie waited for (up to <see cref="SessionCookieWait"/>) once signed in.</summary>
    public const string SessionCookie = "__Secure-1PSIDTS";

    /// <summary>How long to wait for <see cref="SessionCookie"/> after <see cref="SignedInCookie"/> appears.</summary>
    public static readonly TimeSpan SessionCookieWait = TimeSpan.FromSeconds(5);

    /// <summary>The cookies pairing needs; every other cookie is ignored.</summary>
    public static readonly string[] WantedNames = ["SID", "HSID", "SSID", "OSID", "APISID", SignedInCookie, SessionCookie];
}

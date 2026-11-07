namespace GMessages.App;

/// <summary>
/// Holds the Google sign-in values for the WebView2 of the pairing page. The sign-in spike proved them.
/// </summary>
/// <remarks>
/// See <c>docs/superpowers/notes/sign-in-spike.md</c>. Google blocks the default user agent of WebView2 ("This browser
/// or app may not be secure"). Thus the app sets a desktop Chrome user agent before the navigation. Keep its Chrome
/// version current, because Google can start to reject old versions.
/// </remarks>
internal static class SignIn
{
    /// <summary>The desktop Chrome user agent of the sign-in WebView2.</summary>
    public const string ChromeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

    /// <summary>
    /// The browser arguments of the sign-in WebView2. They hide client hints, turn off device-bound sessions, and set
    /// the user agent at browser level.
    /// </summary>
    /// <remarks>
    /// <c>DeviceBoundSessions</c> is the Chromium feature name of Device Bound Session Credentials (DBSC). Chromium 145
    /// and later turn it on by default on Windows (<c>net/base/features.cc</c>). Edge calls the flag "Device Bound
    /// Session Credentials (Standard)". With the feature on, Google binds the sign-in to a key in this throwaway
    /// profile. Then the app cannot renew its copy of <c>__Secure-1PSIDTS</c>, and Google refuses it about 2 hours
    /// later. The earlier <c>DeviceBoundSessionCredentials</c> was the name of the origin trial, not a feature, so it
    /// turned nothing off.
    /// </remarks>
    public const string BrowserArguments = "--disable-features=UserAgentClientHint,DeviceBoundSessions --user-agent=\"" + ChromeUserAgent + "\"";

    /// <summary>The Google origin that also holds the account cookies in production.</summary>
    public const string GoogleOrigin = "https://google.com";

    /// <summary>The cookie that shows that the user is signed in.</summary>
    public const string SignedInCookie = "SAPISID";

    /// <summary>
    /// The cookie of a fresh session. The app waits for it after sign-in, up to <see cref="SessionCookieWait"/>.
    /// </summary>
    public const string SessionCookie = "__Secure-1PSIDTS";

    /// <summary>The main sign-in cookie. The app captures it with <see cref="SessionCookie"/>.</summary>
    public const string MainSessionCookie = "__Secure-1PSID";

    /// <summary>The wait for <see cref="SessionCookie"/> after <see cref="SignedInCookie"/> shows.</summary>
    public static readonly TimeSpan SessionCookieWait = TimeSpan.FromSeconds(5);

    /// <summary>The cookies that pairing needs. The app ignores all other cookies.</summary>
    public static readonly string[] WantedNames = ["SID", "HSID", "SSID", "OSID", "APISID", SignedInCookie, SessionCookie, MainSessionCookie];
}

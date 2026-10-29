using GMessages.Presentation;
using GMessages.Protocol.Http;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace GMessages.App.Views;

/// <summary>
/// The sign-in helper's only window: Google sign-in in a WebView2. Runs in the <c>--sign-in</c> helper process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sign-in.</b> The WebView2 gets a fresh profile folder (<see cref="SignIn.NewProfileFolder"/>), the desktop Chrome
/// user agent and browser arguments (<see cref="SignIn"/>), and no web messages or host objects, all set before
/// navigating to <see cref="GmEndpoints.Authentication"/>.
/// </para>
/// <para>
/// <b>Cookie capture.</b> After every navigation the wanted cookies are read for the authentication page's origin
/// and, in production, <c>https://google.com</c> (ported from the sign-in spike). Capture happens once
/// <c>SAPISID</c> and <c>__Secure-1PSIDTS</c> are both there, or <see cref="SignIn.SessionCookieWait"/> after
/// <c>SAPISID</c> appears without the other. Cookie values are never logged.
/// </para>
/// <para>
/// <b>Finish.</b> Captured cookies (or "canceled" when the user closes the window) go to the app through
/// <c>send</c>; then the WebView2 closes, its profile folder (a live Google session) is deleted with retries, and the
/// helper exits. A WebView2 that can't start sends nothing and exits with code 1, which the app shows as a failure.
/// </para>
/// </remarks>
public sealed partial class SignInWindow : Window
{
    private readonly GmEndpoints _endpoints;
    private readonly ILogger _log;
    private readonly Func<IReadOnlyDictionary<string, string>?, Task> _send;
    private readonly DispatcherQueueTimer _sessionCookieTimer;
    private readonly string _profileFolder = SignIn.NewProfileFolder();

    // The Browser; Null Once Closed
    private WebView2? _webView;
    private bool _finished;
    private bool _closed;

    /// <summary>
    /// Creates the window and starts the sign-in.
    /// </summary>
    /// <param name="endpoints">Where the sign-in page lives.</param>
    /// <param name="log">The helper's own log (never cookie values).</param>
    /// <param name="send">Sends the captured cookies, or null for "canceled", to the app.</param>
    public SignInWindow(GmEndpoints endpoints, ILogger log, Func<IReadOnlyDictionary<string, string>?, Task> send)
    {
        _endpoints = endpoints;
        _log       = log;
        _send      = send;
        InitializeComponent();

        // Window
        Title                      = $"{AppInfo.DisplayName}: Sign in to Google";
        AppTitleBar.Title          = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Grace Wait For The Session Cookie
        _sessionCookieTimer             = DispatcherQueue.CreateTimer();
        _sessionCookieTimer.Interval    = SignIn.SessionCookieWait;
        _sessionCookieTimer.IsRepeating = false;
        _sessionCookieTimer.Tick       += (_, _) => _ = CheckCookiesAsync(force: true);

        // Behavior
        Closed += OnClosed;
        _ = StartSignInAsync();
    }

    /// <summary>
    /// Sets the window's light or dark look from the app theme.
    /// </summary>
    /// <param name="theme">The app theme setting.</param>
    public void ApplyTheme(Presentation.Settings.AppTheme theme)
    {
        // Page Colors
        RootGrid.RequestedTheme = theme switch
        {
            Presentation.Settings.AppTheme.Light => ElementTheme.Light,
            Presentation.Settings.AppTheme.Dark  => ElementTheme.Dark,
            _                                    => ElementTheme.Default,
        };

        // Caption Buttons: Drawn By The System, So Tell Them The App Theme (Or They Follow Windows' Instead)
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            Presentation.Settings.AppTheme.Light => TitleBarTheme.Light,
            Presentation.Settings.AppTheme.Dark  => TitleBarTheme.Dark,
            _                                    => TitleBarTheme.UseDefaultAppMode,
        };
    }

    // =========================================================================
    // SIGN-IN
    // =========================================================================

    private async Task StartSignInAsync()
    {
        var web = new WebView2();
        AutomationProperties.SetAutomationId(web, "Pairing.SignIn");
        AutomationProperties.SetName(web, "Google sign-in");
        _webView         = web;
        SignInHost.Child = web;

        try
        {
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, _profileFolder, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = SignIn.BrowserArguments });
            await web.EnsureCoreWebView2Async(env);

            // Closed While Starting
            if (_finished)
            {
                return;
            }

            // Desktop Chrome User Agent Before Navigating, Or Google Blocks The Sign-In.
            // Google Detects WebView2 Through Its Client Hints And The chrome.webview Bridge.
            // All Three Changes (Browser Arguments, No Web Messages, No Host Objects) Are Needed Together; Verified On 2026-10-28
            web.CoreWebView2.Settings.UserAgent             = SignIn.ChromeUserAgent;
            web.CoreWebView2.Settings.IsWebMessageEnabled   = false;
            web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            web.NavigationCompleted                        += (_, _) => _ = CheckCookiesAsync(force: false);
            web.CoreWebView2.Navigate(_endpoints.Authentication.AbsoluteUri);
        }
#pragma warning disable CA1031 // Any Start-Up Failure (No WebView2 Runtime, Bad Folder) Ends The Helper With An Error
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Closing During Start-Up Is Expected
            if (_finished)
            {
                return;
            }

            AppLog.SignInStartFailed(_log, ex.GetType().Name);
            _ = FinishAsync(null, failed: true);
        }
    }

    // Reads The Wanted Cookies; Captures Once Signed In (Or Forced By The Grace Timer With SAPISID Present)
    private async Task CheckCookiesAsync(bool force)
    {
        WebView2? web = _webView;
        if (_finished || web?.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var jar = new Dictionary<string, string>();
            foreach (string origin in CookieOrigins())
            {
                foreach (CoreWebView2Cookie cookie in await web.CoreWebView2.CookieManager.GetCookiesAsync(origin))
                {
                    if (SignIn.WantedNames.Contains(cookie.Name))
                    {
                        jar.TryAdd(cookie.Name, cookie.Value);
                    }
                }
            }

            // Re-Check After The Awaits: Another Navigation May Have Captured First, Or The Window Closed
            if (_finished || !jar.ContainsKey(SignIn.SignedInCookie))
            {
                return;
            }

            // Signed In Without The Session Cookie Yet: Give It A Few Seconds
            if (!force && !jar.ContainsKey(SignIn.SessionCookie))
            {
                if (!_sessionCookieTimer.IsRunning)
                {
                    _sessionCookieTimer.Start();
                }

                return;
            }

            AppLog.CookiesCaptured(_log, jar.Keys);
            _ = FinishAsync(jar, failed: false);
        }
#pragma warning disable CA1031 // A Failed Read Is Retried On The Next Navigation
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.CookieReadFailed(_log, ex.GetType().Name);

            // The Forced Read Failed: Try Again Later, Since No Navigation May Come To Retry It
            if (force && !_finished)
            {
                _sessionCookieTimer.Start();
            }
        }
    }

    // The Authentication Page's Origin, Plus google.com In Production (The Fake Server Under Test Has Only Its Own)
    private IEnumerable<string> CookieOrigins()
    {
        yield return _endpoints.Authentication.GetLeftPart(UriPartial.Authority);
        if (_endpoints.MessagesBase == GmEndpoints.Production.MessagesBase)
        {
            yield return SignIn.GoogleOrigin;
        }
    }

    // =========================================================================
    // FINISH
    // =========================================================================

    // Once Only: Send (Cookies Go First, Since Google Rejects Stale Ones), Close The Browser, Delete Its Profile, Exit
    private async Task FinishAsync(Dictionary<string, string>? cookies, bool failed)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _sessionCookieTimer.Stop();

        // Send
        if (failed)
        {
            Environment.ExitCode = 1;
        }
        else
        {
            try
            {
                await _send(cookies);
            }
#pragma warning disable CA1031 // The App Is Gone Or Stopped Listening; Still Clean Up And Exit
            catch (Exception ex)
#pragma warning restore CA1031
            {
                AppLog.SignInPipeFailed(_log, ex.GetType().Name);
                Environment.ExitCode = 1;
            }
        }

        // Close The Browser And The Window
        WebView2? web    = _webView;
        _webView         = null;
        SignInHost.Child = null;
        web?.Close();
        if (!_closed)
        {
            Close();
        }

        // Delete The Signed-In Profile Off The UI Thread, Then Exit
        await Task.Run(() => SignIn.DeleteProfileAsync(_profileFolder, _log));
        Application.Current.Exit();
    }

    // The User Closed The Window: "Canceled"
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _ = FinishAsync(null, failed: false);
    }
}

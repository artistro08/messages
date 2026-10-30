using System.ComponentModel;
using System.Globalization;
using GMessages.Presentation.Pairing;
using GMessages.Protocol.Http;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace GMessages.App.Views;

/// <summary>
/// Pairs the app with the phone: Google sign-in in a WebView2, then the emoji to tap on the phone. Fills the main window
/// below the title bar while the app isn't paired.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sign-in.</b> Each attempt gets a fresh WebView2 with its own profile folder
/// (<c>%TEMP%\{InstanceKey}-signin-{guid}</c>, so a test copy and a real copy never share names) and the desktop
/// Chrome user agent (<see cref="SignIn.ChromeUserAgent"/>), set before navigating to
/// <see cref="GmEndpoints.Authentication"/>. Folders a quit or crash left behind are deleted at the next launch.
/// </para>
/// <para>
/// <b>Cookie capture.</b> After every navigation the wanted cookies are read for the authentication page's origin
/// and, in production, <c>https://google.com</c> (ported from the sign-in spike). Capture happens once
/// <c>SAPISID</c> and <c>__Secure-1PSIDTS</c> are both there, or <see cref="SignIn.SessionCookieWait"/> after
/// <c>SAPISID</c> appears without the other. The cookies go straight to the view model, because Google rejects
/// cookies a few hours old; then the WebView2 is closed and its profile folder (a live Google session) deleted.
/// Cookie values are never logged.
/// </para>
/// <para>
/// The window calls <see cref="Close"/> when it drops the page (paired, or the window closed), which cancels a running
/// pairing. Ported in style from Magic Buds' <c>SettingsWindow</c>.
/// </para>
/// </remarks>
public sealed partial class PairingPage : UserControl
{
    private readonly GmEndpoints _endpoints;
    private readonly ILogger _log;
    private readonly DispatcherQueueTimer _sessionCookieTimer;

    // The Current Attempt's Browser And Profile Folder; Null Once Closed
    private WebView2? _webView;
    private string? _profileFolder;
    private bool _captured;
    private bool _closed;

    // Device-Bound Session (DBSC) Header Names Seen This Attempt, As "host response|request Name"; Never Values
    private readonly HashSet<string> _boundHeaders = [];

    /// <summary>
    /// Creates the page and starts the first sign-in.
    /// </summary>
    /// <param name="viewModel">The pairing state.</param>
    /// <param name="endpoints">Where the sign-in page lives.</param>
    /// <param name="log">Writes warnings (never cookie values).</param>
    public PairingPage(PairingViewModel viewModel, GmEndpoints endpoints, ILogger log)
    {
        ViewModel  = viewModel;
        _endpoints = endpoints;
        _log       = log;
        InitializeComponent();

        // Grace Wait For The Session Cookie
        _sessionCookieTimer             = DispatcherQueue.CreateTimer();
        _sessionCookieTimer.Interval    = SignIn.SessionCookieWait;
        _sessionCookieTimer.IsRepeating = false;
        _sessionCookieTimer.Tick       += (_, _) => _ = CheckCookiesAsync(force: true);

        // Behavior
        ViewModel.PropertyChanged += OnViewModelChanged;
        _ = StartSignInAsync();
    }

    /// <summary>
    /// Asks the window to close (Cancel), which cancels the pairing.
    /// </summary>
    public event EventHandler? CancelRequested;

    /// <summary>
    /// Gets the pairing state the view binds to.
    /// </summary>
    public PairingViewModel ViewModel { get; }

    /// <summary>
    /// Cancels a running pairing and removes the browser, so nothing outlives the page. Call when the window drops it.
    /// </summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Bindings.StopTracking();
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Cancel();
        CloseSignIn();
    }

    // =========================================================================
    // SIGN-IN
    // =========================================================================

    // A Fresh Browser And Profile For Each Attempt (A Closed WebView2 Can't Be Reused)
    private async Task StartSignInAsync()
    {
        _captured      = false;
        _boundHeaders.Clear();
        _profileFolder = Path.Combine(Path.GetTempPath(), $"{ProfilePrefix}{Guid.NewGuid():N}");

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
            if (_webView != web)
            {
                return;
            }

            // Desktop Chrome User Agent Before Navigating, Or Google Blocks The Sign-In.
            // Google Detects WebView2 Through Its Client Hints And The chrome.webview Bridge.
            // All Three Changes (Browser Arguments, No Web Messages, No Host Objects) Are Needed Together; Verified On 2026-10-28
            web.CoreWebView2.Settings.UserAgent             = SignIn.ChromeUserAgent;
            web.CoreWebView2.Settings.IsWebMessageEnabled   = false;
            web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            web.NavigationCompleted            += (_, _) => _ = CheckCookiesAsync(force: false);

            // Diagnosis: Does Google Bind This Sign-In To The Browser (DBSC)? Header Names Only
            web.CoreWebView2.WebResourceResponseReceived += (_, e) => NoteBoundSessionHeaders(e);

            // No Popups: a Google link that opens a new window would outlive sign-in and keep the profile folder locked
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.Navigate(_endpoints.Authentication.AbsoluteUri);
        }
#pragma warning disable CA1031 // Any Start-Up Failure (No WebView2 Runtime, Bad Folder) Becomes The Failure Screen
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Closing During Start-Up Is Expected; An Older Attempt Must Not Tear Down A Newer One
            if (_closed || _webView != web)
            {
                return;
            }

            AppLog.SignInStartFailed(_log, ex.GetType().Name);
            CloseSignIn();
            ViewModel.SignInFailed();
        }
    }

    // Reads The Wanted Cookies; Captures Once Signed In (Or Forced By The Grace Timer With SAPISID Present)
    private async Task CheckCookiesAsync(bool force)
    {
        WebView2? web = _webView;
        if (_captured || web?.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var jar      = new Dictionary<string, string>();
            var expiries = new Dictionary<string, string>();
            foreach (string origin in CookieOrigins())
            {
                foreach (CoreWebView2Cookie cookie in await web.CoreWebView2.CookieManager.GetCookiesAsync(origin))
                {
                    if (SignIn.WantedNames.Contains(cookie.Name))
                    {
                        if (jar.TryAdd(cookie.Name, cookie.Value))
                        {
                            // Name And Expiry Only, For The Log; A Session Cookie Has No Expiry
                            expiries[cookie.Name] = cookie.IsSession
                                ? "session"
                                : ExpiryText(cookie.Expires);
                        }
                    }
                }
            }

            // Re-Check After The Awaits: Another Navigation May Have Captured First, Or The Page Closed
            if (_captured || _webView != web || !jar.ContainsKey(SignIn.SignedInCookie))
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

            Capture(jar, expiries);
        }
#pragma warning disable CA1031 // A Failed Read Is Retried On The Next Navigation
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.CookieReadFailed(_log, ex.GetType().Name);

            // The Forced Read Failed: Try Again Later, Since No Navigation May Come To Retry It
            if (force && !_captured && _webView == web)
            {
                _sessionCookieTimer.Start();
            }
        }
    }

    // Notes DBSC Headers: Google Asking To Bind (Response) Or The Browser Holding A Bound Session (Request)
    private void NoteBoundSessionHeaders(CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (_captured)
        {
            return;
        }

        try
        {
            string host = new Uri(e.Request.Uri).Host;
            foreach (KeyValuePair<string, string> header in e.Response.Headers)
            {
                if (GmHttpClient.IsBoundSessionHeader(header.Key))
                {
                    _boundHeaders.Add($"{host} response {header.Key}");
                }
            }

            foreach (KeyValuePair<string, string> header in e.Request.Headers)
            {
                if (GmHttpClient.IsBoundSessionHeader(header.Key))
                {
                    _boundHeaders.Add($"{host} request {header.Key}");
                }
            }
        }
#pragma warning disable CA1031 // Diagnostics Must Never Break Sign-In
        catch (Exception)
#pragma warning restore CA1031
        {
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

    // Logging Must Never Fail Sign-In: An Out-Of-Range Expiry Is Shown As Unknown
    private static string ExpiryText(double expires)
    {
        try
        {
            return "expires " + DateTimeOffset.FromUnixTimeSeconds((long)expires).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "expires ?";
        }
    }

    // Pairs Right Away (Stale Cookies Get Rejected), Then Drops The Browser And Its Signed-In Profile
    private void Capture(Dictionary<string, string> cookies, Dictionary<string, string> expiries)
    {
        _captured = true;
        _sessionCookieTimer.Stop();
        string names = string.Join(", ", cookies.Keys.Select(name => $"{name} ({expiries[name]})"));
        AppLog.CookiesCaptured(_log, names);
        AppLog.BoundSessionHeaders(_log, _boundHeaders.Count > 0 ? string.Join(", ", _boundHeaders.Order(StringComparer.Ordinal)) : "none");

        _ = ViewModel.OnCookiesCapturedAsync(cookies);
        CloseSignIn();
    }

    // Closes The WebView2 And Deletes Its Profile Folder Off The UI Thread
    private void CloseSignIn()
    {
        _sessionCookieTimer.Stop();
        WebView2? web    = _webView;
        string? folder   = _profileFolder;
        _webView         = null;
        _profileFolder   = null;
        SignInHost.Child = null;

        web?.Close();
        if (folder is not null)
        {
            _ = Task.Run(() => DeleteProfileAsync(folder));
        }
    }

    /// <summary>
    /// Deletes sign-in profile folders an earlier run left behind (a quit or crash while signing in ends the process
    /// before its delete finishes), in the background.
    /// </summary>
    /// <remarks>
    /// Call at launch, before any pairing page opens. The single-instance check means no other running copy uses
    /// these names, and the folders are listed now, so a sign-in started later is never touched.
    /// </remarks>
    /// <param name="log">Writes a warning for a folder that can't be deleted (never its contents).</param>
    public static void DeleteLeftoverProfiles(ILogger log)
    {
        string[] leftovers;
        try
        {
            leftovers = Directory.GetDirectories(Path.GetTempPath(), $"{ProfilePrefix}*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string folder in leftovers)
        {
            _ = Task.Run(() => DeleteProfileAsync(folder, log));
        }
    }

    // Per Instance Key, So The .Test Copy Under UI Tests And A Real Copy Never Clean Up Each Other's Profiles
    private static string ProfilePrefix => $"{Program.InstanceKey}-signin-";

    // The Browser Process May Hold Files Briefly After Close, So Retry (As The Spike Does)
    private Task DeleteProfileAsync(string folder) => DeleteProfileAsync(folder, _log);

    private static async Task DeleteProfileAsync(string folder, ILogger log)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }

        if (Directory.Exists(folder))
        {
            AppLog.ProfileNotDeleted(log, folder);
        }
    }

    // =========================================================================
    // VIEW MODEL AND BUTTONS
    // =========================================================================

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowEmojiImage();

        // Try Again: A New Browser. Paired (Done) Needs Nothing Here: The App Switches The Window To The Chats
        if (_closed || ViewModel.Step != PairingStep.SigningIn || _webView is not null)
        {
            return;
        }

        _ = StartSignInAsync();
    }

    // The Emoji's Noto Color Emoji Image (Assets/Emoji, From googlefonts/noto-emoji), So It Looks As On messages.google.com
    // And The Phone; The Text Glyph Stays Only When No Image Is Bundled For It
    private void ShowEmojiImage()
    {
        string? emoji = ViewModel.Emoji;
        string? name  = string.IsNullOrEmpty(emoji) ? null : PairingViewModel.EmojiFileName(emoji);
        bool has_image = name is not null && File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Emoji", name));

        EmojiImage.Source     = has_image ? new BitmapImage(new Uri("ms-appx:///Assets/Emoji/" + name)) : null;
        EmojiImage.Visibility = has_image ? Visibility.Visible : Visibility.Collapsed;
        EmojiText.Visibility  = has_image ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTryAgainClick(object sender, RoutedEventArgs e) => ViewModel.TryAgain();

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}

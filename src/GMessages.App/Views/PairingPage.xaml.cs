using System.ComponentModel;
using System.Globalization;
using GMessages.Presentation.Pairing;
using GMessages.Protocol.Http;
using GMessages.App.Windowing;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Muxc = Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views;

/// <summary>
/// Pairs the app with the phone: the Google sign-in in a WebView2, then the emoji to tap on the phone. The page fills
/// the main window below the title bar while the app is not paired.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sign-in.</b> Each attempt gets a fresh WebView2 with its own profile folder
/// <c>%TEMP%\{InstanceKey}-signin-{guid}</c>. Thus a test copy and a real copy never share names. The page sets the
/// desktop Chrome user agent <see cref="SignIn.ChromeUserAgent"/> before it navigates to
/// <see cref="GmEndpoints.Authentication"/>. The next launch deletes the folders that a quit or a crash left.
/// </para>
/// <para>
/// <b>Cookie capture.</b> After every navigation the page reads the wanted cookies for the origin of the
/// authentication page. In production it also reads them for <c>https://google.com</c>. This logic comes from the
/// sign-in spike. The capture happens when <c>SAPISID</c> and <c>__Secure-1PSIDTS</c> are both there. It also happens
/// <see cref="SignIn.SessionCookieWait"/> after <c>SAPISID</c> appears without the other cookie. The cookies go
/// straight to the view model, because Google rejects cookies a few hours old. The page then closes the WebView2 and
/// deletes its profile folder, which holds a live Google session. The log never names a cookie value.
/// </para>
/// <para>
/// The window calls <see cref="Close"/> when it drops the page after pairing or on close. That cancels a running
/// pairing. The style of the page comes from the <c>SettingsWindow</c> of Magic Buds.
/// </para>
/// </remarks>
public sealed partial class PairingPage : UserControl
{
    private readonly GmEndpoints _endpoints;
    private readonly ILogger _log;
    private readonly DispatcherQueueTimer _sessionCookieTimer;

    // The browser and the profile folder of the current attempt. Null after the close.
    private Muxc.WebView2? _webView;
    private string? _profileFolder;
    private bool _captured;
    private bool _closed;

    /// <summary>
    /// Creates the page and starts the first sign-in.
    /// </summary>
    /// <param name="viewModel">The pairing state.</param>
    /// <param name="endpoints">The endpoints, which hold the address of the sign-in page.</param>
    /// <param name="log">Writes warnings, never cookie values.</param>
    public PairingPage(PairingViewModel viewModel, GmEndpoints endpoints, ILogger log)
    {
        ViewModel  = viewModel;
        _endpoints = endpoints;
        _log       = log;
        InitializeComponent();

        // The grace wait for the session cookie.
        _sessionCookieTimer             = XamlHost.Dispatcher.CreateTimer();
        _sessionCookieTimer.Interval    = SignIn.SessionCookieWait;
        _sessionCookieTimer.IsRepeating = false;
        _sessionCookieTimer.Tick       += (_, _) => _ = CheckCookiesAsync(force: true);

        ViewModel.PropertyChanged += OnViewModelChanged;
        _ = StartSignInAsync();
    }

    /// <summary>
    /// Asks the window to close on Cancel, which cancels the pairing.
    /// </summary>
    public event EventHandler? CancelRequested;

    /// <summary>
    /// Gets the pairing state that the view binds to.
    /// </summary>
    public PairingViewModel ViewModel { get; }

    /// <summary>
    /// Cancels a running pairing and removes the browser, so nothing outlives the page. The window calls this method
    /// when it drops the page.
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

    // Each attempt gets a fresh browser and profile, because a closed WebView2 cannot start again.
    private async Task StartSignInAsync()
    {
        _captured      = false;
        _profileFolder = Path.Combine(Path.GetTempPath(), $"{ProfilePrefix}{Guid.NewGuid():N}");

        var web = new Muxc.WebView2();
        AutomationProperties.SetAutomationId(web, "Pairing.SignIn");
        AutomationProperties.SetName(web, "Google sign-in");
        _webView         = web;
        SignInHost.Child = web;

        try
        {
            // The WebView2 of WinUI 2 takes no environment. It makes its own from these variables when it starts.
            // Thus each attempt sets the profile folder and the browser arguments first.
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", _profileFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", SignIn.BrowserArguments);
            await web.EnsureCoreWebView2Async();

            // The page closed during the start.
            if (_webView != web)
            {
                return;
            }

            // The page must set the desktop Chrome user agent before the navigation, or Google blocks the sign-in.
            // Google detects WebView2 through its client hints and the chrome.webview bridge. The sign-in needs all 3
            // changes together: the browser arguments, no web messages and no host objects. A test on 2026-10-28
            // proved it.
            web.CoreWebView2.Settings.UserAgent             = SignIn.ChromeUserAgent;
            web.CoreWebView2.Settings.IsWebMessageEnabled   = false;
            web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            web.NavigationCompleted                        += (_, _) => _ = CheckCookiesAsync(force: false);

            // No popups. A Google link that opens a new window outlives the sign-in and keeps the profile folder
            // locked.
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;

            // Only https. The page does not allowlist hosts: Workspace SSO uses third-party providers, and Google uses
            // country domains.
            web.CoreWebView2.NavigationStarting += OnNavigationStarting;
            web.CoreWebView2.Navigate(_endpoints.Authentication.AbsoluteUri);
        }
        catch (Exception ex)
        {
            // Any start-up failure becomes the failure screen, such as no WebView2 runtime or a bad folder.
            // A close during the start is expected. An older attempt must not close a newer attempt.
            if (_closed || _webView != web)
            {
                return;
            }

            AppLog.SignInStartFailed(_log, ex.GetType().Name);
            CloseSignIn();
            ViewModel.SignInFailed();
        }
    }

    // Cancels each top-level navigation that is not https, redirects included. WebView2 uses about:blank internally, so
    // the page allows it. A test run also allows http to loopback for the fake auth server. The log names only the
    // host.
    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri))
        {
            e.Cancel = true;
            AppLog.SignInNavigationBlocked(_log, "invalid");
            return;
        }

        bool blank = uri.Scheme == "about" && string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase);
        bool https = uri.Scheme == Uri.UriSchemeHttps;
        bool fake  = TestHooks.Endpoints is not null && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!blank && !https && !fake)
        {
            e.Cancel = true;
            AppLog.SignInNavigationBlocked(_log, uri.Scheme);
            return;
        }

        if (!blank)
        {
            AppLog.SignInNavigating(_log, uri.Host);
        }
    }

    // Reads the wanted cookies and captures them after the sign-in. The grace timer forces the capture when SAPISID is
    // there.
    private async Task CheckCookiesAsync(bool force)
    {
        Muxc.WebView2? web = _webView;
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
                            // The log gets only the name and the expiry. A session cookie has no expiry.
                            expiries[cookie.Name] = cookie.IsSession
                                ? "session"
                                : ExpiryText(cookie.Expires);
                        }
                    }
                }
            }

            // The method checks again after the awaits. Another navigation can capture first, or the page can close.
            if (_captured || _webView != web || !jar.ContainsKey(SignIn.SignedInCookie))
            {
                return;
            }

            // The user signed in, but the session cookie is not there yet. The timer gives it a few seconds.
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
        catch (Exception ex)
        {
            // The next navigation retries a failed read.
            AppLog.CookieReadFailed(_log, ex.GetType().Name);

            // When the forced read fails, the timer tries again later. No navigation can come to retry it.
            if (force && !_captured && _webView == web)
            {
                _sessionCookieTimer.Start();
            }
        }
    }

    // The origin of the authentication page, plus google.com in production. The fake server of a test has only its own
    // origin.
    private IEnumerable<string> CookieOrigins()
    {
        yield return _endpoints.Authentication.GetLeftPart(UriPartial.Authority);
        if (_endpoints.MessagesBase == GmEndpoints.Production.MessagesBase)
        {
            yield return SignIn.GoogleOrigin;
        }
    }

    // The log must never fail the sign-in, so an out-of-range expiry shows as unknown.
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

    // Pairs at once, because Google rejects stale cookies. Then drops the browser and its signed-in profile.
    private void Capture(Dictionary<string, string> cookies, Dictionary<string, string> expiries)
    {
        _captured = true;
        _sessionCookieTimer.Stop();
        string names = string.Join(", ", cookies.Keys.Select(name => $"{name} ({expiries[name]})"));
        AppLog.CookiesCaptured(_log, names);

        _ = ViewModel.OnCookiesCapturedAsync(cookies);
        CloseSignIn();
    }

    // Closes the WebView2 and deletes its profile folder off the UI thread.
    private void CloseSignIn()
    {
        _sessionCookieTimer.Stop();
        Muxc.WebView2? web = _webView;
        string? folder     = _profileFolder;
        _webView           = null;
        _profileFolder     = null;
        SignInHost.Child   = null;

        web?.Close();
        if (folder is not null)
        {
            _ = Task.Run(() => DeleteProfileAsync(folder, _log));
        }
    }

    /// <summary>
    /// Deletes in the background the sign-in profile folders that an earlier run left. A quit or a crash during a
    /// sign-in ends the process before its delete finishes.
    /// </summary>
    /// <remarks>
    /// The App calls this method at launch, before a pairing page opens. The single-instance check means that no other
    /// running copy uses these names. The method lists the folders now, so it never touches a later sign-in.
    /// </remarks>
    /// <param name="log">Writes a warning for a folder that the delete cannot remove, never its contents.</param>
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

    // The prefix uses the instance key. Thus the .Test copy of the UI tests and a real copy never delete the profiles
    // of each other.
    private static string ProfilePrefix => $"{Program.InstanceKey}-signin-";

    // The browser process can hold files for a short time after the close. Thus the delete retries, as the spike does.
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

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowEmojiImage();

        // Try again starts a new browser. Done needs nothing here, because the App switches the window to the chats.
        if (_closed || ViewModel.Step != PairingStep.SigningIn || _webView is not null)
        {
            return;
        }

        _ = StartSignInAsync();
    }

    // Shows the Noto Color Emoji image of the emoji from Assets/Emoji, which comes from googlefonts/noto-emoji. Thus
    // it looks as on messages.google.com and the phone. The text glyph stays only when the app has no image for it.
    private void ShowEmojiImage()
    {
        string? emoji = ViewModel.Emoji;
        string? name  = string.IsNullOrEmpty(emoji) ? null : PairingViewModel.EmojiFileName(emoji);
        bool hasImage = name is not null && File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Emoji", name));

        EmojiImage.Source     = hasImage ? new BitmapImage(new Uri("ms-appx:///Assets/Emoji/" + name)) : null;
        EmojiImage.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        EmojiText.Visibility  = hasImage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTryAgainClick(object sender, RoutedEventArgs e) => ViewModel.TryAgain();

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}

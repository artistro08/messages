using System.ComponentModel;
using GMessages.Presentation;
using GMessages.Presentation.Pairing;
using GMessages.Presentation.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace GMessages.App.Views;

/// <summary>
/// Pairs the app with the phone: Google sign-in in the sign-in helper's window, then the emoji to tap on the phone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sign-in.</b> Once this window first shows, it starts the sign-in helper (<see cref="SignInHelper"/>), a second
/// copy of this exe that hosts the WebView2 outside the MSIX package, and says to sign in there. This process never
/// loads WebView2. Cookies from the helper go straight to the view model, because Google rejects cookies a few hours
/// old. A helper closed without signing in shows "Sign-in was closed"; one that fails shows the generic failure. Try
/// again starts a new helper. Cookie values are never logged.
/// </para>
/// <para>
/// Closing the window (or Cancel) cancels a running pairing and kills a running helper. Ported in style from Magic
/// Buds' <c>SettingsWindow</c>.
/// </para>
/// </remarks>
public sealed partial class PairingWindow : Window
{
    private readonly string _logsFolder;
    private readonly ILogger _log;
    private AppTheme _theme;

    // The Running Helper, Or Null
    private SignInHelper? _helper;
    private bool _started;
    private bool _closed;

    /// <summary>
    /// Creates the window. The first sign-in starts when it first shows.
    /// </summary>
    /// <param name="viewModel">The pairing state.</param>
    /// <param name="logsFolder">Where the sign-in helper writes its own log.</param>
    /// <param name="log">Writes the sign-in steps (never cookie values).</param>
    public PairingWindow(PairingViewModel viewModel, string logsFolder, ILogger log)
    {
        ViewModel   = viewModel;
        _logsFolder = logsFolder;
        _log        = log;
        InitializeComponent();

        // Window
        Title                      = $"{AppInfo.DisplayName}: Pair your phone";
        AppTitleBar.Title          = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Behavior
        ViewModel.PropertyChanged += OnViewModelChanged;
        Activated                 += OnActivated;
        Closed                    += OnClosed;
    }

    /// <summary>
    /// Gets the pairing state the view binds to.
    /// </summary>
    public PairingViewModel ViewModel { get; }

    /// <summary>
    /// Sets the window's light or dark look from the app theme. The next sign-in window uses it too.
    /// </summary>
    /// <param name="theme">The app theme setting.</param>
    public void ApplyTheme(AppTheme theme)
    {
        _theme = theme;

        // Page Colors
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };

        // Caption Buttons: Drawn By The System, So Tell Them The App Theme (Or They Follow Windows' Instead)
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark  => TitleBarTheme.Dark,
            _              => TitleBarTheme.UseDefaultAppMode,
        };
    }

    // =========================================================================
    // SIGN-IN
    // =========================================================================

    // First Show: Started Here, Not In The Constructor, So The Helper's Window Opens In Front Of This One
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_started || _closed)
        {
            return;
        }

        _started = true;
        _ = SignInAsync();
    }

    // One Helper Run: Cookies Pair, A Closed Window Or A Failure Shows Its Message
    private async Task SignInAsync()
    {
        SignInHelper helper;
        try
        {
            helper = SignInHelper.Start(_logsFolder, _theme, _log);
        }
#pragma warning disable CA1031 // Any Launch Failure Becomes The Failure Screen
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.SignInStartFailed(_log, ex.GetType().Name);
            ViewModel.SignInFailed();
            return;
        }

        _helper = helper;
        IReadOnlyDictionary<string, string>? cookies;
        try
        {
            cookies = await helper.Completion;
        }
#pragma warning disable CA1031 // Any Helper Failure Becomes The Failure Screen
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (EndHelper(helper))
            {
                AppLog.SignInHelperFailed(_log, ex.GetType().Name);
                ViewModel.SignInFailed();
            }

            return;
        }

        // Canceled Or Closed Here Meanwhile: Nothing To Show
        if (!EndHelper(helper))
        {
            return;
        }

        if (cookies is null)
        {
            AppLog.SignInClosed(_log);
            ViewModel.SignInClosed();
            return;
        }

        AppLog.CookiesCaptured(_log, cookies.Keys);
        _ = ViewModel.OnCookiesCapturedAsync(cookies);
    }

    // Stops This Helper If It Is Still The Current One; False When This Window Already Dropped It
    private bool EndHelper(SignInHelper helper)
    {
        if (_helper != helper)
        {
            return false;
        }

        _helper = null;
        helper.Dispose();
        return true;
    }

    // =========================================================================
    // VIEW MODEL AND BUTTONS
    // =========================================================================

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        switch (ViewModel.Step)
        {
            // Try Again: A New Helper
            case PairingStep.SigningIn when _started && _helper is null:
                _ = SignInAsync();
                break;

            // Paired: The Tray Takes Over
            case PairingStep.Done:
                Close();
                break;
        }
    }

    private void OnTryAgainClick(object sender, RoutedEventArgs e) => ViewModel.TryAgain();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    // Cancels A Running Pairing And Kills A Running Helper, So Nothing Outlives The Window
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        Bindings.StopTracking();
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Cancel();
        if (_helper is { } helper)
        {
            EndHelper(helper);
        }
    }
}

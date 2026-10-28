using GMessages.App.Views;
using GMessages.Presentation;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Logging;
using GMessages.Presentation.Pairing;
using GMessages.Presentation.Settings;
using GMessages.Protocol.Http;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace GMessages.App;

/// <summary>
/// Tray app composition root. No window opens at launch; the tray icon and its menu are the way in.
/// </summary>
/// <remarks>
/// The tray wiring, quit/show event watching and memory trim are ported from Magic Buds' <c>App.xaml.cs</c>.
/// </remarks>
public partial class App : Application, IDisposable
{
    // =========================================================================
    // TRAY ICON GUIDS
    // =========================================================================

    // Fixed identities, so Windows keeps the icon's taskbar placement across updates. A GUID belongs
    // to one exe location, so the packaged and unpackaged installs each get their own, and UI test
    // runs get a third so they never disturb a real install's icon.
    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-notifyicondataw#troubleshooting
    private static readonly Guid PackagedTrayGuid = new("2C8CFFE0-C9DB-4B2E-AD36-BD27A77E8033");
    private static readonly Guid ClassicTrayGuid  = new("FA25E877-3ACB-4CF3-A958-024545B7CE24");
#if EnableTestHooks
    private static readonly Guid TestTrayGuid     = new("41E044BE-2303-4240-B4ED-C6A29CD3DE24");
#endif

    private RollingFileLoggerProvider? _logProvider;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private AppSettings? _settings;
    private TrayIcon? _trayIcon;
    private TrayMenuWindow? _trayMenu;
    private MemoryTrim? _memoryTrim;
    private ConnectionController? _connection;
    private PairingWindow? _pairingWindow;
    private bool _connectionStarted;
    private EventWaitHandle? _quitRequest;
    private RegisteredWaitHandle? _quitWait;
    private RegisteredWaitHandle? _showWait;
    private bool _quitting;

    /// <summary>
    /// Creates the app. Closing a window must never end a tray app, so it only shuts down on request.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Closing the last window must not end a tray app
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    // TODO: Task 7 handles this by opening the main window; until then it's only logged

    /// <summary>
    /// Asks for the main window: tray left-click, the menu's Open, or a second launch. Carries what asked.
    /// </summary>
    internal event EventHandler<string>? ShowMainWindowRequested;

    /// <summary>
    /// Sets up logging, settings and the tray icon, then starts watching for quit and show requests.
    /// </summary>
    /// <param name="args">Launch details (unused; the command line is read by <see cref="Program"/>).</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Paths
        var paths = AppPaths.Resolve(AppIdentity.IsPackaged ? ApplicationData.Current.LocalFolder.Path : null, Program.TestDataDir);

        // Settings: MSIX keeps them in its package store, unpackaged uses a file
        ISettingsStore settingsStore = AppIdentity.IsPackaged
            ? new LocalSettingsStore()
            : new JsonFileSettingsStore(paths.SettingsFile);
        _settings = new AppSettings(settingsStore);

        // Logging
        var levelSwitch = new LogLevelSwitch { MinimumLevel = _settings.LogLevel };
        _logProvider    = new RollingFileLoggerProvider(paths.LogsFolder, levelSwitch, TimeProvider.System);
        _logger         = _logProvider.CreateLogger("GMessages.App");
        AppLog.Started(_logger, AppInfo.DisplayName, AppInfo.Version, AppIdentity.IsPackaged, Program.IsTestRun);

        // Memory Trim
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _memoryTrim    = new MemoryTrim(dispatcher, () => _trayMenu?.AppWindow.IsVisible != true && _pairingWindow is null);

        // Connection: every controller event comes back on the UI thread
        // TODO: Task 8 passes the --test-endpoints fake server here; until then always production
        _connection = new ConnectionController(paths, GmEndpoints.Production, TimeProvider.System, new SingleProviderLoggerFactory(_logProvider), a => dispatcher.TryEnqueue(() => a()));
        _connection.StateChanged += (_, _) => OnConnectionStateChanged();
        _ = StartConnectionAsync();

        // Tray Icon
        ShowMainWindowRequested += (_, source) =>
        {
            AppLog.OpenRequested(_logger, source);

            // Not paired yet: the way in is the pairing window
            if (_connectionStarted && NeedsPairing())
            {
                ShowPairingWindow();
            }
        };
        _trayIcon = new TrayIcon(AppInfo.DisplayName, SelectTrayGuid());
        _trayIcon.Invoked              += (_, _) => ShowMainWindowRequested?.Invoke(this, "tray");
        _trayIcon.ContextMenuRequested += (_, point) => ShowTrayMenu(point.X, point.Y);
        var trayAdded = _trayIcon.Show();
        AppLog.TrayAdded(_logger, trayAdded);

        // Quit and Show Requests
        _quitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
        _quitWait    = WatchEvent(dispatcher, _quitRequest, Quit, once: true);
        if (Program.ShowRequest is not null)
        {
            _showWait = WatchEvent(dispatcher, Program.ShowRequest, () => ShowMainWindowRequested?.Invoke(this, "second launch"), once: false);
        }

        // Startup itself leaves a lot behind that the idle tray app never needs
        _memoryTrim.Schedule();
    }

    // =========================================================================
    // CONNECTION AND PAIRING
    // =========================================================================

    // Loads the saved session; no session (or a dead one) opens the pairing window
    private async Task StartConnectionAsync()
    {
        try
        {
            await _connection!.StartAsync();
        }
#pragma warning disable CA1031 // Start-up must not take the tray down; the failure is logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.ConnectionStartFailed(_logger, ex);
            return;
        }

        // NotPaired is the starting state, so StartAsync raises nothing for it
        _connectionStarted = true;
        OnConnectionStateChanged();
    }

    private bool NeedsPairing() => _connection?.State is ConnectionState.NotPaired or ConnectionState.SignedOut;

    // NotPaired or SignedOut (also a later sign-out) opens the pairing window; pairing closes it itself on Done
    private void OnConnectionStateChanged()
    {
        if (_quitting || _connection is null || !_connectionStarted)
        {
            return;
        }

        AppLog.ConnectionStateChanged(_logger, _connection.State);
        if (NeedsPairing())
        {
            ShowPairingWindow();
        }
    }

    // Made when needed and destroyed on close, then the memory it used goes back to Windows
    private void ShowPairingWindow()
    {
        if (_quitting || _connection is null)
        {
            return;
        }

        if (_pairingWindow is not null)
        {
            _pairingWindow.Activate();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var viewModel  = new PairingViewModel(_connection, a => dispatcher.TryEnqueue(() => a()));
        _pairingWindow = new PairingWindow(viewModel, GmEndpoints.Production, _logger);
        _pairingWindow.ApplyTheme(_settings?.Theme ?? AppTheme.System);
        _pairingWindow.Closed += (_, _) =>
        {
            _pairingWindow = null;
            if (!_quitting)
            {
                _memoryTrim?.Schedule();
            }
        };
        _pairingWindow.Activate();
    }

    private static Guid SelectTrayGuid()
    {
#if EnableTestHooks
        if (Program.IsTestRun)
        {
            return TestTrayGuid;
        }
#endif
        return AppIdentity.IsPackaged ? PackagedTrayGuid : ClassicTrayGuid;
    }

    // Runs an action on the UI thread each time a named event is signaled
    private static RegisteredWaitHandle WatchEvent(DispatcherQueue dispatcher, WaitHandle handle, Action action, bool once) =>
        ThreadPool.RegisterWaitForSingleObject(
            handle,
            (_, _) => dispatcher.TryEnqueue(() => action()),
            null,
            Timeout.Infinite,
            once);

    // The right-click menu's window is made on first use, since most sessions never open it
    private void ShowTrayMenu(int pointX, int pointY)
    {
        if (_quitting)
        {
            return;
        }

        if (_trayMenu is null)
        {
            _trayMenu = new TrayMenuWindow();
            _trayMenu.ApplyTheme(_settings?.Theme ?? AppTheme.System);
            _trayMenu.OpenRequested     += (_, _) => ShowMainWindowRequested?.Invoke(this, "menu");
            _trayMenu.SettingsRequested += (_, _) => AppLog.SettingsRequested(_logger);
            _trayMenu.QuitRequested     += (_, _) => Quit();

            // Memory Trim: once the menu is gone, hand what it used back to Windows
            _trayMenu.AppWindow.Changed += (sender, e) =>
            {
                if (e.DidVisibilityChange && !sender.IsVisible && !_quitting)
                {
                    _memoryTrim?.Schedule();
                }
            };
        }

        var (x, y, edge) = ScreenGeometry.GetMenuAnchor(pointX, pointY);
        _trayMenu.ShowAt(x, y, edge);
    }

    /// <summary>
    /// Stops the watchers and timer, removes the tray icon and closes the windows and the log.
    /// </summary>
    /// <remarks>
    /// The connection controller is disposed by Quit before this, since that needs an await.
    /// </remarks>
    public void Dispose()
    {
        // A show request already queued must not open windows that are shutting down
        _quitting = true;

        _memoryTrim?.Cancel();
        _quitWait?.Unregister(null);
        _showWait?.Unregister(null);
        _quitRequest?.Dispose();
        _trayIcon?.Dispose();
        _trayMenu?.Close();
        _pairingWindow?.Close();
        _logProvider?.Dispose();
        GC.SuppressFinalize(this);
    }

    // Async void: called from event handlers; it always ends in Exit
    private async void Quit()
    {
        if (_quitting)
        {
            return;
        }

        AppLog.Quit(_logger);
        _quitting = true;
        _pairingWindow?.Close();

        // Let the client close its long poll and flush a save (while the log is still open), but never hang the quit on it
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
#pragma warning disable CA1031 // Quitting regardless
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing left to do but exit
        }
        finally
        {
            Dispose();
            Exit();
        }
    }

    // =========================================================================
    // LOGGER FACTORY
    // =========================================================================

    // The controller wants a factory; the app has one file provider, so a full LoggerFactory isn't needed
    private sealed class SingleProviderLoggerFactory(ILoggerProvider provider) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose()
        {
            // The App owns and disposes the provider
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using GMessages.App.Startup;
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
using Microsoft.Windows.System.Power;
using Windows.Networking.Connectivity;
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
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private IStartupService? _startup;
    private AppPaths? _paths;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _networkDebounce;
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
        _paths    = paths;

        // Settings: MSIX keeps them in its package store, unpackaged uses a file
        ISettingsStore settingsStore = AppIdentity.IsPackaged
            ? new LocalSettingsStore()
            : new JsonFileSettingsStore(paths.SettingsFile);
        _settings                  = new AppSettings(settingsStore);
        _settings.PropertyChanged += OnSettingChanged;

        // Start With Windows: the MSIX startup task when packaged, the Run key when not
        _startup = SelectStartupService(paths);

        // Logging
        var levelSwitch = new LogLevelSwitch { MinimumLevel = _settings.LogLevel };
        _logProvider    = new RollingFileLoggerProvider(paths.LogsFolder, levelSwitch, TimeProvider.System);
        _logger         = _logProvider.CreateLogger("GMessages.App");
        AppLog.Started(_logger, AppInfo.DisplayName, AppInfo.Version, AppIdentity.IsPackaged, Program.IsTestRun);

        // Memory Trim
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher    = dispatcher;
        _memoryTrim    = new MemoryTrim(dispatcher, () => _trayMenu?.AppWindow.IsVisible != true && _pairingWindow is null && _mainWindow is null && _settingsWindow is null);

        // Connection: every controller event comes back on the UI thread
        // TODO: Task 8 passes the --test-endpoints fake server here; until then always production
        _connection = new ConnectionController(paths, GmEndpoints.Production, TimeProvider.System, new SingleProviderLoggerFactory(_logProvider), a => dispatcher.TryEnqueue(() => a()));
        _connection.StateChanged += (_, _) => OnConnectionStateChanged();
        _ = StartConnectionAsync();

        // Resume And Network: reconnect right away instead of waiting out the long poll's retry
        _networkDebounce                         = dispatcher.CreateTimer();
        _networkDebounce.Interval                = TimeSpan.FromSeconds(3);
        _networkDebounce.IsRepeating             = false;
        _networkDebounce.Tick                   += (_, _) => RunOnUi("network", c => c.NotifyNetworkAvailableAsync());
        PowerManager.SystemSuspendStatusChanged += OnSystemSuspendStatusChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;

        // Tray Icon
        ShowMainWindowRequested += (_, source) => OnShowMainWindowRequested(source);
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
            // No "Not paired" main window left behind the pairing window after an unpair or sign-out
            _mainWindow?.Close();
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

    // =========================================================================
    // MAIN AND SETTINGS WINDOWS
    // =========================================================================

    // Tray left-click, the menu's Open, or a second launch. Unpaired, the way in is the pairing window
    private void OnShowMainWindowRequested(string source)
    {
        if (_quitting)
        {
            return;
        }

        AppLog.OpenRequested(_logger, source);
        if (!_connectionStarted)
        {
            return;
        }

        if (NeedsPairing())
        {
            ShowPairingWindow();
            return;
        }

        ShowMainWindow();
    }

    // Made when needed and destroyed on close, then the memory it used goes back to Windows
    private void ShowMainWindow()
    {
        if (_quitting || _connection is null)
        {
            return;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Activate();
            return;
        }

        _mainWindow = new MainWindow(_connection);
        _mainWindow.ApplyTheme(_settings?.Theme ?? AppTheme.System);
        _mainWindow.Closed += (_, _) =>
        {
            _mainWindow = null;
            ScheduleTrim();
        };
        _mainWindow.Activate();
    }

    // Made when needed and destroyed on close, then the memory it used goes back to Windows
    private void ShowSettingsWindow()
    {
        if (_quitting || _connection is null || _settings is null || _startup is null || _paths is null)
        {
            return;
        }

        AppLog.SettingsRequested(_logger);
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var viewModel   = new SettingsViewModel(_settings, _startup, _connection, OpenFolder, _paths.LogsFolder, _logger);
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.ApplyTheme(_settings.Theme);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ScheduleTrim();
        };
        _settingsWindow.Activate();
    }

    // Theme: every open window and the tray menu follow the setting live
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.Theme) || _settings is null)
        {
            return;
        }

        var theme = _settings.Theme;
        _trayMenu?.ApplyTheme(theme);
        _pairingWindow?.ApplyTheme(theme);
        _mainWindow?.ApplyTheme(theme);
        _settingsWindow?.ApplyTheme(theme);
    }

    private void ScheduleTrim()
    {
        if (!_quitting)
        {
            _memoryTrim?.Schedule();
        }
    }

    private void CloseWindows()
    {
        _pairingWindow?.Close();
        _mainWindow?.Close();
        _settingsWindow?.Close();
    }

    // Explorer makes no folder, so a fresh install's empty logs folder is made first
    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        using var explorer = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private static IStartupService SelectStartupService(AppPaths paths)
    {
#if EnableTestHooks
        if (Program.IsTestRun)
        {
            return new MarkerFileStartupService(paths.DataFolder);
        }
#endif
        return AppIdentity.IsPackaged ? new StartupTaskService() : new RegistryStartupService(Environment.ProcessPath!);
    }

    // =========================================================================
    // RESUME AND NETWORK
    // =========================================================================

    // PowerManager (WinAppSDK) rather than SystemEvents: it's a CsWinRT projection the app already ships, so it's
    // AOT-clean and needs no extra package or hidden window thread. Raised on a background thread.
    // AutoResume only: a normal wake raises AutoResume then ManualResume, and a second "reconnect now" would tear down
    // the first. On Modern Standby laptops suspend/resume often isn't raised at all; network-back and the long poll's
    // own retry cover those
    private void OnSystemSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus != SystemSuspendStatus.AutoResume)
        {
            return;
        }

        RunOnUi("resume", c => c.NotifyResumedAsync());
    }

    // Raised on a background thread for any change. Any change with internet (including a Wi-Fi to Ethernet switch
    // with no gap) restarts a 3 s debounce, so a burst of changes gives one reconnect
    private void OnNetworkStatusChanged(object? sender)
    {
        if (!HasInternet())
        {
            return;
        }

        _dispatcher?.TryEnqueue(() =>
        {
            _networkDebounce?.Stop();
            _networkDebounce?.Start();
        });
    }

    private static bool HasInternet() =>
        NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;

    // Marshals a controller call to the UI thread and logs its failure, so no task goes unobserved
    private void RunOnUi(string reason, Func<ConnectionController, Task> call) =>
        _dispatcher?.TryEnqueue(async () =>
        {
            if (_quitting || _connection is null)
            {
                return;
            }

            AppLog.Reconnecting(_logger, reason);
            try
            {
                await call(_connection);
            }
#pragma warning disable CA1031 // A failed reconnect is retried by the client; it must not take the tray down
            catch (Exception ex)
#pragma warning restore CA1031
            {
                AppLog.ReconnectFailed(_logger, reason, ex);
            }
        });

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
            _trayMenu.SettingsRequested += (_, _) => ShowSettingsWindow();
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
        PowerManager.SystemSuspendStatusChanged -= OnSystemSuspendStatusChanged;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _networkDebounce?.Stop();
        _trayIcon?.Dispose();
        _trayMenu?.Close();
        CloseWindows();
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
        CloseWindows();

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

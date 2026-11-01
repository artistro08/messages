using System.ComponentModel;
using System.Diagnostics;
using GMessages.App.Startup;
using GMessages.App.Views;
using GMessages.Presentation;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Logging;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;
using GMessages.Presentation.Pairing;
using GMessages.Presentation.Settings;
using GMessages.Protocol.Http;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
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

    // Dev note: a plain Debug run (no --test-data-dir) often shows no tray icon. Windows binds the classic icon GUID
    // to one exe path, so a build in another folder is refused. Clear it by deleting the IconStreams and
    // PastIconsStream values under HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify
    // and restarting Explorer, or run with --test-data-dir (that uses the test GUID).
    //
    // Fixed identities, so Windows keeps the icon's taskbar placement across updates. A GUID belongs
    // to one exe location, so the packaged and unpackaged installs each get their own, and UI test
    // runs get a third so they never disturb a real install's icon.
    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-notifyicondataw#troubleshooting
    private static readonly Guid PackagedTrayGuid = new("2C8CFFE0-C9DB-4B2E-AD36-BD27A77E8033");
    private static readonly Guid ClassicTrayGuid  = new("FA25E877-3ACB-4CF3-A958-024545B7CE24");
#if EnableTestHooks
    private static readonly Guid TestTrayGuid     = new("41E044BE-2303-4240-B4ED-C6A29CD3DE24");
#endif

    /// <summary>
    /// Gets the app shell's logger, for controls the app doesn't construct (a message bubble's Copy).
    /// </summary>
    internal static ILogger ShellLog { get; private set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private RollingFileLoggerProvider? _logProvider;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private AppSettings? _settings;
    private TrayIcon? _trayIcon;
    private TrayMenuWindow? _trayMenu;
    private MemoryTrim? _memoryTrim;
    private ConnectionController? _connection;
    private ChatStore? _store;
    private MediaStore? _media;
    private NotificationService? _notifications;
    private UnreadBadge? _badge;
    private AppNotificationActivatedEventArgs? _launchNotification;
    private DispatcherQueueTimer? _launchNotificationTimeout;
    private MainWindow? _mainWindow;

    // The Main Window's Link Preview Fetcher: Made With The Window And Disposed On Close, So Its Cache Goes With It
    private LinkPreviewer? _linkPreviewer;
    private IStartupService? _startup;
    private AppPaths? _paths;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _networkDebounce;
    private bool _connectionStarted;
    private string? _heldShowSource;
    private string? _pendingConversationId;

    // The Chat That Was Open When The Window Closed, Opened Again (From The Message Cache) In The Next One
    private string? _lastConversationId;
    private RegisteredWaitHandle? _quitWait;
    private RegisteredWaitHandle? _showWait;
    private bool _quitting;

    // Quitting: The Most Closing The Connection (It Writes A Pending Session Save, Which Matters) And Writing The Queued
    // Message Saves (A Cache) May Take, And How Long After Exit The Process Is Ended For Sure. A Quit Ends Within About
    // Eight Seconds Whatever Hangs, Usually Well Under One
    private static readonly TimeSpan ConnectionLimit = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SavesLimit      = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitGrace       = TimeSpan.FromSeconds(2);
    private bool _launched;

    /// <summary>
    /// Creates the app. Closing a window must never end a tray app, so it only shuts down on request.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Closing the last window must not end a tray app
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Last Resort For UI Event Handlers
        UnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// Asks for the main window: tray left-click, the menu's Open, or a second launch. Carries what asked.
    /// </summary>
    internal event EventHandler<string>? ShowMainWindowRequested;

    /// <summary>
    /// Gets the notifications, so an opened chat can clear its toasts. Null before launch.
    /// </summary>
    internal NotificationService? Notifications => _notifications;

    /// <summary>
    /// Gets the media store, so a bubble can show its photo or open its file. Null before launch.
    /// </summary>
    internal MediaStore? Media => _media;

    /// <summary>
    /// Gets the main window while it is open, so a bubble can show its photo in the viewer or confirm a download. Null
    /// when the window is closed (the app lives on in the tray).
    /// </summary>
    internal MainWindow? MainWindow => _mainWindow;

    /// <summary>
    /// Gets the Windows device ID of the microphone chosen in Settings, or null for Windows' default (and before launch).
    /// </summary>
    internal string? MicrophoneId => _settings?.MicrophoneId;

    /// <summary>
    /// Sets up logging, settings and the tray icon, then starts watching for quit and show requests.
    /// </summary>
    /// <param name="args">Launch details (unused; the command line is read by <see cref="Program"/>).</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Paths
        var paths = AppPaths.Resolve(AppIdentity.IsPackaged ? ApplicationData.Current.LocalFolder.Path : null, TestHooks.DataDir);
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
        ShellLog        = _logger;
        AppLog.Started(_logger, AppInfo.DisplayName, AppInfo.Version, AppIdentity.IsPackaged, Program.IsTestRun);
        if (TestHooks.Endpoints is { } fake)
        {
            var baseUrl = fake.MessagesBase.ToString();
            AppLog.TestEndpoint(_logger, baseUrl, paths.DataFolder);
        }

        // Sign-In Profiles A Quit Or Crash Mid-Sign-In Left Behind (They Can Hold A Live Google Session)
        PairingPage.DeleteLeftoverProfiles(_logger);

        // Memory Trim
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher    = dispatcher;
        _memoryTrim    = new MemoryTrim(dispatcher, () => _trayMenu?.AppWindow.IsVisible != true && _mainWindow is null);

        // Connection: every controller event comes back on the UI thread
        // Debug test hooks can point it at a local fake server; Release is always production
        var endpoints = TestHooks.Endpoints ?? GmEndpoints.Production;
        _connection   = new ConnectionController(paths, endpoints, TimeProvider.System, new SingleProviderLoggerFactory(_logProvider), a => dispatcher.TryEnqueue(() => a()));
        _connection.StateChanged += (_, _) => OnConnectionStateChanged();

        // Media: decrypted files for other apps go to the package's temp folder (Temp\Media under the data folder
        // when unpackaged); whatever the last run left there goes now
        var connection                    = _connection;
        Func<IMessagingClient?> messaging = () => connection.Client is { } client ? new GmMessagingClient(client) : null;
        string media_folder               = AppIdentity.IsPackaged
            ? Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "Media")
            : Path.Combine(paths.DataFolder, "Temp", "Media");
        _media                            = new MediaStore(messaging, media_folder, _logProvider.CreateLogger("GMessages.Media"));
        _media.DownloadTimeout            = static () => TestHooks.ShortWait("download-timeout") ?? MediaStore.DefaultDownloadTimeout;
        _media.Clear();

        // Chat Store: outlives the main window; no client until paired and connected. The one message database for this
        // data folder and the media cache sit under it
        _store = new ChatStore(connection, messaging, _logProvider.CreateLogger("GMessages.Chat"), new MessageDatabase(paths.MessagesDatabase, _logProvider.CreateLogger("GMessages.Database")), media: _media);

        // Notifications: a toast for each new message, also with the main window closed
        var store      = _store;
        _notifications = new NotificationService(store, _settings, new AvatarCache(store, paths.AvatarsFolder), () => _mainWindow?.ViewModel, dispatcher, _logProvider.CreateLogger("GMessages.Notifications"));
        _notifications.OpenConversationRequested += (_, id) => OpenConversation(id);
        store.MessageArrived                     += (_, e) => _ = _notifications.ShowAsync(e);
        _notifications.Register();

        // Taskbar Badge: the unread chat count, recounted on every conversation change
        ILogger badge_log          = _logProvider.CreateLogger("GMessages.Badge");
        var badge                  = new UnreadBadge(store.CachedConversations, count => TaskbarBadge.Show(count, badge_log), TimeProvider.System);
        _badge                     = badge;
        store.ConversationChanged += (_, _) => badge.Update();

        // Started By A Toast Click: Read After Register, As The Windows App SDK Requires. A Reply Waits For Connected; An
        // Open Shows The Main Window At Once (Held Until Start-Up Ends)
        AppActivationArguments activation           = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppNotificationActivatedEventArgs? launched = activation is { Kind: ExtendedActivationKind.AppNotification, Data: AppNotificationActivatedEventArgs toast } ? toast : null;
        if (launched is not null && NotificationArgs.Parse(launched.Argument) is { Action: "reply" })
        {
            _launchNotification                    = launched;
            _launchNotificationTimeout             = dispatcher.CreateTimer();
            _launchNotificationTimeout.Interval    = TimeSpan.FromSeconds(30);
            _launchNotificationTimeout.IsRepeating = false;
            _launchNotificationTimeout.Tick       += (_, _) => HandleLaunchNotification(timedOut: true);
            _launchNotificationTimeout.Start();
        }
        else if (launched is not null)
        {
            _ = _notifications.HandleAsync(launched);
        }

        // Started By The User: The Main Window Shows (Held Until Start-Up Ends). Windows' Startup Task At Sign-In Keeps
        // The App In The Tray
        if (LaunchWindow.ShowsAtLaunch(activation.Kind == ExtendedActivationKind.StartupTask, launched is not null))
        {
            OnShowMainWindowRequested("launch");
        }

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
        _trayIcon.ThemeChanged         += (_, _) => OnSystemThemeChanged();
        var trayAdded = _trayIcon.Show();
        AppLog.TrayAdded(_logger, trayAdded);

        // Quit and Show Requests
        if (Program.QuitRequest is not null)
        {
            _quitWait = WatchEvent(dispatcher, Program.QuitRequest, Quit, once: true);
        }

        if (Program.ShowRequest is not null)
        {
            _showWait = WatchEvent(dispatcher, Program.ShowRequest, () => ShowMainWindowRequested?.Invoke(this, "second launch"), once: false);
        }

        // Startup itself leaves a lot behind that the idle tray app never needs
        _memoryTrim.Schedule();
        _launched = true;
    }

    // An error that escaped a UI event handler (an async void click) is logged by type and HRESULT only, never its
    // message (it can hold content). Once launched it is marked handled, so one failed click can't take the tray, and
    // with it every later notification, down. Before then it still ends the app, so a broken start-up is never hidden.
    // The stack's frames are logged too (code names only, see StackFrames), so the source can be found
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = _launched && e.Exception is not OutOfMemoryException;
        AppLog.UnhandledError(_logger, e.Exception?.GetType().Name ?? "Unknown", e.Exception?.HResult ?? 0, e.Handled, StackFrames(e.Exception));
    }

    // An Error's Stack As "Type.Method(Parameter Types)" Frames Joined By " < ", Innermost First: The Method Names Only,
    // Never The Message, Source Paths Or Line Numbers. "None" When The Error Came From WinUI Without A Managed Stack
    private static string StackFrames(Exception? error)
    {
        if (string.IsNullOrWhiteSpace(error?.StackTrace))
        {
            return "None";
        }

        IEnumerable<string> frames = error.StackTrace
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith("at ", StringComparison.Ordinal) ? line[3..] : line)
            .Select(line => line.IndexOf(" in ", StringComparison.Ordinal) is int at and >= 0 ? line[..at] : line);
        return string.Join(" < ", frames);
    }

    // =========================================================================
    // CONNECTION AND PAIRING
    // =========================================================================

    // Loads the saved session; no session (or a dead one) opens the main window on pairing. Start-up always ends, also
    // when StartAsync throws (then it ends in pairing), so the tray never goes dead. A click held meanwhile runs last
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
        }

        // Paired: The Saved Chats Fill The List Before The Phone Answers (Read Off The UI Thread). A Locked File Holds
        // Start-Up 2 s At Most; The Phone's Page Fills The List Then
        if (!NeedsPairing() && _store is { } store)
        {
            await Task.WhenAny(store.LoadSavedAsync(), Task.Delay(TimeSpan.FromSeconds(2)));
            _badge?.Update();
        }

        // NotPaired is the starting state, so StartAsync raises nothing for it
        _connectionStarted = true;
        OnConnectionStateChanged();

        // The Last Open Request Made During Start-Up
        if (_heldShowSource is { } source)
        {
            _heldShowSource = null;
            OnShowMainWindowRequested(source);
        }
    }

    private bool NeedsPairing() => _connection?.State is ConnectionState.NotPaired or ConnectionState.SignedOut;

    // NotPaired or SignedOut (also a later sign-out) opens the main window on pairing; once paired an open window
    // switches to the chats in place
    private void OnConnectionStateChanged()
    {
        if (_quitting || _connection is null || !_connectionStarted)
        {
            return;
        }

        AppLog.ConnectionStateChanged(_logger, _connection.State);
        HandleLaunchNotification(timedOut: false);
        if (NeedsPairing())
        {
            // The Chats Go (Showing Pairing Drops Them First), And Nothing Of The Old Phone (Names, Numbers, Owner, SIMs)
            // Is Kept For Whichever Phone Pairs Next. The Old Session's Toasts And Waiting Snoozes Go Too (RemoveAllAsync
            // Logs Its Own Failure, So Nothing Goes Unobserved)
            _pendingConversationId = null;
            _lastConversationId    = null;
            ShowMainWindow();
            _store?.Reset();
            _badge?.Clear();
            _ = _notifications?.RemoveAllAsync();
            return;
        }

        ShowBody();
        SelectPendingConversation();
    }

    // =========================================================================
    // NOTIFICATIONS
    // =========================================================================

    // The Toast Reply That Started The App: Sent Once Connected. After 30 s Without A Connection It Gets The Failure Toast
    private void HandleLaunchNotification(bool timedOut)
    {
        if (_launchNotification is not { } args || _notifications is null || (!timedOut && _connection?.State != ConnectionState.Connected))
        {
            return;
        }

        _launchNotification = null;
        _launchNotificationTimeout?.Stop();

        if (timedOut && NotificationArgs.Parse(args.Argument) is { } reply)
        {
            _notifications.ShowReplyFailed(reply.ConversationId);
            return;
        }

        _ = _notifications.HandleAsync(args);
    }

    // A Toast Body Click: The Main Window Shows At Once, On That Conversation As Soon As It Can Be Shown
    private void OpenConversation(string conversationId)
    {
        _pendingConversationId = conversationId;
        OnShowMainWindowRequested("notification");
    }

    // Selected Once Connected, Or Right Away When The Conversation Is Already Cached
    private void SelectPendingConversation()
    {
        if (_pendingConversationId is not { } id || _mainWindow is not { ViewModel: not null } window
            || (_connection?.State != ConnectionState.Connected && _store?.Find(id) is null))
        {
            return;
        }

        _pendingConversationId = null;
        window.SelectConversation(id);
    }

    // =========================================================================
    // MAIN WINDOW
    // =========================================================================

    // Tray left-click, the menu's Open, or a second launch. Unpaired, the window opens on pairing
    private void OnShowMainWindowRequested(string source)
    {
        if (_quitting)
        {
            return;
        }

        // Held Until Start-Up Ends (Only The Last One), Then Run
        AppLog.OpenRequested(_logger, source);
        if (!_connectionStarted)
        {
            _heldShowSource = source;
            return;
        }

        ShowMainWindow();
        BringToFront(_mainWindow);
        SelectPendingConversation();
    }

    // Made when needed and destroyed on close, then the memory it used goes back to Windows. Shows pairing or the
    // chats, whichever the connection calls for
    private void ShowMainWindow()
    {
        if (_quitting || _connection is null || _store is null || _settings is null || _startup is null || _paths is null)
        {
            return;
        }

        if (_mainWindow is null)
        {
            var settings   = new SettingsViewModel(_settings, _startup, _connection, OpenFolder, _paths.LogsFolder, _logger);
            _mainWindow    = new MainWindow(settings, new AvatarCache(_store, _paths.AvatarsFolder));
            _linkPreviewer = new LinkPreviewer(LinkPreviewer.CreateHandler(TestHooks.AllowLoopbackPreviews), TestHooks.AllowLoopbackPreviews, _logger);
            _mainWindow.ApplyTheme(_settings.Theme);
            _mainWindow.SettingsRequested += (_, _) => ShowSettings();
            _mainWindow.Closed            += (_, _) =>
            {
                // A Toast's Conversation Not Yet Shown Goes With The Window, So A Later Open Doesn't Jump To It. The Open
                // Chat Is Remembered For The Next Window
                _lastConversationId    = _mainWindow?.LastConversationId;
                _mainWindow            = null;
                _pendingConversationId = null;
                _linkPreviewer?.Dispose();
                _linkPreviewer = null;
                ScheduleTrim();
            };
        }

        ShowBody();
        _mainWindow.Activate();
    }

    // Asked For By The Person (Tray, Second Launch, A Toast Click): The Window Comes To The Front, Restored If Minimized.
    // Activate Alone Can Leave It Behind Other Windows When A Toast Started It: Windows App SDK Apps Must Bring Their Own
    // Window Forward (Microsoft's Toast Migration Guide: ShowWindow(SW_RESTORE), Then SetForegroundWindow). When Windows
    // Still Refuses (The Foreground Lock), The Input Of The Foreground Window's Thread Is Joined For A Moment, Which Lets
    // The Call Through, Then Let Go Again
    private static void BringToFront(Window? window)
    {
        if (window is null)
        {
            return;
        }

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        if (NativeMethods.SetForegroundWindow(hwnd))
        {
            return;
        }

        uint foreground_thread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), IntPtr.Zero);
        uint my_thread         = NativeMethods.GetCurrentThreadId();
        if (foreground_thread == 0 || foreground_thread == my_thread || !NativeMethods.AttachThreadInput(my_thread, foreground_thread, true))
        {
            return;
        }

        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.AttachThreadInput(my_thread, foreground_thread, false);
    }

    // Pairing While Unpaired Or Signed Out, Else The Chats. An Open Window Switches In Place Only When That Changes, So
    // A Sign-In In Progress Or The Open Chat Survives Other State Changes
    private void ShowBody()
    {
        if (_mainWindow is not { } window || _connection is not { } connection || _store is not { } store)
        {
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (NeedsPairing() && !window.IsPairing)
        {
            var pairing = new PairingViewModel(connection, a => dispatcher.TryEnqueue(() => a()));
            window.ShowPairing(new PairingPage(pairing, TestHooks.Endpoints ?? GmEndpoints.Production, _logger));
        }
        else if (!NeedsPairing() && window.ViewModel is null)
        {
            // The List And The Last Open Chat Show At Once From What The Store Already Has, Then Refresh Quietly. Only An
            // Inbox Chat Comes Back: One From Spam & Blocked, Or Moved Out Of The Inbox Meanwhile, Stays Closed
            window.ShowChats(new MainViewModel(connection, store, TimeProvider.System, a => dispatcher.TryEnqueue(() => a()), _linkPreviewer));
            if (_lastConversationId is { } last && store.Find(last) is { IsMainView: true })
            {
                window.SelectConversation(last);
            }
        }
    }

    // The Tray Menu's Settings And The Window's Own (Profile Menu, Ctrl + ,): The Main Window Opens On The Settings Page
    private void ShowSettings()
    {
        if (_quitting)
        {
            return;
        }

        AppLog.SettingsRequested(_logger);
        ShowMainWindow();
        _mainWindow?.ShowSettings();
    }

    // Theme: every open window and the tray menu follow the setting live
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.Theme) || _settings is null)
        {
            return;
        }

        var theme = _settings.Theme;
        RefreshTrayMenu(theme);
        _mainWindow?.ApplyTheme(theme);
    }

    // Tray Menu: its popup keeps the backdrop of the theme it first opened with (a light theme set later gives dark
    // text on a dark background), so a hidden menu is dropped and made fresh, with the new theme, on the next right-click
    private void RefreshTrayMenu(AppTheme theme)
    {
        if (_trayMenu is { } menu && !menu.AppWindow.IsVisible)
        {
            _trayMenu = null;
            menu.Close();
            return;
        }

        _trayMenu?.ApplyTheme(theme);
    }

    // Windows Switched Light And Dark: With "System" The Tray Menu Follows It, The Same Way As A Theme Setting Change
    private void OnSystemThemeChanged()
    {
        if (_quitting || _settings is not { Theme: AppTheme.System } settings)
        {
            return;
        }

        RefreshTrayMenu(settings.Theme);
    }

    private void ScheduleTrim()
    {
        if (!_quitting)
        {
            _memoryTrim?.Schedule();
        }
    }

    private void CloseWindows() => _mainWindow?.Close();

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
            _trayMenu.SettingsRequested += (_, _) => ShowSettings();
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
        PowerManager.SystemSuspendStatusChanged -= OnSystemSuspendStatusChanged;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _networkDebounce?.Stop();
        _launchNotificationTimeout?.Stop();
        _notifications?.Dispose();
        _badge?.Dispose();
        _trayIcon?.Dispose();
        _trayMenu?.Close();
        CloseWindows();
        _store?.Dispose();
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
        long started = Stopwatch.GetTimestamp();
        CloseWindows();
        long windows_ms    = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long connection_ms = 0;
        long saves_ms      = 0;
        bool saves_done    = false;

        // Let the client close its long poll and flush a save (while the log is still open), but never hang the quit on it
        try
        {
            // Test Runs Take Their Toasts With Them, So None Leak Into The Next Test; Real Runs Keep Theirs
            if (Program.IsTestRun && _notifications is not null)
            {
                await _notifications.RemoveAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            long step = Stopwatch.GetTimestamp();
            if (_connection is not null)
            {
                await _connection.DisposeAsync().AsTask().WaitAsync(ConnectionLimit);
            }

            connection_ms = (long)Stopwatch.GetElapsedTime(step).TotalMilliseconds;

            // The Message Saves Still Queued (After A Catch-Up There Can Be Hundreds) Are Written Before The Exit, Up To
            // A Limit: The Database Is A Cache, So A Save Cut Off Is Fetched Again From The Phone
            step = Stopwatch.GetTimestamp();
            if (_store is not null)
            {
                await _store.FlushSavesAsync().WaitAsync(SavesLimit);
                saves_done = true;
            }

            saves_ms = (long)Stopwatch.GetElapsedTime(step).TotalMilliseconds;
        }
#pragma warning disable CA1031 // Quitting regardless
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing left to do but exit
        }
        finally
        {
            long total_ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            AppLog.QuitTimed(_logger, windows_ms, connection_ms, saves_ms, saves_done, total_ms);

            // No Decrypted File Outlives The App
            _media?.Clear();
            Dispose();
            Exit();
            ExitSoon();
        }
    }

    // Exit Ends The Message Loop, Then The Process Ends Once Main Returns; Anything Native Still Winding Down (A Media
    // Job, A Notification Or Network Teardown) Could Hold The Process Up For About A Minute (0.6.4). Nothing Is Left To
    // Save By Now, So The Process Ends For Sure Shortly After
    // ponytail: a hard exit after a fixed wait; find the native holder in a dump if the wait ever fires in the log-free gap
    private static void ExitSoon() =>
        _ = Task.Delay(ExitGrace).ContinueWith(_ => Environment.Exit(0), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

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

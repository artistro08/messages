using System.ComponentModel;
using System.Diagnostics;
using GMessages.App.Startup;
using GMessages.App.Views;
using GMessages.App.Windowing;
using GMessages.Presentation;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Logging;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;
using GMessages.Presentation.Pairing;
using GMessages.Presentation.Settings;
using GMessages.Protocol;
using GMessages.Protocol.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.System.Power;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;

namespace GMessages.App;

/// <summary>
/// Composes the tray app. No window opens at launch: the tray icon and its menu open the app.
/// </summary>
/// <remarks>
/// The app outlives its main window and keeps its toasts from the tray. The tray wiring, the quit and show event watch
/// and the memory trim come from the <c>App.xaml.cs</c> of Magic Buds.
/// </remarks>
public sealed partial class App : Application, IDisposable
{
    // =========================================================================
    // TRAY ICON GUIDS
    // =========================================================================

    // Each GUID is a fixed identity, so Windows keeps the place of the icon on the taskbar across updates. Windows
    // binds a GUID to a single exe location. Thus the packaged and the unpackaged installs each have a GUID. UI test
    // runs have a third GUID, so they never change the icon of a real install.
    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-notifyicondataw#troubleshooting
    //
    // A plain Debug run without --test-data-dir often shows no tray icon. Windows refuses the classic GUID for a
    // build in another folder. To clear the binding, delete the IconStreams and PastIconsStream values under the key
    // HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify. Then restart Explorer.
    // Or run with --test-data-dir, which uses the test GUID.
    private static readonly Guid PackagedTrayGuid = new("2C8CFFE0-C9DB-4B2E-AD36-BD27A77E8033");
    private static readonly Guid ClassicTrayGuid  = new("FA25E877-3ACB-4CF3-A958-024545B7CE24");
#if EnableTestHooks
    private static readonly Guid TestTrayGuid     = new("41E044BE-2303-4240-B4ED-C6A29CD3DE24");
#endif

    /// <summary>
    /// Gets the logger of the app shell. Controls that the app does not construct use it, such as the Copy of a bubble.
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

    // The app hides a closed window for a quick reopen. Later the app destroys the window, so its memory returns to
    // Windows.
    private static readonly TimeSpan KeepClosedWindow = TimeSpan.FromMinutes(5);
    private DispatcherQueueTimer? _dropWindow;

    // The app makes the link preview fetcher with the main window. The close disposes it, which also clears its cache.
    private LinkPreviewer? _linkPreviewer;
    private IStartupService? _startup;
    private AppPaths? _paths;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _networkDebounce;
    private bool _connectionStarted;
    private string? _heldShowSource;
    private string? _pendingConversationId;

    // The chat that was open when the window closed. The next window opens it again from the message cache.
    private string? _lastConversationId;
    private RegisteredWaitHandle? _quitWait;
    private RegisteredWaitHandle? _showWait;
    private bool _quitting;

    // The limits of a quit: the connection close, the queued message saves, and the wait before the hard exit. The
    // connection close writes a pending session save, which matters. The message saves are a cache. A quit ends in
    // about 8 seconds whatever hangs, and usually in less than 1 second.
    private static readonly TimeSpan ConnectionLimit = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SavesLimit      = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitGrace       = TimeSpan.FromSeconds(2);
    private bool _launched;

    /// <summary>
    /// Creates the app and starts XAML on the UI thread. A close of a window never ends the tray app. The message loop
    /// runs until <see cref="Quit"/> ends it.
    /// </summary>
    public App()
    {
        // A desktop process has no UWP launch, so XAML starts here, before App.xaml loads.
        XamlManager = WindowsXamlManager.InitializeForCurrentThread();
        InitializeComponent();

        // These handlers get the errors that escape UI event handlers and awaits that resume on the UI thread.
        UnhandledException          += OnUnhandledException;
        XamlHost.UnhandledException  = OnEscapedException;
    }

    /// <summary>
    /// Gets the XAML core of the UI thread. The app holds it, so XAML runs between windows while the app lives in the
    /// tray.
    /// </summary>
    public WindowsXamlManager XamlManager { get; }

    /// <summary>
    /// Gets the notifications, so an opened chat can clear its toasts. It is null before launch.
    /// </summary>
    internal NotificationService? Notifications => _notifications;

    /// <summary>
    /// Gets the media store, so a bubble can show its photo or open its file. It is null before launch.
    /// </summary>
    internal MediaStore? Media => _media;

    /// <summary>
    /// Gets the open main window, so a bubble can show its photo in the viewer or confirm a download. It is null while
    /// the window is closed and the app lives in the tray.
    /// </summary>
    internal MainWindow? MainWindow => _mainWindow;

    /// <summary>
    /// Gets the Windows device ID of the microphone that Settings chose. It is null for the Windows default and before
    /// launch.
    /// </summary>
    internal string? MicrophoneId => _settings?.MicrophoneId;

    /// <summary>
    /// Starts the logging, the settings and the tray icon, then watches for quit and show requests.
    /// <see cref="Program"/> reads the command line, then calls this method once before the message loop starts.
    /// </summary>
    internal void Launch()
    {
        var paths = AppPaths.Resolve(AppIdentity.IsPackaged ? ApplicationData.Current.LocalFolder.Path : null, TestHooks.DataDir);
        _paths    = paths;

        // A packaged app keeps its settings in the package store. An unpackaged app keeps them in a file.
        ISettingsStore settingsStore = AppIdentity.IsPackaged
            ? new LocalSettingsStore()
            : new JsonFileSettingsStore(paths.SettingsFile);
        _settings                  = new AppSettings(settingsStore);
        _settings.PropertyChanged += OnSettingChanged;

        _startup = SelectStartupService(paths);

        _logProvider = new RollingFileLoggerProvider(paths.LogsFolder, _settings.LogLevel, TimeProvider.System);
        _logger      = _logProvider.CreateLogger("GMessages.App");
        ShellLog     = _logger;
        AppLog.Started(_logger, AppInfo.DisplayName, AppInfo.Version, AppIdentity.IsPackaged, Program.IsTestRun);
        if (TestHooks.Endpoints is { } fake)
        {
            var baseUrl = fake.MessagesBase.ToString();
            AppLog.TestEndpoint(_logger, baseUrl, paths.DataFolder);
        }

        // A quit or a crash during a sign-in can leave sign-in profiles. A profile can hold a live Google session.
        PairingPage.DeleteLeftoverProfiles(_logger);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher    = dispatcher;
        _memoryTrim    = new MemoryTrim(dispatcher, () => _trayMenu?.IsVisible != true && _mainWindow is null);

        // The controller raises every event on the UI thread. Debug test hooks can point it at a local fake server.
        // Release always uses the production servers.
        var endpoints = TestHooks.Endpoints ?? GmEndpoints.Production;
        _connection   = new ConnectionController(paths, endpoints, TimeProvider.System, new SingleProviderLoggerFactory(_logProvider), a => dispatcher.TryEnqueue(() => a()));
        _connection.StateChanged += (_, _) => OnConnectionStateChanged();

        // Decrypted files for other apps go to the temp folder of the package. Unpackaged, they go to Temp\Media under
        // the data folder. The app deletes the files of the last run now.
        var connection                    = _connection;
        Func<IMessagingClient?> messaging = () => connection.Client;
        string mediaFolder                = AppIdentity.IsPackaged
            ? Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "Media")
            : Path.Combine(paths.DataFolder, "Temp", "Media");
        _media                            = new MediaStore(messaging, mediaFolder, _logProvider.CreateLogger("GMessages.Media"));
        _media.DownloadTimeout            = static () => TestHooks.ShortWait("download-timeout") ?? MediaStore.DefaultDownloadTimeout;
        _media.Clear();

        // The chat store outlives the main window. It has no client until the phone is paired and connected. The store
        // holds the single message database of this data folder, and the media cache.
        _store = new ChatStore(connection, messaging, _logProvider.CreateLogger("GMessages.Chat"), new MessageDatabase(paths.MessagesDatabase, _logProvider.CreateLogger("GMessages.Database")), media: _media);

        // The app shows a toast for each new message, also while the main window is closed.
        var store      = _store;
        _notifications = new NotificationService(store, _settings, new AvatarCache(store, paths.AvatarsFolder), () => _mainWindow?.ViewModel, dispatcher, _logProvider.CreateLogger("GMessages.Notifications"));
        _notifications.OpenConversationRequested += (_, id) => OpenConversation(id);
        store.MessageArrived                     += (_, e) =>
        {
            _ = _notifications.ShowAsync(e);
            CopyCode(e);
            ScheduleBackgroundTrim();
        };
        _notifications.Register();

        // The taskbar badge counts the unread main-view chats again on each conversation change.
        ILogger badgeLog           = _logProvider.CreateLogger("GMessages.Badge");
        var badge                  = new UnreadBadge(store.CachedConversations, count => TaskbarBadge.Show(count, badgeLog), TimeProvider.System);
        _badge                     = badge;
        store.ConversationChanged += (_, _) =>
        {
            badge.Update();
            ScheduleBackgroundTrim();
        };

        // The app reads a launch by a toast click after Register, as the Windows App SDK requires. A reply waits for
        // the Connected state, for 30 seconds at most. An open shows the main window as soon as the start-up ends.
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

        // A launch by the user shows the main window as soon as the start-up ends. A launch by the Windows startup task
        // at sign-in keeps the app in the background. When the run in background setting is off, that launch also
        // shows the window.
        if (LaunchWindow.ShowsAtLaunch(activation.Kind == ExtendedActivationKind.StartupTask, launched is not null, _settings.RunInBackground))
        {
            OnShowMainWindowRequested("launch");
        }

        _ = StartConnectionAsync();

        // After a resume or a network change, the app reconnects at once. It does not wait for the long poll to retry.
        _networkDebounce                         = dispatcher.CreateTimer();
        _networkDebounce.Interval                = TimeSpan.FromSeconds(3);
        _networkDebounce.IsRepeating             = false;
        _networkDebounce.Tick                   += (_, _) => RunOnUi("network", c => c.ReconnectAsync());
        PowerManager.SystemSuspendStatusChanged += OnSystemSuspendStatusChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;

        _trayIcon = new TrayIcon(AppInfo.DisplayName, SelectTrayGuid());
        _trayIcon.Invoked              += (_, _) => OnShowMainWindowRequested("tray");
        _trayIcon.ContextMenuRequested += (_, point) => ShowTrayMenu(point.X, point.Y);
        _trayIcon.ThemeChanged         += (_, _) => OnSystemThemeChanged();
        ApplyTrayIcon();

        if (Program.QuitRequest is not null)
        {
            _quitWait = WatchEvent(dispatcher, Program.QuitRequest, Quit, once: true);
        }

        if (Program.ShowRequest is not null)
        {
            _showWait = WatchEvent(dispatcher, Program.ShowRequest, () => OnShowMainWindowRequested("second launch"), once: false);
        }

        // The start-up leaves much memory that the idle tray app never uses.
        _memoryTrim.Schedule();
        _launched = true;
    }

    // The log holds the type, the HRESULT and the stack frames of an error that escapes a UI event handler. An example
    // is an async void click. The log never holds the message, because the message can hold content. The frames hold
    // code names only, as StackFrames shows, and they show the source. After the launch, the handler marks the error
    // as handled. Thus a failed click cannot stop the tray and all later notifications. Before the launch, the error
    // still ends the app, so a broken start-up is never hidden.
    private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = _launched && e.Exception is not OutOfMemoryException;
        AppLog.UnhandledError(_logger, e.Exception?.GetType().Name ?? "Unknown", e.Exception?.HResult ?? 0, e.Handled, ExceptionText.StackFrames(e.Exception));
    }

    // This handler does the same for an exception that escapes an await continuation on the UI thread. An example is
    // an async void click after its first await.
    private bool OnEscapedException(Exception ex)
    {
        bool handled = _launched && ex is not OutOfMemoryException;
        AppLog.UnhandledError(_logger, ex.GetType().Name, ex.HResult, handled, ExceptionText.StackFrames(ex));
        return handled;
    }

    // =========================================================================
    // CONNECTION AND PAIRING
    // =========================================================================

    // Loads the saved session. Without a live session, the main window opens on pairing. The start-up always ends, so
    // the tray always responds. When StartAsync throws, the start-up ends in pairing. An open request held during the
    // start-up runs last.
    private async Task StartConnectionAsync()
    {
        try
        {
            if (_connection is { } connection)
            {
                await connection.StartAsync();
            }
        }
        catch (Exception ex)
        {
            // The start-up must not stop the tray. The log holds the failure.
            AppLog.ConnectionStartFailed(_logger, ex);
        }

        // When paired, the saved chats fill the list before the phone answers. The store reads them off the UI thread.
        // A locked file holds the start-up for 2 s at most. The page from the phone then fills the list.
        if (!NeedsPairing() && _store is { } store)
        {
            await Task.WhenAny(store.LoadSavedAsync(), Task.Delay(TimeSpan.FromSeconds(2)));
            _badge?.Update();
        }

        // NotPaired is the first state, so StartAsync raises no event for it.
        _connectionStarted = true;
        OnConnectionStateChanged();

        if (_heldShowSource is { } source)
        {
            _heldShowSource = null;
            OnShowMainWindowRequested(source);
        }
    }

    private bool NeedsPairing() => _connection?.State is ConnectionState.NotPaired or ConnectionState.SignedOut;

    // NotPaired or SignedOut opens the main window on pairing, also after a later sign-out. After the pairing, an open
    // window switches to the chats in place.
    private void OnConnectionStateChanged()
    {
        if (_quitting || _connection is null || !_connectionStarted)
        {
            return;
        }

        AppLog.ConnectionStateChanged(_logger, _connection.State);
        HandleLaunchNotification(timedOut: false);

        // With nothing on screen, the memory trims after each connect and reconnect, when the catch-up is done.
        if (_connection.State == ConnectionState.Connected && _mainWindow is null)
        {
            _memoryTrim?.Schedule(TimeSpan.FromSeconds(10));
        }

        if (NeedsPairing())
        {
            // The pairing page drops the chats first, then the store resets. The app keeps no names, numbers, owner or
            // SIMs of the old phone for the next phone. The toasts and waiting snoozes of the old session go too.
            // RemoveAllAsync logs its own failure, so no task goes unobserved.
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

    // Sends the toast reply that started the app when the state is Connected. After 30 s without a connection, the
    // reply gets the failure toast.
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
            QuitUnlessInBackground();
            return;
        }

        _ = ReplyFromLaunchAsync(_notifications, args);
    }

    // Sends the toast reply that started the app. Then the app quits when the run in background setting is off. The
    // app started only for the reply and has no window.
    private async Task ReplyFromLaunchAsync(NotificationService notifications, AppNotificationActivatedEventArgs args)
    {
        await notifications.HandleAsync(args);
        QuitUnlessInBackground();
    }

    // Quits when the run in background setting is off and no window is open.
    private void QuitUnlessInBackground()
    {
        if (_settings?.RunInBackground != false || _mainWindow is not null)
        {
            return;
        }

        Quit();
    }

    // A click on a toast body shows the main window at once. The window selects the conversation as soon as it can.
    private void OpenConversation(string conversationId)
    {
        _pendingConversationId = conversationId;
        OnShowMainWindowRequested("notification");
    }

    // Selects the conversation of a toast click in the Connected state. When the store already holds the conversation,
    // the window selects it at once.
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

    // Handles an open request from the launch, the tray icon, the menu, a second launch or a toast click. Without a
    // paired phone, the window opens on pairing.
    private void OnShowMainWindowRequested(string source)
    {
        if (_quitting)
        {
            return;
        }

        // The app holds the last request until the start-up ends, then runs it.
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

    // Makes the main window when needed, and shows pairing or the chats as the connection requires. The app destroys
    // the window after a close, so its memory returns to Windows.
    private void ShowMainWindow()
    {
        if (_quitting || _connection is null || _store is null || _settings is null || _startup is null || _paths is null)
        {
            return;
        }

        if (_mainWindow is null)
        {
            var settings   = new SettingsViewModel(_settings, _startup, _connection, OpenFolder, _paths.LogsFolder, _logger);
            _mainWindow    = new MainWindow(settings, new AvatarCache(_store, _paths.AvatarsFolder), _settings);
            _linkPreviewer = new LinkPreviewer(TestHooks.AllowLoopbackPreviews, _logger);
            _mainWindow.ApplyTheme(_settings.Theme);
            _mainWindow.SettingsRequested += (_, _) => ShowSettings();
            _mainWindow.QuitRequested     += (_, _) => Quit();
            _mainWindow.Closing           += OnMainWindowClosing;
            _mainWindow.Closed            += (_, _) =>
            {
                // The app forgets a toast conversation that the window did not show yet. Thus a later open does not
                // jump to that chat. The app remembers the open chat for the next window.
                _lastConversationId    = _mainWindow?.LastConversationId;
                _mainWindow            = null;
                _pendingConversationId = null;
                _linkPreviewer?.Dispose();
                _linkPreviewer = null;

                // A close quits when the run in background setting is off, or with no tray icon and no paired phone.
                if (_settings is { } saved && LaunchWindow.QuitsOnClose(saved.RunInBackground, saved.ShowTrayIcon, NeedsPairing()))
                {
                    Quit();
                    return;
                }

                if (!_quitting)
                {
                    _ = DropThenTrimAsync();
                }
            };
        }

        _dropWindow?.Stop();
        ShowBody();
        _mainWindow.Activate();
    }

    // When the person closes the window, the app hides it for 5 minutes, so a reopen is instant. Then the app destroys
    // the window. A close that quits the app happens at once.
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_mainWindow is not { } window || _settings is not { } saved || LaunchWindow.QuitsOnClose(saved.RunInBackground, saved.ShowTrayIcon, NeedsPairing()))
        {
            return;
        }

        e.Cancel               = true;
        _pendingConversationId = null;
        window.Hide();

        if (_dropWindow is null)
        {
            _dropWindow             = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _dropWindow.IsRepeating = false;
            _dropWindow.Tick       += (_, _) => _mainWindow?.Close();
        }

        _dropWindow.Interval = TestHooks.ShortWait("keep-window") ?? KeepClosedWindow;
        _dropWindow.Start();
    }

    // Brings the window to the front for a request of the person, and restores the window when it is minimized. When a
    // toast started the app, Activate alone can leave the window behind other windows. A Windows App SDK app must bring
    // its own window to the front. The toast migration guide of Microsoft calls ShowWindow(SW_RESTORE), then
    // SetForegroundWindow. The foreground lock can still refuse the call. Then the app attaches its input to the thread
    // of the foreground window for a moment. The call then succeeds, and the app detaches the input again.
    private static void BringToFront(MainWindow? window)
    {
        if (window is null)
        {
            return;
        }

        IntPtr hwnd = window.Hwnd;
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        if (NativeMethods.SetForegroundWindow(hwnd))
        {
            return;
        }

        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), IntPtr.Zero);
        uint myThread         = NativeMethods.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == myThread || !NativeMethods.AttachThreadInput(myThread, foregroundThread, true))
        {
            return;
        }

        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.AttachThreadInput(myThread, foregroundThread, false);
    }

    // Shows pairing while no phone is paired or after a sign-out, and the chats otherwise. An open window switches in
    // place only when that choice changes. Thus a sign-in in progress or the open chat survives other state changes.
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
            // The list and the last open chat show at once from the store, then refresh quietly. Only an inbox chat
            // opens again. A chat from Spam & Blocked, or a chat that left the inbox, stays closed.
            window.ShowChats(new MainViewModel(connection, store, TimeProvider.System, a => dispatcher.TryEnqueue(() => a()), _linkPreviewer, () => _settings?.ShowReceivedLinkPreviews ?? true));
            if (_lastConversationId is { } last && store.Find(last) is { IsMainView: true })
            {
                window.SelectConversation(last);
            }
        }
    }

    // Opens the main window on the Settings page. The tray menu, the profile menu of the window and Ctrl + , call it.
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

    // When the setting is on, the one-time passcode of a new message goes to the clipboard. Clipboard history and
    // cloud sync do not get it. The messages that show a toast copy their codes, wherever the window is. Muted, spam,
    // archived and blocked chats copy no code.
    private void CopyCode(MessageArrivedEventArgs e)
    {
        if (_settings?.CopyCodes != true
            || !NotificationPolicy.ShouldNotify(e, notificationsEnabled: true, viewedConversationId: null, windowFocused: false, notifyForViewedChat: true)
            || OneTimeCode.Find(e.Message.Text) is not { } code)
        {
            return;
        }

        _dispatcher?.TryEnqueue(() => UiHelpers.ToClipboard(code, keepPrivate: true));
    }

    private void ApplyTrayIcon()
    {
        if (_settings is null || _trayIcon is null)
        {
            return;
        }

        if (!_settings.ShowTrayIcon)
        {
            _trayIcon.Hide();
            return;
        }

        bool trayAdded = _trayIcon.Show();
        AppLog.TrayAdded(_logger, trayAdded);
    }

    // Applies a live change of the tray icon setting or the theme setting.
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ShowTrayIcon))
        {
            ApplyTrayIcon();
            return;
        }

        if (e.PropertyName != nameof(AppSettings.Theme) || _settings is null)
        {
            return;
        }

        var theme = _settings.Theme;
        _trayMenu?.ApplyTheme(theme);
        _mainWindow?.ApplyTheme(theme);
    }

    // When Windows switches between light and dark and the theme setting is System, the tray menu follows Windows. The
    // menu takes the theme again, as for a change of the setting.
    private void OnSystemThemeChanged()
    {
        if (_quitting || _settings is not { Theme: AppTheme.System } settings)
        {
            return;
        }

        _trayMenu?.ApplyTheme(settings.Theme);
    }

    // Drops what only the window needs: the decrypted media in memory and the message pages. The next window reads the
    // pages again from the database. The pages drop after the queue writes its saves, so the trim waits for the drop.
    // DropPagesAsync logs its own failure. The await resumes on the UI thread.
    private async Task DropThenTrimAsync()
    {
        _media?.ClearMemory();
        if (_store is not null)
        {
            await _store.DropPagesAsync();
        }

        ScheduleTrim();
    }

    private void ScheduleTrim()
    {
        if (!_quitting)
        {
            _memoryTrim?.Schedule();
        }
    }

    // While only the tray shows, the memory trims 30 s after the last new message. Thus a burst of messages ends in a
    // single trim.
    private void ScheduleBackgroundTrim()
    {
        if (!_quitting && _mainWindow is null)
        {
            _memoryTrim?.Schedule(TimeSpan.FromSeconds(30));
        }
    }

    // Explorer does not make a missing folder. Thus the app makes the empty logs folder of a new install first.
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

    // The app uses PowerManager of the Windows App SDK, not SystemEvents. PowerManager is a CsWinRT projection that the
    // app already ships. Thus it gives no Native AOT warnings and needs no extra package or hidden window thread.
    // Windows raises the event on a background thread. A normal wake raises AutoResume, then ManualResume. A second
    // reconnect would stop the first, so only AutoResume reconnects. Modern Standby laptops often raise no suspend or
    // resume. The return of the network and the retry of the long poll cover those laptops.
    private void OnSystemSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus != SystemSuspendStatus.AutoResume)
        {
            return;
        }

        RunOnUi("resume", c => c.ReconnectAsync());
    }

    // Windows raises the event on a background thread for each change. Each change with internet access starts the
    // 3 s debounce again. A switch from Wi-Fi to Ethernet with no gap also counts. Thus a burst of changes gives a
    // single reconnect.
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

    // Runs a controller call on the UI thread and logs its failure, so no task goes unobserved.
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
            catch (Exception ex)
            {
                // The client retries a failed reconnect. The failure must not stop the tray.
                AppLog.ReconnectFailed(_logger, reason, ex);
            }
        });

    // Runs an action on the UI thread each time another process signals a named event.
    private static RegisteredWaitHandle WatchEvent(DispatcherQueue dispatcher, WaitHandle handle, Action action, bool once) =>
        ThreadPool.RegisterWaitForSingleObject(
            handle,
            (_, _) => dispatcher.TryEnqueue(() => action()),
            null,
            Timeout.Infinite,
            once);

    // =========================================================================
    // LOGGER FACTORY
    // =========================================================================

    // The controller takes a factory. The app has a single file provider, so it needs no full LoggerFactory.
    private sealed class SingleProviderLoggerFactory(ILoggerProvider provider) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose()
        {
            // The app owns and disposes the provider.
        }
    }
}

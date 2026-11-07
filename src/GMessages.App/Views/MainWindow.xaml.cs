using System.Collections.Specialized;
using System.ComponentModel;
using GMessages.App.Media;
using GMessages.App.Views.Controls;
using GMessages.Presentation;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Settings;
using GMessages.Presentation.Shortcuts;
using GMessages.App.Windowing;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace GMessages.App.Views;

/// <summary>
/// Shows the main window: the title bar with ☰ and the profile menu, and the body below it. The body shows the pairing
/// steps until the app is paired, then the chats. Settings opens as a page over both.
/// </summary>
/// <remarks>
/// <para>
/// The App makes the window on request and destroys it on close, then trims memory. The App picks the body with
/// <see cref="ShowPairing"/> or <see cref="ShowChats"/>. It switches the body in place when pairing ends or the phone
/// unpairs. A drop of the body on a switch or the close cancels a running pairing or disposes the
/// <see cref="MainViewModel"/>. That unhooks its lists from the store that outlives the window.
/// </para>
/// <para>
/// The settings page covers the body but keeps it running, such as a sign-in in progress or the open chat. Thus Back
/// returns to the body as it was. ☰ and the profile picture hide while pairing. They have nothing to show yet.
/// On Settings, Archived and Spam &amp; blocked, the Back button of the title bar shows in place of ☰. It carries the
/// AutomationId <c>Settings.Back</c>, <c>Main.ArchivedBack</c> or <c>Main.SpamBack</c>. ☰ is the pane toggle of the
/// title bar and carries <c>Main.MenuButton</c>. Thus both buttons have the spacing and animated icons of Windows.
/// </para>
/// <para>
/// In WinUI 2.8 the window is a <see cref="XamlWindow"/>: a Win32 window that hosts this content in a XAML Island.
/// It has synced resizing and Mica. The title bar is the <see cref="TitleBar"/> of the app.
/// </para>
/// </remarks>
public sealed partial class MainWindow : UserControl
{
    private readonly XamlWindow _window = new(XamlWindowKind.Main);
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly SettingsViewModel _settings;
    private readonly AppSettings _appSettings;
    private readonly AvatarCache _avatars;

    // The body is pairing or chats, never both. The settings page sits over it while open.
    private PairingPage? _pairing;
    private ChatsView? _chats;
    private SettingsPage? _settingsPage;
    private UIElement? _focusBeforeSettings;

    private bool _isActive;

    // The window asks for the photo of the owner when a load names the owner. It asks again after an answer with no
    // photo.
    private bool _profileAsked;

    // The least window size, in effective pixels. The width is the least width of the chat list. The height holds the
    // title bar, a few rows and the message box.
    private const double MinWindowWidth  = ChatsView.MinListPaneWidth;
    private const double MinWindowHeight = 500;

    // The file of the last download for Show in folder, and the timer that closes its confirmation.
    private static readonly TimeSpan DownloadedShownFor = TimeSpan.FromSeconds(6);
    private string? _downloadedPath;
    private DispatcherQueueTimer? _downloadedTimer;

    /// <summary>
    /// Creates the window with an empty body. The App then calls <see cref="ShowPairing"/> or <see cref="ShowChats"/>.
    /// </summary>
    /// <param name="settings">
    /// The state of the settings page, which also runs Unpair of the profile menu. The window disposes it on close.
    /// </param>
    /// <param name="avatars">
    /// The photo cache of the window. The window clears it on close and when it goes back to pairing.
    /// </param>
    /// <param name="appSettings">The stored settings, where the window keeps its place and size.</param>
    public MainWindow(SettingsViewModel settings, AvatarCache avatars, AppSettings appSettings)
    {
        _settings    = settings;
        _avatars     = avatars;
        _appSettings = appSettings;
        Avatar.Cache = avatars;
        InitializeComponent();

        // This content goes in the island of the window, and the title bar goes in its title bar row.
        _window.Content   = this;
        _window.Title     = AppInfo.WindowTitle;
        AppTitleBar.Title = AppInfo.WindowTitle;
        AppTitleBar.Icon  = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico"));
        AppTitleBar.Attach(_window);
        AppTitleBar.AddPassthrough(ProfileButton);

        _window.Resize(1100, 760);
        _window.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RestoreWindowBounds();
        _window.BoundsChanged += (_, _) => OnBoundsChanged();

        ProfileArchived.Text    = ChatCopy.Archived;
        ProfileSpamBlocked.Text = ChatCopy.SpamAndBlocked;

        RootGrid.Loaded += (_, _) =>
        {
            ApplyMinimumSize();
            ApplyFrameTheme();
            RootGrid.XamlRoot.Changed += (_, _) =>
            {
                ApplyMinimumSize();
                PunchViewerToolbar();
            };
        };
        RootGrid.ActualThemeChanged += (_, _) => ApplyFrameTheme();

        // The open photo viewer covers the title bar row. Its toolbar is then the only click-through hole of the row.
        PhotoViewer.IsOpenChanged += (_, _) => ShowViewerTitleBar();
        AppTitleBar.SizeChanged   += (_, _) =>
        {
            if (PhotoViewer.IsOpen)
            {
                _ = _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, PunchViewerToolbar);
            }
        };

        // In an inactive window only the title bar dims, as in the apps of Windows. A fade of the content faded each
        // element by itself. Thus overlapped elements showed through each other, such as a photo over its letter.
        _window.Activated += (_, active) =>
        {
            _isActive = active;
            ShowChatFocus();
            PhotoViewer.ShowActive(_isActive);
        };
        _window.Closed += (_, _) =>
        {
            CloseSettings();
            DropBody();
            Bindings.StopTracking();
            _settings.Dispose();
            Avatar.Cache = null;
            _avatars.Clear();
            Closed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Occurs after the window closes, by <see cref="Close"/> or by the user. The window is gone after this event.
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Gets the handle of the window.
    /// </summary>
    public nint Hwnd => _window.Hwnd;

    /// <summary>
    /// Shows the window and brings it to the front.
    /// </summary>
    public void Activate() => _window.Activate();

    /// <summary>
    /// Closes the window and raises <see cref="Closed"/>.
    /// </summary>
    public void Close() => _window.Close();

    /// <summary>
    /// Occurs when the user closes the window. A cancel keeps the window.
    /// </summary>
    public event EventHandler<CancelEventArgs>? Closing
    {
        add    => _window.Closing += value;
        remove => _window.Closing -= value;
    }

    /// <summary>
    /// Hides the window and keeps it for a quick reopen. The playback or recording of the open chat stops.
    /// </summary>
    /// <remarks>
    /// The window comes back as a new window does: no settings or photo over it, and the inbox. A chat opened from
    /// another folder does not come back.
    /// </remarks>
    public void Hide()
    {
        _chats?.StopMedia();
        CloseSettings();
        PhotoViewer.Close();

        // ponytail: an open chat in a folder view closes, also an inbox chat left beside it. A new window kept that
        // inbox chat.
        if (ViewModel is { IsFolderView: true } chats)
        {
            chats.ExitConversation();
            chats.BackToInbox();
        }

        _window.Hide();
    }

    /// <summary>
    /// Asks the App to open the settings page, from Settings of the profile menu or Ctrl + ,.
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Asks the App to quit, from Quit of the profile menu. Quit shows while the tray icon is off.
    /// </summary>
    public event EventHandler? QuitRequested;

    /// <summary>
    /// Gets the state of the chats, or null while the window shows pairing.
    /// </summary>
    public MainViewModel? ViewModel => _chats?.ViewModel;

    /// <summary>
    /// Gets a value that tells whether the window shows the pairing steps.
    /// </summary>
    public bool IsPairing => _pairing is not null;

    /// <summary>
    /// Gets the conversation that was open at the last drop of the chats, or null. The App opens it again in the next
    /// window.
    /// </summary>
    public string? LastConversationId { get; private set; }

    /// <summary>
    /// Gets the settings state that Unpair of the profile menu binds to.
    /// </summary>
    public SettingsViewModel Settings => _settings;

    /// <summary>Gets the title of the download confirmation.</summary>
    public static string SavedToDownloadsText => ChatCopy.SavedToDownloads;

    /// <summary>Gets the Show in folder text of the download confirmation.</summary>
    public static string ShowInFolderText => ChatCopy.ShowInFolder;

    /// <summary>
    /// Shows a photo in the viewer of the window, over both panes. See <see cref="PhotoViewer"/>.
    /// </summary>
    /// <param name="part">The media part of the photo.</param>
    /// <param name="messageId">The ID of the message.</param>
    /// <param name="name">The name that Narrator reads for the photo: its caption, or "Photo".</param>
    /// <param name="returnFocus">The photo in the chat, which gets focus again when the viewer closes.</param>
    public void ShowPhoto(MediaPart part, string messageId, string name, Control returnFocus) =>
        _ = PhotoViewer.ShowAsync(part, messageId, name, returnFocus);

    /// <summary>
    /// Plays a video in the viewer of the window, over both panes. See <see cref="PhotoViewer.ShowVideoAsync"/>.
    /// </summary>
    /// <param name="part">The media part of the video.</param>
    /// <param name="messageId">The ID of the message.</param>
    /// <param name="name">The name that Narrator reads for the video: its caption, or "Video".</param>
    /// <param name="returnFocus">The video frame in the chat, which gets focus again when the viewer closes.</param>
    /// <param name="returnState">The focus state of the clicked button, which the close gives back.</param>
    /// <param name="start">The start position, where the player of the bubble got to.</param>
    /// <param name="play">True to play from there. False to stay paused there, as the player of the bubble was.</param>
    /// <param name="frame">
    /// The still frame of the bubble, which grows in its place before the player shows. Null when no frame showed.
    /// </param>
    /// <param name="closed">
    /// Gets the position of the viewer and its play state when it closes. Null when nothing carries on.
    /// </param>
    public void ShowVideo(MediaPart part, string messageId, string name, Control returnFocus, FocusState returnState, TimeSpan start, bool play, ImageSource? frame, Action<TimeSpan, bool>? closed) =>
        _ = PhotoViewer.ShowVideoAsync(part, messageId, name, returnFocus, returnState, start, play, frame, closed);

    /// <summary>
    /// Shows the "Saved to Downloads" confirmation for a few seconds. Show in folder opens File Explorer on the file.
    /// </summary>
    /// <param name="path">The full path of the saved file.</param>
    public void ShowDownloaded(string path)
    {
        _downloadedPath      = path;
        DownloadedBar.IsOpen = true;

        _downloadedTimer ??= _dispatcher.CreateTimer();
        _downloadedTimer.Interval    = DownloadedShownFor;
        _downloadedTimer.IsRepeating = false;
        _downloadedTimer.Tick       -= OnDownloadedTimerTick;
        _downloadedTimer.Tick       += OnDownloadedTimerTick;
        _downloadedTimer.Start();
    }

    private void OnDownloadedTimerTick(DispatcherQueueTimer sender, object args) => DownloadedBar.IsOpen = false;

    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        DownloadedBar.IsOpen = false;
        if (_downloadedPath is { } path)
        {
            _ = MediaActions.ShowInFolderAsync(path);
        }
    }

    /// <summary>
    /// Sets the light or dark look of the window from the app theme.
    /// </summary>
    /// <param name="theme">The app theme setting.</param>
    public void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };

        // The title bar draws the caption buttons, so they follow the page colors. They are dark while the scrim of the
        // photo viewer is under them.
        AppTitleBar.CaptionTheme = PhotoViewer.IsOpen ? ElementTheme.Dark : ElementTheme.Default;
        ApplyFrameTheme();
    }

    // The border and the Mica of the window take the dark tint when the page is dark. DWM cannot tell by itself.
    private void ApplyFrameTheme() => _window.SetDarkFrame(RootGrid.ActualTheme == ElementTheme.Dark);

    // =========================================================================
    // PHOTO VIEWER TITLE BAR
    // =========================================================================

    // The photo viewer opened or closed. While it is open, its scrim also tints the title bar row. The caption buttons
    // turn dark, so they read on the scrim. Clicks go through only where its toolbar is, and the rest of the row still
    // drags the window. After the close, the caption buttons follow the app theme again. The title bar then gets its
    // own click-through holes back.
    private void ShowViewerTitleBar()
    {
        AppTitleBar.CaptionTheme = PhotoViewer.IsOpen ? ElementTheme.Dark : ElementTheme.Default;
        if (!PhotoViewer.IsOpen)
        {
            AppTitleBar.SetPassthroughOverride(null);
            return;
        }

        PunchViewerToolbar();
    }

    // While the photo viewer is open, its toolbar sits in the top-left corner of the row. It becomes the only
    // click-through hole of the title bar. It replaces ☰, Back and the profile, which the scrim covers.
    private void PunchViewerToolbar()
    {
        if (!PhotoViewer.IsOpen || RootGrid.XamlRoot is null)
        {
            return;
        }

        PhotoViewer.FitToolbar(0);
        PhotoViewer.UpdateLayout();
        AppTitleBar.SetPassthroughOverride([PhotoViewer.Toolbar]);
    }

    // =========================================================================
    // BODY AND SETTINGS
    // =========================================================================

    /// <summary>
    /// Shows the pairing steps in place of the chats or the empty body, and closes the settings page.
    /// </summary>
    /// <remarks>
    /// The method drops the chats first. Nothing of the old phone stays: its photos and the owner picture go too.
    /// </remarks>
    /// <param name="page">
    /// The pairing page, which starts its sign-in when it is made. The window closes it when it drops the page.
    /// </param>
    public void ShowPairing(PairingPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        CloseSettings();
        DropBody();
        _avatars.Clear();
        LastConversationId            = null;
        _profileAsked                 = false;
        ProfilePicture.ProfilePicture = null;
        AutomationProperties.SetItemStatus(ProfileButton, string.Empty);

        _pairing              = page;
        page.CancelRequested += OnPairingCancel;
        BodyHost.Child        = page;
        ShowTitleBarButtons();
    }

    /// <summary>
    /// Shows the chats in place of the pairing steps or the empty body, and starts the load of the inbox.
    /// </summary>
    /// <param name="viewModel">The state of the chats. The window disposes it when it drops the chats.</param>
    public void ShowChats(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DropBody();
        _chats                                  = new ChatsView(viewModel, _settings);
        viewModel.Connected                    += OnConnected;
        viewModel.PropertyChanged              += OnChatsChanged;
        viewModel.Inbox.Rows.CollectionChanged += OnInboxRowsChanged;
        BodyHost.Child                          = _chats;
        ShowTitleBarButtons();
        ShowChatFocus();

        // The list can show from the cache of the store before these handlers exist. A refresh with no change moves no
        // row. Thus the window also asks for the photo of the owner now.
        _ = ShowProfilePhotoAsync();
    }

    /// <summary>
    /// Opens a conversation for a request from outside the window, such as a toast click. The method closes the
    /// settings page, so the chat shows. It does nothing while pairing.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    public void SelectConversation(string conversationId)
    {
        if (ViewModel is not { } view)
        {
            return;
        }

        CloseSettings();
        view.Select(conversationId);
    }

    /// <summary>
    /// Opens the settings page over the body. The method does nothing when the page is already open.
    /// </summary>
    public void ShowSettings()
    {
        if (_settingsPage is not null)
        {
            return;
        }

        // Settings from the tray replaces the photo viewer.
        PhotoViewer.Close();

        // Back puts the keyboard focus where it was.
        _focusBeforeSettings = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as UIElement : null;

        _settingsPage                  = new SettingsPage(_settings);
        _settingsPage.UnpairRequested += OnSettingsUnpair;
        SettingsHost.Child             = _settingsPage;
        BodyHost.Visibility            = Visibility.Collapsed;
        ShowChatFocus();

        // The keyboard focus moves to Back, so Tab does not start in the hidden body.
        ShowTitleBarButtons()?.Focus(FocusState.Programmatic);
    }

    // Back or a switch to pairing shows the body again.
    private void CloseSettings()
    {
        if (_settingsPage is not { } page)
        {
            return;
        }

        page.UnpairRequested -= OnSettingsUnpair;
        page.Close();
        _settingsPage       = null;
        SettingsHost.Child  = null;
        BodyHost.Visibility = Visibility.Visible;
        ShowChatFocus();
        ShowTitleBarButtons();

        // The UIElement of system XAML has no Focus(), so the focus manager moves the focus.
        if (_focusBeforeSettings is { } previous)
        {
            _ = FocusManager.TryFocusAsync(previous, FocusState.Programmatic);
        }

        _focusBeforeSettings = null;
    }

    // Cancels a running pairing or disposes the view model of the chats.
    private void DropBody()
    {
        // The photo goes with the chats on a sign-out, an unpair or the close.
        PhotoViewer.Close();

        if (_pairing is { } pairing)
        {
            pairing.CancelRequested -= OnPairingCancel;
            pairing.Close();
            _pairing = null;
        }

        if (_chats is { } chats)
        {
            LastConversationId                            = chats.ViewModel.SelectedConversationId;
            chats.ViewModel.Connected                    -= OnConnected;
            chats.ViewModel.PropertyChanged              -= OnChatsChanged;
            chats.ViewModel.Inbox.Rows.CollectionChanged -= OnInboxRowsChanged;
            chats.Close();
            _chats = null;
        }

        BodyHost.Child = null;
    }

    // ☰ and the profile picture have nothing to show while pairing. A narrow window shows 1 pane and has no list to
    // hide, so it has no ☰. Settings, Archived, Spam & blocked and the open chat of a narrow window show Back in place
    // of ☰. Back carries the name of the page that it leaves, and the tests find it by that name. The method returns
    // Back while it shows.
    private Button? ShowTitleBarButtons()
    {
        bool showBack = ShowsBack();

        AppTitleBar.IsBackButtonVisible       = showBack;
        AppTitleBar.IsPaneToggleButtonVisible = ShowsMenu(showBack);
        ProfileButton.Visibility              = _chats is not null ? Visibility.Visible : Visibility.Collapsed;
        NameTitleBarButtons();

        // The title bar lets clicks through where its buttons are only when it computes its regions. While the photo
        // viewer is open, only its toolbar lets clicks through.
        AppTitleBar.RecomputeDragRegions();
        PunchViewerToolbar();
        return showBack ? AppTitleBar.Back : null;
    }

    // Gives Back and ☰ the names that the tests and Narrator know. The name of Back is the page that it leaves.
    private void NameTitleBarButtons()
    {
        if (AppTitleBar.IsBackButtonVisible)
        {
            AutomationProperties.SetAutomationId(AppTitleBar.Back, BackId());
        }

        AutomationProperties.SetAutomationId(AppTitleBar.PaneToggle, "Main.MenuButton");
        AutomationProperties.SetName(AppTitleBar.PaneToggle, ChatCopy.ShowOrHideConversations);
        ToolTipService.SetToolTip(AppTitleBar.PaneToggle, ChatCopy.ShowOrHideConversations);
    }

    // ☰ shows after pairing in a wide window, where Back does not show.
    private bool ShowsMenu(bool showBack) => _chats is not null && !showBack && ViewModel?.IsNarrow != true;

    // Back shows on Settings, in a folder and over the open chat of a narrow window.
    private bool ShowsBack() => _settingsPage is not null || ViewModel?.IsFolderView == true || ViewModel?.ShowChatPane == true;

    // The AutomationId of Back, for the page that it leaves.
    private string BackId() =>
        _settingsPage is not null ? "Settings.Back"
        : ViewModel?.ShowChatPane == true ? "Chat.Back"
        : ViewModel?.IsArchivedView == true ? "Main.ArchivedBack"
        : "Main.SpamBack";

    // The open chat counts as viewed only while the window is active and Settings does not cover it.
    private void ShowChatFocus()
    {
        if (_chats is { } chats)
        {
            chats.ViewModel.IsWindowFocused = _isActive && _settingsPage is null;

            // The user is back on the open chat after a window switch, a restore or a close of Settings. Its toasts
            // count as read now. That includes toasts kept while the chat was on screen with "Notify for the chat I'm
            // viewing" on.
            if (chats.ViewModel is { IsWindowFocused: true, SelectedConversationId: { } viewed })
            {
                (Application.Current as App)?.Notifications?.RemoveConversation(viewed);
            }
        }
    }

    // =========================================================================
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Hides an element while the flag is set.
    /// </summary>
    /// <param name="hidden">True to hide.</param>
    /// <returns>Collapsed when set, else Visible.</returns>
    public static Visibility HiddenIf(bool hidden) => hidden ? Visibility.Collapsed : Visibility.Visible;

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // The owner chose that the window opens where it was last left. Its place and size come back when they still land
    // on a display. An unplugged monitor leaves the default. The window maximizes again when it was maximized.
    private unsafe void RestoreWindowBounds()
    {
        // Bounds exist only after a move or a resize of the restored window. A window maximized at once has none, but
        // it still maximizes.
        if (_appSettings.WindowBounds is { } saved)
        {
            var bounds = new WindowInterop.RECT { Left = saved.Left, Top = saved.Top, Right = saved.Left + saved.Width, Bottom = saved.Top + saved.Height };
            if (WindowInterop.MonitorFromRect(&bounds, 0) == 0)
            {
                return;
            }

            _window.MoveAndResize(saved.Left, saved.Top, saved.Width, saved.Height);
        }

        if (_appSettings.WindowMaximized)
        {
            _window.Maximize();
        }
    }

    // The settings keep every move or resize of the shown window. While maximized or minimized, they keep only that
    // state, and the bounds stay those of the restored window.
    private void OnBoundsChanged()
    {
        if (!_window.IsVisible)
        {
            return;
        }

        if (!_window.IsMaximized && !_window.IsMinimized)
        {
            WindowInterop.RECT bounds = _window.Bounds;
            _appSettings.WindowBounds = (bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        }

        if (!_window.IsMinimized)
        {
            _appSettings.WindowMaximized = _window.IsMaximized;
        }
    }

    // The window never shrinks below the narrowest layout of the chat list, which shows alone in a narrow window. It
    // also keeps room for the title bar, a few rows and the message box. The window takes physical pixels of the
    // whole window. Thus the method scales the minimum by the display scale, again on each change, and adds the frame.
    private void ApplyMinimumSize()
    {
        // While minimized or maximized, the rect of the window is not that of the restored frame. The restore calls
        // this method again.
        if (RootGrid.XamlRoot is not { } root || _window.IsMinimized || _window.IsMaximized)
        {
            return;
        }

        double scale              = root.RasterizationScale;
        WindowInterop.RECT outer  = _window.Bounds;
        (int width, int height)   = _window.ClientSize;
        int frameWidth            = Math.Max(0, outer.Right - outer.Left - width);
        int frameHeight           = Math.Max(0, outer.Bottom - outer.Top - height);
        _window.SetMinimumSize((int)Math.Ceiling(MinWindowWidth * scale) + frameWidth, (int)Math.Ceiling(MinWindowHeight * scale) + frameHeight);
    }

    // A folder, a width change or the chat of a narrow window can switch between Back and ☰. Other changes leave the
    // title bar alone.
    private void OnChatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The view model raises all properties at once with an empty name. Thus the method changes the title bar only
        // when its buttons change.
        bool showBack = ShowsBack();
        if (AppTitleBar.IsBackButtonVisible != showBack || AppTitleBar.IsPaneToggleButtonVisible != ShowsMenu(showBack)
            || (showBack && AutomationProperties.GetAutomationId(AppTitleBar.Back) != BackId()))
        {
            ShowTitleBarButtons();
        }
    }

    // Back leaves Settings. Else it leaves the chat of a narrow window for the list. Else it leaves the folder.
    private void OnTitleBarBackRequested(object? sender, EventArgs args)
    {
        if (_settingsPage is not null)
        {
            CloseSettings();
            return;
        }

        if (ViewModel?.ShowChatPane == true)
        {
            ViewModel.ExitConversation();
            return;
        }

        ViewModel?.BackToInbox();
    }

    // New rows can name the owner now, so the window shows the photo of the owner.
    private void OnInboxRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ = ShowProfilePhotoAsync();

    // Back online, the window asks again for the photos that failed while the phone was away.
    private void OnConnected(object? sender, EventArgs e)
    {
        Avatar.ReloadMissing(RootGrid);
        _ = ShowProfilePhotoAsync();
    }

    // PersonPicture keeps its glyph without a photo. When no photo comes, the next load or reconnect asks again. The
    // cache remembers "no photo", so the second request costs the phone nothing.
    private async Task ShowProfilePhotoAsync()
    {
        if (_profileAsked || ViewModel?.MeId is not { } meId)
        {
            return;
        }

        _profileAsked = true;
        if (await _avatars.GetAsync(meId, CancellationToken.None) is not { } photo)
        {
            _profileAsked = false;
            return;
        }

        // After a switch back to pairing meanwhile, the owner of the old phone must not show.
        if (_chats is null)
        {
            return;
        }

        // UI Automation cannot see a picture, so the button status tells that it shows one.
        ProfilePicture.ProfilePicture = await Avatar.ToImageAsync(photo);
        AutomationProperties.SetItemStatus(ProfileButton, "Photo");
    }

    private void OnPaneToggleRequested(object? sender, EventArgs args) => ViewModel?.ToggleList();

    // Archived and Spam & blocked are lists, so they close Settings to show.
    private void OnArchivedClick(object sender, RoutedEventArgs e)
    {
        PhotoViewer.Close();
        CloseSettings();
        ViewModel?.OpenArchived();
    }

    private void OnSpamBlockedClick(object sender, RoutedEventArgs e)
    {
        PhotoViewer.Close();
        CloseSettings();
        ViewModel?.OpenSpamBlocked();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnQuitClick(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);

    // Cancel of pairing closes the window, which cancels the pairing.
    private void OnPairingCancel(object? sender, EventArgs e) => Close();

    private void OnUnpairClick(object sender, RoutedEventArgs e) => ConfirmUnpair();

    private void OnSettingsUnpair(object? sender, EventArgs e) => ConfirmUnpair();

    // Asks for a confirm, then unpairs. The App switches this window to pairing on NotPaired. The method is async void
    // as a click handler is. Thus a failure reaches the unhandled error log of the app.
    private async void ConfirmUnpair()
    {
        UnpairDialog.RequestedTheme = RootGrid.RequestedTheme;
        await UnpairDialog.ConfirmAndUnpairAsync(_settings);
    }

    // =========================================================================
    // KEYBOARD SHORTCUTS
    // =========================================================================

    // Every key press in the window comes here first. The method handles a key only when a shortcut ran. Thus other
    // keys reach the controls. A shortcut with nothing to do also reaches them, such as "r" with no chat open.
    // Shortcuts run only for the chats, not for pairing or Settings. They do not run while a dialog or a menu is open.
    // An open tooltip does not count. The method tells it apart by its automation type, not a type test. The Native
    // AOT build can give back the child of a popup as a base type.
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The photo viewer takes Escape, so the chat stays open. No shortcut runs under the viewer.
        if (PhotoViewer.IsOpen)
        {
            if (e.Key == VirtualKey.Escape)
            {
                PhotoViewer.Close(animate: true);
                e.Handled = true;
            }

            return;
        }

        if (_chats is not { } chats || _settingsPage is not null
            || VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Any(p => p.Child is null
                || FrameworkElementAutomationPeer.CreatePeerForElement(p.Child)?.GetAutomationControlType() != AutomationControlType.ToolTip))
        {
            return;
        }

        // The method matches the box by name, not by type. The Native AOT build can give back the box as a base type.
        // These names are the only text boxes of the app, and a new text box must go here.
        bool focusInTextBox = FocusManager.GetFocusedElement(RootGrid.XamlRoot) is FrameworkElement { Name: "DraftBox" or "QueryBox" };
        if (ShortcutMap.Resolve(e.Key, UiHelpers.IsDown(VirtualKey.Control), UiHelpers.IsDown(VirtualKey.Shift), UiHelpers.IsDown(VirtualKey.Menu), focusInTextBox, UiHelpers.IsDown(VirtualKey.RightMenu)) is { } command)
        {
            e.Handled = Run(chats, command);
        }
    }

    // The window runs Settings and Help. The chats run the other shortcuts.
    private bool Run(ChatsView chats, ShortcutCommand command)
    {
        switch (command)
        {
            case ShortcutCommand.OpenSettings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                return true;

            case ShortcutCommand.ShowHelp:
                ShortcutDialog.RequestedTheme = RootGrid.RequestedTheme;
                _ = ShortcutDialog.ShowAsync();
                return true;

            default:
                return chats.Run(command);
        }
    }
}

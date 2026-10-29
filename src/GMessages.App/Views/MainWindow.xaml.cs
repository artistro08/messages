using System.Collections.Specialized;
using GMessages.App.Views.Controls;
using GMessages.Presentation;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Settings;
using GMessages.Presentation.Shortcuts;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace GMessages.App.Views;

/// <summary>
/// The main window: the title bar with ☰ and the profile menu, and below it the pairing steps until the app is paired,
/// then the chats. Settings opens as a page over both.
/// </summary>
/// <remarks>
/// <para>
/// Made when asked for and destroyed on close (the App then trims memory). The App picks the body with
/// <see cref="ShowPairing"/> or <see cref="ShowChats"/> and switches it in place when pairing ends or the phone is
/// unpaired. Dropping the body (a switch or the close) cancels a running pairing or disposes the
/// <see cref="MainViewModel"/>, which unhooks its lists from the store that outlives the window.
/// </para>
/// <para>
/// The settings page covers the body but keeps it running (a sign-in in progress, the open chat), so Back returns to it
/// as it was. ☰ and the profile picture hide while pairing, since there is nothing for them yet.
/// </para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly SettingsViewModel _settings;
    private readonly AvatarCache _avatars;

    // The Body (Pairing Or Chats, Never Both) And The Settings Page Over It While Open
    private PairingPage? _pairing;
    private ChatsView? _chats;
    private SettingsPage? _settingsPage;
    private Control? _focusBeforeSettings;
    private bool _isActive;

    // The Owner's Photo Is Asked For Once A Load Names The Owner, And Again After An Answer Without One
    private bool _profileAsked;

    /// <summary>
    /// Creates the window with an empty body; the App then calls <see cref="ShowPairing"/> or <see cref="ShowChats"/>.
    /// </summary>
    /// <param name="settings">The settings page's state, also doing the profile menu's Unpair; the window disposes it on close.</param>
    /// <param name="avatars">The window's photo cache; the window clears it on close and on going back to pairing.</param>
    public MainWindow(SettingsViewModel settings, AvatarCache avatars)
    {
        _settings    = settings;
        _avatars     = avatars;
        Avatar.Cache = avatars;
        InitializeComponent();

        // Window
        Title                      = AppInfo.DisplayName;
        AppTitleBar.Title          = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Tall Caption Buttons, As High As The Title Bar Row (48 px)
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Copy
        ProfileSpamBlocked.Text = ChatCopy.SpamAndBlocked;

        // Behavior
        Activated += (_, e) =>
        {
            _isActive = e.WindowActivationState != WindowActivationState.Deactivated;
            ShowChatFocus();
        };
        AppTitleBar.LayoutUpdated += OnTitleBarLayoutUpdated;
        Closed                    += (_, _) =>
        {
            AppTitleBar.LayoutUpdated -= OnTitleBarLayoutUpdated;
            CloseSettings();
            DropBody();
            Bindings.StopTracking();
            _settings.Dispose();
            Avatar.Cache = null;
            _avatars.Clear();
        };
    }

    /// <summary>
    /// Asks the App to open the settings page (the profile menu's Settings, Ctrl + ,).
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Gets the chats' state, or null while the window shows pairing.
    /// </summary>
    public MainViewModel? ViewModel => _chats?.ViewModel;

    /// <summary>
    /// Gets whether the window shows the pairing steps.
    /// </summary>
    public bool IsPairing => _pairing is not null;

    /// <summary>
    /// Gets the settings state the profile menu's Unpair binds to.
    /// </summary>
    public SettingsViewModel Settings => _settings;

    /// <summary>
    /// Sets the window's light or dark look from the app theme.
    /// </summary>
    /// <param name="theme">The app theme setting.</param>
    public void ApplyTheme(AppTheme theme)
    {
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
    // BODY AND SETTINGS
    // =========================================================================

    /// <summary>
    /// Shows the pairing steps in place of the chats (or the empty body), and closes the settings page.
    /// </summary>
    /// <remarks>
    /// The chats are dropped first, and nothing of the old phone stays: its photos and the owner's picture go too.
    /// </remarks>
    /// <param name="page">The pairing page, which starts its sign-in when made; the window closes it when dropped.</param>
    public void ShowPairing(PairingPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        CloseSettings();
        DropBody();
        _avatars.Clear();
        _profileAsked                 = false;
        ProfilePicture.ProfilePicture = null;

        _pairing              = page;
        page.CancelRequested += OnPairingCancel;
        BodyHost.Child        = page;
        ShowTitleBarButtons(false);
    }

    /// <summary>
    /// Shows the chats in place of the pairing steps (or the empty body) and starts loading the inbox.
    /// </summary>
    /// <param name="viewModel">The chats' state; the window disposes it when the chats are dropped.</param>
    public void ShowChats(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DropBody();
        _chats                                  = new ChatsView(viewModel);
        viewModel.Connected                    += OnConnected;
        viewModel.Inbox.Rows.CollectionChanged += OnInboxRowsChanged;
        BodyHost.Child                          = _chats;
        ShowTitleBarButtons(true);
        ShowChatFocus();
    }

    /// <summary>
    /// Opens the settings page over the body. Does nothing when it is already open.
    /// </summary>
    public void ShowSettings()
    {
        if (_settingsPage is not null)
        {
            return;
        }

        // Back Puts Keyboard Focus Where It Was
        _focusBeforeSettings = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;

        _settingsPage                  = new SettingsPage(_settings);
        _settingsPage.BackRequested   += OnSettingsBack;
        _settingsPage.UnpairRequested += OnSettingsUnpair;
        SettingsHost.Child             = _settingsPage;
        BodyHost.Visibility            = Visibility.Collapsed;
        ShowChatFocus();
    }

    // Back (Or A Switch To Pairing): The Body Shows Again
    private void CloseSettings()
    {
        if (_settingsPage is not { } page)
        {
            return;
        }

        page.BackRequested   -= OnSettingsBack;
        page.UnpairRequested -= OnSettingsUnpair;
        page.Close();
        _settingsPage       = null;
        SettingsHost.Child  = null;
        BodyHost.Visibility = Visibility.Visible;
        ShowChatFocus();

        _focusBeforeSettings?.Focus(FocusState.Programmatic);
        _focusBeforeSettings = null;
    }

    // Cancels A Running Pairing Or Disposes The Chats' View Model
    private void DropBody()
    {
        if (_pairing is { } pairing)
        {
            pairing.CancelRequested -= OnPairingCancel;
            pairing.Close();
            _pairing = null;
        }

        if (_chats is { } chats)
        {
            chats.ViewModel.Connected                    -= OnConnected;
            chats.ViewModel.Inbox.Rows.CollectionChanged -= OnInboxRowsChanged;
            chats.Close();
            _chats = null;
        }

        BodyHost.Child = null;
    }

    // ☰ And The Profile Picture Have Nothing To Show While Pairing
    private void ShowTitleBarButtons(bool show)
    {
        MenuButton.Visibility    = show ? Visibility.Visible : Visibility.Collapsed;
        ProfileButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // The Open Chat Counts As Viewed Only While The Window Is Active And Settings Doesn't Cover It
    private void ShowChatFocus()
    {
        if (_chats is { } chats)
        {
            chats.ViewModel.IsWindowFocused = _isActive && _settingsPage is null;
        }
    }

    // =========================================================================
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Hides an element while the flag is set.
    /// </summary>
    /// <param name="hidden">True to hide.</param>
    /// <returns>Collapsed when set.</returns>
    public static Visibility HiddenIf(bool hidden) => hidden ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Shows an element only when there is text.
    /// </summary>
    /// <param name="text">The text, or null.</param>
    /// <returns>Visible for non-empty text.</returns>
    public static Visibility ShownIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // The TitleBar Control Reserves Room For The Caption Buttons With Their Width In Physical Pixels, As If Effective, So
    // Above 100% Scale A Gap Opened Between The Profile Picture And Minimize. Its Last Column Is That Reserve: Size It
    // To The Buttons' Real Width After Each Layout, Since The Control Can Set It Again Whenever The Window Changes
    private void OnTitleBarLayoutUpdated(object? sender, object e)
    {
        if (AppTitleBar.XamlRoot is not { } root || VisualTreeHelper.GetChildrenCount(AppTitleBar) == 0
            || VisualTreeHelper.GetChild(AppTitleBar, 0) is not Grid { ColumnDefinitions.Count: > 0 } layout)
        {
            return;
        }

        var reserve = new GridLength(AppWindow.TitleBar.RightInset / root.RasterizationScale);
        if (layout.ColumnDefinitions[^1].Width != reserve)
        {
            layout.ColumnDefinitions[^1].Width = reserve;
        }
    }

    // Rows Came In, So A Conversation May Now Name The Owner: Show Their Photo
    private void OnInboxRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ = ShowProfilePhotoAsync();

    // Back Online: Photos That Failed While The Phone Was Away Are Asked For Again
    private void OnConnected(object? sender, EventArgs e)
    {
        Avatar.ReloadMissing(RootGrid);
        _ = ShowProfilePhotoAsync();
    }

    // PersonPicture Keeps Its Glyph Without A Photo. No Answer (No Photo, Or The Phone Didn't Answer) Lets The Next
    // Load Or Reconnect Ask Again; The Cache Remembers "No Photo", So That Costs The Phone Nothing
    private async Task ShowProfilePhotoAsync()
    {
        if (_profileAsked || ViewModel?.MeId is not { } me_id)
        {
            return;
        }

        _profileAsked = true;
        if (await _avatars.GetAsync(me_id, CancellationToken.None) is not { } photo)
        {
            _profileAsked = false;
            return;
        }

        // Back To Pairing Meanwhile: The Old Phone's Owner Must Not Show
        if (_chats is null)
        {
            return;
        }

        ProfilePicture.ProfilePicture = await Avatar.ToImageAsync(photo);
    }

    private void OnMenuClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleList();

    // Spam & Blocked Is A List, So It Closes Settings To Show It
    private void OnSpamBlockedClick(object sender, RoutedEventArgs e)
    {
        CloseSettings();
        ViewModel?.OpenSpamBlocked();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsBack(object? sender, EventArgs e) => CloseSettings();

    // Pairing's Cancel Closes The Window, Which Cancels The Pairing
    private void OnPairingCancel(object? sender, EventArgs e) => Close();

    private void OnUnpairClick(object sender, RoutedEventArgs e) => ConfirmUnpair();

    private void OnSettingsUnpair(object? sender, EventArgs e) => ConfirmUnpair();

    // Confirm, Then Unpair: The App Switches This Window To Pairing On NotPaired. Async Void Like A Click Handler, So A
    // Failure Reaches The App's Unhandled Error Log
    private async void ConfirmUnpair()
    {
        UnpairDialog.RequestedTheme = RootGrid.RequestedTheme;
        await UnpairDialog.ConfirmAndUnpairAsync(_settings);
    }

    // =========================================================================
    // KEYBOARD SHORTCUTS
    // =========================================================================

    // Every Key Press In The Window Passes Here First. Handled Only When A Shortcut Ran, So Other Keys (And A Shortcut
    // With Nothing To Do, Like "r" With No Chat Open) Reach The Controls. Only For The Chats (Not Pairing Or Settings),
    // And Skipped While A Dialog Or Menu Is Open
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_chats is not { } chats || _settingsPage is not null
            || VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Any(p => p.Child is not ToolTip))
        {
            return;
        }

        bool focus_in_text_box = FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox;
        if (ShortcutMap.Resolve(e.Key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), IsDown(VirtualKey.Menu), focus_in_text_box) is { } command)
        {
            e.Handled = Run(chats, command);
        }
    }

    // Settings And Help Belong To The Window; The Rest To The Chats
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

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}

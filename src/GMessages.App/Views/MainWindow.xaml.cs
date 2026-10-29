using System.Collections.Specialized;
using System.ComponentModel;
using GMessages.App.Views.Controls;
using GMessages.Presentation;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Settings;
using GMessages.Presentation.Shortcuts;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace GMessages.App.Views;

/// <summary>
/// The main window: the title bar with ☰ and the profile menu, the conversation list (or Spam &amp; blocked) and the
/// chat pane.
/// </summary>
/// <remarks>
/// <para>
/// Made when asked for and destroyed on close (the App then trims memory). Closing disposes the
/// <see cref="MainViewModel"/>, which unhooks its lists from the store that outlives the window.
/// </para>
/// <para>
/// Wide windows (720 effective px and up) show both panes; narrow ones show one at a time, following
/// <see cref="MainViewModel.ShowChatPane"/>. ☰ collapses the list in both. The width is read from the root grid's
/// <c>SizeChanged</c>, not an <c>AdaptiveTrigger</c>: visual state triggers never run on a window's root element
/// (it isn't inside a control), so the panes are laid out with <c>x:Bind</c> on <see cref="MainViewModel.IsNarrow"/>.
/// </para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    // Layout: Below This Width (Effective Pixels) One Pane Shows At A Time; The List Pane's Width Beside The Chat
    private const double NarrowBelow   = 720;
    private const double ListPaneWidth = 360;

    private readonly SettingsViewModel _settings;
    private readonly AvatarCache _avatars;

    // The Owner's Photo Is Asked For Once A Load Names The Owner, And Again After An Answer Without One
    private bool _profileAsked;

    /// <summary>
    /// Creates the window and starts loading the inbox.
    /// </summary>
    /// <param name="viewModel">The window's state; the window disposes it on close.</param>
    /// <param name="settings">Does the profile menu's Unpair, the same way the settings window does.</param>
    /// <param name="avatars">The window's photo cache; the window clears it on close.</param>
    public MainWindow(MainViewModel viewModel, SettingsViewModel settings, AvatarCache avatars)
    {
        ViewModel    = viewModel;
        _settings    = settings;
        _avatars     = avatars;
        Avatar.Cache = avatars;
        InitializeComponent();

        // Window
        Title                      = AppInfo.DisplayName;
        AppTitleBar.Title          = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Copy
        StartChatText.Text      = ChatCopy.StartChat;
        ListTitle.Text          = ChatCopy.SpamAndBlocked;
        ProfileSpamBlocked.Text = ChatCopy.SpamAndBlocked;
        EmptyStateText.Text     = ChatCopy.StartAConversation;

        // Behavior
        Activated                              += (_, e) => ViewModel.IsWindowFocused = e.WindowActivationState != WindowActivationState.Deactivated;
        ViewModel.PropertyChanged              += OnViewModelChanged;
        ViewModel.Connected                    += OnConnected;
        ViewModel.Inbox.Rows.CollectionChanged += OnInboxRowsChanged;
        Closed                                 += (_, _) =>
        {
            ViewModel.PropertyChanged              -= OnViewModelChanged;
            ViewModel.Connected                    -= OnConnected;
            ViewModel.Inbox.Rows.CollectionChanged -= OnInboxRowsChanged;
            Bindings.StopTracking();
            ViewModel.Dispose();
            Avatar.Cache = null;
            _avatars.Clear();
        };
        ShowCurrentList();
        _ = ViewModel.Inbox.LoadAsync();
    }

    /// <summary>
    /// Asks the App to open the settings window (the profile menu's Settings).
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Gets the window's state the view binds to.
    /// </summary>
    public MainViewModel ViewModel { get; }

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
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Gives the list pane's width: fixed beside the chat in a wide window, the whole width in a narrow one.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <returns>The width in effective pixels, or NaN to stretch.</returns>
    public static double ListWidth(bool isNarrow) => isNarrow ? double.NaN : ListPaneWidth;

    /// <summary>
    /// Gives how many columns a pane spans: one each side by side, both when a narrow window shows one pane.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <returns>The column span.</returns>
    public static int PaneSpan(bool isNarrow) => isNarrow ? 2 : 1;

    /// <summary>
    /// Gives the chat pane's column: beside the list in a wide window, the first column in a narrow one.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <returns>The column.</returns>
    public static int ChatColumn(bool isNarrow) => isNarrow ? 0 : 1;

    /// <summary>
    /// Shows the list unless ☰ collapsed it or a narrow window shows the chat instead.
    /// </summary>
    /// <param name="isListCollapsed">True when ☰ collapsed the list.</param>
    /// <param name="showChatPane">True when a narrow window shows the chat.</param>
    /// <returns>The list pane's visibility.</returns>
    public static Visibility ListVisibility(bool isListCollapsed, bool showChatPane) =>
        isListCollapsed || showChatPane ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Shows the chat pane in a wide window, and in a narrow one when it holds a chat or the list is collapsed.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <param name="showChatPane">True when a narrow window shows the chat.</param>
    /// <param name="isListCollapsed">True when ☰ collapsed the list.</param>
    /// <returns>The chat pane's visibility.</returns>
    public static Visibility ChatVisibility(bool isNarrow, bool showChatPane, bool isListCollapsed) =>
        !isNarrow || showChatPane || isListCollapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Shows the empty state while no conversation and no Start chat is open.
    /// </summary>
    /// <param name="isChatOpen">True when a conversation is open.</param>
    /// <param name="isStartChatOpen">True when Start chat is open.</param>
    /// <returns>The empty state's visibility.</returns>
    public static Visibility EmptyStateVisibility(bool isChatOpen, bool isStartChatOpen) =>
        !isChatOpen && !isStartChatOpen ? Visibility.Visible : Visibility.Collapsed;

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

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowCurrentList();
        ShowSelection();
    }

    // Inbox Or Spam & Blocked: Rewrapped Only When The List Itself Changes, So Other Changes Keep The Selection
    // (See BindableList For Why The Rows Aren't Bound Directly)
    private void ShowCurrentList() => BindableList.Show(ConversationList, ViewModel.CurrentList.Rows);

    // The List Highlights The Open Conversation: Picking A Row Opens It, And This Follows Opens From Elsewhere
    // (Ctrl + . And Ctrl + ,, Start Chat, Escape), Scrolling The Row Into View
    private void ShowSelection()
    {
        ConversationRowViewModel? row = ViewModel.CurrentList.Rows.FirstOrDefault(r => r.Id == ViewModel.SelectedConversationId);
        if (ConversationList.SelectedItem == row)
        {
            return;
        }

        ConversationList.SelectedItem = row;
        if (row is not null)
        {
            ConversationList.ScrollIntoView(row);
        }
    }

    // Narrow Or Wide From The Window Width (See The Class Remarks); The View Model Needs It For The One-Pane Rule
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool is_narrow = e.NewSize.Width < NarrowBelow;
        if (is_narrow != ViewModel.IsNarrow)
        {
            ViewModel.IsNarrow = is_narrow;
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
        if (_profileAsked || ViewModel.MeId is not { } me_id)
        {
            return;
        }

        _profileAsked = true;
        if (await _avatars.GetAsync(me_id, CancellationToken.None) is not { } photo)
        {
            _profileAsked = false;
            return;
        }

        ProfilePicture.ProfilePicture = await Avatar.ToImageAsync(photo);
    }

    private void OnMenuClick(object sender, RoutedEventArgs e) => ViewModel.ToggleList();

    private void OnStartChatClick(object sender, RoutedEventArgs e) => ViewModel.OpenStartChat();

    // Start Chat Opened (Or Found) The Conversation: Show It In Place Of Start Chat
    private void OnChatStarted(object? sender, string conversationId) => ViewModel.Select(conversationId);

    // The Back Arrow Of A Narrow Window Closes The Chat Or Start Chat, Which Shows The List
    private void OnPaneBack(object? sender, EventArgs e) => ViewModel.ExitConversation();

    private void OnSpamBlockedClick(object sender, RoutedEventArgs e) => ViewModel.OpenSpamBlocked();

    private void OnSpamBackClick(object sender, RoutedEventArgs e) => ViewModel.BackToInbox();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    // Confirm, Then Unpair: The App Closes This Window And Opens Pairing On NotPaired
    private async void OnUnpairClick(object sender, RoutedEventArgs e)
    {
        UnpairDialog.RequestedTheme = RootGrid.RequestedTheme;
        await UnpairDialog.ConfirmAndUnpairAsync(_settings);
    }

    // Picking A Row Opens It; A Cleared Selection (List Swapped, Row Moved) Keeps The Open Conversation
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationRowViewModel row || row.Id == ViewModel.SelectedConversationId)
        {
            return;
        }

        ViewModel.Select(row.Id);
    }

    // Names Each Row For UI Automation (Row.{id}), And Loads The Next Page Once The Last Row Is Realized
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ConversationRowViewModel row)
        {
            return;
        }

        AutomationProperties.SetAutomationId(args.ItemContainer, "Row." + row.Id);

        if (args.ItemIndex == sender.Items.Count - 1)
        {
            _ = ViewModel.CurrentList.LoadMoreAsync();
        }
    }

    // =========================================================================
    // KEYBOARD SHORTCUTS
    // =========================================================================

    // Every Key Press In The Window Passes Here First. Handled Only When A Shortcut Ran, So Other Keys (And A Shortcut
    // With Nothing To Do, Like "r" With No Chat Open) Reach The Controls. Skipped While A Dialog Or Menu Is Open
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Any(p => p.Child is not ToolTip))
        {
            return;
        }

        bool focus_in_text_box = FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox;
        if (ShortcutMap.Resolve(e.Key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), IsDown(VirtualKey.Menu), focus_in_text_box) is { } command)
        {
            e.Handled = Run(command);
        }
    }

    // Runs A Shortcut; False When It Had Nothing To Act On (Such As A Focus Shortcut For A Pane That Isn't Showing)
    private bool Run(ShortcutCommand command)
    {
        switch (command)
        {
            case ShortcutCommand.StartChat:
                ViewModel.OpenStartChat();

                // A Turn Later, Once The Pane Shows: A Hidden Box Can't Take Focus
                return DispatcherQueue.TryEnqueue(() => StartChatView.FocusQuery());

            case ShortcutCommand.FocusConversationList:
                return ConversationList.Focus(FocusState.Keyboard);

            case ShortcutCommand.FocusMessageBox:
                return ChatView.FocusDraft();

            case ShortcutCommand.ToggleEmoji:
                return ChatView.ToggleEmojiPanel();

            case ShortcutCommand.FocusMessages:
                return ChatView.FocusMessages();

            case ShortcutCommand.ExitConversation:
                ViewModel.ExitConversation();
                return true;

            case ShortcutCommand.NextConversation:
                ViewModel.NextConversation();
                return true;

            case ShortcutCommand.PreviousConversation:
                ViewModel.PreviousConversation();
                return true;

            case ShortcutCommand.OpenSettings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                return true;

            case ShortcutCommand.ShowHelp:
                ShortcutDialog.RequestedTheme = RootGrid.RequestedTheme;
                _ = ShortcutDialog.ShowAsync();
                return true;

            default:
                return false;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}

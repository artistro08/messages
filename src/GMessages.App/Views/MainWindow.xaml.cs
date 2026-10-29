using System.ComponentModel;
using GMessages.App.Views.Controls;
using GMessages.Presentation;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

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

    /// <summary>
    /// Creates the window and starts loading the inbox.
    /// </summary>
    /// <param name="viewModel">The window's state; the window disposes it on close.</param>
    /// <param name="settings">Does the profile menu's Unpair, the same way the settings window does.</param>
    public MainWindow(MainViewModel viewModel, SettingsViewModel settings)
    {
        ViewModel = viewModel;
        _settings = settings;
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
        Activated                 += (_, e) => ViewModel.IsWindowFocused = e.WindowActivationState != WindowActivationState.Deactivated;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Closed                    += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            Bindings.StopTracking();
            ViewModel.Dispose();
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
    /// <param name="selectedConversationId">The open conversation, or null.</param>
    /// <param name="isStartChatOpen">True when Start chat is open.</param>
    /// <returns>The empty state's visibility.</returns>
    public static Visibility EmptyStateVisibility(string? selectedConversationId, bool isStartChatOpen) =>
        selectedConversationId is null && !isStartChatOpen ? Visibility.Visible : Visibility.Collapsed;

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

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => ShowCurrentList();

    // Inbox Or Spam & Blocked: Wrapped For XAML Only When The List Itself Changes, So Other Changes Keep The Selection
    // (See BindableRows For Why The Rows Aren't Bound Directly)
    private void ShowCurrentList()
    {
        if (ConversationList.ItemsSource is BindableRows shown && shown.Rows == ViewModel.CurrentList.Rows)
        {
            return;
        }

        ConversationList.ItemsSource = new BindableRows(ViewModel.CurrentList.Rows);
    }

    // The Adaptive Trigger Picked A Layout; The View Model Needs It For The One-Pane Rule
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool is_narrow = e.NewSize.Width < NarrowBelow;
        if (is_narrow != ViewModel.IsNarrow)
        {
            ViewModel.IsNarrow = is_narrow;
        }
    }

    private void OnMenuClick(object sender, RoutedEventArgs e) => ViewModel.ToggleList();

    private void OnStartChatClick(object sender, RoutedEventArgs e) => ViewModel.OpenStartChat();

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
}

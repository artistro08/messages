using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using GMessages.App.Views.Controls;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Settings;
using GMessages.Presentation.Shortcuts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// The chats, shown in the main window below the title bar once paired: the conversation list (or Spam &amp; blocked)
/// and the chat pane.
/// </summary>
/// <remarks>
/// <para>
/// Made by the main window once paired and dropped when the window closes or goes back to pairing. Dropping it calls
/// <see cref="Close"/>, which disposes the <see cref="MainViewModel"/> and so unhooks its lists from the store that
/// outlives the window.
/// </para>
/// <para>
/// Wide views (720 effective px and up) show both panes; narrow ones show one at a time, following
/// <see cref="MainViewModel.ShowChatPane"/>. ☰ collapses the list in both. The width is read from <c>SizeChanged</c>,
/// not an <c>AdaptiveTrigger</c> (it was moved here from the window's root, where visual state triggers never run), so
/// the panes are laid out with <c>x:Bind</c> on <see cref="MainViewModel.IsNarrow"/>.
/// </para>
/// <para>
/// The list is the pane of an inline <c>SplitView</c>, so opening and closing it plays the Windows pane animation. A
/// narrow view makes the pane as wide as the view, so closing it (a chat opened) slides the chat in over the whole
/// width. The chat sits on an island above the Mica, rounded, with the same gap on its left (beside the list, or the
/// window edge once the list is collapsed), right and bottom.
/// </para>
/// <para>
/// Start chat floats over the inbox's bottom right. Scrolling the list down slides it away; scrolling up (or reaching
/// the top) brings it back (<see cref="ScrollHider"/> decides, ignoring small nudges).
/// </para>
/// <para>
/// In a wide window the edge between the list and the island (<see cref="PaneSplitter"/>) resizes the list, from a bit
/// narrower than its default up to half the window. The width is saved in the settings, so it comes back in the next
/// window and after a restart.
/// </para>
/// </remarks>
public sealed partial class ChatsView : UserControl
{
    // Layout: Below This Width (Effective Pixels) One Pane Shows At A Time; The List Pane's Default Width Beside The
    // Chat And The Narrowest It Can Be Dragged; How Far Start Chat Slides Down To Hide, And Its Shadow's Lift; How Far
    // The List Must Scroll Against The Current Direction To Hide Or Show Start Chat
    private const double NarrowBelow      = 720;
    private const double ListPaneWidth    = 360;
    private const double MinListPaneWidth = 300;
    private const float StartChatDrop     = 96;
    private const float StartChatLift     = 16;
    private const double StartChatNudge   = 24;

    private readonly SettingsViewModel _settings;
    private readonly ScrollHider _startChatHider = new(StartChatNudge);

    // The List's Scroller, Found Once The List Has Its Template
    private ScrollViewer? _listScroller;

    // The Rows On Show, Watched So A Moved Open Row Gets Its Highlight Back
    private ObservableCollection<ConversationRowViewModel>? _shownRows;

    // A Navigation Key In The List Is Moving Its Selection (See OnListPreviewKeyDown)
    private bool _selectingWithKeys;

    // The List's Width Beside The Chat (Saved Or Default), And What It Was When A Resize Started
    private double _listWidth;
    private double? _widthAtResize;

    /// <summary>
    /// Creates the view and starts loading the inbox.
    /// </summary>
    /// <param name="viewModel">The chats' state; <see cref="Close"/> disposes it.</param>
    /// <param name="settings">Where the list's dragged width is kept (owned by the window).</param>
    public ChatsView(MainViewModel viewModel, SettingsViewModel settings)
    {
        ViewModel  = viewModel;
        _settings  = settings;
        _listWidth = settings.ListWidth ?? ListPaneWidth;
        InitializeComponent();

        // Copy
        StartChatText.Text  = ChatCopy.StartChat;
        ListTitle.Text      = ChatCopy.SpamAndBlocked;
        EmptyStateText.Text = ChatCopy.StartAConversation;

        // Behavior
        ViewModel.PropertyChanged += OnViewModelChanged;
        ConversationList.AddHandler(KeyDownEvent, new KeyEventHandler(OnListKeyDown), handledEventsToo: true);
        ConversationList.PreviewKeyDown += OnListPreviewKeyDown;
        ShowCurrentList();
        _ = ViewModel.Inbox.LoadAsync();
    }

    /// <summary>
    /// Gets the chats' state the view binds to.
    /// </summary>
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Stops the bindings and disposes the view model. Call when the window drops the view.
    /// </summary>
    public void Close()
    {
        if (_listScroller is not null)
        {
            _listScroller.ViewChanged -= OnListScrolled;
        }

        if (_shownRows is not null)
        {
            _shownRows.CollectionChanged -= OnRowsChanged;
        }

        ViewModel.PropertyChanged -= OnViewModelChanged;
        Bindings.StopTracking();
        ViewModel.Dispose();
    }

    // =========================================================================
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Opens the list pane unless ☰ collapsed it or a narrow window shows the chat instead.
    /// </summary>
    /// <param name="isListCollapsed">True when ☰ collapsed the list.</param>
    /// <param name="showChatPane">True when a narrow window shows the chat.</param>
    /// <returns>True to open the pane.</returns>
    public static bool IsListOpen(bool isListCollapsed, bool showChatPane) => !isListCollapsed && !showChatPane;

    /// <summary>
    /// Shows the list's resize edge in a wide window while the list is open.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <param name="isListCollapsed">True when the menu button collapsed the list.</param>
    /// <returns>The edge's visibility.</returns>
    public static Visibility SplitterVisibility(bool isNarrow, bool isListCollapsed) =>
        !isNarrow && !isListCollapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The gap left of the chat island: with the list open in a wide window, the list's scrollbar lane already makes
    /// most of the gap, so only a thin resize edge follows it; otherwise the island's usual 8 pixel gap.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <param name="isListCollapsed">True when the menu button collapsed the list.</param>
    /// <returns>The gap column's width.</returns>
    public static GridLength GapWidth(bool isNarrow, bool isListCollapsed) =>
        new(!isNarrow && !isListCollapsed ? 4 : 8);

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
    private void ShowCurrentList()
    {
        ObservableCollection<ConversationRowViewModel> rows = ViewModel.CurrentList.Rows;
        BindableList.Show(ConversationList, rows);
        if (rows == _shownRows)
        {
            return;
        }

        if (_shownRows is not null)
        {
            _shownRows.CollectionChanged -= OnRowsChanged;
        }

        _shownRows                    = rows;
        _shownRows.CollectionChanged += OnRowsChanged;
    }

    // The List Highlights The Open Conversation: Picking A Row Opens It, And This Follows Opens From Elsewhere
    // (Ctrl + . And Ctrl + ,, Start Chat, Escape), Scrolling The Row Into View
    private void ShowSelection() => SelectOpenRow(scroll: true);

    // Selects The Open Conversation's Row (None When It Isn't In The List). Selecting It Opens Nothing: The Row Is
    // Already The Open Chat, So OnSelectionChanged Ignores It (No Second Open, No Second Mark Read)
    private void SelectOpenRow(bool scroll)
    {
        ConversationRowViewModel? row = ViewModel.CurrentList.Rows.FirstOrDefault(r => r.Id == ViewModel.SelectedConversationId);
        if (ConversationList.SelectedItem == row)
        {
            return;
        }

        ConversationList.SelectedItem = row;
        if (scroll && row is not null)
        {
            ConversationList.ScrollIntoView(row);
        }
    }

    // A Row Moved (A New Message Lifts It), Came Or Went: The List Applies A Move As A Remove Plus An Insert, Which Drops
    // The Open Row's Highlight. It Is Put Back A Turn Later, Once The List Has Finished The Change, Without Scrolling, So
    // A List Scrolled Down Isn't Pulled To The Top On Every New Message
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (sender == _shownRows)
            {
                SelectOpenRow(scroll: false);
            }
        });

    // Narrow Or Wide From The Width (See The Class Remarks); The View Model Needs It For The One-Pane Rule, And A Narrow
    // View's List Pane Fills The Width
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool is_narrow       = e.NewSize.Width < NarrowBelow;
        Panes.OpenPaneLength = is_narrow ? e.NewSize.Width : FitListWidth(_listWidth, e.NewSize.Width);
        if (is_narrow != ViewModel.IsNarrow)
        {
            ViewModel.IsNarrow = is_narrow;
        }
    }

    // Dragging (Or Arrowing) The Edge: The List Follows, Kept Between Its Narrowest And Half The Window
    private void OnListResized(object? sender, double distance)
    {
        _widthAtResize     ??= Panes.OpenPaneLength;
        Panes.OpenPaneLength = FitListWidth(_widthAtResize.Value + distance, ActualWidth);
    }

    // The Resize Ended: Keep The Width For The Next Window And The Next Start
    private void OnListResizeCompleted(object? sender, EventArgs e)
    {
        _widthAtResize      = null;
        _listWidth          = Panes.OpenPaneLength;
        _settings.ListWidth = _listWidth;
    }

    private void OnStartChatClick(object sender, RoutedEventArgs e) => ViewModel.OpenStartChat();

    private void OnConversationListLoaded(object sender, RoutedEventArgs e) => WatchListScroller();

    // The List's Scroller Exists Once Its Template Is Applied. Loaded Can Come Before That (A List Still Collapsed), So
    // Each Realized Row Tries Again Until It Is Found
    private void WatchListScroller()
    {
        if (_listScroller is not null || ChatPane.FindScroller(ConversationList) is not { } scroller)
        {
            return;
        }

        _listScroller              = scroller;
        _listScroller.ViewChanged += OnListScrolled;
        _ = ScrollFade.Attach(scroller);
    }

    // Start Chat Hides While Scrolling Down And Shows Again Scrolling Up Or At The Top, Sliding And Fading (The Button's
    // Transitions Animate It). Hidden, It Takes No Clicks Or Tab Stops, And UI Automation Reads "Hidden" As Its Status
    private void OnListScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_listScroller is not { } scroller || !_startChatHider.Scrolled(scroller.VerticalOffset))
        {
            return;
        }

        bool show = _startChatHider.IsShown;

        StartChatButton.Opacity          = show ? 1 : 0;
        StartChatButton.Translation      = new Vector3(0, show ? 0 : StartChatDrop, StartChatLift);
        StartChatButton.IsHitTestVisible = show;
        StartChatButton.IsTabStop        = show;
        AutomationProperties.SetItemStatus(StartChatButton, show ? string.Empty : "Hidden");
    }

    // Start Chat Opened (Or Found) The Conversation: Show It In Place Of Start Chat
    private void OnChatStarted(object? sender, string conversationId) => ViewModel.Select(conversationId);


    // Picking A Row Opens It; A Cleared Selection (List Swapped, Row Moved) Keeps The Open Conversation
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationRowViewModel row || row.Id == ViewModel.SelectedConversationId)
        {
            return;
        }

        // Arrowing Through The List Opens Each Chat But Keeps The Focus On The List; A Click, A Shortcut, A Toast, Start Chat
        // Or A Screen Reader's Select Moves It To The Message Box
        if (_selectingWithKeys)
        {
            _selectingWithKeys = false;
            ChatView.KeepFocusOnNextOpen();
        }

        ViewModel.Select(row.Id);
    }

    // A Navigation Key Pressed In The List (Before The List Moves Its Selection): The Selection Change It Makes Keeps The
    // Focus On The List. The Mark Is Dropped Once The Key Is Done (A Turn Later), So A Key That Moved Nothing (The End Of
    // The List) Leaves No Mark For The Next Click
    private void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown
            or VirtualKey.GamepadDPadUp or VirtualKey.GamepadDPadDown))
        {
            return;
        }

        _selectingWithKeys = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _selectingWithKeys = false);
    }

    // Enter On A Row Moves To The Open Chat's Message Box (The Arrow Keys Opened It But Left The Focus Here). Caught Even
    // When The List Handled The Key
    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.IsChatOpen || !IsBrowsingWithKeys())
        {
            return;
        }

        e.Handled = ChatView.FocusDraft();
    }

    // A Row Of The List Has Keyboard Focus (Arrow Keys, Tab), Not Pointer Or Programmatic Focus. Matched As A Row Of The
    // List And Read Through A WinRT Cast, Not A Type Test (See ChatPane.FindPart)
    private bool IsBrowsingWithKeys() =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused
        && ConversationList.IndexFromContainer(focused.As<DependencyObject>()) >= 0
        && focused.As<Control>().FocusState == FocusState.Keyboard;

    // Names Each Row For UI Automation (Row.{id}), And Loads The Next Page Once The Last Row Is Realized
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ConversationRowViewModel row)
        {
            return;
        }

        AutomationProperties.SetAutomationId(args.ItemContainer, "Row." + row.Id);
        WatchListScroller();

        if (args.ItemIndex == sender.Items.Count - 1)
        {
            _ = ViewModel.CurrentList.LoadMoreAsync();
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // A List Width That Fits: No Narrower Than Its Minimum, No Wider Than Half The View (Unless That Is Below The Minimum)
    private static double FitListWidth(double width, double viewWidth) =>
        Math.Clamp(width, MinListPaneWidth, Math.Max(MinListPaneWidth, viewWidth / 2));

    // =========================================================================
    // KEYBOARD SHORTCUTS
    // =========================================================================

    /// <summary>
    /// Runs a chats shortcut (the window runs Settings and Help itself).
    /// </summary>
    /// <param name="command">The shortcut.</param>
    /// <returns>False when it had nothing to act on (such as a focus shortcut for a pane that isn't showing).</returns>
    public bool Run(ShortcutCommand command)
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

            // Only On A Focused Bubble Row; Anywhere Else The Key Goes On To The Controls
            case ShortcutCommand.OpenReactions:
                return ChatView.OpenReactionsForFocused();

            case ShortcutCommand.ReplyToMessage:
                return ChatView.ReplyToFocused();

            // The Message Box Goes With The Chat, So Focus Goes Back To The List (Not Lost)
            case ShortcutCommand.ExitConversation:
                ViewModel.ExitConversation();
                DispatcherQueue.TryEnqueue(() => ConversationList.Focus(FocusState.Programmatic));
                return true;

            case ShortcutCommand.NextConversation:
                ViewModel.NextConversation();
                return true;

            case ShortcutCommand.PreviousConversation:
                ViewModel.PreviousConversation();
                return true;

            default:
                return false;
        }
    }
}

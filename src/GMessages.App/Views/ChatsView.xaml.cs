using System.ComponentModel;
using System.Numerics;
using GMessages.App.Views.Controls;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Shortcuts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

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
/// width. The chat sits on an island above the Mica, rounded, with a gap to the window's right and bottom edges.
/// </para>
/// <para>
/// Start chat floats over the inbox's bottom right. Scrolling the list down slides it away; scrolling up (or reaching
/// the top) brings it back.
/// </para>
/// </remarks>
public sealed partial class ChatsView : UserControl
{
    // Layout: Below This Width (Effective Pixels) One Pane Shows At A Time; The List Pane's Width Beside The Chat; The
    // Gap Around The Chat Island; How Far Start Chat Slides Down To Hide, And Its Shadow's Lift
    private const double NarrowBelow   = 720;
    private const double ListPaneWidth = 360;
    private const double IslandGap     = 8;
    private const float StartChatDrop  = 96;
    private const float StartChatLift  = 16;

    // The List's Scroller (Found Once The List Has Its Template) And Where It Was Last, For The Scroll Direction
    private ScrollViewer? _listScroller;
    private double _lastListOffset;

    /// <summary>
    /// Creates the view and starts loading the inbox.
    /// </summary>
    /// <param name="viewModel">The chats' state; <see cref="Close"/> disposes it.</param>
    public ChatsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Copy
        StartChatText.Text  = ChatCopy.StartChat;
        ListTitle.Text      = ChatCopy.SpamAndBlocked;
        EmptyStateText.Text = ChatCopy.StartAConversation;

        // Behavior
        ViewModel.PropertyChanged += OnViewModelChanged;
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
    /// Gives the chat island's gap: clear of the window's right and bottom edges, and of the left edge too in a narrow
    /// window, where no list sits beside it.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <returns>The margin.</returns>
    public static Thickness LayerMargin(bool isNarrow) => new(isNarrow ? IslandGap : 0, 0, IslandGap, IslandGap);

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

    // Narrow Or Wide From The Width (See The Class Remarks); The View Model Needs It For The One-Pane Rule, And A Narrow
    // View's List Pane Fills The Width
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool is_narrow       = e.NewSize.Width < NarrowBelow;
        Panes.OpenPaneLength = is_narrow ? e.NewSize.Width : ListPaneWidth;
        if (is_narrow != ViewModel.IsNarrow)
        {
            ViewModel.IsNarrow = is_narrow;
        }
    }

    private void OnStartChatClick(object sender, RoutedEventArgs e) => ViewModel.OpenStartChat();

    // The List's Scroller Exists Once Its Template Is Applied
    private void OnConversationListLoaded(object sender, RoutedEventArgs e)
    {
        if (_listScroller is not null || ChatPane.FindScroller(ConversationList) is not { } scroller)
        {
            return;
        }

        _listScroller              = scroller;
        _listScroller.ViewChanged += OnListScrolled;
    }

    // Start Chat Hides While Scrolling Down And Shows Again Scrolling Up Or At The Top, Sliding And Fading (The Button's
    // Transitions Animate It). Hidden, It Takes No Clicks Or Tab Stops, And UI Automation Reads "Hidden" As Its Status
    private void OnListScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_listScroller is not { } scroller || scroller.VerticalOffset == _lastListOffset)
        {
            return;
        }

        bool show       = scroller.VerticalOffset < _lastListOffset || scroller.VerticalOffset <= 0;
        _lastListOffset = scroller.VerticalOffset;

        StartChatButton.Opacity          = show ? 1 : 0;
        StartChatButton.Translation      = new Vector3(0, show ? 0 : StartChatDrop, StartChatLift);
        StartChatButton.IsHitTestVisible = show;
        StartChatButton.IsTabStop        = show;
        AutomationProperties.SetItemStatus(StartChatButton, show ? string.Empty : "Hidden");
    }

    // Start Chat Opened (Or Found) The Conversation: Show It In Place Of Start Chat
    private void OnChatStarted(object? sender, string conversationId) => ViewModel.Select(conversationId);

    // The Back Arrow Of A Narrow Window Closes The Chat Or Start Chat, Which Shows The List
    private void OnPaneBack(object? sender, EventArgs e) => ViewModel.ExitConversation();

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

            case ShortcutCommand.ExitConversation:
                ViewModel.ExitConversation();
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

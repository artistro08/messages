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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// The chats, shown in the main window below the title bar once paired: the conversation list (or Archived, or Spam
/// &amp; blocked) and the chat pane.
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
/// <para>
/// A row's menu (archive, mute, block &amp; report spam, move to trash) opens on right-click, the Application key or
/// Shift + F10 on the row, and from the ⋮ the row shows on hover (<see cref="ConversationMenuFlyout"/>). The open chat's
/// header has the same menu with Details first (<see cref="ConversationDetailsDialog"/>); Ctrl + Shift + o, Ctrl + d
/// and Ctrl + Alt + h run its Details, Move to trash and Archive. A failed action shows the banner above the list.
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

    // Reads The Windows Animation Setting Live (Settings > Accessibility > Visual Effects)
    private static readonly UISettings Ui = new();

    // The Rows On Show, Watched So A Moved Open Row Gets Its Highlight Back; Their List, Watched For Live Changes And
    // New Messages (The List Motion And The Row Flash)
    private ObservableCollection<ConversationRowViewModel>? _shownRows;
    private ConversationListViewModel? _shownList;

    // The List's Add, Remove And Reorder Motion Is On (Only For A Live Change)
    private bool _animatingRows;

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
        EmptyStateText.Text = ChatCopy.StartAConversation;

        // Behavior
        ViewModel.PropertyChanged += OnViewModelChanged;
        ConversationList.AddHandler(KeyDownEvent, new KeyEventHandler(OnListKeyDown), handledEventsToo: true);
        ConversationList.PreviewKeyDown += OnListPreviewKeyDown;
        ChatView.MenuRequested          += OnChatMenuRequested;
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

        if (_shownList is not null)
        {
            _shownList.RowsChanging -= OnRowsChanging;
            _shownList.RowFlashed   -= OnRowFlashed;
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

    /// <summary>
    /// The folder list's title (shown only in a folder; the inbox has none).
    /// </summary>
    /// <param name="isArchivedView">True while Archived shows.</param>
    /// <returns>Archived, else Spam &amp; blocked.</returns>
    public static string FolderTitle(bool isArchivedView) => isArchivedView ? ChatCopy.Archived : ChatCopy.SpamAndBlocked;

    /// <summary>Gets the battery banner's title (for bindings).</summary>
    public static string BatteryTitle => ChatCopy.PhoneBatteryLow;

    /// <summary>Gets the Undo bar's button text (for bindings).</summary>
    public static string UndoText => ChatCopy.Undo;

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
        ConversationListViewModel list                      = ViewModel.CurrentList;
        ObservableCollection<ConversationRowViewModel> rows = list.Rows;

        // A Folder Switch Shows Its Rows Without Motion
        if (rows != _shownRows)
        {
            AnimateRows(false);
        }

        BindableList.Show(ConversationList, rows);
        if (rows == _shownRows)
        {
            return;
        }

        if (_shownRows is not null)
        {
            _shownRows.CollectionChanged -= OnRowsChanged;
        }

        if (_shownList is not null)
        {
            _shownList.RowsChanging -= OnRowsChanging;
            _shownList.RowFlashed   -= OnRowFlashed;
        }

        _shownRows                    = rows;
        _shownRows.CollectionChanged += OnRowsChanged;
        _shownList                    = list;
        _shownList.RowsChanging      += OnRowsChanging;
        _shownList.RowFlashed        += OnRowFlashed;
    }

    // The Rows Are About To Change: Windows' List Motion Only For A Live Change (A New Message Lifts A Row, A
    // Conversation Arrives Or Leaves), And Only With Animation Effects On; Loads And Pages Land In Place
    private void OnRowsChanging(object? sender, bool live) => AnimateRows(live && Ui.AnimationsEnabled);

    // Turns The List's Motion On Or Off: The Inserted Row Slides And Fades In Where It Lands (The Lifted Row At The
    // Top), A Removed One Fades Out, And The Rows Between Slide Aside. The Collection Is Changed In Place (New, Then
    // Add; Never A Collection Expression: AOT), So The List's Panel Always Sees The Current Set
    private void AnimateRows(bool animate)
    {
        if (animate == _animatingRows)
        {
            return;
        }

        _animatingRows                   = animate;
        TransitionCollection transitions = ConversationList.ItemContainerTransitions;
        transitions.Clear();
        if (animate)
        {
            transitions.Add(new AddDeleteThemeTransition());
            transitions.Add(new ReorderThemeTransition());
        }
    }

    // A New Message, Sent Or Received: Flash Its Row. On Top Already, It Flashes At Once; Lifted, Its Row Is Shown Again
    // At The Next Layout, So It Flashes Then. A Row Out Of View Isn't Shown, So It Doesn't Flash
    private void OnRowFlashed(object? sender, ConversationRowViewModel row)
    {
        if (!Ui.AnimationsEnabled || FlashRowView(row))
        {
            return;
        }

        void OnLayoutUpdated(object? s, object e)
        {
            ConversationList.LayoutUpdated -= OnLayoutUpdated;
            _ = FlashRowView(row);
        }

        ConversationList.LayoutUpdated += OnLayoutUpdated;
    }

    // Flashes A Row Shown In The List, Found By Its Name In Its Container (AOT Safe; See FindPart)
    private bool FlashRowView(ConversationRowViewModel row)
    {
        if (ConversationList.ContainerFromItem(row) is not { } container
            || ChatPane.FindPart<ConversationRow>(container, "RowView") is not { } view)
        {
            return false;
        }

        view.Flash();
        return true;
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

    private void OnActionErrorClosed(InfoBar sender, object args) => ViewModel.DismissActionError();

    // The Undo Bar: Undo Puts The Conversation Back; Its Close Button Hides It
    private void OnUndoClick(object sender, RoutedEventArgs e) => _ = ViewModel.UndoAsync();

    private void OnUndoClosed(InfoBar sender, object args) => ViewModel.DismissUndo();

    // The Bar Never Takes The Focus: When The Archived Row Had It, Windows Hands It On To The Next Tab Stop, Undo, As A
    // Move From The Last Input Device (The Mouse After The Row's Menu). Any Move That Isn't The Keyboard's (Tab, Arrows) Or
    // A Gamepad's Goes To The Selected Row, Or The First, Instead; A Click On Undo Still Runs It
    private void OnUndoGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (args.InputDevice is FocusInputDeviceKind.Keyboard or FocusInputDeviceKind.GameController)
        {
            return;
        }

        if (ConversationList.ContainerFromIndex(Math.Max(0, ConversationList.SelectedIndex)) is not { } row || !args.TrySetNewFocusedElement(row))
        {
            args.TryCancel();
        }
    }

    // The Battery Banner's Own Close Button Dismisses It; The View Model Closing It On Recovery Doesn't
    private void OnBatteryLowClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            ViewModel.DismissPhoneBattery();
        }
    }

    // A Row's ⋮: Its Menu Opens Below The Button. The Sender Is The Row That Raised It (A Plain .NET Event, Not Read From
    // The Visual Tree)
    private void OnRowMoreRequested(object? sender, EventArgs e)
    {
        if (sender is not ConversationRow { Row: { } row } view)
        {
            return;
        }

        ShowConversationMenu(row.Id, view.MoreAnchor, position: null, from: view);
    }

    // Right-Click On A Row Opens Its Menu At The Pointer; The Application Key Or Shift + F10 On The Focused Row Opens It
    // On The Row. The Row Is Found By Walking Up From The Source To The List's Container, Matched By Index And Never By
    // A Type Test (See ChatPane.FindPart)
    private void OnConversationListContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is null || RowContainer(e.OriginalSource.As<DependencyObject>()) is not { } container
            || ConversationList.ItemFromContainer(container) is not ConversationRowViewModel row)
        {
            return;
        }

        FrameworkElement target = container.As<FrameworkElement>();
        e.Handled               = true;
        ShowConversationMenu(row.Id, target, e.TryGetPosition(target, out Windows.Foundation.Point at) ? at : null, from: null);
    }

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

    // The List's Row Container Holding An Element (The Element Itself When It Is One), Or Null Outside The Rows
    private DependencyObject? RowContainer(DependencyObject? element)
    {
        while (element is not null && ConversationList.IndexFromContainer(element) < 0)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element;
    }

    // The Chat Header's ⋮ Or Right-Click: The Open Chat's Menu, With Details
    private void OnChatMenuRequested(object? sender, (FrameworkElement Target, Windows.Foundation.Point? Position) e)
    {
        if (ViewModel.Chat is { } chat)
        {
            ShowConversationMenu(chat.ConversationId, e.Target, e.Position, from: null, inHeader: true);
        }
    }

    // Opens A Conversation's Menu, Read Fresh From The Store So It Matches The Conversation Now (Archived, Muted). Opened
    // From A Row's ⋮, That ⋮ Stays Shown Until The Menu Closes
    private void ShowConversationMenu(string conversationId, FrameworkElement target, Windows.Foundation.Point? position, ConversationRow? from, bool inHeader = false)
    {
        if (ViewModel.FindConversation(conversationId) is not { } c || XamlRoot is not { } root)
        {
            return;
        }

        MenuFlyout menu = ConversationMenuFlyout.Build(ConversationMenu.For(c, inHeader), item => RunMenuItem(root, c, item));
        if (from is not null)
        {
            from.KeepShown(true);
            menu.Closed += (_, _) => from.KeepShown(false);
        }

        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    // Async Void Like A Click Handler, So A Failure Reaches The App's Unhandled Error Log (The Action's Own Failures Show
    // The Banner). An Action That Closed The Open Chat Takes The Message Box With It, So Focus Goes Back To The List As
    // Escape Does
    private async void RunMenuItem(XamlRoot root, ConversationInfo c, ConversationMenuItem item)
    {
        bool was_open = ViewModel.SelectedConversationId == c.Id;
        await ConversationMenuFlyout.RunAsync(ViewModel, root, c, item, () => ShowDetailsAsync(root, c.Id));

        if (was_open && !ViewModel.IsChatOpen)
        {
            DispatcherQueue.TryEnqueue(() => ConversationList.Focus(FocusState.Programmatic));
        }
    }

    // Details, Read Fresh From The Store (One Dialog At A Time, As The Menu's). Its Block & Report Spam Closes It, Then
    // Asks As The Menu Does
    private async Task ShowDetailsAsync(XamlRoot root, string conversationId)
    {
        if (ViewModel.FindConversation(conversationId) is not { } c)
        {
            return;
        }

        var dialog = new ConversationDetailsDialog(ViewModel, c)
        {
            XamlRoot       = root,
            RequestedTheme = root.Content.As<FrameworkElement>().ActualTheme,
        };
        await ConversationMenuFlyout.ShowAsync(dialog);

        if (dialog.IsBlockPicked && ViewModel.FindConversation(conversationId) is { } now)
        {
            await ConversationMenuFlyout.RunAsync(ViewModel, root, now, ConversationMenuItem.BlockAndReport, showDetails: null);
        }
    }

    // A Conversation Shortcut On The Open Chat, Read Fresh From The Store; Not Handled With No Chat Open, Or When The
    // Header's Menu Doesn't Offer The Item (Such As Archive Outside The Inbox)
    private bool RunOnOpenChat(ConversationMenuItem item)
    {
        if (!ViewModel.IsChatOpen || ViewModel.Chat is not { } chat || ViewModel.FindConversation(chat.ConversationId) is not { } c
            || XamlRoot is not { } root || !ConversationMenu.For(c, inHeader: true).Contains(item))
        {
            return false;
        }

        RunMenuItem(root, c, item);
        return true;
    }

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

            // The File Picker Over The Open Chat: Any Files, Or Photos Only
            case ShortcutCommand.AttachFiles:
                return ChatView.OpenAttachPicker(photos: false);

            case ShortcutCommand.AttachPhotos:
                return ChatView.OpenAttachPicker(photos: true);

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

            // The Open Chat's Menu Items: Details, Move To Trash (Asks First) And Archive
            case ShortcutCommand.ShowDetails:
                return RunOnOpenChat(ConversationMenuItem.Details);

            case ShortcutCommand.DeleteConversation:
                return RunOnOpenChat(ConversationMenuItem.MoveToTrash);

            case ShortcutCommand.ArchiveConversation:
                return RunOnOpenChat(ConversationMenuItem.Archive);

            default:
                return false;
        }
    }
}

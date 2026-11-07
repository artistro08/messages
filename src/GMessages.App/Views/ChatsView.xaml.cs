using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using GMessages.App.Views.Controls;
using GMessages.App.Windowing;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Settings;
using GMessages.Presentation.Shortcuts;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using WinRT;
using Muxc = Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views;

/// <summary>
/// Shows the chats in the main window below the title bar after pairing: the conversation list and the chat pane. The
/// list shows the inbox, Archived or Spam &amp; blocked.
/// </summary>
/// <remarks>
/// <para>
/// The main window makes the view after pairing. It drops the view when the window closes or goes back to pairing.
/// The drop calls <see cref="Close"/>, which disposes the <see cref="MainViewModel"/>. That unhooks its lists from the
/// store that outlives the window.
/// </para>
/// <para>
/// A wide view of 720 effective px or more shows both panes. A narrow view shows 1 pane at a time and follows
/// <see cref="MainViewModel.ShowChatPane"/>. ☰ collapses the list in both. The view reads the width from
/// <c>SizeChanged</c>, not from an <c>AdaptiveTrigger</c>. The width logic came here from the root of the window,
/// where visual state triggers never run. Thus the panes use <c>x:Bind</c> on <see cref="MainViewModel.IsNarrow"/>.
/// </para>
/// <para>
/// The list is the pane of an inline <c>SplitView</c>, so an open or a close plays the Windows pane animation. A narrow
/// view makes the pane as wide as the view. Thus a close for an opened chat slides the chat in over the whole width.
/// The chat sits on a rounded island above the Mica. The island has the same gap on its left, right and bottom. The
/// left gap is beside the list, or beside the window edge when the list is collapsed.
/// </para>
/// <para>
/// Start chat floats over the bottom right of the inbox. A scroll down slides it away. A scroll up or a return to the
/// top brings it back. <see cref="ScrollHider"/> decides and ignores small nudges.
/// </para>
/// <para>
/// In a wide window the edge between the list and the island resizes the list. See <see cref="PaneSplitter"/>. The
/// width goes from a bit narrower than its default up to half the window. The settings keep the width, so it comes
/// back in the next window and after a restart.
/// </para>
/// <para>
/// The menu of a row offers archive, mute, block &amp; report spam and move to trash. See
/// <see cref="ConversationMenuFlyout"/>. A right-click, the Application key or Shift + F10 on the row opens it. The ⋮
/// that the row shows on hover also opens it. The header of the open chat has the same menu with Details first. See
/// <see cref="ConversationDetailsDialog"/>. Ctrl + Shift + o, Ctrl + d and Ctrl + Alt + h run its Details, Move to
/// trash and Archive. A failed action shows the banner above the list.
/// </para>
/// <para>
/// With "Show pinned chats as a grid" on in Settings, the pinned conversations of the inbox show as tiles. The tiles
/// sit in the header of the list instead of as rows. See <see cref="ConversationListViewModel.Pinned"/>. A tile opens,
/// highlights and has its menu as a row does. A flip of the setting moves the conversations at once.
/// </para>
/// </remarks>
public sealed partial class ChatsView : UserControl
{
    // Below NarrowBelow, in effective pixels, only 1 pane shows at a time. ListPaneWidth is the default width of the
    // list beside the chat, and MinListPaneWidth the narrowest drag. StartChatDrop is how far Start chat slides down to
    // hide. StartChatLift is the lift of its shadow. StartChatGap is the gap to the Undo bar above it. It matches the
    // spacing of the bottom row in the XAML. StartChatNudge is how far the list must scroll against the current
    // direction to hide or show Start chat. PinnedTileWidth is the narrowest column of the pinned grid before it takes
    // 1 more column.
    private const double NarrowBelow       = 720;
    private const double ListPaneWidth     = 360;
    internal const double MinListPaneWidth = 300;
    private const float StartChatDrop      = 96;
    private const float StartChatLift      = 16;
    private const float StartChatGap       = 8;
    private const double StartChatNudge    = 24;
    private const double PinnedTileWidth   = 120;

    // The time that the selection pill of a tile takes to grow in or shrink away. It matches
    // ControlFastAnimationDuration of Windows.
    private static readonly TimeSpan PillGrowTime = TimeSpan.FromMilliseconds(167);

    private readonly SettingsViewModel _settings;
    private readonly ScrollHider _startChatHider = new(StartChatNudge);

    // The scroller of the list, found after the list has its template, and its last offset.
    private ScrollViewer? _listScroller;
    private double _listOffset;

    // Reads the animation setting of Windows live: Settings > Accessibility > Visual effects.
    private static readonly UISettings Ui = new();

    // The view watches the shown rows, so a moved open row gets its highlight back. It watches their list for live
    // changes, which get the list motion.
    private ObservableCollection<ConversationRowViewModel>? _shownRows;
    private ConversationListViewModel? _shownList;

    // True while the add, remove and reorder motion of the list is on. Only a live change turns it on.
    private bool _animatingRows;

    // True while a navigation key in the list moves its selection. See OnListPreviewKeyDown.
    private bool _selectingWithKeys;

    // The open chat that the tile pills last showed for. See ShowPinnedPills.
    private string? _pillChatId;

    // The width of the list beside the chat, saved or default, and the width at the start of a resize.
    private double _listWidth;
    private double? _widthAtResize;

    /// <summary>
    /// Creates the view and starts the load of the inbox.
    /// </summary>
    /// <param name="viewModel">The state of the chats. <see cref="Close"/> disposes it.</param>
    /// <param name="settings">The settings that keep the dragged width of the list. The window owns them.</param>
    public ChatsView(MainViewModel viewModel, SettingsViewModel settings)
    {
        ViewModel  = viewModel;
        _settings  = settings;
        _listWidth = settings.ListWidth ?? ListPaneWidth;
        InitializeComponent();

        StartChatText.Text  = ChatCopy.StartChat;
        EmptyStateText.Text = ChatCopy.StartAConversation;

        ViewModel.Inbox.PinnedApart = settings.ShowPinnedGrid;
        _settings.PropertyChanged  += OnSettingsChanged;
        ViewModel.PropertyChanged  += OnViewModelChanged;
        ConversationList.AddHandler(KeyDownEvent, new KeyEventHandler(OnListKeyDown), handledEventsToo: true);
        ConversationList.PreviewKeyDown     += OnListPreviewKeyDown;
        PinnedGrid.ContainerContentChanging += OnPinnedContentChanging;
        ChatView.MenuRequested              += OnChatMenuRequested;
        ChatView.MuteRequested              += (_, _) => RunOnOpenChat(ViewModel.Chat?.IsMuted == true ? ConversationMenuItem.Unmute : ConversationMenuItem.Mute);
        ShowCurrentList();
        _ = ViewModel.Inbox.LoadAsync();
    }

    /// <summary>
    /// Gets the state of the chats that the view binds to.
    /// </summary>
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Stops the playback or recording of the open chat. The window calls this method when it hides.
    /// </summary>
    internal void StopMedia() => ChatView.StopMedia();

    /// <summary>
    /// Stops the bindings and disposes the view model. The window calls this method when it drops the view.
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
            _shownList.RowsChanging             -= OnRowsChanging;
            _shownList.Pinned.CollectionChanged -= OnRowsChanged;
        }

        _settings.PropertyChanged -= OnSettingsChanged;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        Bindings.StopTracking();
        ViewModel.Dispose();
    }

    // =========================================================================
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Opens the list pane, unless ☰ collapsed it or a narrow window shows the chat instead.
    /// </summary>
    /// <param name="isListCollapsed">True when ☰ collapsed the list.</param>
    /// <param name="showChatPane">True when a narrow window shows the chat.</param>
    /// <returns>True to open the pane.</returns>
    public static bool IsListOpen(bool isListCollapsed, bool showChatPane) => !isListCollapsed && !showChatPane;

    /// <summary>
    /// Shows the resize edge of the list in a wide window while the list is open.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <param name="isListCollapsed">True when the menu button collapsed the list.</param>
    /// <returns>The visibility of the edge.</returns>
    public static Visibility SplitterVisibility(bool isNarrow, bool isListCollapsed) =>
        !isNarrow && !isListCollapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the gap left of the chat island. With the list open in a wide window, the right padding of the rows makes
    /// most of the gap. Only a thin resize edge follows it then. Otherwise the island has its usual 8 pixel gap.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <param name="isListCollapsed">True when the menu button collapsed the list.</param>
    /// <returns>The width of the gap column.</returns>
    public static GridLength GapWidth(bool isNarrow, bool isListCollapsed) =>
        new(!isNarrow && !isListCollapsed ? 4 : 8);

    /// <summary>
    /// Shows the chat pane in a wide window. A narrow window shows it when it holds a chat or the list is collapsed.
    /// </summary>
    /// <param name="isNarrow">True for a narrow window.</param>
    /// <param name="showChatPane">True when a narrow window shows the chat.</param>
    /// <param name="isListCollapsed">True when ☰ collapsed the list.</param>
    /// <returns>The visibility of the chat pane.</returns>
    public static Visibility ChatVisibility(bool isNarrow, bool showChatPane, bool isListCollapsed) =>
        !isNarrow || showChatPane || isListCollapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Shows the empty state while no conversation and no Start chat is open.
    /// </summary>
    /// <param name="isChatOpen">True when a conversation is open.</param>
    /// <param name="isStartChatOpen">True when Start chat is open.</param>
    /// <returns>The visibility of the empty state.</returns>
    public static Visibility EmptyStateVisibility(bool isChatOpen, bool isStartChatOpen) =>
        !isChatOpen && !isStartChatOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the title of the folder list. Only a folder shows it, and the inbox has none.
    /// </summary>
    /// <param name="isArchivedView">True while Archived shows.</param>
    /// <returns>Archived, else Spam &amp; blocked.</returns>
    public static string FolderTitle(bool isArchivedView) => isArchivedView ? ChatCopy.Archived : ChatCopy.SpamAndBlocked;

    /// <summary>Gets the title of the battery banner for bindings.</summary>
    public static string BatteryTitle => ChatCopy.PhoneBatteryLow;

    /// <summary>
    /// Gets the padding of the list. The bottom has room for Start chat, and for the Undo bar above it while it shows.
    /// Thus neither covers the last row when the list is at its end.
    /// </summary>
    /// <param name="hasUndo">True while the Undo bar shows.</param>
    /// <returns>The padding.</returns>
    public static Thickness ListPadding(bool hasUndo) => new(0, 0, 0, hasUndo ? 112 : 64);

    /// <summary>Gets the button text of the Undo bar for bindings.</summary>
    public static string UndoText => ChatCopy.Undo;

    /// <summary>Gets the name of the close button of the Undo bar for bindings.</summary>
    public static string CloseText => ChatCopy.Close;

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowCurrentList();
        SelectOpenRow(scroll: true);

        if (e.PropertyName is nameof(MainViewModel.HasUndo) or nameof(MainViewModel.UndoNotice))
        {
            AnnounceUndo();
        }
    }

    // A flip of "Show pinned chats as a grid" in Settings moves the pinned rows at once.
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.ShowPinnedGrid))
        {
            return;
        }

        ViewModel.Inbox.PinnedApart = _settings.ShowPinnedGrid;
        ShowPinnedGrid();
        SelectOpenRow(scroll: false);
    }

    // Narrator reads the notice of the Undo bar as it shows, as a polite live region. The event goes 1 dispatcher turn
    // later, after the bindings show the notice.
    private void AnnounceUndo() => XamlHost.Dispatcher.TryEnqueue(() =>
    {
        if (ViewModel.HasUndo && FrameworkElementAutomationPeer.CreatePeerForElement(UndoNoticeText) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    });

    // Shows the inbox or a folder. The view wraps the rows again only when the list itself changes, so other changes
    // keep the selection. BindableList tells why the rows are not bound directly.
    private void ShowCurrentList()
    {
        ConversationListViewModel list                      = ViewModel.CurrentList;
        ObservableCollection<ConversationRowViewModel> rows = list.Rows;

        // A folder switch shows its rows without motion.
        if (rows != _shownRows)
        {
            AnimateRows(false);
        }

        BindableList.Show(ConversationList, rows);
        BindableList.Show(PinnedGrid, list.Pinned);
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
            _shownList.RowsChanging             -= OnRowsChanging;
            _shownList.Pinned.CollectionChanged -= OnRowsChanged;
        }

        _shownRows                          = rows;
        _shownRows.CollectionChanged        += OnRowsChanged;
        _shownList                           = list;
        _shownList.RowsChanging             += OnRowsChanging;
        _shownList.Pinned.CollectionChanged += OnRowsChanged;
        ShowPinnedGrid();
    }

    // The grid shows only while the shown list keeps pinned rows apart. Only the inbox does, with the setting on.
    private void ShowPinnedGrid()
    {
        PinnedGrid.Visibility = ViewModel.CurrentList.Pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FitPinnedColumns(PinnedGrid.Width);
    }

    // The rows are about to change. The list motion of Windows plays only for a live change, with the animation effects
    // on. A live change is a new message that lifts a row, or a conversation that arrives or leaves. Loads and pages
    // land in place.
    private void OnRowsChanging(object? sender, bool live) => AnimateRows(live && Ui.AnimationsEnabled);

    // Turns the list motion on or off. The inserted row slides and fades in where it lands, and the lifted row lands at
    // the top. A removed row fades out, and the rows between slide aside. The code changes the collection in place, so
    // the panel of the list always sees the current set. It uses new and Add, never a collection expression, for AOT.
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

    // Selects the row of the open conversation, or none when the row is not in the list. A pick of a row opens it.
    // This method follows opens from elsewhere: Ctrl + . and Ctrl + ,, Start chat and Escape. A scroll brings the row
    // into view. The selection opens nothing, because OnSelectionChanged ignores the open chat. Thus there is no second
    // open and no second mark as read.
    private void SelectOpenRow(bool scroll)
    {
        // The grid highlights a pinned tile instead. The grid heads the list, so no scroll goes to a tile.
        PinnedGrid.SelectedItem = ViewModel.CurrentList.Pinned.FirstOrDefault(r => r.Id == ViewModel.SelectedConversationId);
        ShowPinnedPills();

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

    // When the open chat changes, the pill of its tile grows in at the tile bottom from its middle. The pill of the
    // last tile shrinks away. Windows grows the pill of a row at its side in the same way. Tiles that are made or
    // recycled later set their own pill in OnSelectionPillLoaded and OnPinnedContentChanging. Thus nothing runs while
    // the open chat stays the same.
    private void ShowPinnedPills()
    {
        if (ViewModel.SelectedConversationId == _pillChatId)
        {
            return;
        }

        _pillChatId = ViewModel.SelectedConversationId;
        foreach (object item in PinnedGrid.Items)
        {
            if (PinnedGrid.ContainerFromItem(item) is { } container && FindPill(container) is { } pill)
            {
                ShowPill(pill, IsOpen(item), animate: true);
            }
        }
    }

    // The pill of a tile loads with its tile, which can come after the container of the tile. The pill shows the state
    // of its tile at once. It grows from its middle, and the pill is 16 x 3.
    private void OnSelectionPillLoaded(object sender, RoutedEventArgs e)
    {
        FrameworkElement pill = sender.As<FrameworkElement>();
        pill.CenterPoint      = new Vector3(8, 1.5f, 0);
        ShowPill(pill, IsOpen(pill.DataContext), animate: false);
    }

    // Every pill uses this test: true when the chat of the tile is the open chat.
    private bool IsOpen(object? tile) => tile is ConversationRowViewModel row && row.Id == ViewModel.SelectedConversationId;

    // The pill of a tile, found by name, which is AOT safe. See UiHelpers.FindPart. Null before the tile exists.
    private static UIElement? FindPill(DependencyObject container) => UiHelpers.FindPart<UIElement>(container, "SelectionPill");

    // Grows or shrinks 1 pill. The pill snaps with the animations of Windows off, or on a tile just made or recycled.
    // The method leaves a pill alone when it is already right, so an animation in progress goes on.
    private static void ShowPill(UIElement pill, bool open, bool animate)
    {
        float scale = open ? 1 : 0;
        if (pill.Scale.X == scale)
        {
            return;
        }

        pill.ScaleTransition = animate && Ui.AnimationsEnabled ? new Vector3Transition { Duration = PillGrowTime } : null;
        pill.Scale           = new Vector3(scale, 1, 1);
    }

    // A row moved, came or went. A new message lifts a row. The list applies a move as a remove plus an insert, which
    // drops the highlight of the open row. The view puts the highlight back 1 dispatcher turn later, after the list
    // ends the change. It does not scroll, so a new message never pulls a scrolled list to the top. The pinned grid
    // works the same way. It also shows or hides as its first tile comes or its last tile goes.
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        XamlHost.Dispatcher.TryEnqueue(() =>
        {
            if (sender == _shownRows || sender == _shownList?.Pinned)
            {
                ShowPinnedGrid();
                SelectOpenRow(scroll: false);
            }
        });

    // The width sets narrow or wide. See the class remarks. The view model needs it for the rule of 1 pane at a time.
    // The list pane of a narrow view fills the width.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool isNarrow        = e.NewSize.Width < NarrowBelow;
        Panes.OpenPaneLength = isNarrow ? e.NewSize.Width : FitListWidth(_listWidth, e.NewSize.Width);
        if (isNarrow != ViewModel.IsNarrow)
        {
            SkipPaneAnimation(isNarrow);
            ViewModel.IsNarrow = isNarrow;
        }
    }

    // A flip between narrow and wide with an open chat opens or closes the pane. Without this step, the pane
    // animation of Windows slides the chat and the list by the list width while the window edge moves. The pane goes
    // to its next state here without a transition. The SplitView then finds that state in place and plays nothing.
    // ☰ and a pick in a narrow window keep the animation. The state names come from the SplitView template of Windows.
    private void SkipPaneAnimation(bool isNarrow)
    {
        bool open = IsListOpen(ViewModel.IsListCollapsed, isNarrow && (ViewModel.IsChatOpen || ViewModel.IsStartChatOpen));
        _ = VisualStateManager.GoToState(Panes, open ? "OpenInlineLeft" : "Closed", useTransitions: false);
    }

    // A drag or an arrow key on the edge resizes the list, between its narrowest width and half the window.
    private void OnListResized(object? sender, double distance)
    {
        _widthAtResize     ??= Panes.OpenPaneLength;
        Panes.OpenPaneLength = FitListWidth(_widthAtResize.Value + distance, ActualWidth);
    }

    // At the end of a resize, the settings keep the width for the next window and the next start.
    private void OnListResizeCompleted(object? sender, EventArgs e)
    {
        _widthAtResize      = null;
        _listWidth          = Panes.OpenPaneLength;
        _settings.ListWidth = _listWidth;
    }

    private void OnStartChatClick(object sender, RoutedEventArgs e) => ViewModel.OpenStartChat();

    private void OnConversationListLoaded(object sender, RoutedEventArgs e) => WatchListScroller();

    // The scroller of the list exists after the list applies its template. Loaded can come before that while the list
    // is still collapsed. Thus each realized row tries again until the view finds the scroller.
    private void WatchListScroller()
    {
        if (_listScroller is not null || UiHelpers.FindScroller(ConversationList) is not { } scroller)
        {
            return;
        }

        _listScroller              = scroller;
        _listScroller.ViewChanged += OnListScrolled;
        _ = ScrollFade.Attach(scroller);
    }

    // Within a screen of the bottom, the next page loads. It also loads when the last shown row did not ask for it. A
    // row asks only as it draws. Start chat hides during a scroll down. It shows again on a scroll up or at
    // the top, with a slide and a fade. The transitions of its backing animate it. A hidden Start chat takes no clicks
    // or tab stops, and UI Automation reads "Hidden" as its status.
    private void OnListScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_listScroller is not { } scroller)
        {
            return;
        }

        // Only a scroll down by the user asks for a page here. Otherwise the realized last row asks. A list that cannot
        // scroll, or whose rows just moved, also raises this event. The owner reported that it asked for a page with no
        // scroll at all.
        bool scrolledDown = scroller.ScrollableHeight > 0 && scroller.VerticalOffset > _listOffset;
        _listOffset       = scroller.VerticalOffset;
        if (scrolledDown && !ViewModel.CurrentList.IsLoading && scroller.VerticalOffset >= scroller.ScrollableHeight - scroller.ViewportHeight)
        {
            _ = ViewModel.CurrentList.LoadMoreAsync();
        }

        if (!_startChatHider.Scrolled(scroller.VerticalOffset))
        {
            return;
        }

        bool show = _startChatHider.IsShown;

        StartChatHost.Opacity            = show ? 1 : 0;
        StartChatHost.Translation        = new Vector3(0, show ? 0 : StartChatDrop, StartChatLift);
        StartChatHost.IsHitTestVisible   = show;
        StartChatButton.IsTabStop        = show;
        AutomationProperties.SetItemStatus(StartChatButton, show ? string.Empty : "Hidden");

        // The Undo bar above slides down into the place of Start chat, so it never hangs over an empty gap.
        UndoBar.Translation = new Vector3(0, show ? 0 : (float)StartChatHost.ActualHeight + StartChatGap, StartChatLift);
    }

    // Start chat opened or found the conversation, which shows in place of Start chat.
    private void OnChatStarted(object? sender, string conversationId) => ViewModel.Select(conversationId);

    private void OnActionErrorClosed(Muxc.InfoBar sender, object args) => ViewModel.DismissActionError();

    private void OnUndoClick(object sender, RoutedEventArgs e) => _ = ViewModel.UndoAsync();

    private void OnUndoClosed(object sender, RoutedEventArgs e) => ViewModel.DismissUndo();

    // The Undo bar never takes the focus. When the archived row had focus, Windows gives it to the next tab stop, Undo.
    // It counts as a move from the last input device, which is the mouse after the menu of the row. Only the keyboard
    // or a gamepad can move focus to Undo. Other moves go to the selected row or the first row. A click on Undo still
    // runs it.
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

    // Only the close button of the battery banner dismisses it. A close by the view model on recovery does not.
    private void OnBatteryLowClosed(Muxc.InfoBar sender, Muxc.InfoBarClosedEventArgs args)
    {
        if (args.Reason == Muxc.InfoBarCloseReason.CloseButton)
        {
            ViewModel.DismissPhoneBattery();
        }
    }

    // The ⋮ of a row opens its menu below the button. The sender is the row that raised this plain .NET event. The view
    // does not read the row from the visual tree.
    private void OnRowMoreRequested(object? sender, EventArgs e)
    {
        if (sender is not ConversationRow { Row: { } row } view)
        {
            return;
        }

        ShowConversationMenu(row.Id, view.MoreAnchor, position: null, from: view);
    }

    // A right-click on a row opens its menu at the pointer. The Application key or Shift + F10 on the focused row opens
    // it on the row. The view goes up from the source to the container of the list. It matches the container by index,
    // never by a type test. See UiHelpers.FindPart.
    private void OnConversationListContextRequested(UIElement sender, ContextRequestedEventArgs e) => ShowItemMenu(ConversationList, e);

    // A pinned tile works the same way. The grid sits in the header of the list, so the list must not also take the
    // request.
    private void OnPinnedGridContextRequested(UIElement sender, ContextRequestedEventArgs e) => ShowItemMenu(PinnedGrid, e);

    // Opens the menu of the list or grid item under the source, at the pointer or on the item.
    private void ShowItemMenu(ListViewBase list, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is null || ItemContainer(list, e.OriginalSource.As<DependencyObject>()) is not { } container
            || list.ItemFromContainer(container) is not ConversationRowViewModel row)
        {
            return;
        }

        FrameworkElement target = container.As<FrameworkElement>();
        e.Handled               = true;
        ShowConversationMenu(row.Id, target, e.TryGetPosition(target, out Windows.Foundation.Point at) ? at : null, from: null);
    }

    // A pick of a pinned tile opens it, as for a row. The arrow keys keep the focus on the grid.
    private void OnPinnedSelectionChanged(object sender, SelectionChangedEventArgs e) => OpenPicked(PinnedGrid.SelectedItem);

    // The header of the list sizes to its content. Thus the view makes the grid as wide as the rows, inside the padding
    // of the list.
    private void OnConversationListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Thickness padding = ConversationList.Padding;
        Thickness margin  = PinnedGrid.Margin;
        PinnedGrid.Width  = Math.Max(0, e.NewSize.Width - padding.Left - padding.Right - margin.Left - margin.Right);
    }

    // Splits the width of the grid into even columns: 3, or more when each column can be wider than PinnedTileWidth.
    // The view reads the panel through a WinRT cast, not a type test. See UiHelpers.FindPart.
    private void OnPinnedGridSizeChanged(object sender, SizeChangedEventArgs e) => FitPinnedColumns(e.NewSize.Width);

    // This also runs as the grid shows, because its panel can be missing at its last size change.
    private void FitPinnedColumns(double width)
    {
        if (PinnedGrid.ItemsPanelRoot is not { } panel || !(width > 0))
        {
            return;
        }

        int columns                         = Math.Max(3, (int)(width / PinnedTileWidth));
        panel.As<ItemsWrapGrid>().ItemWidth = Math.Floor(width / columns);
    }

    // Names each tile for UI Automation as a row, Row.{id}. A conversation is a tile or a row, never both. The rows of
    // the list hook its scroller and ask for the next page. When every row of the page is pinned, there are no rows.
    // Thus the tiles do that work.
    private void OnPinnedContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ConversationRowViewModel tile)
        {
            return;
        }

        AutomationProperties.SetAutomationId(args.ItemContainer, "Row." + tile.Id);
        if (FindPill(args.ItemContainer) is { } pill)
        {
            ShowPill(pill, IsOpen(tile), animate: false);
        }

        WatchListScroller();

        if (args.ItemIndex == sender.Items.Count - 1 && ViewModel.CurrentList.Rows.Count == 0)
        {
            _ = ViewModel.CurrentList.LoadMoreAsync();
        }
    }

    // A pick of a row opens it. A cleared selection keeps the open conversation, as after a list swap or a row move.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => OpenPicked(ConversationList.SelectedItem);

    // Opens a picked row or tile, unless it is already open.
    private void OpenPicked(object? picked)
    {
        if (picked is not ConversationRowViewModel row || row.Id == ViewModel.SelectedConversationId)
        {
            return;
        }

        // The arrow keys open each chat but keep the focus on the list. A click, a shortcut, a toast, Start chat or a
        // screen reader Select moves the focus to the message box.
        if (_selectingWithKeys)
        {
            _selectingWithKeys = false;
            ChatView.KeepFocusOnNextOpen();
        }

        ViewModel.Select(row.Id);
    }

    // A navigation key in the list or in the pinned grid of its header comes here first. The selection change that the
    // key makes keeps the focus there. The mark goes 1 dispatcher turn later, after the key. Thus a key that moved
    // nothing at the end of the list leaves no mark for the next click.
    private void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End
            or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.GamepadDPadUp or VirtualKey.GamepadDPadDown
            or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight))
        {
            return;
        }

        _selectingWithKeys = true;
        XamlHost.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => _selectingWithKeys = false);
    }

    // Enter on a row moves to the message box of the open chat. The arrow keys opened the chat but left the focus here.
    // The handler also gets the key when the list handled it.
    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.IsChatOpen || !IsBrowsingWithKeys())
        {
            return;
        }

        e.Handled = ChatView.FocusDraft();
    }

    // True when a row of the list has keyboard focus from the arrow keys or Tab. Pointer or programmatic focus does not
    // count. The view matches the row by its index and reads it through a WinRT cast, not a type test. See
    // UiHelpers.FindPart.
    private bool IsBrowsingWithKeys() =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused
        && (ConversationList.IndexFromContainer(focused.As<DependencyObject>()) >= 0
            || PinnedGrid.IndexFromContainer(focused.As<DependencyObject>()) >= 0)
        && focused.As<Control>().FocusState == FocusState.Keyboard;

    // Names each row Row.{id} for UI Automation, and loads the next page when the last row is realized.
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

    // The item container of the list or grid that holds an element. The element itself when it is a container. Null
    // outside the items.
    private static DependencyObject? ItemContainer(ListViewBase list, DependencyObject? element)
    {
        while (element is not null && list.IndexFromContainer(element) < 0)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element;
    }

    // The ⋮ or a right-click on the chat header opens the menu of the open chat, with Details.
    private void OnChatMenuRequested(object? sender, (FrameworkElement Target, Windows.Foundation.Point? Position) e)
    {
        if (ViewModel.Chat is { } chat)
        {
            ShowConversationMenu(chat.ConversationId, e.Target, e.Position, from: null, inHeader: true);
        }
    }

    // Opens the menu of a conversation. The view reads the conversation fresh from the store. Thus the menu matches its
    // current state, such as archived or muted. When the ⋮ of a row opens the menu, that ⋮ stays until the menu closes.
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

        // A ⋮ button of a row or of the chat header opens the menu below it, with the right edges aligned. The button
        // sits by the right edge of the window, and the profile menu opens the same way. A right-click or the keyboard
        // opens the menu at the pointer or on the row.
        FlyoutShowOptions options = position is null && (from is not null || inHeader)
            ? new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight }
            : new FlyoutShowOptions { Position = position };
        menu.ShowAt(target, options);
    }

    // The method is async void as a click handler is. Thus a failure reaches the unhandled error log of the app. The
    // failures of the action itself show the banner. An action that closed the open chat takes the message box with
    // it. An archive, a block or a trash moves the row out of the list. The focus that the menu gave back to the row
    // goes with it. In both cases focus goes back to the list, as Escape does. Focus on the removed row was nowhere:
    // keys did nothing, and UI Automation lost it.
    private async void RunMenuItem(XamlRoot root, ConversationInfo c, ConversationMenuItem item)
    {
        bool wasOpen = ViewModel.SelectedConversationId == c.Id;
        await ConversationMenuFlyout.RunAsync(ViewModel, root, c, item, () => ShowDetailsAsync(root, c.Id));

        XamlHost.Dispatcher.TryEnqueue(() =>
        {
            if ((wasOpen && !ViewModel.IsChatOpen) || FocusIsOnRemovedRow(root, c.Id))
            {
                ConversationList.Focus(FocusState.Programmatic);
            }
        });
    }

    // True when focus sits on the row of a conversation that left the list, or on its ⋮. The search goes up by the
    // AutomationId Row.{id} that OnContainerContentChanging sets, as BubbleViewAround does. It never uses a type test.
    private bool FocusIsOnRemovedRow(XamlRoot root, string conversationId)
    {
        if (ConversationList.Items.Concat(PinnedGrid.Items).Any(i => i is ConversationRowViewModel r && r.Id == conversationId)
            || FocusManager.GetFocusedElement(root) is not { } focused)
        {
            return false;
        }

        string rowId = "Row." + conversationId;
        for (DependencyObject? at = focused.As<DependencyObject>(); at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (AutomationProperties.GetAutomationId(at) == rowId)
            {
                return true;
            }
        }

        return false;
    }

    // Shows Details, read fresh from the store, with 1 dialog at a time as for the menu. Its Block & report spam closes
    // the dialog, then asks as the menu does.
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

    // Runs a conversation shortcut on the open chat, read fresh from the store. The method returns false with no chat
    // open. It also returns false when the header menu does not offer the item, such as Archive outside the inbox.
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

    // A list width that fits: no narrower than its minimum, and no wider than half the view. When half the view is
    // below the minimum, the minimum wins.
    private static double FitListWidth(double width, double viewWidth) =>
        Math.Clamp(width, MinListPaneWidth, Math.Max(MinListPaneWidth, viewWidth / 2));

    // =========================================================================
    // KEYBOARD SHORTCUTS
    // =========================================================================

    /// <summary>
    /// Runs a shortcut of the chats. The window runs Settings and Help itself.
    /// </summary>
    /// <param name="command">The shortcut.</param>
    /// <returns>False when the shortcut had nothing to act on, such as a focus shortcut for a hidden pane.</returns>
    public bool Run(ShortcutCommand command)
    {
        switch (command)
        {
            case ShortcutCommand.StartChat:
                ViewModel.OpenStartChat();

                // The focus goes 1 dispatcher turn later, when the pane shows. A hidden box cannot take focus.
                return XamlHost.Dispatcher.TryEnqueue(() => StartChatView.FocusQuery());

            case ShortcutCommand.FocusConversationList:
                return ConversationList.Focus(FocusState.Keyboard);

            case ShortcutCommand.FocusMessageBox:
                return ChatView.FocusDraft();

            case ShortcutCommand.ToggleEmoji:
                return ChatView.ToggleEmojiPanel();

            case ShortcutCommand.AttachFiles:
                return ChatView.OpenAttachPicker(photos: false);

            case ShortcutCommand.AttachPhotos:
                return ChatView.OpenAttachPicker(photos: true);

            case ShortcutCommand.FocusMessages:
                return ChatView.FocusMessages();

            // These shortcuts act only on a focused bubble row. Anywhere else the key goes on to the controls.
            case ShortcutCommand.OpenReactions:
                return ChatView.OpenReactionsForFocused();

            case ShortcutCommand.ReplyToMessage:
                return ChatView.ReplyToFocused();

            // The message box goes with the chat, so focus goes back to the list and is not lost.
            case ShortcutCommand.ExitConversation:
                ViewModel.ExitConversation();
                XamlHost.Dispatcher.TryEnqueue(() => ConversationList.Focus(FocusState.Programmatic));
                return true;

            case ShortcutCommand.NextConversation:
                ViewModel.NextConversation();
                return true;

            case ShortcutCommand.PreviousConversation:
                ViewModel.PreviousConversation();
                return true;

            // The menu items of the open chat: Details, Move to trash and Archive. Move to trash asks first.
            case ShortcutCommand.ShowDetails:
                return RunOnOpenChat(ConversationMenuItem.Details);

            case ShortcutCommand.DeleteConversation:
                return RunOnOpenChat(ConversationMenuItem.MoveToTrash);

            case ShortcutCommand.ArchiveConversation:
                return RunOnOpenChat(ConversationMenuItem.Archive);

            // Ctrl + S collapses or expands the list as ☰ does. It works only where ☰ shows, in a wide window.
            case ShortcutCommand.ToggleSidebar:
                if (ViewModel.IsNarrow)
                {
                    return false;
                }

                ViewModel.ToggleList();
                return true;

            // Ctrl + Shift + Z puts back the latest archive of this session, with or without the Undo bar.
            case ShortcutCommand.UndoArchive:
                if (!ViewModel.CanUndo)
                {
                    return false;
                }

                _ = ViewModel.UndoAsync();
                return true;

            default:
                return false;
        }
    }
}

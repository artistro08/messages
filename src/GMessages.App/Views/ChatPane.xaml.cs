using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using GMessages.App.Views.Controls;
using GMessages.Presentation.Main;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// The open conversation: the header, the message bubbles and the message box with its emoji and send buttons.
/// </summary>
/// <remarks>
/// <para>
/// Shows whatever <see cref="Chat"/> holds; the main window sets it from <see cref="MainViewModel.Chat"/>. The bubbles
/// are wrapped with <see cref="BindableList"/> (see there for why), and the list stays scrolled to the newest bubble
/// as messages arrive. Scrolling to the top loads the older page; a page too short to scroll loads the next older one
/// on its own, until the list scrolls or no older pages are left.
/// </para>
/// <para>
/// Enter sends the draft and Shift + Enter adds a line. The emoji button opens the Windows emoji panel on the
/// message box.
/// </para>
/// </remarks>
public sealed partial class ChatPane : UserControl
{
    /// <summary>Identifies the <see cref="Chat"/> property.</summary>
    public static readonly DependencyProperty ChatProperty =
        DependencyProperty.Register(nameof(Chat), typeof(ChatViewModel), typeof(ChatPane), new PropertyMetadata(null, OnChatChanged));

    private ScrollViewer? _scroller;

    // Pinned To The Newest Message (See ChatScroll.StaysPinned), And The Content Height Last Seen
    private bool _pinned = true;
    private double _lastExtent;

    // On The Way To The Newest Message (See ScrollToNewest)
    private bool _toNewest;

    // An Older Page Going In Above (See KeepOlderAnchor): How Far The View's Top Sat From The Content's End Before It (Null
    // Once Used), The Bubble At The View's Top Then And Its Top In The View (Null When Done), And The Corrections Left
    private double? _olderFromEnd;
    private MessageBubbleViewModel? _olderBubble;
    private double _olderTop;
    private int _olderTries;

    // The Last Scroll Decisions Logged (Wave 7 Diagnostics), So Only Changes Are Logged
    private string? _lastScrollTrace;

    /// <summary>
    /// Creates the pane.
    /// </summary>
    public ChatPane() => InitializeComponent();

    /// <summary>Gets or sets the open conversation, or null.</summary>
    public ChatViewModel? Chat
    {
        get => (ChatViewModel?)GetValue(ChatProperty);
        set => SetValue(ChatProperty, value);
    }

    /// <summary>
    /// Puts the cursor in the message box (the "r" shortcut).
    /// </summary>
    /// <returns>True when the box took focus (false while no chat shows).</returns>
    public bool FocusDraft() => DraftBox.Focus(FocusState.Keyboard);

    /// <summary>
    /// Moves focus to the message bubbles (the Ctrl + j shortcut).
    /// </summary>
    /// <returns>True when the list took focus (false while no chat shows).</returns>
    public bool FocusMessages() => MessageList.Focus(FocusState.Keyboard);

    /// <summary>
    /// Opens (or, pressed again, closes) the Windows emoji panel on the message box: the emoji button and the
    /// Ctrl + Shift + e shortcut.
    /// </summary>
    /// <remarks>
    /// The panel types into whatever has focus, so the message box is focused first. It is opened with the Win +
    /// Period shortcut: <c>CoreInputView.GetForCurrentView().TryShow(Emoji)</c> needs a CoreWindow, which a WinUI 3
    /// desktop window doesn't have, and showed nothing when tried. The shortcut waits a turn: sent before the box has
    /// taken focus, Windows ignores it.
    /// </remarks>
    /// <returns>True when the panel was asked for (false while no chat shows).</returns>
    public bool ToggleEmojiPanel()
    {
        if (Chat is null || !DraftBox.Focus(FocusState.Programmatic))
        {
            return false;
        }

        return DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SendEmojiShortcut);
    }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // Another Chat Opened: Show Its Bubbles And Follow Their Changes
    private static void OnChatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pane = (ChatPane)d;

        if (e.OldValue is ChatViewModel old_chat)
        {
            old_chat.Messages.CollectionChanged -= pane.OnMessagesChanged;
            old_chat.PropertyChanged            -= pane.OnChatPropertyChanged;
        }

        var chat = (ChatViewModel?)e.NewValue;
        BindableList.Show(pane.MessageList, chat?.Messages);
        pane.ShowScrollToBottom(false);
        pane._lastScrollTrace = null;
        pane._pinned          = true;
        pane._toNewest        = false;
        pane._olderFromEnd    = null;
        pane._olderBubble     = null;

        // The Last Chat's Content Height Means Nothing Here: Unset, The First Look Counts As A Content Change, Not As The
        // Person Scrolling (Two Chats Of The Same Height Would Otherwise Unpin On The Offset Reset To The Top)
        pane._lastExtent      = -1;
        chat?.Trace($"opened in pane (scroller {(pane._scroller is null ? "not yet hooked" : "hooked")})");

        if (chat is not null)
        {
            chat.Messages.CollectionChanged += pane.OnMessagesChanged;
            chat.PropertyChanged            += pane.OnChatPropertyChanged;

            // The Chat Is On Screen, So Its Toasts Are Read
            (Application.Current as App)?.Notifications?.RemoveConversation(chat.ConversationId);
        }
    }

    // A Bubble Added At The Bottom (Sent, Arrived Or The First Page) Comes Into View; Older Pages Go In Above. The List
    // Scrolls To Its End Instead Of ScrollIntoView: On This Bottom-Aligned List, ScrollIntoView Made The Panel Realize
    // Its Rows Again, So Every Bubble Above The New One Replayed Its Entrance (The Whole Chat Seemed To Reload On Send).
    // Already At The End (The Usual Case), KeepPinned Has Done It And Nothing Moves
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // An Older Page's First Bubble: Note Where The View Sits, Before The Page Moves It (Pinned, The Pin Keeps The Newest
        // Bubbles In Place)
        if (e is { Action: NotifyCollectionChangedAction.Add, NewStartingIndex: 0 } && Chat is { IsLoadingOlder: true } chat
            && !_pinned && _olderBubble is null && _scroller is { } anchored)
        {
            NoteOlderAnchor(anchored, chat);
        }

        if (e.Action != NotifyCollectionChangedAction.Add || sender is not ICollection<MessageBubbleViewModel> messages
            || e.NewStartingIndex + e.NewItems!.Count != messages.Count)
        {
            return;
        }

        // After The List Has Measured The New Bubble
        DispatcherQueue.TryEnqueue(() =>
        {
            MessageList.UpdateLayout();
            if (Scroller() is { } scroller && scroller.VerticalOffset < scroller.ScrollableHeight)
            {
                ScrollToNewest(scroller);
            }
        });
    }

    // The Scroller Is Hooked As Soon As The List Has Its Template, So Scrolling Up Loads Older Even Before Any Page Ends
    private void OnMessageListLoaded(object sender, RoutedEventArgs e)
    {
        _ = Scroller();
        FillWithOlder();
    }

    // The List's Own Scroller Tells When The Top Is Reached. Found On First Need: While The Pane Is Still Collapsed At
    // Load, The List Has No Template Yet, So There Is No Scroller To Find
    private ScrollViewer? Scroller()
    {
        if (_scroller is null && FindScroller(MessageList) is { } found)
        {
            _scroller                  = found;
            _scroller.ViewChanged     += OnScrolled;
            MessageList.LayoutUpdated += OnMessageListLayoutUpdated;
            bool faded = ScrollFade.Attach(found) is not null;
            Chat?.Trace($"scroller hooked (edge fades {faded})");
        }

        return _scroller;
    }

    // A Page Finished Loading: Too Short To Scroll Means There Is No Top To Reach, So Load Older Here
    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsLoading) && sender == Chat && Chat is { IsLoading: false })
        {
            DispatcherQueue.TryEnqueue(FillWithOlder);
        }
    }

    // Each Older Page Ends In Another IsLoading Change, So This Repeats While The List Is Too Short To Scroll Or Still
    // Near Its Top, Until No Pages Are Left, A Page Adds Nothing Or A Load Fails (See ChatViewModel.CanFillOlder)
    private void FillWithOlder()
    {
        if (Chat is not { IsLoading: false, CanFillOlder: true } chat)
        {
            return;
        }

        // Measure The New Bubbles Now, So The Scroll Height Is Current
        MessageList.UpdateLayout();
        if (Scroller() is { } scroller && ChatScroll.ShouldLoadOlder(scroller.VerticalOffset, scroller.ScrollableHeight, scroller.ViewportHeight))
        {
            chat.Trace($"fill older at {scroller.VerticalOffset:0} of {scroller.ScrollableHeight:0}, view {scroller.ViewportHeight:0}");
            _ = chat.LoadOlderAsync();
        }
    }

    // Nearing The Top Starts The Older Page (Mid-Scroll Too, So It Is Usually In Before The Top), And The Scroll-To-Bottom
    // Button Follows How Far Up The List Is. LoadOlderAsync Ignores Calls While A Page Loads
    private void OnScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroller is not { } scroller || Chat is null)
        {
            return;
        }

        // The Same Content Height Means The Person (Or The Arrow) Moved The List; A New One Is Bubbles Measuring Or A Page
        bool content_changed = ContentChanged(scroller);
        _pinned              = ChatScroll.StaysPinned(_pinned, content_changed, scroller.VerticalOffset, scroller.ScrollableHeight);
        if (content_changed)
        {
            KeepPinned(scroller);
        }

        // Come To Rest After Scrolling To The Newest Message: Pinned There, And Put Right At It If Rows Measured On The
        // Way Grew The Content Past Where The Scroll Was Headed
        if (_toNewest && !e.IsIntermediate)
        {
            _toNewest = false;
            _pinned   = true;
            KeepPinned(scroller);
        }

        bool show_arrow = ChatScroll.ShowScrollToBottom(scroller.VerticalOffset, scroller.ScrollableHeight, scroller.ViewportHeight);
        bool load_older = scroller.ScrollableHeight > 0 && ChatScroll.ShouldLoadOlder(scroller.VerticalOffset, scroller.ScrollableHeight, scroller.ViewportHeight);

        // TODO: Wave 7 Diagnostics (Numbers Only); Remove Once Older Paging Works On The Real Phone. Logged When A
        // Decision Changes, Not On Every Scroll
        string trace = $"scroll load older {load_older}, arrow {show_arrow}";
        if (trace != _lastScrollTrace)
        {
            _lastScrollTrace = trace;
            Chat.Trace($"{trace} at {scroller.VerticalOffset:0} of {scroller.ScrollableHeight:0}, view {scroller.ViewportHeight:0}, {Chat.Messages.Count} shown");
        }

        ShowScrollToBottom(show_arrow);
        if (load_older)
        {
            _ = Chat.LoadOlderAsync();
        }
    }

    private void OnScrollToBottomClick(object sender, RoutedEventArgs e)
    {
        if (_scroller is { } scroller)
        {
            ScrollToNewest(scroller);
        }
    }

    // Bubbles Measure Their Real Heights After They Show (The List Only Estimates Them First), So A Just-Opened Chat Would
    // Stop A Few Pixels Short Of Its Newest Message, Showing The Bottom Fade. While Pinned, Each Change In Content Height
    // Puts It Back At The Bottom. Scrolling (Same Height) Is Left To OnScrolled
    private void OnMessageListLayoutUpdated(object? sender, object e)
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        KeepOlderAnchor(scroller);
        if (ContentChanged(scroller))
        {
            KeepPinned(scroller);
        }
    }

    // Enter Or Space On A Focused Bubble Shows Or Hides Its Time, Like A Click (Not On Its Retry Link, Which Has Focus Then).
    // The Row Is Matched As A ContentControl Holding A Bubble: In The Native AOT Build It Arrives As A ContentControl, Not
    // A ListViewItem (See FindScroller)
    private void OnMessageListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space) || e.OriginalSource is not ContentControl { Content: MessageBubbleViewModel bubble })
        {
            return;
        }

        e.Handled = true;
        bubble.ToggleTime();
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = Chat?.SendAsync();

    // Enter Sends; Shift + Enter Is Left To The Box, Which Adds A Line
    private void OnDraftKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || IsShiftDown() || Chat is null)
        {
            return;
        }

        e.Handled = true;
        _ = Chat.SendAsync();
    }

    private void OnRetryRequested(object? sender, EventArgs e)
    {
        if (sender is MessageBubble { Bubble: { } bubble } && Chat is not null)
        {
            _ = Chat.RetryAsync(bubble);
        }
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e) => ToggleEmojiPanel();

    // Win + Period, Pressed And Released. Ctrl And Shift Still Held From The Ctrl + Shift + e Shortcut Are Let Go First:
    // Windows Opens Nothing For Win + Ctrl + Shift + Period
    private static void SendEmojiShortcut()
    {
        List<NativeMethods.KEYBOARD_INPUT> keys = [];
        if (IsDown(VirtualKey.Control))
        {
            keys.Add(Key(NativeMethods.VK_CONTROL, NativeMethods.KEYEVENTF_KEYUP));
        }

        if (IsDown(VirtualKey.Shift))
        {
            keys.Add(Key(NativeMethods.VK_SHIFT, NativeMethods.KEYEVENTF_KEYUP));
        }

        keys.Add(Key(NativeMethods.VK_LWIN, 0));
        keys.Add(Key(NativeMethods.VK_OEM_PERIOD, 0));
        keys.Add(Key(NativeMethods.VK_OEM_PERIOD, NativeMethods.KEYEVENTF_KEYUP));
        keys.Add(Key(NativeMethods.VK_LWIN, NativeMethods.KEYEVENTF_KEYUP));

        // A Refused Shortcut (Another App Holds The Input) Just Opens Nothing. Cut Short, Any Key It Pressed But Didn't
        // Release (Win Above All) Is Released, So No Key Is Left Held Down
        int size = Marshal.SizeOf<NativeMethods.KEYBOARD_INPUT>();
        int sent = (int)NativeMethods.SendInput((uint)keys.Count, [.. keys], size);
        if (sent < keys.Count)
        {
            NativeMethods.KEYBOARD_INPUT[] releases = [.. keys.Take(sent)
                .Where(k => (k.dwFlags & NativeMethods.KEYEVENTF_KEYUP) == 0)
                .Select(k => k.wVk)
                .Except(keys.Take(sent).Where(k => (k.dwFlags & NativeMethods.KEYEVENTF_KEYUP) != 0).Select(k => k.wVk))
                .Select(vk => Key(vk, NativeMethods.KEYEVENTF_KEYUP))];
            if (releases.Length > 0)
            {
                _ = NativeMethods.SendInput((uint)releases.Length, releases, size);
            }
        }

        static NativeMethods.KEYBOARD_INPUT Key(ushort virtualKey, uint flags) =>
            new() { type = NativeMethods.INPUT_KEYBOARD, wVk = virtualKey, dwFlags = flags };
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Notes The Content Height; True When It Differs From The Last One Seen
    private bool ContentChanged(ScrollViewer scroller)
    {
        bool changed = Math.Abs(scroller.ExtentHeight - _lastExtent) >= 0.5;
        _lastExtent  = scroller.ExtentHeight;
        return changed;
    }

    // Notes, Before An Older Page Goes In Above, How Far The View's Top Sits From The Content's End And The First Shown
    // Bubble With Its Top In The View (Its Row Is Still Where The Last Layout Put It)
    private void NoteOlderAnchor(ScrollViewer scroller, ChatViewModel chat)
    {
        foreach (MessageBubbleViewModel bubble in chat.Messages.Skip(1))
        {
            if (RowTop(scroller, bubble) is not { } top)
            {
                continue;
            }

            if (top + 1 >= 0)
            {
                _olderFromEnd = scroller.ExtentHeight - scroller.VerticalOffset;
                _olderBubble  = bubble;
                _olderTop     = top;
                _olderTries   = 10;
                return;
            }
        }
    }

    // The Older Page Went In Above: Puts The View Back (No Animation) So The Shown Bubbles Stay Put. The List's Own
    // KeepItemsInView Does Nothing At The Very Top, Where The Page Is Asked For: There The Offset Stayed 0 And The New
    // Page Took The View, Pushing The Shown Bubbles Below It. The First Layout Has Only The New Page's Rows Realized, So
    // The View First Goes Back The Same Distance From The Content's End; Moving Re-Estimates The Heights Of The Rows Not
    // Shown, So Each Layout After Puts The Noted Bubble Back At Its Top Until It Is There (A Few Tries At Most)
    private void KeepOlderAnchor(ScrollViewer scroller)
    {
        if (_olderBubble is not { } bubble || Chat is null || Chat.IsLoadingOlder)
        {
            return;
        }

        if (_olderFromEnd is { } from_end)
        {
            _olderFromEnd = null;
            scroller.ChangeView(null, Math.Max(0, scroller.ExtentHeight - from_end), null, true);
            return;
        }

        double? top = RowTop(scroller, bubble);
        if (top is null || Math.Abs(top.Value - _olderTop) < 0.5 || --_olderTries <= 0)
        {
            _olderBubble = null;
            return;
        }

        scroller.ChangeView(null, scroller.VerticalOffset + top.Value - _olderTop, null, true);
    }

    // A Bubble's Row Top In The View, Or Null While Its Row Isn't Realized
    private double? RowTop(ScrollViewer scroller, MessageBubbleViewModel bubble) =>
        MessageList.ContainerFromItem(bubble)?.As<UIElement>().TransformToVisual(scroller).TransformPoint(default).Y;

    // Back To The Newest Message At Once (No Animation) While Pinned And Short Of It
    private void KeepPinned(ScrollViewer scroller)
    {
        if (_pinned && !ChatScroll.IsAtBottom(scroller.VerticalOffset, scroller.ScrollableHeight))
        {
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
        }
    }

    // Scrolls (Animated) To The Newest Message. The List Only Estimates The Heights Of Rows Not Yet Shown, So The Content
    // Can Grow Past The Scroll's Target While It Runs; OnScrolled Finishes The Job Where It Comes To Rest
    private void ScrollToNewest(ScrollViewer scroller)
    {
        _toNewest = true;
        scroller.ChangeView(null, scroller.ScrollableHeight, null);
    }

    // Fades The Scroll-To-Bottom Button In Or Out (Its OpacityTransition Animates It); Hidden, It Takes No Clicks Or Focus
    private void ShowScrollToBottom(bool show)
    {
        ScrollToBottomButton.Opacity          = show ? 1 : 0;
        ScrollToBottomButton.IsHitTestVisible = show;
        ScrollToBottomButton.IsTabStop        = show;
    }

    private static bool IsShiftDown() => IsDown(VirtualKey.Shift);

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Finds the first <see cref="ScrollViewer"/> under an element (a list's template holds one).
    /// </summary>
    /// <remarks>
    /// In the Native AOT build, the visual tree hands a template's scroller back as a base type (a
    /// <see cref="ContentControl"/>), so a plain <c>as ScrollViewer</c> finds nothing there. The template part is
    /// found by its name instead and cast with a WinRT cast (a COM QueryInterface), which needs no type lookup.
    /// </remarks>
    /// <param name="root">The element to search under.</param>
    /// <returns>The scroller, or null before the element has its template.</returns>
    internal static ScrollViewer? FindScroller(DependencyObject root) => FindPart<ScrollViewer>(root, "ScrollViewer");

    /// <summary>
    /// Finds the first element of a type, or a template part of that name, under an element (AOT safe; see
    /// <see cref="FindScroller"/>).
    /// </summary>
    /// <typeparam name="T">The element's type.</typeparam>
    /// <param name="root">The element to search under.</param>
    /// <param name="partName">The template part's name, such as "ScrollViewer".</param>
    /// <returns>The element, or null when there is none (yet).</returns>
    internal static T? FindPart<T>(DependencyObject root, string partName)
        where T : class
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            T? part = child as T ?? (child is FrameworkElement element && element.Name == partName ? element.As<T>() : null);
            if ((part ?? FindPart<T>(child, partName)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

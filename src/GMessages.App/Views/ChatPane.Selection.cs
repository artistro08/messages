using GMessages.App.Views.Controls;
using GMessages.App.Windowing;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// Selects text with the mouse across the bubbles of the chat, as a page in a browser does. It also toggles the time
/// of a bubble after a click.
/// </summary>
/// <remarks>
/// <para>
/// The text of a bubble does not select by itself for the mouse. This part of <see cref="ChatPane"/> selects. A drag
/// from 1 click selects letters. A drag from a double-click selects whole words, and a drag from a triple-click
/// selects whole messages. The drag continues into the bubbles above and below.
/// </para>
/// <para>
/// The pane keeps the selection as 2 places in the texts of 2 messages, not in the rows of the list. Each row that
/// shows a message in the selection draws its part of the highlight, also a row that scrolls into view later. Ctrl + C
/// and Copy in the bubble menu copy the selection, 1 message to a line. Touch and pen select with the own selection of
/// the text.
/// </para>
/// <para>
/// A click with no drag toggles the time of its bubble when the double-click time of Windows passes with no second
/// click. A toggle at once moved the bubbles, and the second click of a double-click missed the text. ChatPane.xaml.cs
/// holds the other members.
/// </para>
/// </remarks>
public sealed partial class ChatPane
{
    // A drag starts when the pointer moves this far from the press, in pixels. A click that comes again within this
    // distance counts as a double-click.
    private const double DragStart = 4;

    private enum TextUnit
    {
        Letter,
        Word,
        Message,
    }

    // The unit of the drag, and the unit around the press that always stays selected.
    private TextUnit _textUnit;
    private MessageBubbleViewModel? _anchorBubble;
    private int _anchorStart;
    private int _anchorEnd;

    // The first flag is true while the press of the mouse is on text and the button is down. The second flag is true
    // after the pointer moves away from the press.
    private bool _textPressed;
    private bool _textMoved;
    private Point _pressAt;

    // The clicks so far: 1, 2 or 3. Each click within the double-click time of the last one adds 1.
    private int _clicks;
    private ulong _lastClickTime;
    private Point _lastClickAt;

    // The selection: from a place in the shown text of the first message to a place in the shown text of the last
    // message. The messages between them are selected whole.
    private MessageBubbleViewModel? _fromBubble;
    private MessageBubbleViewModel? _toBubble;
    private int _fromAt;
    private int _toAt;

    // The bubbles whose taps wait to toggle the time, and the timer of the wait. The first flag is true when the
    // current click does not toggle the time: a double-click, a triple-click or a drag. The last field is the bubble
    // that the tap of the current click added.
    private readonly List<MessageBubbleViewModel> _tapBubbles = [];
    private DispatcherQueueTimer? _tapTimer;
    private bool _skipTap;
    private MessageBubbleViewModel? _clickTap;

    private void HookTextSelection()
    {
        MessageList.AddHandler(PointerPressedEvent, new PointerEventHandler(OnTextPointerPressed), handledEventsToo: true);
        MessageList.AddHandler(PointerMovedEvent, new PointerEventHandler(OnTextPointerMoved), handledEventsToo: true);
        MessageList.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnTextPointerReleased), handledEventsToo: true);
        MessageList.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnTextPointerCaptureLost), handledEventsToo: true);
    }

    // =========================================================================
    // POINTER
    // =========================================================================

    private void OnTextPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(MessageList);
        bool mouse         = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse;
        if (mouse && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        bool again     = point.Timestamp - _lastClickTime <= Ui.DoubleClickTime * 1000UL && Near(point.Position, _lastClickAt);
        _clicks        = again ? Math.Min(_clicks + 1, 3) : 1;
        _lastClickTime = point.Timestamp;
        _lastClickAt   = point.Position;

        // A double-click or triple-click never toggles the time, so the tap of its first click stops its wait. The
        // taps of other clicks wait until the release.
        _skipTap = _clicks > 1;
        if (_clicks > 1 && _clickTap is { } first && _tapBubbles.LastIndexOf(first) is >= 0 and var last)
        {
            _tapBubbles.RemoveAt(last);
        }

        _clickTap = null;
        _tapTimer?.Stop();

        // Touch and pen select with the own selection of the text.
        if (!mouse)
        {
            return;
        }

        DependencyObject? source = e.OriginalSource is TextElement inline ? inline.ContentStart.VisualParent : e.OriginalSource as DependencyObject;
        MessageBubble? view      = TextViewOf(source);
        if (view?.Bubble is not { } bubble)
        {
            // A press on the buttons of a bubble or on the scroll bar keeps the selection, so the menu of the ⋮ can
            // copy it.
            if (!IsOnControl(source))
            {
                ClearTextSelection();
            }

            return;
        }

        _textUnit = _clicks switch { 1 => TextUnit.Letter, 2 => TextUnit.Word, _ => TextUnit.Message };

        // A new single click drops the old selection. The clear also ends the press, so it comes before the press.
        if (_textUnit == TextUnit.Letter)
        {
            ClearTextSelection();
        }

        (int caret, int letter) = view.TextHit(e.GetCurrentPoint(view.TextView).Position);
        (_anchorStart, _anchorEnd) = UnitAround(bubble.BodyText, caret, letter, _textUnit);
        _anchorBubble = bubble;
        _textPressed  = true;
        _pressAt      = point.Position;

        // A single click selects nothing until the pointer moves. A double-click or triple-click selects at once.
        if (_textUnit == TextUnit.Letter)
        {
            return;
        }

        TakePointer(e.Pointer);
        SelectTo(bubble, caret, letter);
    }

    private void OnTextPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(MessageList);
        bool pressed       = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && point.Properties.IsLeftButtonPressed;

        // A press that becomes a drag does not toggle the time, on the text or beside it.
        if (pressed && !Near(point.Position, _lastClickAt))
        {
            _skipTap = true;
        }

        // The release can be outside the list, where the pane does not get it.
        if (!pressed)
        {
            WaitForTaps();
            EndTextDrag();
            return;
        }

        if (!_textPressed)
        {
            return;
        }

        // The selection of a click does not change until the pointer moves away from the press.
        if (!_textMoved)
        {
            if (Near(point.Position, _pressAt))
            {
                return;
            }

            _textMoved = true;
            TakePointer(e.Pointer);
        }

        SelectToPointer(e);
    }

    // Windows can drop the last moves before the release when the app is busy. The release then gives the end of the
    // selection.
    private void OnTextPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_textPressed && _textMoved)
        {
            SelectToPointer(e);
        }

        EndTextDrag();
        WaitForTaps();
    }

    private void SelectToPointer(PointerRoutedEventArgs e)
    {
        if (TextViewNear(e.GetCurrentPoint(MessageList).Position.Y) is { Bubble: { } bubble } view)
        {
            (int caret, int letter) = view.TextHit(e.GetCurrentPoint(view.TextView).Position);
            SelectTo(bubble, caret, letter);
        }
    }

    // The panel lost the pointer with no release, as when a menu opens. The drag ends there.
    private void OnTextPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndTextDrag();

    // The panel of the rows takes the pointer, so the drag continues outside the list. The panel stays when rows
    // scroll out, and it is inside the scroll viewer, so the mouse wheel still scrolls during the drag.
    private void TakePointer(Pointer pointer) => MessageList.ItemsPanelRoot?.CapturePointer(pointer);

    private void EndTextDrag()
    {
        if (!_textPressed)
        {
            return;
        }

        _textPressed = false;
        _textMoved   = false;
        MessageList.ItemsPanelRoot?.ReleasePointerCaptures();
    }

    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= DragStart && Math.Abs(a.Y - b.Y) <= DragStart;

    // =========================================================================
    // TIME OF A BUBBLE
    // =========================================================================

    // A tap on a bubble with no drag. The time toggles when the double-click time passes after the release with no
    // other click. The tap of a click on another bubble waits with it and does not drop it.
    private void OnBubbleTimeTapped(object? sender, EventArgs e)
    {
        if (_skipTap || sender is not MessageBubble { Bubble: { } bubble })
        {
            return;
        }

        _tapBubbles.Add(bubble);
        _clickTap = bubble;
        WaitForTaps();
    }

    // Starts the wait of the taps again, when taps wait and no button is down.
    private void WaitForTaps()
    {
        if (_tapBubbles.Count == 0 || _tapTimer?.IsRunning == true)
        {
            return;
        }

        if (_tapTimer is null)
        {
            _tapTimer             = XamlHost.Dispatcher.CreateTimer();
            _tapTimer.IsRepeating = false;
            _tapTimer.Tick       += (_, _) =>
            {
                _tapTimer.Stop();
                MessageBubbleViewModel[] bubbles = [.. _tapBubbles];
                _tapBubbles.Clear();
                _clickTap = null;
                foreach (MessageBubbleViewModel bubble in bubbles)
                {
                    bubble.ToggleTime();
                }
            };
        }

        _tapTimer.Interval = TimeSpan.FromMilliseconds(Ui.DoubleClickTime);
        _tapTimer.Start();
    }

    // Enter or Space toggles the time at once. A tap of the same bubble that waits must not toggle it a second time.
    private void DropTaps(MessageBubbleViewModel bubble) => _tapBubbles.RemoveAll(waiting => ReferenceEquals(waiting, bubble));

    private void DropTaps()
    {
        _tapTimer?.Stop();
        _tapBubbles.Clear();
        _clickTap = null;
    }

    // =========================================================================
    // SELECTION
    // =========================================================================

    // A letter drag goes from caret to caret. A word or message drag covers the whole unit under the pointer.
    private static (int Start, int End) UnitAround(string text, int caret, int letter, TextUnit unit) => unit switch
    {
        TextUnit.Letter => (caret, caret),
        TextUnit.Word   => ChatFormat.WordAround(text, letter),
        _               => (0, text.Length),
    };

    // Selects from the unit of the press to the unit of the pointer, in the order of the chat.
    private void SelectTo(MessageBubbleViewModel bubble, int caret, int letter)
    {
        if (Chat is not { } chat || _anchorBubble is not { } anchor)
        {
            return;
        }

        int anchorIndex = chat.Messages.IndexOf(anchor);
        int headIndex   = chat.Messages.IndexOf(bubble);
        if (anchorIndex < 0 || headIndex < 0)
        {
            return;
        }

        (int headStart, int headEnd) = UnitAround(bubble.BodyText, caret, letter, _textUnit);
        bool forward = headIndex > anchorIndex || (headIndex == anchorIndex && headStart >= _anchorStart);
        (_fromBubble, _fromAt) = forward ? (anchor, _anchorStart) : (bubble, headStart);
        (_toBubble, _toAt)     = forward ? (bubble, headEnd) : (anchor, _anchorEnd);
        if (anchorIndex == headIndex)
        {
            _toAt = Math.Max(_toAt, forward ? _anchorEnd : headEnd);
        }

        ShowTextSelection();
    }

    // Clears the selection of the pane.
    private void ClearTextSelection()
    {
        _fromBubble   = null;
        _toBubble     = null;
        _anchorBubble = null;
        ShowTextSelection();
        EndTextDrag();
    }

    // Finds the first and the last message of the selection in the chat. False with no selection.
    private bool TryFindSelection(out int fromIndex, out int toIndex)
    {
        fromIndex = Chat is { } chat && _fromBubble is { } from ? chat.Messages.IndexOf(from) : -1;
        toIndex   = Chat is { } open && _toBubble is { } to ? open.Messages.IndexOf(to) : -1;
        return fromIndex >= 0 && toIndex >= fromIndex;
    }

    // Gives the selected part of the shown text of the message at the index. The part is empty off the selection.
    private (int Start, int End) SelectedPart(int index, MessageBubbleViewModel bubble, int fromIndex, int toIndex)
    {
        if (index < fromIndex || index > toIndex)
        {
            return (0, 0);
        }

        int length = bubble.BodyText.Length;
        int start  = Math.Min(index == fromIndex ? _fromAt : 0, length);
        return (start, Math.Clamp(index == toIndex ? _toAt : length, start, length));
    }

    // Each row that the list holds shows its part of the selection.
    private void ShowTextSelection()
    {
        if (MessageList.ItemsPanelRoot?.As<ItemsStackPanel>() is not { } panel)
        {
            return;
        }

        bool selected = TryFindSelection(out int fromIndex, out int toIndex);
        for (int i = Math.Max(panel.FirstCacheIndex, 0); i <= panel.LastCacheIndex; i++)
        {
            if (BubbleViewAt(i) is { Bubble: { } bubble } view)
            {
                (int start, int end) = selected ? SelectedPart(i, bubble, fromIndex, toIndex) : (0, 0);
                view.SelectText(start, end);
            }
        }
    }

    // A row that comes into view, or that the list uses again for another message, shows its part of the selection.
    private void ShowTextSelection(int index, MessageBubble view)
    {
        if (view.Bubble is not { } bubble)
        {
            return;
        }

        (int start, int end) = TryFindSelection(out int fromIndex, out int toIndex) ? SelectedPart(index, bubble, fromIndex, toIndex) : (0, 0);
        view.SelectText(start, end);
    }

    /// <summary>
    /// Gives the selected text, as sent, 1 message to a line. It gives null with no selection, and when the bubble is
    /// given but not in the selection.
    /// </summary>
    /// <remarks>
    /// A message that is selected whole gives its text as sent, with the link that its preview card took out of the
    /// bubble. A message that is selected in part gives that part of the shown text.
    /// </remarks>
    private string? SelectedChatText(MessageBubbleViewModel? within)
    {
        if (Chat is not { } chat || !TryFindSelection(out int fromIndex, out int toIndex))
        {
            return null;
        }

        if (within is not null && chat.Messages.IndexOf(within) is var at && (at < fromIndex || at > toIndex))
        {
            return null;
        }

        List<string> lines = [];
        for (int i = fromIndex; i <= toIndex; i++)
        {
            MessageBubbleViewModel bubble = chat.Messages[i];
            (int start, int end)          = SelectedPart(i, bubble, fromIndex, toIndex);
            if (end > start)
            {
                bool whole = start == 0 && end == bubble.BodyText.Length;
                lines.Add(whole ? bubble.Text : ChatFormat.OriginalOf(bubble.Text, bubble.BodyText[start..end]));
            }
        }

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
    }

    // =========================================================================
    // ROWS
    // =========================================================================

    // Finds the bubble whose text the element is, or null off the text of a bubble.
    private static MessageBubble? TextViewOf(DependencyObject? element) =>
        element is not null && BubbleViewAround(element).View is { Bubble.HasBodyText: true } view && view.IsText(element) ? view : null;

    // Tells whether the element is in the hover strip or the reaction pill of a bubble, or in the scroll bar of the
    // list. The names are those in MessageBubble.xaml and in the template of the scroll viewer.
    private bool IsOnControl(DependencyObject? element)
    {
        for (DependencyObject? at = element; at is not null && !ReferenceEquals(at, MessageList); at = VisualTreeHelper.GetParent(at))
        {
            if (at is FrameworkElement { Name: "HoverStrip" or "PillHost" or "VerticalScrollBar" })
            {
                return true;
            }
        }

        return false;
    }

    // Finds the shown bubble with text nearest the height on the list: the last one whose text starts above it, or the
    // first one.
    private MessageBubble? TextViewNear(double y)
    {
        if (MessageList.ItemsPanelRoot?.As<ItemsStackPanel>() is not { } panel)
        {
            return null;
        }

        MessageBubble? near = null;
        for (int i = Math.Max(panel.FirstVisibleIndex, 0); i <= panel.LastVisibleIndex; i++)
        {
            if (BubbleViewAt(i) is not { Bubble.HasBodyText: true } view)
            {
                continue;
            }

            if (near is not null && view.TextView.TransformToVisual(MessageList).TransformPoint(default).Y > y)
            {
                break;
            }

            near = view;
        }

        return near;
    }

    private MessageBubble? BubbleViewAt(int index) =>
        MessageList.ContainerFromIndex(index) is ContentControl { ContentTemplateRoot: MessageBubble view } ? view : null;
}

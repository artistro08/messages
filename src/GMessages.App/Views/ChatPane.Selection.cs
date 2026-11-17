using GMessages.App.Views.Controls;
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
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// Selects text with the mouse across the bubbles of the chat, as a page in a browser does.
/// </summary>
/// <remarks>
/// <para>
/// The text of each bubble selects only inside itself, and a drag after a double-click selected nothing more. This
/// part of <see cref="ChatPane"/> takes the drag from the text. A drag from 1 click selects letters. A drag from a
/// double-click selects whole words, and a drag from a triple-click selects whole messages. The drag continues into
/// the bubbles above and below.
/// </para>
/// <para>
/// A single click without a drag stays with the text, so a click on a link still opens it. Each bubble draws its own
/// highlight, so the selection shows in every bubble at once and stays through a right-click. Ctrl + C and Copy in the
/// bubble menu copy the selection, 1 message to a line. ChatPane.xaml.cs holds the other members.
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

    // The press is in text and the button is down. The pane holds the pointer after the drag starts.
    private bool _textPressed;
    private bool _textDragging;
    private Point _pressAt;
    private UIElement? _pressedText;

    // The clicks so far: 1, 2 or 3. Each click within the double-click time of the last one adds 1.
    private int _clicks;
    private ulong _lastClickTime;
    private Point _lastClickAt;

    // The selection across bubbles, from the first bubble to the last. The end texts are the selected parts of the
    // first and the last bubble, as sent. The bubbles between them are selected whole.
    private MessageBubbleViewModel? _fromBubble;
    private MessageBubbleViewModel? _toBubble;
    private string _fromText = string.Empty;
    private string _toText   = string.Empty;

    // The bubbles that show a selection of the pane.
    private HashSet<MessageBubble> _textShown = [];

    private void HookTextSelection()
    {
        MessageList.AddHandler(PointerPressedEvent, new PointerEventHandler(OnTextPointerPressed), handledEventsToo: true);
        MessageList.AddHandler(PointerMovedEvent, new PointerEventHandler(OnTextPointerMoved), handledEventsToo: true);
        MessageList.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnTextPointerReleased), handledEventsToo: true);
        MessageList.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnTextPointerCaptureLost), handledEventsToo: true);
    }

    // The text handled the press first. A press off the text clears the selection.
    private void OnTextPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(MessageList);
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        bool again     = point.Timestamp - _lastClickTime <= Ui.DoubleClickTime * 1000UL && Near(point.Position, _lastClickAt);
        _clicks        = again ? Math.Min(_clicks + 1, 3) : 1;
        _lastClickTime = point.Timestamp;
        _lastClickAt   = point.Position;

        MessageBubble? view = TextViewOf(e.OriginalSource as DependencyObject);
        if (view?.Bubble is not { } bubble)
        {
            ClearTextSelection();
            return;
        }

        _textUnit     = _clicks switch { 1 => TextUnit.Letter, 2 => TextUnit.Word, _ => TextUnit.Message };

        // A new single click drops the old selection. The clear also ends the press, so it comes before the press.
        if (_textUnit == TextUnit.Letter)
        {
            ClearTextSelection();
        }

        (int caret, int letter) = view.TextHit(e.GetCurrentPoint(view.TextView).Position);
        (_anchorStart, _anchorEnd) = UnitAround(view.ShownText, caret, letter, _textUnit);
        _anchorBubble = bubble;
        _pressedText  = view.TextView;
        _textPressed  = true;
        _pressAt      = point.Position;

        // A single click stays with the text until the pointer moves. A double-click or triple-click selects at once.
        if (_textUnit == TextUnit.Letter)
        {
            return;
        }

        view.CancelTextTap();
        TakePointer(e.Pointer);
        SelectTo(bubble, view, caret, letter);
    }

    private void OnTextPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_textPressed)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(MessageList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndTextDrag(e.Pointer);
            return;
        }

        if (!_textDragging)
        {
            if (Near(point.Position, _pressAt))
            {
                return;
            }

            TakePointer(e.Pointer);
        }

        if (TextViewNear(point.Position.Y) is { Bubble: { } bubble } view)
        {
            (int caret, int letter) = view.TextHit(e.GetCurrentPoint(view.TextView).Position);
            SelectTo(bubble, view, caret, letter);
        }
    }

    private void OnTextPointerReleased(object sender, PointerRoutedEventArgs e) => EndTextDrag(e.Pointer);

    private void OnTextPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MessageList))
        {
            _textPressed  = false;
            _textDragging = false;
        }
    }

    // The pane takes the pointer from the text. The text then stops its own drag. The list cannot capture the pointer
    // while the text holds it.
    private void TakePointer(Pointer pointer)
    {
        _pressedText?.ReleasePointerCaptures();
        _textDragging = MessageList.CapturePointer(pointer);
    }

    private void EndTextDrag(Pointer pointer)
    {
        if (_textDragging)
        {
            MessageList.ReleasePointerCapture(pointer);
        }

        _textPressed  = false;
        _textDragging = false;
    }

    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= DragStart && Math.Abs(a.Y - b.Y) <= DragStart;

    // A letter drag goes from caret to caret. A word or message drag covers the whole unit under the pointer.
    private static (int Start, int End) UnitAround(string text, int caret, int letter, TextUnit unit) => unit switch
    {
        TextUnit.Letter => (caret, caret),
        TextUnit.Word   => ChatFormat.WordAround(text, letter),
        _               => (0, text.Length),
    };

    // Selects from the unit of the press to the unit of the pointer, in the order of the chat.
    private void SelectTo(MessageBubbleViewModel bubble, MessageBubble view, int caret, int letter)
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

        (int headStart, int headEnd) = UnitAround(view.ShownText, caret, letter, _textUnit);
        bool forward = headIndex > anchorIndex || (headIndex == anchorIndex && headStart >= _anchorStart);
        (int fromIndex, int fromAt) = forward ? (anchorIndex, _anchorStart) : (headIndex, headStart);
        (int toIndex, int toAt)     = forward ? (headIndex, headEnd) : (anchorIndex, _anchorEnd);
        if (fromIndex == toIndex)
        {
            toAt = Math.Max(toAt, forward ? _anchorEnd : headEnd);
        }

        _fromBubble = chat.Messages[fromIndex];
        _toBubble   = chat.Messages[toIndex];

        // Each shown bubble in the range shows its part. The end texts come from the first and the last bubble while
        // they show.
        HashSet<MessageBubble> shown = [];
        for (int i = fromIndex; i <= toIndex; i++)
        {
            if (BubbleViewAt(i) is not { Bubble: { HasBodyText: true } message } part)
            {
                continue;
            }

            string text = part.ShownText;
            int start   = Math.Min(i == fromIndex ? fromAt : 0, text.Length);
            int end     = Math.Min(i == toIndex ? toAt : text.Length, text.Length);
            part.SelectText(start, Math.Max(start, end));
            shown.Add(part);

            if (i == fromIndex)
            {
                _fromText = ChatFormat.OriginalOf(message.Text, part.SelectedText);
            }

            if (i == toIndex)
            {
                _toText = ChatFormat.OriginalOf(message.Text, part.SelectedText);
            }
        }

        foreach (MessageBubble old in _textShown.Where(old => !shown.Contains(old)))
        {
            old.SelectText(0, 0);
        }

        _textShown = shown;
    }

    // Clears the selection of the pane.
    private void ClearTextSelection()
    {
        foreach (MessageBubble view in _textShown)
        {
            view.SelectText(0, 0);
        }

        _textShown    = [];
        _fromBubble   = null;
        _toBubble     = null;
        _anchorBubble = null;
        _textPressed  = false;
    }

    /// <summary>
    /// Gives the selected text, as sent, 1 message to a line. It gives null with no selection, and when the bubble is
    /// given but not in the selection.
    /// </summary>
    private string? SelectedChatText(MessageBubbleViewModel? within)
    {
        if (Chat is not { } chat || _fromBubble is not { } from || _toBubble is not { } to)
        {
            return null;
        }

        int fromIndex = chat.Messages.IndexOf(from);
        int toIndex   = chat.Messages.IndexOf(to);
        if (fromIndex < 0 || toIndex < 0)
        {
            return null;
        }

        if (within is not null && chat.Messages.IndexOf(within) is var at && (at < fromIndex || at > toIndex))
        {
            return null;
        }

        List<string> lines = [_fromText];
        for (int i = fromIndex + 1; i < toIndex; i++)
        {
            if (chat.Messages[i] is { HasBodyText: true } message)
            {
                lines.Add(message.Text);
            }
        }

        if (toIndex > fromIndex)
        {
            lines.Add(_toText);
        }

        string text = string.Join(Environment.NewLine, lines);
        return text.Length > 0 ? text : null;
    }

    // Finds the bubble whose text the element is in, or null off the text of a bubble. A link is in the text.
    private static MessageBubble? TextViewOf(DependencyObject? element)
    {
        DependencyObject? text = element is TextElement inline ? inline.ContentStart.VisualParent : element;
        return text is not null && BubbleViewAround(text).View is { Bubble.HasBodyText: true } view && ReferenceEquals(text, view.TextView)
            ? view
            : null;
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

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

    /// <summary>Identifies the <see cref="ShowBack"/> property.</summary>
    public static readonly DependencyProperty ShowBackProperty =
        DependencyProperty.Register(nameof(ShowBack), typeof(bool), typeof(ChatPane), new PropertyMetadata(false));

    private ScrollViewer? _scroller;

    /// <summary>
    /// Creates the pane.
    /// </summary>
    public ChatPane() => InitializeComponent();

    /// <summary>
    /// Asks the window to close the chat and show the list (the back button of a narrow window).
    /// </summary>
    public event EventHandler? BackRequested;

    /// <summary>Gets or sets the open conversation, or null.</summary>
    public ChatViewModel? Chat
    {
        get => (ChatViewModel?)GetValue(ChatProperty);
        set => SetValue(ChatProperty, value);
    }

    /// <summary>Gets or sets whether the back button shows (narrow windows).</summary>
    public bool ShowBack
    {
        get => (bool)GetValue(ShowBackProperty);
        set => SetValue(ShowBackProperty, value);
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

        if (chat is not null)
        {
            chat.Messages.CollectionChanged += pane.OnMessagesChanged;
            chat.PropertyChanged            += pane.OnChatPropertyChanged;

            // The Chat Is On Screen, So Its Toasts Are Read
            (Application.Current as App)?.Notifications?.RemoveConversation(chat.ConversationId);
        }
    }

    // A Bubble Added At The Bottom (Sent, Arrived Or The First Page) Scrolls Into View; Older Pages Go In Above
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || sender is not ICollection<MessageBubbleViewModel> messages
            || e.NewStartingIndex + e.NewItems!.Count != messages.Count)
        {
            return;
        }

        // After The List Has Made The New Item's Container
        object? last = e.NewItems[e.NewItems.Count - 1];
        DispatcherQueue.TryEnqueue(() => MessageList.ScrollIntoView(last));
    }

    private void OnMessageListLoaded(object sender, RoutedEventArgs e) => FillWithOlder();

    // The List's Own Scroller Tells When The Top Is Reached. Found On First Need: While The Pane Is Still Collapsed At
    // Load, The List Has No Template Yet, So There Is No Scroller To Find
    private ScrollViewer? Scroller()
    {
        if (_scroller is null && FindScroller(MessageList) is { } found)
        {
            _scroller              = found;
            _scroller.ViewChanged += OnScrolled;
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

    // Each Older Page Ends In Another IsLoading Change, So This Repeats Until The List Scrolls Or No Pages Are Left
    private void FillWithOlder()
    {
        if (Chat is not { IsLoading: false, HasOlder: true } chat)
        {
            return;
        }

        // Measure The New Bubbles Now, So The Scroll Height Is Current
        MessageList.UpdateLayout();
        if (Scroller() is { ScrollableHeight: <= 0 })
        {
            _ = chat.LoadOlderAsync();
        }
    }

    private void OnScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || _scroller is not { VerticalOffset: <= 0, ScrollableHeight: > 0 } || Chat is null)
        {
            return;
        }

        _ = Chat.LoadOlderAsync();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

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

    private static bool IsShiftDown() => IsDown(VirtualKey.Shift);

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // First ScrollViewer Under An Element (A ListView's Template Holds One)
    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if ((child as ScrollViewer ?? FindScroller(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

using System.Collections.Specialized;
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
/// as messages arrive. Scrolling to the top loads the older page.
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
        }

        var chat = (ChatViewModel?)e.NewValue;
        BindableList.Show(pane.MessageList, chat?.Messages);

        if (chat is not null)
        {
            chat.Messages.CollectionChanged += pane.OnMessagesChanged;

            // TODO: Task 12 — remove this conversation's toasts here: NotificationService.RemoveConversation(chat.ConversationId)
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

    // The List's Own Scroller Tells When The Top Is Reached
    private void OnMessageListLoaded(object sender, RoutedEventArgs e)
    {
        if (_scroller is not null)
        {
            return;
        }

        _scroller = FindScroller(MessageList);
        _scroller?.ViewChanged += OnScrolled;
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

    // The Windows Emoji Panel Types Into Whatever Has Focus, So Focus The Message Box First. Opened With The Win +
    // Period Shortcut: CoreInputView.GetForCurrentView().TryShow(Emoji) Needs A CoreWindow, Which A WinUI 3 Desktop
    // Window Doesn't Have, And Showed Nothing When Tried. The Shortcut Waits A Turn: Sent Before The Box Has Taken
    // Focus, Windows Ignores It
    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
        DraftBox.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SendEmojiShortcut);
    }

    // Win + Period, Pressed And Released
    private static void SendEmojiShortcut()
    {
        NativeMethods.KEYBOARD_INPUT[] keys =
        [
            new() { type = NativeMethods.INPUT_KEYBOARD, wVk = NativeMethods.VK_LWIN },
            new() { type = NativeMethods.INPUT_KEYBOARD, wVk = NativeMethods.VK_OEM_PERIOD },
            new() { type = NativeMethods.INPUT_KEYBOARD, wVk = NativeMethods.VK_OEM_PERIOD, dwFlags = NativeMethods.KEYEVENTF_KEYUP },
            new() { type = NativeMethods.INPUT_KEYBOARD, wVk = NativeMethods.VK_LWIN, dwFlags = NativeMethods.KEYEVENTF_KEYUP },
        ];

        // A Refused Shortcut (Another App Holds The Input) Just Opens Nothing
        _ = NativeMethods.SendInput((uint)keys.Length, keys, Marshal.SizeOf<NativeMethods.KEYBOARD_INPUT>());
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static bool IsShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

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

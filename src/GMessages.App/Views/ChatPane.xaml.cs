using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using GMessages.App.Views.Controls;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// The open conversation: the header with the lock, the message bubbles, the typing dots, the reply chip and the
/// message box with its emoji and send buttons.
/// </summary>
/// <remarks>
/// <para>
/// Shows whatever <see cref="Chat"/> holds; the main window sets it from <see cref="MainViewModel.Chat"/>. The bubbles
/// are wrapped with <see cref="BindableList"/> (see there for why), and the list stays scrolled to the newest bubble
/// as messages arrive. Scrolling to the top loads the older page; a page too short to scroll loads the next older one
/// on its own, until the list scrolls or no older pages are left.
/// </para>
/// <para>
/// Opening a chat puts the cursor in the message box. Enter sends the draft and Shift + Enter adds a line. The emoji
/// button opens the Windows emoji panel on the message box. While replying, the reply chip and the message box join into
/// one control.
/// </para>
/// <para>
/// One reaction picker (<c>ReactionPicker</c>, buttons <c>ReactionPicker.1</c> to <c>.7</c>) serves every bubble: a
/// bubble's pill or menu, or Shift + + on its row, opens it over that bubble (its hover strip's React, over that
/// button). A bubble's Reply, or Ctrl + Enter on its row, starts the reply and moves to the message box. The
/// Application key or Shift + F10 on a row opens its menu.
/// </para>
/// <para>
/// The header's ⋮ (<c>Chat.More</c>), or a right-click on the header, raises <see cref="MenuRequested"/>; the chats
/// show the conversation's menu.
/// </para>
/// <para>
/// While the other person types, the typing bubble (<c>Chat.Typing</c>) shows three dots that bounce in turn, like Google
/// Messages and Teams: a Composition loop that runs only while the bubble shows, and not at all when Windows' animation
/// effects are off.
/// </para>
/// </remarks>
public sealed partial class ChatPane : UserControl
{
    /// <summary>Identifies the <see cref="Chat"/> property.</summary>
    public static readonly DependencyProperty ChatProperty =
        DependencyProperty.Register(nameof(Chat), typeof(ChatViewModel), typeof(ChatPane), new PropertyMetadata(null, OnChatChanged));

    // LibGM's Reactions: EmojiType 1 To 7
    private const int ReactionCount = 7;

    // The Message Box's Corners (Windows' Control Corner Radius) And The Compose Area's Padding
    private const double DraftCorner  = 4;
    private const double ComposeInset = 12;
    private const double ComposeTop   = 8;

    // A Disabled Icon That Keeps Its Own Color Shows At This Opacity
    private const double DisabledOpacity = 0.4;

    // Typing Dots: Each Rises This Far And Back Within Its Beat (See TypingCycle)
    private const float TypingRise = 4;

    // Another Chat Fades In Over WinUI's Normal Control Animation Time
    private static readonly TimeSpan ChatFadeIn = TimeSpan.FromMilliseconds(250);

    // Typing Dots: Each Rises (TypingRise) Within Its Beat, The Next Starting This Much Later, Every Cycle
    private static readonly TimeSpan TypingCycle   = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan TypingStagger = TimeSpan.FromMilliseconds(150);

    // Windows' Own Focus Visual: A 2 px Outer Ring And A 1 px Inner One, Put Back On A Focused Row In High Contrast
    private static readonly Thickness SystemFocusPrimary   = new(2);
    private static readonly Thickness SystemFocusSecondary = new(1);

    // Reads The Windows Animation Setting Live (Settings > Accessibility > Visual Effects)
    private static readonly UISettings Ui = new();

    // Reads Whether High Contrast Is On (Settings > Accessibility > Contrast Themes)
    private static readonly AccessibilitySettings Access = new();

    private ScrollViewer? _scroller;

    // Pinned To The Newest Message (See ChatScroll.StaysPinned), And The Content And View Heights Last Seen
    private bool _pinned = true;
    private double _lastExtent;
    private double _lastViewport;

    // The Bubble The Reaction Picker Is Open For, And Its View, Whose Hover Strip Stays While The Picker Is Open
    private MessageBubbleViewModel? _reactTarget;
    private MessageBubble? _reactView;

    // The Picker's Buttons With Their Emoji, So The One Matching My Reaction Can Show Selected
    private readonly List<(Button Button, string Emoji)> _reactionButtons = [];

    // On The Way To The Newest Message (See ScrollToNewest)
    private bool _toNewest;

    // The Next Chat Opens From The List's Arrow Keys: The Focus Stays On The List (See KeepFocusOnNextOpen)
    private bool _keepFocus;

    // An Older Page Going In Above (See KeepOlderAnchor): How Far The View's Top Sat From The Content's End Before It (Null
    // Once Used), The Bubble At The View's Top Then (Null When Done), Every Shown Bubble Then With Its Top In The View, And
    // The Corrections Left
    private double? _olderFromEnd;
    private MessageBubbleViewModel? _olderBubble;
    private List<(MessageBubbleViewModel Bubble, double Top)> _olderRows = [];
    private int _olderTries;

    /// <summary>
    /// Creates the pane.
    /// </summary>
    public ChatPane()
    {
        InitializeComponent();

        // Reaction Picker: One Button Per Reaction In Enum Order, Labeled With Its Emoji (Styled By The Panel); Love Shows
        // Google's Red Heart Instead Of Heart Eyes
        for (int i = 1; i <= ReactionCount; i++)
        {
            EmojiType type = (EmojiType)i == EmojiType.Love ? EmojiType.RedHeart : (EmojiType)i;
            string emoji   = EmojiTypes.Unicode(type);
            var button   = new Button { Content = emoji };
            AutomationProperties.SetAutomationId(button, "ReactionPicker." + i);
            button.Click += (_, _) => React(emoji);
            ReactionButtons.Children.Add(button);
            _reactionButtons.Add((button, emoji));
        }

        ReactionPicker.Closed += (_, _) =>
        {
            _reactView?.KeepShown(false);
            _reactView = null;
        };

        // Typing Dots Bounce Only While Their Bubble Shows, And Follow Windows' Animation Setting As It Changes (Raised Off The
        // UI Thread)
        TypingBubble.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => AnimateTypingDots(TypingBubble.Visibility == Visibility.Visible));
        Ui.AnimationsEnabledChanged += (_, _) => DispatcherQueue.TryEnqueue(() => AnimateTypingDots(TypingBubble.Visibility == Visibility.Visible));

        // High Contrast Turned On Or Off: The Focused Row Swaps Between Its Bubble's Ring And The System Visual. Windows
        // Changes Its Colors Then (Raised Off The UI Thread); AccessibilitySettings.HighContrastChanged Can't Be Used, As
        // Subscribing To It Crashes A Desktop App's Window
        Ui.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused)
            {
                ShowFocusRing(focused.As<DependencyObject>());
            }
        });
    }

    /// <summary>Gets the lock's tooltip and name.</summary>
    public static string EncryptedText => ChatCopy.EndToEndEncrypted;

    /// <summary>Gets the typing dots' name.</summary>
    public static string TypingText => ChatCopy.Typing;

    /// <summary>Gets the reply chip's close button name.</summary>
    public static string CancelReplyText => ChatCopy.CancelReply;

    /// <summary>Gets the header ⋮'s tooltip and name.</summary>
    public static string MoreOptionsText => ChatCopy.MoreOptions;

    /// <summary>
    /// Raised when the header's ⋮ is clicked or the header is right-clicked (or gets the Application key or Shift + F10);
    /// the chats show the conversation's menu at the target, at the point when there is one.
    /// </summary>
    public event EventHandler<(FrameworkElement Target, Windows.Foundation.Point? Position)>? MenuRequested;

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
    /// Opens the reaction picker over the bubble whose row has focus (the Shift + + shortcut).
    /// </summary>
    /// <returns>True when a bubble row had focus.</returns>
    public bool OpenReactionsForFocused() => FocusedBubbleView() is { } view && OpenReactionPicker(view, view.Anchor);

    /// <summary>
    /// Starts a reply to the bubble whose row has focus and moves to the message box (the Ctrl + Enter shortcut).
    /// </summary>
    /// <returns>True when a bubble row had focus.</returns>
    public bool ReplyToFocused()
    {
        if (FocusedBubbleView() is not { Bubble: { } bubble })
        {
            return false;
        }

        StartReply(bubble);
        return true;
    }

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
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Gives the compose area's padding: none on top while replying, so the message box meets the reply chip.
    /// </summary>
    /// <param name="isReplying">True while a reply is being written.</param>
    /// <returns>The padding.</returns>
    public static Thickness ComposePadding(bool isReplying) =>
        new(ComposeInset, isReplying ? 0 : ComposeTop, ComposeInset, ComposeInset);

    /// <summary>
    /// Gives the message box's corners: square on top while replying, so it and the reply chip read as one control.
    /// </summary>
    /// <param name="isReplying">True while a reply is being written.</param>
    /// <returns>The corners.</returns>
    public static CornerRadius DraftCorners(bool isReplying) =>
        isReplying ? new CornerRadius(0, 0, DraftCorner, DraftCorner) : new CornerRadius(DraftCorner);

    /// <summary>
    /// Dims an icon that keeps its own color (send's accent) while its button is disabled, as the icon buttons did before
    /// they took the shared icon button style.
    /// </summary>
    /// <param name="isEnabled">True while the button is enabled.</param>
    /// <returns>The icon's opacity.</returns>
    public static double EnabledOpacity(bool isEnabled) => isEnabled ? 1 : DisabledOpacity;

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
            pane.FadeInMessages();
        }
        pane.ShowScrollToBottom(false);
        pane._pinned       = true;
        pane._toNewest     = false;
        pane.EndOlderAnchor();

        // The Last Chat's Content Height Means Nothing Here: Unset, The First Look Counts As A Content Change, Not As The
        // Person Scrolling (Two Chats Of The Same Height Would Otherwise Unpin On The Offset Reset To The Top)
        pane._lastExtent   = -1;
        pane._lastViewport = -1;
        pane._reactTarget  = null;

        if (chat is not null)
        {
            chat.Messages.CollectionChanged += pane.OnMessagesChanged;
            chat.PropertyChanged            += pane.OnChatPropertyChanged;

            // The Chat Is On Screen, So Its Toasts Are Read
            (Application.Current as App)?.Notifications?.RemoveConversation(chat.ConversationId);

            // Ready To Type: The Message Box Takes Focus Once The Pane Shows (A Turn Later; A Hidden Box Can't Take It). This
            // Is Focus Inside The Window Only: It Never Activates The Window Or Takes The Foreground, So A Window Opened In The
            // Background Stays There, And Shows The Cursor In The Box Once The Person Brings It Up. Browsing The List With
            // The Arrow Keys Opens Each Chat But Keeps The Focus On The List
            bool keep_focus = pane._keepFocus;
            pane.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (pane.Chat == chat && !keep_focus)
                {
                    pane.DraftBox.Focus(FocusState.Programmatic);
                }
            });
        }

        pane._keepFocus = false;
    }

    // The New Chat's Bubbles Fade In From Clear, Already Pinned To The Newest, So No Frame Shows Them Being Laid Out Or
    // Their First Composition Frame (The Gray Frame The Owner Saw Was The List's Scrolling Placeholders, Now Off In The
    // XAML). A Composition Animation, Not The Opacity Property: It Starts From Clear At Once, Before The Next Frame Draws
    private void FadeInMessages()
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(MessageList);
        ScalarKeyFrameAnimation fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = ChatFadeIn;
        visual.StartAnimation(nameof(Visual.Opacity), fade);
    }

    // Starts The Dots' Loop (Each Rises And Falls, Brightening On The Way Up, A Beat After The One Before) Or Stops It And
    // Puts Them Back At Rest. With Animation Effects Off They Stay Still
    private void AnimateTypingDots(bool run)
    {
        Ellipse[] dots = [TypingDot1, TypingDot2, TypingDot3];
        for (int i = 0; i < dots.Length; i++)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(dots[i]);
            ElementCompositionPreview.SetIsTranslationEnabled(dots[i], true);
            visual.StopAnimation("Translation.Y");
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Opacity = 1;
            if (!run || !Ui.AnimationsEnabled)
            {
                continue;
            }

            Compositor compositor          = visual.Compositor;
            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.6f, 1));

            ScalarKeyFrameAnimation rise = compositor.CreateScalarKeyFrameAnimation();
            rise.InsertKeyFrame(0, 0);
            rise.InsertKeyFrame(0.2f, -TypingRise, ease);
            rise.InsertKeyFrame(0.4f, 0, ease);
            rise.InsertKeyFrame(1, 0);
            rise.Duration          = TypingCycle;
            rise.DelayTime         = TypingStagger * i;
            rise.IterationBehavior = AnimationIterationBehavior.Forever;

            ScalarKeyFrameAnimation glow = compositor.CreateScalarKeyFrameAnimation();
            glow.InsertKeyFrame(0, 0.5f);
            glow.InsertKeyFrame(0.2f, 1, ease);
            glow.InsertKeyFrame(0.4f, 0.5f, ease);
            glow.InsertKeyFrame(1, 0.5f);
            glow.Duration          = TypingCycle;
            glow.DelayTime         = TypingStagger * i;
            glow.DelayBehavior     = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            glow.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation("Translation.Y", rise);
            visual.StartAnimation(nameof(Visual.Opacity), glow);
        }
    }

    /// <summary>
    /// Leaves the focus where it is when the next chat opens (the conversation list's arrow keys open each chat they pass,
    /// and the person is still browsing).
    /// </summary>
    public void KeepFocusOnNextOpen() => _keepFocus = true;

    // A Bubble Added At The Bottom (Sent, Arrived Or The First Page) Comes Into View; Older Pages Go In Above. The List
    // Scrolls To Its End Instead Of ScrollIntoView: On This Bottom-Aligned List, ScrollIntoView Made The Panel Realize
    // Its Rows Again, So Every Bubble Above The New One Replayed Its Entrance (The Whole Chat Seemed To Reload On Send).
    // Already At The End (The Usual Case), KeepPinned Has Done It And Nothing Moves
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // An Older Page's First Bubble: Note Where The View Sits, Before The Page Moves It (Pinned, Or On The Way To The
        // Newest Message, The Newest Bubbles Are Where The View Belongs Instead)
        if (e is { Action: NotifyCollectionChangedAction.Add, NewStartingIndex: 0 } && Chat is { IsLoadingOlder: true } chat
            && !_pinned && !_toNewest && _olderBubble is null && _scroller is { } anchored)
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
    // Load, The List Has No Template Yet, So There Is No Scroller To Find. No Edge Fade Here (Unlike The Chat List): Its
    // Gradient Fades Each Pixel By Its Distance From The Edge, So A Bubble Cut By The Top Edge Showed Its Lower Fill
    // Strong While Its Text Above Faded Out (Owner Report). The Scroller's Plain Clip Cuts Text And Fill On One Line
    private ScrollViewer? Scroller()
    {
        if (_scroller is null && FindScroller(MessageList) is { } found)
        {
            _scroller                  = found;
            _scroller.ViewChanged     += OnScrolled;
            MessageList.LayoutUpdated += OnMessageListLayoutUpdated;
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
        if (Chat is not { IsLoading: false, CanFillOlder: true } chat || _olderFromEnd is not null)
        {
            return;
        }

        // Measure The New Bubbles Now, So The Scroll Height Is Current
        MessageList.UpdateLayout();
        if (Scroller() is { } scroller && ChatScroll.ShouldLoadOlder(scroller.VerticalOffset, scroller.ScrollableHeight, scroller.ViewportHeight))
        {
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

        // The Person Is Scrolling: An Older Page's Corrections Would Snap Them Back, So They End
        if (e.IsIntermediate && !content_changed)
        {
            EndOlderAnchor();
        }
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
        // Not While An Older Page's Corrections Are Pending: One Passing Near The Top Would Ask For Another Page, And They
        // Wait While It Loads
        bool load_older = scroller.ScrollableHeight > 0 && _olderBubble is null
            && ChatScroll.ShouldLoadOlder(scroller.VerticalOffset, scroller.ScrollableHeight, scroller.ViewportHeight);

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
    // Stop A Few Pixels Short Of Its Newest Message. While Pinned, Each Change In Content Height
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

    // A Bubble's Pill, Its Menu's React Or Its Hover Strip's React: The Picker Opens Over The Element Given
    private void OnReactRequested(object? sender, FrameworkElement anchor)
    {
        if (sender is MessageBubble view)
        {
            OpenReactionPicker(view, anchor);
        }
    }

    // A Bubble Menu's Reply Or Its Hover Strip's Reply
    private void OnReplyRequested(object? sender, EventArgs e)
    {
        if (sender is MessageBubble { Bubble: { } bubble })
        {
            StartReply(bubble);
        }
    }

    // The Reply Chip's Close: Its Button Goes With It, So Focus Goes Back To The Message Box
    private void OnCancelReplyClick(object sender, RoutedEventArgs e)
    {
        Chat?.CancelReply();
        DraftBox.Focus(FocusState.Programmatic);
    }

    // The Application Key Or Shift + F10 On A Bubble's Row Opens That Bubble's Menu (A Right-Click On The Bubble Opens It
    // There, Before This)
    private void OnMessageListContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is null || RowBubbleView(e.OriginalSource.As<DependencyObject>()) is not { } view)
        {
            return;
        }

        e.Handled = true;
        view.ShowMenu();
    }

    // The Header's ⋮: The Menu Opens Below It
    private void OnMoreClick(object sender, RoutedEventArgs e) => MenuRequested?.Invoke(this, (MoreButton, null));

    // Right-Click On The Header Opens The Menu At The Pointer; From The Keyboard (The ⋮ Focused) It Opens Below The ⋮
    private void OnHeaderContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        MenuRequested?.Invoke(this, e.TryGetPosition(HeaderBar, out Windows.Foundation.Point at) ? (HeaderBar, at) : (MoreButton, null));
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

    // Notes The Content And View Heights; True When Either Differs From The Last One Seen. The View Shrinks Or Grows When
    // The Typing Dots, The Reply Chip Or A Longer Draft Come And Go Below The List: Not The Person Scrolling, So A Pinned
    // List Stays On The Newest Message
    private bool ContentChanged(ScrollViewer scroller)
    {
        bool changed  = Math.Abs(scroller.ExtentHeight - _lastExtent) >= 0.5 || Math.Abs(scroller.ViewportHeight - _lastViewport) >= 0.5;
        _lastExtent   = scroller.ExtentHeight;
        _lastViewport = scroller.ViewportHeight;
        return changed;
    }

    // A Row Gained Or Lost Focus (Or Something Inside One, Which Is Ignored)
    private void OnMessageListFocusChanged(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not null)
        {
            ShowFocusRing(e.OriginalSource.As<DependencyObject>());
        }
    }

    // A Row With Keyboard Or Programmatic Focus Rings Its Bubble, Raised Over Its Neighbors So They Don't Cover The Ring;
    // A Click Doesn't. In High Contrast The Row's System Focus Visual Shows Instead (See MessageBubbleViewModel.ShowsFocusRing)
    private void ShowFocusRing(DependencyObject element)
    {
        if (RowBubbleView(element) is not { } view)
        {
            return;
        }

        Control row        = element.As<Control>();
        bool high_contrast = Access.HighContrast;
        bool shown         = MessageBubbleViewModel.ShowsFocusRing(row.FocusState != FocusState.Unfocused, row.FocusState == FocusState.Pointer, high_contrast);

        if (high_contrast)
        {
            row.FocusVisualPrimaryThickness   = SystemFocusPrimary;
            row.FocusVisualSecondaryThickness = SystemFocusSecondary;
        }
        else
        {
            row.ClearValue(FocusVisualPrimaryThicknessProperty);
            row.ClearValue(FocusVisualSecondaryThicknessProperty);
        }

        Canvas.SetZIndex(row, shown ? 1 : 0);
        view.ShowFocusRing(shown, high_contrast);
    }

    // The Bubble Of The Row With Keyboard Focus, Or Null When Focus Is Elsewhere (Also On A Link Inside A Row)
    private MessageBubble? FocusedBubbleView() =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused ? RowBubbleView(focused.As<DependencyObject>()) : null;

    // A Row's Bubble, Or Null When The Element Isn't One Of The List's Rows. Found By Its Name (See FindPart)
    private MessageBubble? RowBubbleView(DependencyObject element) =>
        MessageList.IndexFromContainer(element) >= 0 ? FindPart<MessageBubble>(element, "BubbleView") : null;

    // Opens The Reaction Picker Over A Bubble's Part; Not For A Bubble The Phone Hasn't Reported Yet (Nothing To React
    // To). The Bubble's Hover Strip Stays Until The Picker Closes
    private bool OpenReactionPicker(MessageBubble view, FrameworkElement anchor)
    {
        if (view.Bubble is not { Id.Length: > 0 } bubble)
        {
            return false;
        }

        _reactView?.KeepShown(false);
        _reactTarget = bubble;
        _reactView   = view;
        view.KeepShown(true);

        // My Reaction Shows Selected: The Pill's Accent Fill, And Narrator Reads "Selected"
        Style selected = ReactionButtons.Resources["ReactionButtonSelectedStyle"].As<Style>();
        foreach ((Button button, string emoji) in _reactionButtons)
        {
            if (emoji == bubble.MyReaction)
            {
                button.Style = selected;
                AutomationProperties.SetItemStatus(button, ChatCopy.Selected);
            }
            else
            {
                button.ClearValue(StyleProperty);
                button.ClearValue(AutomationProperties.ItemStatusProperty);
            }
        }

        ReactionPicker.ShowAt(anchor);
        return true;
    }

    // A Picker Button: Closes The Picker And Sends The Reaction (Add, Switch Or Remove; See ChatViewModel.ReactAsync)
    private void React(string emoji)
    {
        ReactionPicker.Hide();
        if (_reactTarget is { } bubble && Chat is { } chat)
        {
            _ = chat.ReactAsync(bubble, emoji);
        }
    }

    // Starts The Reply, Then Moves To The Message Box A Turn Later: A Closing Menu Puts Focus Back Where It Was First
    private void StartReply(MessageBubbleViewModel bubble)
    {
        if (Chat is not { } chat)
        {
            return;
        }

        chat.StartReply(bubble);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => DraftBox.Focus(FocusState.Keyboard));
    }

    // Notes, Before An Older Page Goes In Above, How Far The View's Top Sits From The Content's End And Each Shown Bubble
    // With Its Top In The View (Their Rows Are Still Where The Last Layout Put Them). The Panel's Shown Rows As Of That
    // Layout Sit One Index Lower Now That The New Bubble Is At 0
    private void NoteOlderAnchor(ScrollViewer scroller, ChatViewModel chat)
    {
        if (MessageList.ItemsPanelRoot?.As<ItemsStackPanel>() is not { } panel)
        {
            return;
        }

        int first  = Math.Max(1, panel.FirstVisibleIndex + 1);
        int last   = Math.Min(chat.Messages.Count - 1, panel.LastVisibleIndex + 1);
        _olderRows = [.. Enumerable.Range(first, Math.Max(0, last - first + 1))
            .Select(i => (Bubble: chat.Messages[i], Top: RowTop(scroller, chat.Messages[i])))
            .Where(row => row.Top + 1 >= 0)
            .Select(row => (row.Bubble, row.Top!.Value))];
        if (_olderRows.Count == 0)
        {
            return;
        }

        _olderFromEnd = scroller.ExtentHeight - scroller.VerticalOffset;
        _olderBubble  = _olderRows[0].Bubble;
        _olderTries   = 10;
    }

    // The Older Page Went In Above: Puts The View Back (No Animation) So The Shown Bubbles Stay Put. The List's Own
    // KeepItemsInView Does Nothing At The Very Top, Where The Page Is Asked For: There The Offset Stayed 0 And The New
    // Page Took The View, Pushing The Shown Bubbles Below It. The First Layout Has Only The New Page's Rows Realized, So
    // The View First Goes Back The Same Distance From The Content's End; Moving Re-Estimates The Heights Of The Rows Not
    // Shown, So Each Layout After Puts The Noted Bubble Back At Its Top Until It Is There (A Few Tries At Most)
    private void KeepOlderAnchor(ScrollViewer scroller)
    {
        if (_olderBubble is null || Chat is null || Chat.IsLoadingOlder)
        {
            return;
        }

        // Pinned Or On The Way To The Newest Message Since: That Wins
        if (_pinned || _toNewest)
        {
            EndOlderAnchor();
            return;
        }

        if (_olderFromEnd is { } from_end)
        {
            _olderFromEnd = null;
            scroller.ChangeView(null, Math.Max(0, scroller.ExtentHeight - from_end), null, true);
            return;
        }

        // The First Noted Bubble Still Shown Says How Far Off The View Is (The Jump Can Land A Few Rows Off). Only A Shown
        // Row's Top Counts: A Row Outside The Panel's Shown Range Can Still Have A Container Parked Thousands Of Pixels Off
        // (Traced At -16,544), And Correcting By That Threw The View To The Top, Which Asked For The Next Page And Held The
        // View There Until It Landed
        var panel                       = MessageList.ItemsPanelRoot?.As<ItemsStackPanel>();
        (double Now, double Then)? mark = null;
        foreach ((MessageBubbleViewModel row, double then) in _olderRows)
        {
            int index = Chat.Messages.IndexOf(row);
            if (panel is not null && index >= panel.FirstVisibleIndex && index <= panel.LastVisibleIndex && RowTop(scroller, row) is { } now)
            {
                mark = (now, then);
                break;
            }
        }

        // None Shown Yet (The Jump Can Still Be On Its Way When A Layout Runs): Wait For The Next Layout, Within The Tries
        if (mark is not { } shown)
        {
            if (--_olderTries <= 0)
            {
                EndOlderAnchor();
            }

            return;
        }

        if (Math.Abs(shown.Now - shown.Then) < 0.5 || --_olderTries <= 0)
        {
            EndOlderAnchor();
            return;
        }

        scroller.ChangeView(null, scroller.VerticalOffset + shown.Now - shown.Then, null, true);
    }

    // An Older Page's Anchoring Is Done, Given Up Or Overruled: No Jump Or Correction Is Left
    private void EndOlderAnchor()
    {
        _olderFromEnd = null;
        _olderBubble  = null;
        _olderRows    = [];
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

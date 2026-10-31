using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using GMessages.App.Media;
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
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// The open conversation: the header with the lock, the message bubbles, the typing dots, and the compose area: the
/// chips (the voice preview, then the attachments), the reply chip, and the message box with its emoji, attach and mic
/// buttons, the recording bar over it, and Send.
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
/// The paperclip inside the box's right edge (<c>Chat.Attach</c>, or Ctrl + Shift + a; Ctrl + Shift + p for photos)
/// opens the file picker; each picked file becomes a chip above the box, read only at Send. The mic beside it
/// (<c>Chat.Record</c>) records a voice message: the recording bar covers the box with the time, the live waveform,
/// Cancel recording and Done (a check mark). A finished recording waits as the first chip (the voice preview, which plays and seeks)
/// and sends first.
/// </para>
/// <para>
/// One reaction picker (<c>ReactionPicker</c>, buttons <c>ReactionPicker.1</c> to <c>.7</c>) serves every bubble: a
/// bubble's menu, or Shift + + on its row, opens it over that bubble (its hover strip's React, over that
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

    // A Photo Chip's Side, In Effective Pixels
    private const int ChipSide = 64;

    // The Recording Preview's Name In The Voice Player
    private const string PreviewKey = "Chat.VoicePreview";

    // How Often The Recording's Time And Level Update
    private static readonly TimeSpan RecordTick = TimeSpan.FromMilliseconds(50);

    // The Photo Picker's Types (The Spec's Photos) With Their Mime Types: Windows Doesn't Always Know Them (A PC Whose
    // Registry Has No Content Type For .png Reports None)
    private static readonly Dictionary<string, string> PhotoMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"]  = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"]  = "image/png",
        [".gif"]  = "image/gif",
        [".bmp"]  = "image/bmp",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
    };
    private static readonly string[] AnyType = ["*"];

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

    // A File Picker Is Open (Or Its Files Are Being Added): Another Ask Waits For It To Finish
    private bool _picking;

    // An Older Page Going In Above (See KeepOlderAnchor): How Far The View's Top Sat From The Content's End Before It (Null
    // Once Used), The Bubble At The View's Top Then (Null When Done), Every Shown Bubble Then With Its Top In The View, And
    // The Corrections Left
    private double? _olderFromEnd;
    private MessageBubbleViewModel? _olderBubble;
    private List<(MessageBubbleViewModel Bubble, double Top)> _olderRows = [];
    private int _olderTries;

    // Plays The Chat's Voice Messages, One At A Time; Made On The First Play, Let Go On A Chat Switch Or Unload
    private VoicePlayer? _voice;

    // Recording: The Recorder And Its File While One Runs, And The Timer That Shows Its Time And Level. The Time Is
    // What Was Recorded Before The Last Pause, Plus The Run Since It Started Or Resumed
    private VoiceRecorder? _recorder;
    private ChatViewModel? _recordChat;
    private string _recordPath = string.Empty;
    private long _recordStart;
    private long _recordedBefore;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _recordTimer;

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

        // The Static Settings' Events Hold The Pane Only While It Is Loaded, So A Closed Window's Pane Isn't Kept Alive
        Loaded += (_, _) =>
        {
            Ui.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            Ui.ColorValuesChanged       -= OnColorValuesChanged;
            Ui.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
            Ui.ColorValuesChanged       += OnColorValuesChanged;
        };
        Unloaded += (_, _) =>
        {
            Ui.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            Ui.ColorValuesChanged       -= OnColorValuesChanged;
            StopVoice();
            DropRecorder();
        };

        // A Recycled Row Starts Below Its Neighbors Again (A Focused Row Was Raised For Its Ring)
        MessageList.ContainerContentChanging += (_, args) => Canvas.SetZIndex(args.ItemContainer, 0);

        // Recording: The Time And The Input Level, 20 Times A Second
        _recordTimer          = DispatcherQueue.CreateTimer();
        _recordTimer.Interval = RecordTick;
        _recordTimer.Tick    += (_, _) => ShowRecording();

        ShowPreviewPlaying(false, 0);
    }

    /// <summary>Gets the lock's tooltip and name.</summary>
    public static string EncryptedText => ChatCopy.EndToEndEncrypted;

    /// <summary>Gets the typing dots' name.</summary>
    public static string TypingText => ChatCopy.Typing;

    /// <summary>Gets the reply chip's close button name.</summary>
    public static string CancelReplyText => ChatCopy.CancelReply;

    /// <summary>Gets the ＋ button's tooltip and name.</summary>
    public static string AttachText => ChatCopy.Attach;

    /// <summary>Gets a chip's remove button tooltip and name.</summary>
    public static string RemoveAttachmentText => ChatCopy.RemoveAttachment;

    /// <summary>Gets the header ⋮'s tooltip and name.</summary>
    public static string MoreOptionsText => ChatCopy.MoreOptions;

    /// <summary>Gets the mic button's tooltip and name.</summary>
    public static string RecordText => ChatCopy.RecordVoiceMessage;

    /// <summary>Gets the recording's Done tooltip and name.</summary>
    public static string DoneText => ChatCopy.Done;

    /// <summary>Gets the recording's Cancel recording tooltip and name.</summary>
    public static string CancelRecordingText => ChatCopy.CancelRecording;

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

    /// <summary>
    /// Opens the file picker over this window and adds what is picked as chips (the ＋ button, Ctrl + Shift + a and
    /// Ctrl + Shift + p).
    /// </summary>
    /// <remarks>
    /// Several files may be picked. Each one's size is checked, and none is read until Send: a file over 100 MB, or one
    /// taking the chips past 200 MB, is refused with a note. Only one picker opens at a time. Under the test hooks the
    /// picked files come from the test (see <see cref="TestHooks.PickFiles"/>) and no picker opens.
    /// </remarks>
    /// <param name="photos">True for the photo picker (the spec's photo types only).</param>
    /// <returns>True when the picker was asked for (false while no chat shows or a picker is already open).</returns>
    public bool OpenAttachPicker(bool photos)
    {
        if (Chat is not { } chat || _picking)
        {
            return false;
        }

        _picking = true;
        _ = PickAttachmentsAsync(chat, photos);
        return true;
    }

    // =========================================================================
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Gives a chip part's AutomationId: the prefix and the chip's number.
    /// </summary>
    /// <param name="prefix">The prefix, such as <c>Chat.Attachment.</c>.</param>
    /// <param name="id">The chip's number.</param>
    /// <returns>The AutomationId.</returns>
    public static string ChipIdOf(string prefix, int id) => prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Gives a photo chip its picture, decoded small straight from the file (never read whole); the image fills in once
    /// decoded. A file chip gets none.
    /// </summary>
    /// <param name="open">Opens the picked file (<see cref="PendingAttachment.Open"/>).</param>
    /// <param name="isPhoto">True for a photo.</param>
    /// <returns>The image, or null for a file.</returns>
    public static ImageSource? ChipImage(Func<Task<Stream>> open, bool isPhoto)
    {
        if (!isPhoto || open is null)
        {
            return null;
        }

        var image = new BitmapImage { DecodePixelHeight = ChipSide, DecodePixelType = DecodePixelType.Logical };
        _ = LoadChipImageAsync(image, open);
        return image;
    }

    /// <summary>
    /// Shows the chip strip while a recording waits in the preview chip or any attachment does.
    /// </summary>
    /// <param name="hasAttachments">True while attachments wait.</param>
    /// <param name="isPreview">True while a recording waits.</param>
    /// <returns>Visible when either waits, else Collapsed.</returns>
    public static Visibility AnyChips(bool hasAttachments, bool isPreview) =>
        hasAttachments || isPreview ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Shows an element when the flag is off.
    /// </summary>
    /// <param name="value">The flag.</param>
    /// <returns>Collapsed when true, else Visible.</returns>
    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

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

    // Windows' Animation Setting Changed (Raised Off The UI Thread): The Typing Dots Follow It
    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => AnimateTypingDots(TypingBubble.Visibility == Visibility.Visible));

    // High Contrast Turned On Or Off: The Focused Row Swaps Between Its Bubble's Ring And The System Visual. Windows
    // Changes Its Colors Then (Raised Off The UI Thread); AccessibilitySettings.HighContrastChanged Can't Be Used, As
    // Subscribing To It Crashes A Desktop App's Window. The High-Contrast Flag Can Lag The Colors, So It Is Checked Once
    // More On A Low-Priority Pass
    private void OnColorValuesChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            RecheckFocusRing();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RecheckFocusRing);
        });

    // The Focused Row's Ring Or System Visual, For The Current High-Contrast Setting
    private void RecheckFocusRing()
    {
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused)
        {
            ShowFocusRing(focused.As<DependencyObject>());
        }
    }

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
        pane.StopVoice();
        pane.DropRecorder();
        BindableList.Show(pane.MessageList, chat?.Messages);
        BindableList.Show(pane.AttachmentList, chat?.Attachments);
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

        // The Recording Moved On: The Preview Chip Shows Its Waveform, And Stops Playing Once Sent Or Removed
        if (e.PropertyName == nameof(ChatViewModel.Voice) && sender == Chat && Chat is { } chat)
        {
            PreviewBars.Show(chat.Voice.Wave?.Peaks);
            PreviewBars.Length = chat.Voice.Wave?.Length ?? TimeSpan.Zero;
            if (!chat.Voice.IsPreview && _voice?.Key == PreviewKey)
            {
                StopVoice();
            }
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

    // A Bubble's Play, Pause Or Seek: One Message Plays At A Time
    private void OnVoiceRequested(object? sender, VoiceRequest request)
    {
        _voice ??= new VoicePlayer(DispatcherQueue);
        _voice.Handle(request);
    }

    // Stops The Playing Voice Message And Lets Go Of The Player
    private void StopVoice()
    {
        _voice?.Dispose();
        _voice = null;
    }

    // =========================================================================
    // RECORDING
    // =========================================================================

    // The Mic: Records To A New Temp File In The Media Store's Private Folder. Access Off Or No Microphone Shows The
    // Settings Message; Any Other Failure Goes Back To The Mic. A Cancel Or Chat Switch While Starting Drops The Recorder
    private async void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (Chat is not { Voice.IsIdle: true } chat || (Application.Current as App) is not { Media: { } media } app)
        {
            return;
        }

        string path = System.IO.Path.Combine(media.TempFolder, "voice-" + Guid.NewGuid().ToString("N") + ".m4a");
        chat.Voice.Start(path);
        chat.ClearAttachError();
        _recordChat = chat;

        VoiceRecorder recorder;
        try
        {
            recorder = await VoiceRecorder.StartAsync(path, app.MicrophoneId);
        }
        catch (MicrophoneUnavailableException ex)
        {
            AppLog.MediaFailed(App.ShellLog, "record", chat.ConversationId, ex.Message);
            chat.Voice.Cancel();
            await AskMicrophoneSettingsAsync();
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "record", chat.ConversationId, ex.GetType().Name);
            chat.Voice.Cancel();
            return;
        }

        // Cancelled Or Switched Away While Starting
        if (!ReferenceEquals(chat, Chat) || !chat.Voice.IsCapturing || chat.Voice.FilePath != path)
        {
            recorder.Dispose();
            VoiceDraft.DeleteFile(path);
            return;
        }

        _recorder       = recorder;
        _recordPath     = path;
        _recordStart    = Environment.TickCount64;
        _recordedBefore = 0;
        ShowPaused(false);
        RecordBars.Show(null);
        ShowRecording();
        _recordTimer.Start();

        // The Mic Is Covered Now: Focus Goes To Done
        StopRecordingButton.Focus(FocusState.Programmatic);
    }

    // Microphone Off Or Missing: A Desktop App Can't Show Windows' Own Prompt, So This Dialog Asks The User To Turn It On,
    // And Open Settings Goes To Windows' Microphone Page
    private async Task AskMicrophoneSettingsAsync()
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        ContentDialog dialog     = Dialogs.Create(root);
        dialog.Title             = ChatCopy.MicrophoneTitle;
        dialog.Content           = new TextBlock { Text = ChatCopy.MicrophoneText, TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = ChatCopy.OpenSettings;
        dialog.CloseButtonText   = ChatCopy.Cancel;
        dialog.DefaultButton     = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(dialog, "Dialog.Microphone");

        if (await ConversationMenuFlyout.ShowAsync(dialog))
        {
            await OpenMicrophoneSettingsAsync();
        }
    }

    // Windows' Microphone Privacy Page. A Test Run Only Notes The Address; A Failure Logs Its Error Type Only
    private async Task OpenMicrophoneSettingsAsync()
    {
        var page               = new Uri("ms-settings:privacy-microphone");
        string conversation_id = Chat?.ConversationId ?? string.Empty;
        if (TestHooks.RecordLaunch(page.OriginalString))
        {
            return;
        }

        try
        {
            if (!await Launcher.LaunchUriAsync(page))
            {
                AppLog.MediaFailed(App.ShellLog, "settings", conversation_id, "NotLaunched");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "settings", conversation_id, ex.GetType().Name);
        }
    }

    // Done: The File Is Finished, Read Back With Its Waveform, And Waits In The Preview Chip. From Here The Recording Is
    // This Handler's, So Hiding The Window Meanwhile Doesn't Drop It. A Failure Says So Over The Chips, Never Silently
    private async void OnStopRecordingClick(object sender, RoutedEventArgs e)
    {
        if (_recorder is not { } recorder || Chat is not { } chat)
        {
            return;
        }

        string path = _recordPath;
        _recorder   = null;
        _recordChat = null;
        _recordPath = string.Empty;
        _recordTimer.Stop();

        try
        {
            await recorder.StopAsync();
            recorder.Dispose();

            // Read Sharing Whatever Handle Windows Still Holds On The Finished File
            byte[] audio;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
            {
                audio = new byte[stream.Length];
                await stream.ReadExactlyAsync(audio);
            }

            VoiceWave wave = await WaveformReader.ReadAsync(audio);

            // Dropped Meanwhile (A Chat Switch): The File Goes
            if (!chat.Voice.Stop(audio, wave))
            {
                VoiceDraft.DeleteFile(path);
                return;
            }

            DraftBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The Error Type And Code Only (A File Error's Message Names The Path)
            AppLog.MediaFailed(App.ShellLog, "record", chat.ConversationId, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
            recorder.Dispose();
            chat.Voice.Cancel();
            VoiceDraft.DeleteFile(path);
            chat.NoteRecordingFailed();
        }
    }

    // Pause Or Resume: The Recorder Stops Or Restarts Into The Same File, And The Time And Waveform Hold Still Meanwhile,
    // So The Time Counts Recorded Time Only
    private void OnPauseRecordingClick(object sender, RoutedEventArgs e)
    {
        if (_recorder is not { } recorder || Chat is not { } chat)
        {
            return;
        }

        // Pause
        if (chat.Voice.Pause())
        {
            recorder.Pause();
            _recordTimer.Stop();
            _recordedBefore += Environment.TickCount64 - _recordStart;
            ShowPaused(true);
            return;
        }

        // Resume
        if (chat.Voice.Resume())
        {
            recorder.Resume();
            _recordStart = Environment.TickCount64;
            _recordTimer.Start();
            ShowPaused(false);
        }
    }

    // Pause Becomes Resume (The Mic, To Record More) While Paused, And Back
    private void ShowPaused(bool paused)
    {
        string name                = paused ? ChatCopy.Resume : ChatCopy.Pause;
        PauseRecordingGlyph.Glyph  = paused ? "" : "";
        AutomationProperties.SetName(PauseRecordingButton, name);
        ToolTipService.SetToolTip(PauseRecordingButton, name);
    }

    // Cancel Recording: The Recorder Lets Go Of The File, Which Is Deleted
    private void OnCancelRecordingClick(object sender, RoutedEventArgs e)
    {
        DropRecorder();
        Chat?.Voice.Cancel();
        DraftBox.Focus(FocusState.Programmatic);
    }

    // Stops A Running Recorder And Deletes Its File (A Cancel, A Chat Switch Or The Pane Unloading). Its Chat Goes Back
    // To Idle Too: A Chat Kept Over A Closed Window Was Left Recording With No Recorder, So Its Bar Came Back In The Next
    // Window With A Done That Did Nothing
    private void DropRecorder()
    {
        if (_recordChat is { Voice.IsCapturing: true } recording)
        {
            recording.Voice.Cancel();
        }

        _recordChat = null;
        _recordTimer.Stop();
        _recorder?.Dispose();
        _recorder = null;
        VoiceDraft.DeleteFile(_recordPath);
        _recordPath = string.Empty;
    }

    // The Recorded Time (Whole Seconds, Pauses Left Out) And The Newest Input Level
    private void ShowRecording()
    {
        if (_recorder is not { } recorder)
        {
            return;
        }

        long seconds     = (_recordedBefore + Environment.TickCount64 - _recordStart) / 1000;
        float level      = recorder.TakeLevel();
        RecordTimer.Text = VoiceWave.LengthText(TimeSpan.FromSeconds(seconds));
        RecordBars.Push(level);

        // Test Run: The Newest Bar, So A Test Sees The Waveform Move
        TestHooks.NoteState("record-level", level.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
    }

    // The Preview Chip: Play Or Pause, And A Click On Its Waveform Seeks
    private void OnPreviewPlayClick(object sender, RoutedEventArgs e) => RequestPreview(null);

    private void OnPreviewSeekRequested(object? sender, double share) => RequestPreview(share);

    private void RequestPreview(double? seekTo)
    {
        if (Chat is not { Voice: { IsPreview: true, Wave: { } wave } voice })
        {
            return;
        }

        OnVoiceRequested(this, new VoiceRequest(PreviewKey, voice.Audio, wave.Length, seekTo, ShowPreviewPlaying));
    }

    // The Preview Chip's Remove: The Recording And Its File Go
    private void OnPreviewRemoveClick(object sender, RoutedEventArgs e)
    {
        Chat?.Voice.Cancel();
        DraftBox.Focus(FocusState.Programmatic);
    }

    // The Preview's Play Becomes Pause While It Plays, And Its Waveform Fills Up To How Much Has Played
    private void ShowPreviewPlaying(bool playing, double share)
    {
        string name        = playing ? ChatCopy.Pause : ChatCopy.Play;
        PreviewGlyph.Glyph = playing ? "\uE769" : "\uE768";
        AutomationProperties.SetName(PreviewPlay, name);
        ToolTipService.SetToolTip(PreviewPlay, name);
        PreviewBars.ShowProgress(share);
        TestHooks.ShowState(PreviewPlay, playing ? "Playing" : "Paused");
    }

    // A Bubble's Menu's React Or Its Hover Strip's React: The Picker Opens Over The Element Given
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

    // The ＋: Any Files
    private void OnAttachClick(object sender, RoutedEventArgs e) => OpenAttachPicker(photos: false);

    // A Chip's Remove Button: That Attachment Goes, And The Cursor Goes Back To The Message Box
    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender.As<FrameworkElement>().DataContext is PendingAttachment attachment && Chat is not null)
        {
            Chat.RemoveAttachment(attachment);
            DraftBox.Focus(FocusState.Programmatic);
        }
    }

    // =========================================================================
    // ATTACHING
    // =========================================================================

    // The Picked Files (The Test's, Or The Picker's Over This Window), Each Checked For Size, Then Added As Chips In
    // Order. Only A Photo's Header Is Read Now (For Its Shape); Send Reads The Files. A File That Can't Be Opened Is
    // Skipped And Logged By Its Error Type Only
    private async Task PickAttachmentsAsync(ChatViewModel chat, bool photos)
    {
        try
        {
            IReadOnlyList<StorageFile> files = TestHooks.PickFiles(photos) is { } paths
                ? await Task.WhenAll(paths.Select(p => StorageFile.GetFileFromPathAsync(p).AsTask()))
                : await PickWithDialogAsync(photos);
            if (files.Count == 0)
            {
                return;
            }

            chat.ClearAttachError();
            foreach (StorageFile file in files)
            {
                Windows.Storage.FileProperties.BasicProperties properties = await file.GetBasicPropertiesAsync();
                if (!chat.CanAttach((long)properties.Size))
                {
                    continue;
                }

                string mime             = PhotoMimes.GetValueOrDefault(file.FileType) ?? (file.ContentType.Length > 0 ? file.ContentType : "application/octet-stream");
                (int width, int height) = mime.StartsWith("image/", StringComparison.Ordinal) ? await PhotoSizeAsync(file) : (0, 0);
                chat.Attach(file.Name, mime, (long)properties.Size, () => file.OpenStreamForReadAsync(), width, height);
            }

            AppLog.AttachmentsPicked(App.ShellLog, chat.ConversationId, files.Count, chat.Attachments.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "attach", chat.ConversationId, ex.GetType().Name);
        }
        finally
        {
            _picking = false;
        }

        DraftBox.Focus(FocusState.Programmatic);
    }

    // The Windows File Picker Over This Window: Any Files, Or Photos Only; Several May Be Picked
    private async Task<IReadOnlyList<StorageFile>> PickWithDialogAsync(bool photos)
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } island)
        {
            return [];
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = photos ? PickerLocationId.PicturesLibrary : PickerLocationId.DocumentsLibrary,
            ViewMode               = photos ? PickerViewMode.Thumbnail : PickerViewMode.List,
            CommitButtonText       = photos ? ChatCopy.AttachPhoto : ChatCopy.Attach,
        };
        foreach (string type in photos ? (IEnumerable<string>)PhotoMimes.Keys : AnyType)
        {
            picker.FileTypeFilter.Add(type);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId));
        return await picker.PickMultipleFilesAsync();
    }

    // A Photo's Size As Shown (Turned By Its EXIF Orientation), So Its Bubble Keeps Its Shape, Read From The File's
    // Header Without Decoding It; 0 x 0 When It Won't Decode
    private static async Task<(int Width, int Height)> PhotoSizeAsync(StorageFile file)
    {
        try
        {
            using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (0, 0);
        }
    }

    // A Photo Chip's Picture, Decoded Small From The File; A File Windows Can't Read Leaves The Chip Blank
    private static async Task LoadChipImageAsync(BitmapImage image, Func<Task<Stream>> open)
    {
        try
        {
            await using Stream file = await open();
            using IRandomAccessStream stream = file.AsRandomAccessStream();
            await image.SetSourceAsync(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "chip", string.Empty, ex.GetType().Name);
        }
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

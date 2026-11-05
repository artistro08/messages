using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using GMessages.App.Media;
using GMessages.App.Views.Controls;
using GMessages.App.Windowing;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;
using WinRT;
using Muxc = Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views;

/// <summary>
/// The open conversation: the header with the lock, the message bubbles, the typing dots, and the compose area: the
/// reply chip, the chips (the voice preview, then the attachments), and the message box with its emoji, attach and mic
/// buttons, the recording bar over it, and Send. The reply chip and the chips are joined to the box's top.
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
/// button). A bubble's Reply, or Ctrl + Enter on its row, starts the reply and moves to the message box.
/// </para>
/// <para>
/// One bubble menu (<c>BubbleMenu</c>) serves every bubble too: a right-click on a bubble opens it at the pointer, the
/// Application key or Shift + F10 on its row over its back, and its hover strip's ⋮ at the ⋮. Its items call the bubble
/// it opened on. A link preview card opens its own menu (<c>PreviewMenu</c>, with Copy link) instead.
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

    // A Picker Button's Corners, Its Hover Tint's And Its Selected Fill's Alike (ReactionButtonStyle In ChatPane.xaml)
    private const double ReactionCorner = 8;

    // More Reactions: Its Button's Size, And The Emoji Picker's Columns And Scrolling Height
    private const double MoreReactionsSize = 40;
    private const int EmojiPickerColumns   = 8;
    private const double EmojiPickerHeight = 280;
    private const double EmojiCellSize     = 44;

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

    // Videos And Audio By Their Type, For A PC Whose Registry Has No Content Type For Them: The Phone Then Shows A Video As A
    // Video (MediaFormatsMap Falls Back On The Part Before The Slash)
    private static readonly Dictionary<string, string> MediaMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"]  = "video/mp4",
        [".m4v"]  = "video/mp4",
        [".mov"]  = "video/quicktime",
        [".webm"] = "video/webm",
        [".3gp"]  = "video/3gpp",
        [".3g2"]  = "video/3gpp2",
        [".mkv"]  = "video/x-matroska",
        [".avi"]  = "video/x-msvideo",
        [".mp3"]  = "audio/mpeg",
        [".m4a"]  = "audio/mp4",
        [".aac"]  = "audio/aac",
        [".amr"]  = "audio/amr",
        [".ogg"]  = "audio/ogg",
        [".wav"]  = "audio/wav",
    };

    // A Picture Pasted In Its Own Clipboard Format (Before The Plain Bitmap), With The Mime Type And Name It Sends As: A GIF
    // Keeps Its Frames, A PNG Its Transparency
    private static readonly (string Format, string Mime, string Name)[] PastedPictures =
    [
        ("GIF", "image/gif", "image.gif"),
        ("image/gif", "image/gif", "image.gif"),
        ("PNG", "image/png", "image.png"),
        ("image/png", "image/png", "image.png"),
    ];

    // A Pasted Bitmap (A Screenshot, A Browser's Copy Image) Is Sent As A PNG Under This Name, A Web GIF (The Emoji Panel's)
    // As A GIF Under This One
    private const string PastedBitmapName = "image.png";
    private const string PastedGifName    = "image.gif";

    // Typing Dots: Each Rises This Far And Back Within Its Beat (See TypingCycle)
    private const float TypingRise = 4;

    // Another Chat Fades In Over WinUI's Faster Control Animation Time: The Normal 250 ms Made Switching Feel Slow
    private static readonly TimeSpan ChatFadeIn = TimeSpan.FromMilliseconds(83);

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

    // The List's Bottom Fade (Behind The Message Box), And How Much Of The List The Typing Bubble, The Chips And The
    // Message Box Cover
    private ScrollFade? _fade;
    private double _composerHeight = -1;

    // The Rows' Panel Hands Focus Scrolls To OnBubbleBringIntoView (Hooked Once), Which Keeps A Shown Bubble This Far
    // From The View's Edge
    private const double BringMargin = 8;
    private bool _bringHooked;

    // Pinned To The Newest Message (See ChatScroll.StaysPinned), And The Content And View Heights Last Seen
    private bool _pinned = true;

    // The Opened Chat Whose First Drawn Bubbles Are Still To Be Logged, Or Null
    private ChatViewModel? _firstDrawFor;
    private double _lastExtent;
    private double _lastViewport;

    // The Bubble The Reaction Picker Is Open For, Its View (Whose Hover Strip Stays While The Picker Is Open), And What
    // The Picker Opened Over
    private MessageBubbleViewModel? _reactTarget;
    private MessageBubble? _reactView;
    private FrameworkElement? _reactAnchor;

    // The Emoji Picker (Made On First Use), Its Search Box, Its Rows (Virtualized: Only Those In View Are Made), Its Empty
    // Note, The Bubble It Picks A Reaction For, And How Many Times It Has Opened (For The Timing Log)
    private Flyout? _emojiPicker;
    private TextBox? _emojiSearch;
    private Muxc.ItemsRepeater? _emojiRows;
    private ScrollViewer? _emojiScroller;
    private EmojiRowFactory? _emojiFactory;
    private TextBlock? _emojiEmpty;
    private MessageBubbleViewModel? _emojiTarget;
    private int _emojiOpens;

    // More Reactions Picked: What The Emoji Picker Opens For Once The Reaction Picker Has Closed
    private (MessageBubbleViewModel Bubble, MessageBubble View, FrameworkElement Anchor)? _emojiPending;

    // The Bubble The Bubble Menu Or Preview Card Menu Last Opened On (Its Items Act On It); Let Go On A Chat Switch
    private MessageBubble? _menuView;

    // The Picker's Buttons With The Fill Behind Each And Their Emoji, So The One Matching My Reaction Can Show Selected
    private readonly List<(Button Button, Border Back, string Emoji)> _reactionButtons = [];

    // On The Way To The Newest Message (See ScrollToNewest)
    private bool _toNewest;

    // The Next Chat Opens From The List's Arrow Keys: The Focus Stays On The List (See KeepFocusOnNextOpen)
    private bool _keepFocus;

    // A File Picker Is Open (Or Its Files Are Being Added): Another Ask Waits For It To Finish
    private bool _picking;

    // Plays The Chat's Voice Messages, One At A Time; Made On The First Play, Let Go On A Chat Switch Or Unload
    private VoicePlayer? _voice;

    // Recording: The Recorder And Its File While One Runs, And The Timer That Shows Its Time And Level. The Time Is
    // What Was Recorded Before The Last Pause, Plus The Run Since It Started Or Resumed
    private VoiceRecorder? _recorder;
    private ChatViewModel? _recordChat;
    private string _recordPath = string.Empty;
    private long _recordStart;
    private long _recordedBefore;
    private readonly DispatcherQueueTimer _recordTimer;

    /// <summary>
    /// Creates the pane.
    /// </summary>
    public ChatPane()
    {
        InitializeComponent();

        // Scroll To Bottom Floats Over The Bubbles With Its Shadow. Set Here: XAML Can't Set Translation On A Custom Button
        ScrollToBottomButton.Translation = new System.Numerics.Vector3(0, 0, 16);

        // Reaction Picker: One Button Per Reaction In Enum Order, Labeled With Its Emoji (Styled By The Panel), Over A Fill
        // That Shows My Reaction Selected; Love Shows Google's Red Heart Instead Of Heart Eyes
        for (int i = 1; i <= ReactionCount; i++)
        {
            EmojiType type = (EmojiType)i == EmojiType.Love ? EmojiType.RedHeart : (EmojiType)i;
            string emoji   = EmojiTypes.Unicode(type);
            var button     = new Button { Content = emoji };
            var back       = new Border { CornerRadius = new CornerRadius(ReactionCorner) };
            AutomationProperties.SetAutomationId(button, "ReactionPicker." + i);
            button.Click += (_, _) => React(emoji);
            ReactionButtons.Children.Add(new Grid { Children = { back, button } });
            _reactionButtons.Add((button, back, emoji));
        }

        // More Reactions: Any Emoji, From The App's Emoji Picker
        var more = new Button
        {
            Width   = MoreReactionsSize,
            Content = new FontIcon { Glyph = "", FontSize = 16 },
            Style   = Application.Current.Resources["IconButtonStyle"].As<Style>(),
        };
        AutomationProperties.SetAutomationId(more, "ReactionPicker.More");
        AutomationProperties.SetName(more, ChatCopy.MoreReactions);
        ToolTipService.SetToolTip(more, ChatCopy.MoreReactions);
        more.Click += (_, _) => OnMoreReactionsClick();
        ReactionButtons.Children.Add(more);

        // The Bubble Menu Closed: Its Bubble's Hover Strip Hides Unless The Pointer Is On It
        BubbleMenu.Closed += (_, _) => _menuView?.KeepShown(false);

        ReactionPicker.Closed += (_, _) =>
        {
            _reactView?.KeepShown(false);
            _reactView = null;

            // More Reactions Was Picked: The Emoji Picker Opens Now (See OnMoreReactionsClick)
            if (_emojiPending is { } pending)
            {
                _emojiPending = null;
                ShowEmojiPicker(pending.Bubble, pending.View, pending.Anchor);
            }
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

            // The Message Box's Menu Is Windows' Shared Text Menu, So It Too Holds The Pane Only While Loaded
            if (DraftBox.ContextFlyout is { } menu)
            {
                menu.Opening -= OnDraftMenuOpening;
                menu.Opening += OnDraftMenuOpening;
            }
        };
        Unloaded += (_, _) =>
        {
            Ui.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            Ui.ColorValuesChanged       -= OnColorValuesChanged;
            if (DraftBox.ContextFlyout is { } menu)
            {
                menu.Opening -= OnDraftMenuOpening;
            }

            StopVoice();
            DropRecorder();
        };

        // A Recycled Row Starts Below Its Neighbors Again (A Focused Row Was Raised For Its Ring), And Lets Go Of Its Media
        MessageList.ContainerContentChanging += OnMessageContainerChanging;

        // Recording: The Time And The Input Level, 20 Times A Second, Over A Live Waveform As Wide As The Bar Allows
        RecordBars.FillsWidth = true;
        _recordTimer          = XamlHost.Dispatcher.CreateTimer();
        _recordTimer.Interval = RecordTick;
        _recordTimer.Tick    += (_, _) => ShowRecording();

        ShowPreviewPlaying(false, 0);
    }

    /// <summary>
    /// Exposes the pane to UI Automation as <c>Chat.Pane</c>.
    /// </summary>
    /// <remarks>
    /// A user control has no automation peer of its own, so the pane's bounds (the chat card's, inside its 1 px border)
    /// couldn't be read; the layout tests line the conversation list up against them.
    /// </remarks>
    /// <returns>The pane's peer.</returns>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    /// <summary>Gets the lock's tooltip and name.</summary>
    public static string EncryptedText => ChatCopy.EndToEndEncrypted;

    /// <summary>Gets the reply chip's close button name.</summary>
    public static string CancelReplyText => ChatCopy.CancelReply;

    /// <summary>Gets the ＋ button's tooltip and name.</summary>
    public static string AttachText => ChatCopy.Attach;

    /// <summary>Gets a chip's remove button tooltip and name.</summary>
    public static string RemoveAttachmentText => ChatCopy.RemoveAttachment;

    /// <summary>Gets the header ⋮'s tooltip and name.</summary>
    public static string MoreOptionsText => ChatCopy.MoreOptions;

    /// <summary>Gets the header mute button's tooltip and name: what a click does.</summary>
    /// <param name="isMuted">Whether the chat is muted.</param>
    /// <returns>Unmute while muted, else Mute.</returns>
    public static string MuteText(bool isMuted) => isMuted ? ChatCopy.Unmute : ChatCopy.Mute;

    /// <summary>Gets the header mute button's icon: the chat's state, as the ⋮ menu's icons.</summary>
    /// <param name="isMuted">Whether the chat is muted.</param>
    /// <returns>The crossed-out bell while muted, else the bell.</returns>
    public static string MuteGlyph(bool isMuted) => isMuted ? "" : "";

    /// <summary>Raised when the header's mute button is clicked; the chats mute or unmute the open chat.</summary>
    public event EventHandler? MuteRequested;

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

        return XamlHost.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => SendEmojiShortcut());
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
    /// Gives a photo chip its picture, decoded small straight from the file (never read whole), and a video chip its
    /// thumbnail when Windows had one; the image fills in once decoded. A file chip gets none.
    /// </summary>
    /// <param name="open">Opens the picked file (<see cref="PendingAttachment.Open"/>).</param>
    /// <param name="thumbnail">Opens the file's thumbnail (<see cref="PendingAttachment.Thumbnail"/>), or null.</param>
    /// <param name="isPhoto">True for a photo.</param>
    /// <returns>The image, or null for a file without a thumbnail.</returns>
    public static ImageSource? ChipImage(Func<Task<Stream>> open, Func<Task<Stream>>? thumbnail, bool isPhoto)
    {
        Func<Task<Stream>>? picture = isPhoto ? open : thumbnail;
        if (picture is null)
        {
            return null;
        }

        var image = new BitmapImage { DecodePixelHeight = ChipSide, DecodePixelType = DecodePixelType.Logical };
        _ = LoadChipImageAsync(image, picture);
        return image;
    }

    /// <summary>
    /// Gives a chip without a picture its icon: a video's, an audio file's, or a document's.
    /// </summary>
    /// <param name="kind">What the attachment sends as.</param>
    /// <returns>The Segoe Fluent Icons glyph.</returns>
    public static string ChipGlyph(MessageKind kind) => kind switch
    {
        MessageKind.Video => "",
        MessageKind.Audio => "",
        _                 => "",
    };

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
    /// Gives the compose area's padding: none on top while the reply chip or the attachment chips sit over the message
    /// box, so the box meets them.
    /// </summary>
    /// <param name="isReplying">True while a reply is being written.</param>
    /// <param name="hasAttachments">True while attachments wait.</param>
    /// <param name="isPreview">True while a recording waits.</param>
    /// <returns>The padding.</returns>
    public static Thickness ComposePadding(bool isReplying, bool hasAttachments, bool isPreview) =>
        new(ComposeInset, isReplying || hasAttachments || isPreview ? 0 : ComposeTop, ComposeInset, ComposeInset);

    /// <summary>
    /// Gives the message box's corners: square on top while the reply chip or the attachment chips sit over it, so they
    /// and the box read as one control.
    /// </summary>
    /// <param name="isReplying">True while a reply is being written.</param>
    /// <param name="hasAttachments">True while attachments wait.</param>
    /// <param name="isPreview">True while a recording waits.</param>
    /// <returns>The corners.</returns>
    public static CornerRadius DraftCorners(bool isReplying, bool hasAttachments, bool isPreview) =>
        isReplying || hasAttachments || isPreview ? new CornerRadius(0, 0, DraftCorner, DraftCorner) : new CornerRadius(DraftCorner);

    /// <summary>
    /// Gives the attachment chips' strip its margin: joined under the reply chip while replying, else with the compose
    /// area's gap above it.
    /// </summary>
    /// <param name="isReplying">True while a reply is being written.</param>
    /// <returns>The margin.</returns>
    public static Thickness StripMargin(bool isReplying) => new(ComposeInset, isReplying ? 0 : ComposeTop, ComposeInset, 0);

    /// <summary>
    /// Gives the attachment chips' strip its corners: square under the reply chip, else rounded on top like the reply chip.
    /// </summary>
    /// <param name="isReplying">True while a reply is being written.</param>
    /// <returns>The corners.</returns>
    public static CornerRadius StripCorners(bool isReplying) =>
        isReplying ? new CornerRadius(0) : new CornerRadius(DraftCorner, DraftCorner, 0, 0);

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
        XamlHost.Dispatcher.TryEnqueue(() => AnimateTypingDots(TypingBubble.Visibility == Visibility.Visible));

    // High Contrast Turned On Or Off: The Focused Row Swaps Between Its Bubble's Ring And The System Visual. Windows
    // Changes Its Colors Then (Raised Off The UI Thread); AccessibilitySettings.HighContrastChanged Can't Be Used, As
    // Subscribing To It Crashes A Desktop App's Window. The High-Contrast Flag Can Lag The Colors, So It Is Checked Once
    // More On A Low-Priority Pass
    private void OnColorValuesChanged(UISettings sender, object args) =>
        XamlHost.Dispatcher.TryEnqueue(() =>
        {
            RecheckFocusRing();
            XamlHost.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, RecheckFocusRing);
        });

    // The Focused Row's Ring Or System Visual, For The Current High-Contrast Setting
    private void RecheckFocusRing()
    {
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused)
        {
            ShowFocusRing(focused.As<DependencyObject>());
        }
    }

    // A Row Leaves The List Or Gets A Message. Leaving (The Recycle Queue): It Starts Below Its Neighbors Again (A Focused
    // Row Was Raised For Its Ring) And Lets Go Of Its Media. Getting One: Its Media Loads In Phase 1, Once x:Bind Has
    // Given It The Message (A Row Given The Same Message Back Gets No Bubble Change, So Its Released Media Loads Here)
    private static void OnMessageContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        Canvas.SetZIndex(args.ItemContainer, 0);
        if (args.ItemContainer.ContentTemplateRoot is not MessageBubble view)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            view.ReleaseMedia();
            return;
        }

        args.RegisterUpdateCallback(1, OnMessageContainerPhase);
    }

    private static void OnMessageContainerPhase(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        (args.ItemContainer.ContentTemplateRoot as MessageBubble)?.LoadMedia();

    // Another Chat Opened: Show Its Bubbles And Follow Their Changes
    private static void OnChatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pane = (ChatPane)d;

        if (e.OldValue is ChatViewModel oldChat)
        {
            oldChat.Messages.CollectionChanged -= pane.OnMessagesChanged;
            oldChat.PropertyChanged            -= pane.OnChatPropertyChanged;
        }

        var chat = (ChatViewModel?)e.NewValue;
        pane.StopVoice();
        MessageBubble.StopInlineVideo();
        pane.DropRecorder();
        pane._menuView?.KeepShown(false);
        pane._menuView = null;
        BindableList.Show(pane.MessageList, chat?.Messages);
        BindableList.Show(pane.AttachmentList, chat?.Attachments);
        if (chat is not null)
        {
            pane.HideMessages();
        }
        pane.ShowScrollToBottom(false);
        pane._pinned       = true;
        pane._toNewest     = false;

        // The Last Chat's Content Height Means Nothing Here: Unset, The First Look Counts As A Content Change, Not As The
        // Person Scrolling (Two Chats Of The Same Height Would Otherwise Unpin On The Offset Reset To The Top)
        pane._lastExtent   = -1;
        pane._lastViewport = -1;
        pane._reactTarget  = null;

        if (chat is not null)
        {
            chat.Messages.CollectionChanged += pane.OnMessagesChanged;
            chat.PropertyChanged            += pane.OnChatPropertyChanged;
            pane._firstDrawFor               = chat;
            pane.LogFirstDraw();

            // The Chat Is On Screen, So Its Toasts Are Read
            (Application.Current as App)?.Notifications?.RemoveConversation(chat.ConversationId);

            // Ready To Type: The Message Box Takes Focus Once The Pane Shows (A Turn Later; A Hidden Box Can't Take It). This
            // Is Focus Inside The Window Only: It Never Activates The Window Or Takes The Foreground, So A Window Opened In The
            // Background Stays There, And Shows The Cursor In The Box Once The Person Brings It Up. Browsing The List With
            // The Arrow Keys Opens Each Chat But Keeps The Focus On The List
            bool keepFocus = pane._keepFocus;
            XamlHost.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (pane.Chat == chat && !keepFocus)
                {
                    pane.DraftBox.Focus(FocusState.Programmatic);
                }
            });
        }

        pane._keepFocus = false;
    }

    // The Frame That Draws The Opened Chat's First Bubbles: They Fade In From There, And The Open-Timing Log Takes Its Last
    // Step (Once Per Chat, Milliseconds And Counts Only). Called On Open And On Each Change Of The Bubbles Until Then
    private void LogFirstDraw()
    {
        if (_firstDrawFor is not { Messages.Count: > 0 } chat || chat != Chat)
        {
            return;
        }

        _firstDrawFor = null;
        void OnRendering(object? sender, object e)
        {
            Windows.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            if (chat == Chat)
            {
                FadeInMessages();
            }

            chat.LogOpenStep("first bubbles drawn");
        }

        Windows.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
    }

    // The New Chat's Bubbles Stay Clear Until Their First Frame, So No Frame Shows The Last Chat's Or Them Being Laid Out
    private void HideMessages()
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(MessageList);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Opacity = 0;
    }

    // Then They Fade In From Clear, Already Pinned To The Newest, Starting With The Frame That Draws Them (The Gray Frame
    // The Owner Saw Was The List's Scrolling Placeholders, Now Off In The XAML). A Composition Animation, Not The Opacity
    // Property, So It Starts In That Same Frame. A Chat With No Bubbles Stays Clear Until Its First One Comes
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
        LogFirstDraw();

        if (e.Action != NotifyCollectionChangedAction.Add || sender is not ICollection<MessageBubbleViewModel> messages
            || e.NewStartingIndex + e.NewItems!.Count != messages.Count)
        {
            return;
        }

        // After The List Has Measured The New Bubble
        XamlHost.Dispatcher.TryEnqueue(() =>
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
    // Load, The List Has No Template Yet, So There Is No Scroller To Find. The List's Edges Fade Like The Chat List's,
    // The Bottom Always, Behind The Message Box (Owner)
    private ScrollViewer? Scroller()
    {
        if (_scroller is null && UiHelpers.FindScroller(MessageList) is { } found)
        {
            _scroller                  = found;
            _scroller.ViewChanged     += OnScrolled;
            MessageList.LayoutUpdated += OnMessageListLayoutUpdated;
            PaneGrid.LayoutUpdated    += (_, _) => KeepComposerClear();
        }

        // The Fade Needs The Scroller's Own Template, Which Can Come After The Scroller Itself: Tried Until It Takes
        if (_scroller is not null && _fade is null && ScrollFade.Attach(_scroller, bottomAlways: true) is { } fade)
        {
            _fade           = fade;
            _composerHeight = -1;
            KeepComposerClear();
        }

        // Bubbles Brought Into View By Focus (Moving Between Messages With The Keyboard) Scroll Smoothly. Hooked On The
        // Rows' Panel: The Request Reaches The Scroller Before The List, So It Must Be Taken Below It
        if (_scroller is not null && !_bringHooked && MessageList.ItemsPanelRoot is { } panel)
        {
            _bringHooked                  = true;
            panel.BringIntoViewRequested += OnBubbleBringIntoView;
        }

        return _scroller;
    }

    // A Bubble Asked To Be Seen (It Took Focus): The Scroller's Own Jump Is Taken Over By The Shared Smooth Scroll, Just
    // Far Enough To Show The Whole Bubble, 8 px Clear Of The Edge
    private void OnBubbleBringIntoView(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        if (_scroller is not { } scroller || args.TargetElement is null)
        {
            return;
        }

        Windows.Foundation.Rect shown;
        try
        {
            shown = args.TargetElement.TransformToVisual(scroller).TransformBounds(args.TargetRect);
        }
        catch (ArgumentException)
        {
            return;
        }

        args.Handled = true;
        double offset = scroller.VerticalOffset;
        if (shown.Top < 0)
        {
            offset += shown.Top - BringMargin;
        }
        else if (shown.Bottom > scroller.ViewportHeight)
        {
            offset += Math.Min(shown.Top - BringMargin, shown.Bottom - scroller.ViewportHeight + BringMargin);
        }

        _ = ScrollSmoothlyAsync(scroller, offset);
    }

    // A Page Finished Loading: Too Short To Scroll Means There Is No Top To Reach, So Load Older Here
    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsLoading) && sender == Chat && Chat is { IsLoading: false })
        {
            XamlHost.Dispatcher.TryEnqueue(FillWithOlder);
        }

        // An Older Page Goes In Above: The Last Shown Bubble Holds Its Place While It Does (See The Rows' Panel In The
        // XAML), And The First Shown One Again Once The List Has Laid The Page Out (Measured Here, Before The Mode Goes Back)
        if (e.PropertyName == nameof(ChatViewModel.IsLoadingOlder) && sender == Chat && Chat is { } loading
            && MessageList.ItemsPanelRoot?.As<ItemsStackPanel>() is { } panel)
        {
            if (!loading.IsLoadingOlder)
            {
                MessageList.UpdateLayout();
            }

            panel.ItemsUpdatingScrollMode = loading.IsLoadingOlder ? ItemsUpdatingScrollMode.KeepLastItemInView : ItemsUpdatingScrollMode.KeepItemsInView;
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
        if (Chat is not { IsLoading: false, CanFillOlder: true } chat)
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
        bool contentChanged  = ContentChanged(scroller);
        bool wasPinned       = _pinned;
        _pinned              = ChatScroll.StaysPinned(_pinned, contentChanged, scroller.VerticalOffset, scroller.ScrollableHeight);

        if (contentChanged)
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

        // Back At The Newest Message After Scrolling Up: The Older Pages Above The Newest 200 Bubbles Go (Scrolling Up
        // Again Loads Them Back)
        if (!wasPinned && _pinned)
        {
            Chat.TrimToNewest();
        }

        bool showArrow = ChatScroll.ShowScrollToBottom(scroller.VerticalOffset, scroller.ScrollableHeight, scroller.ViewportHeight);
        bool loadOlder = scroller.ScrollableHeight > 0
            && ChatScroll.ShouldLoadOlder(scroller.VerticalOffset, scroller.ScrollableHeight, scroller.ViewportHeight);

        ShowScrollToBottom(showArrow);
        if (loadOlder)
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

        if (_fade is null)
        {
            _ = Scroller();
        }

        if (ContentChanged(scroller))
        {
            KeepPinned(scroller);
        }
    }

    // Enter Or Space On A Focused Bubble Shows Or Hides Its Time, Like A Click (Not On Its Retry Link, Which Has Focus Then).
    // The Row Is Matched As A ContentControl Holding A Bubble: In The Native AOT Build It Arrives As A ContentControl, Not
    // A ListViewItem (See UiHelpers.FindScroller)
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

    // The Box Emptied (Sent, Or Cleared): Its Text's Own Scroller Goes Back To The Top. Left Scrolled Down From Lines Since
    // Gone, It Showed Only The Top Of The Cursor (Owner Report)
    private void OnDraftTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DraftBox.Text.Length == 0 && UiHelpers.FindPart<ScrollViewer>(DraftBox, "ContentElement") is { } text)
        {
            text.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    // Enter Sends; Shift + Enter Is Left To The Box, Which Adds A Line. Ctrl + V Or Shift + Insert With Media On The
    // Clipboard Pastes It As Chips (The Box Would Ignore A Picture)
    private void OnDraftKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Test Run: Every Paste Is Ours At Once; The Hook's Clipboard Is Built After This Event
        if (IsPasteKey(e.Key) && TestHooks.BlockLaunches)
        {
            e.Handled = true;
            _ = PasteFromTestHookAsync();
            return;
        }

        if (IsPasteKey(e.Key) && ClipboardView() is { } view && OwnsPaste(view))
        {
            e.Handled = true;
            Paste(view, owned: true);
            return;
        }

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
        _voice ??= new VoicePlayer(XamlHost.Dispatcher);
        _voice.Handle(request);
    }

    // Stops The Playing Voice Message And Lets Go Of The Player
    private void StopVoice()
    {
        _voice?.Dispose();
        _voice = null;
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

    // A Reply Quote's Click, Enter Or Space: Scroll To The Quoted Message (Paging Older Until It Shows, Up To A Cap), With
    // No Pulse (Owner, Plan 10). Not Found, Or The Chat Changed Meanwhile: Nothing Happens. Async Void Like A Click Handler, So A Failure
    // Reaches The App's Unhandled Error Log
    private async void OnQuoteRequested(object? sender, EventArgs e)
    {
        if (sender is not MessageBubble { Bubble.ReplyToId: { Length: > 0 } id } || Chat is not { } chat)
        {
            return;
        }

        MessageBubbleViewModel? target = await chat.FindForJumpAsync(id);
        if (target is null || Chat != chat || Scroller() is not { } scroller)
        {
            return;
        }

        // Off The Newest Messages: Nothing Pins The View Back Down
        _pinned = false;

        // Where It Sits Centered In The View (Where It Fits): Found By Bringing Its Row In, Then The View Goes Back Where
        // The Person Was Before Anything Draws, And Scrolls There Smoothly
        double from = scroller.VerticalOffset;
        MessageList.ScrollIntoView(target);
        MessageList.UpdateLayout();
        if (RowTop(scroller, target) is { } top && MessageList.ContainerFromItem(target) is { } container)
        {
            double height = container.As<FrameworkElement>().ActualHeight;
            double offset = scroller.VerticalOffset + top - Math.Max(0, (scroller.ViewportHeight - height) / 2);
            scroller.ChangeView(null, from, null, true);
            MessageList.UpdateLayout();
            await ScrollSmoothlyAsync(scroller, offset);
        }

        TestHooks.NoteState("reply-jump", target.Id);
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
    // BUBBLE MENU
    // =========================================================================

    // A Right-Click On A Bubble, Or The Application Key Or Shift + F10 On Its Row Or Inside It, Opens The Bubble Menu On
    // That Bubble; On A Link Preview Card, The Card's Menu. A Right-Click Opens It At The Pointer, The Keyboard Over The
    // Bubble's Back (Or The Card)
    private void OnMessageListContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is null)
        {
            return;
        }

        DependencyObject source = e.OriginalSource.As<DependencyObject>();
        (MessageBubble? view, FrameworkElement? card) = RowBubbleView(source) is { } row ? (row, null) : BubbleViewAround(source);
        if (view is null)
        {
            return;
        }

        e.Handled = true;
        if (card is not null)
        {
            _menuView = view;
            ShowMenuAt(PreviewMenu, card, e.TryGetPosition(card, out Windows.Foundation.Point onCard) ? onCard : null);
            return;
        }

        bool pointer = e.TryGetPosition(view, out Windows.Foundation.Point at);
        ShowBubbleMenu(view, pointer ? view : view.Anchor, pointer ? at : null);
    }

    // A Bubble's Hover Strip ⋮, Or Its ShowMenu
    private void OnMenuRequested(object? sender, FrameworkElement anchor)
    {
        if (sender is MessageBubble view)
        {
            ShowBubbleMenu(view, anchor, null);
        }
    }

    // Opens The Bubble Menu On A Bubble, With The Items It Takes, Keeping Its Hover Strip Shown Until The Menu Closes
    private void ShowBubbleMenu(MessageBubble view, FrameworkElement anchor, Windows.Foundation.Point? at)
    {
        if (view.Bubble is not { } bubble)
        {
            return;
        }

        if (!ReferenceEquals(view, _menuView))
        {
            _menuView?.KeepShown(false);
        }

        _menuView = view;
        view.KeepShown(true);

        // Open On A Photo, Video Or Voice Message Is The App's Own Viewer Or Player; On A File, Only A Type Safe To Launch
        bool canOpen = bubble.HasPhoto || bubble.HasVideo || bubble.HasVoice || bubble.CanOpenMedia;

        BubbleMenuCopy.Visibility     = bubble.HasCopyText ? Visibility.Visible : Visibility.Collapsed;
        BubbleMenuOpen.Visibility     = canOpen ? Visibility.Visible : Visibility.Collapsed;
        BubbleMenuOpenWith.Visibility = bubble.HasMediaFile ? Visibility.Visible : Visibility.Collapsed;
        BubbleMenuSaveAs.Visibility   = bubble.HasMediaFile ? Visibility.Visible : Visibility.Collapsed;
        BubbleMenuDownload.Visibility = bubble.HasMediaFile ? Visibility.Visible : Visibility.Collapsed;
        ShowMenuAt(BubbleMenu, anchor, at);
    }

    // A Menu At A Point On The Element (A Right-Click), Else At The Element Itself
    private static void ShowMenuAt(MenuFlyout menu, FrameworkElement anchor, Windows.Foundation.Point? at)
    {
        if (at is { } point)
        {
            menu.ShowAt(anchor, new Windows.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
            return;
        }

        menu.ShowAt(anchor);
    }

    // The Bubble An Element Sits In, And The Link Preview Card It Sits On (Null Off The Card): Found Going Up By Name, As
    // UiHelpers.FindPart Goes Down (AOT Safe). A Link In The Text Isn't In The Visual Tree, So Its Text Stands In For It
    private static (MessageBubble? View, FrameworkElement? Card) BubbleViewAround(DependencyObject element)
    {
        FrameworkElement? card = null;
        DependencyObject? at   = element is TextElement text ? text.ContentStart.VisualParent : element;
        for (; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is not FrameworkElement named)
            {
                continue;
            }

            if (named.Name == "Preview")
            {
                card ??= named;
            }
            else if (named.Name == "BubbleView")
            {
                return (named.As<MessageBubble>(), card);
            }
        }

        return (null, null);
    }

    // The Menus' Items Act On The Bubble The Menu Opened On. It Stays Set After The Menu Closes, Since An Item's Click Can
    // Come After The Close
    private void OnBubbleMenuCopyClick(object sender, RoutedEventArgs e) => _menuView?.Copy();

    private void OnBubbleMenuOpenClick(object sender, RoutedEventArgs e) => _menuView?.Open();

    private void OnBubbleMenuOpenWithClick(object sender, RoutedEventArgs e) => _menuView?.OpenWith();

    private void OnBubbleMenuSaveAsClick(object sender, RoutedEventArgs e) => _menuView?.SaveAs();

    private void OnBubbleMenuDownloadClick(object sender, RoutedEventArgs e) => _menuView?.Download();

    private void OnBubbleMenuReactClick(object sender, RoutedEventArgs e) => _menuView?.React();

    private void OnBubbleMenuReplyClick(object sender, RoutedEventArgs e) => _menuView?.Reply();

    private void OnPreviewMenuCopyLinkClick(object sender, RoutedEventArgs e) => _menuView?.CopyLink();

    // The Header's ⋮: The Menu Opens Below It
    private void OnMoreClick(object sender, RoutedEventArgs e) => MenuRequested?.Invoke(this, (MoreButton, null));

    private void OnMuteClick(object sender, RoutedEventArgs e) => MuteRequested?.Invoke(this, EventArgs.Empty);

    // Right-Click On The Header Opens The Menu At The Pointer; From The Keyboard (The ⋮ Focused) It Opens Below The ⋮
    private void OnHeaderContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        MenuRequested?.Invoke(this, e.TryGetPosition(HeaderBar, out Windows.Foundation.Point at) ? (HeaderBar, at) : (MoreButton, null));
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e) => ToggleEmojiPanel();

    // Win + Period, Pressed And Released. Ctrl And Shift Still Held From The Ctrl + Shift + e Shortcut Are Let Go First:
    // Windows Opens Nothing For Win + Ctrl + Shift + Period. True When Windows Took Every Key
    private static bool SendEmojiShortcut()
    {
        List<NativeMethods.KEYBOARD_INPUT> keys = [];
        if (UiHelpers.IsDown(VirtualKey.Control))
        {
            keys.Add(Key(NativeMethods.VK_CONTROL, NativeMethods.KEYEVENTF_KEYUP));
        }

        if (UiHelpers.IsDown(VirtualKey.Shift))
        {
            keys.Add(Key(NativeMethods.VK_SHIFT, NativeMethods.KEYEVENTF_KEYUP));
        }

        keys.Add(Key(NativeMethods.VK_LWIN, 0));
        keys.Add(Key(NativeMethods.VK_OEM_PERIOD, 0));
        keys.Add(Key(NativeMethods.VK_OEM_PERIOD, NativeMethods.KEYEVENTF_KEYUP));
        keys.Add(Key(NativeMethods.VK_LWIN, NativeMethods.KEYEVENTF_KEYUP));
        return SendKeys(keys);
    }

    // Presses And Releases Keys In One Go. A Refused Send (Another App Holds The Input) Just Does Nothing. Cut Short, Any
    // Key It Pressed But Didn't Release (Win Above All) Is Released, So No Key Is Left Held Down. True When Windows Took
    // Every Key
    private static bool SendKeys(List<NativeMethods.KEYBOARD_INPUT> keys)
    {
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

        return sent == keys.Count;
    }

    private static NativeMethods.KEYBOARD_INPUT Key(ushort virtualKey, uint flags) =>
        new() { type = NativeMethods.INPUT_KEYBOARD, wVk = virtualKey, dwFlags = flags };

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
        bool highContrast  = Access.HighContrast;
        bool shown         = MessageBubbleViewModel.ShowsFocusRing(row.FocusState != FocusState.Unfocused, row.FocusState == FocusState.Pointer, highContrast);

        if (highContrast)
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
        view.ShowFocusRing(shown, highContrast);
    }

    // The Bubble Of The Row With Keyboard Focus, Or Null When Focus Is Elsewhere (Also On A Link Inside A Row)
    private MessageBubble? FocusedBubbleView() =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused ? RowBubbleView(focused.As<DependencyObject>()) : null;

    // A Row's Bubble, Or Null When The Element Isn't One Of The List's Rows. Found By Its Name (See UiHelpers.FindPart)
    private MessageBubble? RowBubbleView(DependencyObject element) =>
        MessageList.IndexFromContainer(element) >= 0 ? UiHelpers.FindPart<MessageBubble>(element, "BubbleView") : null;

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
        _reactAnchor = anchor;
        view.KeepShown(true);

        // My Reaction Shows Selected: The Pill's Accent Fill Behind Its Button, And Narrator Reads "Selected"
        Style selected = ReactionButtons.Resources["ReactionSelectedBackStyle"].As<Style>();
        foreach ((Button button, Border back, string emoji) in _reactionButtons)
        {
            if (emoji == bubble.MyReaction)
            {
                back.Style = selected;
                AutomationProperties.SetItemStatus(button, ChatCopy.Selected);
            }
            else
            {
                back.ClearValue(StyleProperty);
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

    // The Picker's More Reactions: Closes The Picker, Then (A Turn Later, Once It Has Closed) Opens The Emoji Picker Where
    // It Was. The Windows Emoji Panel Isn't Used: It Types Into A Text Box, And Aimed At A Hidden One It Neither Reacted Nor
    // Closed (Owner's Report)
    private void OnMoreReactionsClick()
    {
        MessageBubbleViewModel? bubble = _reactTarget;
        MessageBubble? view            = _reactView;
        FrameworkElement? anchor       = _reactAnchor;
        ReactionPicker.Hide();
        if (bubble is null || view is null || anchor is null)
        {
            return;
        }

        // Opened Once The Reaction Picker's Closed Has Run: Before That, Its Close Hides The Hover Strip The Picker Opens Over,
        // And The Emoji Picker Didn't Show (Seen On A Cold First Run)
        _emojiPending = (bubble, view, anchor);
    }

    // The Emoji Picker: A Search Box Over The Catalog's Emoji By Category, Scrolling, In A Flyout Where The Reaction Picker Was
    // (EmojiPicker; Buttons EmojiPicker.{Code Points In Hex}). A Click Sends That Emoji As The Reaction And Closes It; Escape
    // Or A Click Outside Closes It. Focus Starts In The Search Box. The Bubble's Hover Strip Stays Until It Closes
    private void ShowEmojiPicker(MessageBubbleViewModel bubble, MessageBubble view, FrameworkElement anchor)
    {
        // Timed (Milliseconds Only): The First Open Froze The App When It Drew Every Emoji At Once (See EmojiRowFactory)
        long start     = System.Diagnostics.Stopwatch.GetTimestamp();
        Flyout picker  = _emojiPicker ??= BuildEmojiPicker();
        long built     = System.Diagnostics.Stopwatch.GetTimestamp();
        int open       = ++_emojiOpens;
        _emojiTarget   = bubble;
        if (_emojiSearch is { } search)
        {
            search.Text = string.Empty;
        }

        FilterEmoji();

        view.KeepShown(true);
        picker.Closed += OnClosed;
        picker.ShowAt(anchor);

        // Timed For Two Seconds From The Open: The Longest Gap Between Frames Is How Long The App Stood Still
        long last  = System.Diagnostics.Stopwatch.GetTimestamp();
        long stall = 0;
        Windows.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        void OnRendering(object? sender, object e)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            stall    = Math.Max(stall, (long)System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last     = now;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start, now).TotalSeconds < 2)
            {
                return;
            }

            Windows.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            long buildMs  = (long)System.Diagnostics.Stopwatch.GetElapsedTime(start, built).TotalMilliseconds;
            int rows      = _emojiFactory?.Made ?? 0;
            AppLog.EmojiPickerShown(App.ShellLog, open, buildMs, stall, rows);
        }

        void OnClosed(object? sender, object e)
        {
            picker.Closed -= OnClosed;
            view.KeepShown(false);
            _emojiTarget = null;
        }
    }

    // Builds The Emoji Picker Once: Its Search Box, Its Empty Note, And Its Rows In A Scroller. The Rows Are Virtualized
    // (An ItemsRepeater), So Only Those In View Are Made, Then Reused As They Scroll Out. The Rows Fill The Picker's Width,
    // Edge To Edge With The Search Box; The Scroll Bar Overlays Them The Windows Way
    private Flyout BuildEmojiPicker()
    {
        Style buttonStyle  = ReactionButtons.Resources["ReactionButtonStyle"].As<Style>();
        Style noteStyle    = ReactionButtons.Resources["EmojiPickerNoteStyle"].As<Style>();
        var flyout         = new Flyout { Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };

        // Search Box
        _emojiSearch = new TextBox { PlaceholderText = ChatCopy.SearchEmoji, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(_emojiSearch, "EmojiPicker.Search");
        AutomationProperties.SetName(_emojiSearch, ChatCopy.SearchEmoji);
        _emojiSearch.TextChanged += (_, _) => FilterEmoji();

        // Empty Search
        _emojiEmpty = new TextBlock { Text = ChatCopy.NoEmojiFound, Margin = new Thickness(4, 8, 0, 8), Style = noteStyle, Visibility = Visibility.Collapsed };

        // Rows: About Half A Screen Above And Below The View Is Made Ahead
        _emojiFactory = new EmojiRowFactory(EmojiPickerColumns, EmojiCellSize, buttonStyle, noteStyle, PickEmoji);
        _emojiRows    = new Muxc.ItemsRepeater { ItemTemplate = _emojiFactory, VerticalCacheLength = 1 };
        _emojiScroller = new ScrollViewer { Content = _emojiRows, Height = EmojiPickerHeight, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var root = new StackPanel { Width = EmojiPickerColumns * EmojiCellSize, Children = { _emojiSearch, _emojiEmpty, _emojiScroller } };
        AutomationProperties.SetAutomationId(root, "EmojiPicker");
        AutomationProperties.SetName(root, ChatCopy.EmojiPicker);
        flyout.Content = root;
        return flyout;
    }

    // The Search Box's Words (See EmojiCatalog.Search): The Matching Categories, Each A Name Row Over Rows Of Up To Eight
    // Emoji, From The Top. Nothing Typed Shows Them All; No Match At All Shows The Empty Note
    private void FilterEmoji()
    {
        if (_emojiRows is null || _emojiEmpty is null)
        {
            return;
        }

        var rows = new List<EmojiRow>();
        foreach ((string name, IReadOnlyList<(string Emoji, string Words)> emoji) in EmojiCatalog.Search(_emojiSearch?.Text))
        {
            rows.Add(new EmojiRow(name, []));
            for (int i = 0; i < emoji.Count; i += EmojiPickerColumns)
            {
                rows.Add(new EmojiRow(null, [.. emoji.Skip(i).Take(EmojiPickerColumns)]));
            }
        }

        _emojiRows.ItemsSource = rows;
        _emojiEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emojiScroller?.ChangeView(null, 0, null, disableAnimation: true);
    }

    // A Picked Emoji: Closes The Picker And Sends It As The Reaction (Custom When It Isn't One Of The Seven)
    private void PickEmoji(string emoji)
    {
        MessageBubbleViewModel? bubble = _emojiTarget;
        _emojiPicker?.Hide();
        if (bubble is not null)
        {
            SendReaction(bubble, emoji);
        }
    }

    // Sends A Reaction For A Bubble (Add, Switch Or Remove; See ChatViewModel.ReactAsync)
    private void SendReaction(MessageBubbleViewModel bubble, string emoji)
    {
        if (Chat is { } chat)
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
        XamlHost.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => DraftBox.Focus(FocusState.Keyboard));
    }

    // A Bubble's Row Top In The View, Or Null While Its Row Isn't Realized
    private double? RowTop(ScrollViewer scroller, MessageBubbleViewModel bubble) =>
        MessageList.ContainerFromItem(bubble)?.As<UIElement>().TransformToVisual(scroller).TransformPoint(default).Y;

    // The List's Bottom Padding Keeps The Newest Bubble Above What Floats Over It (The Typing Bubble, The Chips, The Message
    // Box: The Pane's Last Three Rows), And The Bottom Fade Covers Just That. Runs After Every Layout, So Only A Change
    // Sets Anything (Padding Changes The Content Height, Which KeepPinned Follows)
    private void KeepComposerClear()
    {
        double below = PaneGrid.RowDefinitions[2].ActualHeight + PaneGrid.RowDefinitions[3].ActualHeight + PaneGrid.RowDefinitions[4].ActualHeight;
        if (Math.Abs(below - _composerHeight) < 0.5)
        {
            return;
        }

        _composerHeight     = below;
        MessageList.Padding = new Thickness(12, 8, 12, 8 + below);
        _fade?.SetBottomFade(below + 8);
    }

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
        _         = ScrollSmoothlyAsync(scroller, scroller.ScrollableHeight);
    }

    // Every Jump The Chat Makes On Its Own (To The Newest Message, To A Reply's Original) Goes Through Here: A Smooth
    // Scroll, Like CSS's scroll-behavior: smooth, Or At Once With Windows' Animation Effects Off. The Task Ends When The
    // View Comes To Rest (At Most 1.5 s). Not For The Chat's First Place At The Bottom Or The Quiet Corrections That Keep
    // The View Still (KeepPinned), Which Stays Instant
    private static async Task ScrollSmoothlyAsync(ScrollViewer scroller, double offset)
    {
        offset = Math.Clamp(offset, 0, scroller.ScrollableHeight);
        if (Math.Abs(offset - scroller.VerticalOffset) < 1)
        {
            return;
        }

        bool smooth = Ui.AnimationsEnabled;
        TestHooks.NoteState("chat-scroll", smooth ? "Smooth" : "Instant");
        if (!smooth)
        {
            scroller.ChangeView(null, offset, null, disableAnimation: true);
            return;
        }

        var rested = new TaskCompletionSource();
        void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!e.IsIntermediate)
            {
                rested.TrySetResult();
            }
        }

        scroller.ViewChanged += OnViewChanged;
        scroller.ChangeView(null, offset, null, disableAnimation: false);
        await Task.WhenAny(rested.Task, Task.Delay(TimeSpan.FromSeconds(1.5)));
        scroller.ViewChanged -= OnViewChanged;
    }

    // Fades The Scroll-To-Bottom Button In Or Out (Its OpacityTransition Animates It); Hidden, It Takes No Clicks Or Focus
    private void ShowScrollToBottom(bool show)
    {
        ScrollToBottomButton.Opacity          = show ? 1 : 0;
        ScrollToBottomButton.IsHitTestVisible = show;
        ScrollToBottomButton.IsTabStop        = show;
    }

    private static bool IsShiftDown() => UiHelpers.IsDown(VirtualKey.Shift);
}

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
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
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

    // Fetches A Pasted Web Picture (See PastedWebGifAsync) As The Link Previewer Does: Vetted Public Hosts, No Cookies
    private static readonly Lazy<HttpClient> WebPictures = new(() =>
        new HttpClient(LinkPreviewer.CreateHandler(TestHooks.AllowLoopbackPreviews)) { Timeout = TimeSpan.FromSeconds(15) });

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

    // The Bubble The Reaction Picker Is Open For, Its View (Whose Hover Strip Stays While The Picker Is Open), And What
    // The Picker Opened Over
    private MessageBubbleViewModel? _reactTarget;
    private MessageBubble? _reactView;
    private FrameworkElement? _reactAnchor;

    // The Emoji Picker (Made On First Use), Its Search Box, Its Rows (Virtualized: Only Those In View Are Made), Its Empty
    // Note, The Bubble It Picks A Reaction For, And How Many Times It Has Opened (For The Timing Log)
    private Flyout? _emojiPicker;
    private TextBox? _emojiSearch;
    private ItemsRepeater? _emojiRows;
    private ScrollViewer? _emojiScroller;
    private EmojiRowFactory? _emojiFactory;
    private TextBlock? _emojiEmpty;
    private MessageBubbleViewModel? _emojiTarget;
    private int _emojiOpens;

    // More Reactions Picked: What The Emoji Picker Opens For Once The Reaction Picker Has Closed
    private (MessageBubbleViewModel Bubble, MessageBubble View, FrameworkElement Anchor)? _emojiPending;

    // The Picker's Buttons With The Fill Behind Each And Their Emoji, So The One Matching My Reaction Can Show Selected
    private readonly List<(Button Button, Border Back, string Emoji)> _reactionButtons = [];

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

        // A Recycled Row Starts Below Its Neighbors Again (A Focused Row Was Raised For Its Ring)
        MessageList.ContainerContentChanging += (_, args) => Canvas.SetZIndex(args.ItemContainer, 0);

        // Recording: The Time And The Input Level, 20 Times A Second, Over A Live Waveform As Wide As The Bar Allows
        RecordBars.FillsWidth = true;
        _recordTimer          = DispatcherQueue.CreateTimer();
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

        return DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => SendEmojiShortcut());
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
        MessageBubble.StopInlineVideo();
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

    // Enter Sends; Shift + Enter Is Left To The Box, Which Adds A Line. Ctrl + V Or Shift + Insert With Media On The
    // Clipboard Pastes It As Chips (The Box Would Ignore A Picture)
    private void OnDraftKeyDown(object sender, KeyRoutedEventArgs e)
    {
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

        // Test Run: How Far The Bars Strayed From The Center Line (Measured Only Then: It Walks Every Bar)
        if (TestHooks.BlockLaunches)
        {
            TestHooks.NoteState("record-drift", RecordBars.CenterDrift().ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        }
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
            await AttachFilesAsync(chat, files);
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

    // Files Picked Or Pasted, Each Checked For Size, Then Added As Chips In Order. Only A Photo's Header Is Read Now (For
    // Its Shape), And A Video's Thumbnail When Windows Has One; Send Reads The Files
    private static async Task AttachFilesAsync(ChatViewModel chat, IReadOnlyList<StorageFile> files)
    {
        foreach (StorageFile file in files)
        {
            Windows.Storage.FileProperties.BasicProperties properties = await file.GetBasicPropertiesAsync();
            if (!chat.CanAttach((long)properties.Size))
            {
                continue;
            }

            string mime                   = PhotoMimes.GetValueOrDefault(file.FileType) ?? MediaMimes.GetValueOrDefault(file.FileType) ?? (file.ContentType.Length > 0 ? file.ContentType : "application/octet-stream");
            (int width, int height)       = mime.StartsWith("image/", StringComparison.Ordinal) ? await PhotoSizeAsync(file) : (0, 0);
            Func<Task<Stream>>? thumbnail = mime.StartsWith("video/", StringComparison.Ordinal) ? await VideoThumbnailAsync(file) : null;
            chat.Attach(file.Name, mime, (long)properties.Size, () => file.OpenStreamForReadAsync(), width, height, thumbnail);
        }

        AppLog.AttachmentsPicked(App.ShellLog, chat.ConversationId, files.Count, chat.Attachments.Count);
    }

    // A Video's Frame As Windows Shows It In Explorer, For Its Chip; Null When Windows Only Has An Icon For It (Or None)
    private static async Task<Func<Task<Stream>>?> VideoThumbnailAsync(StorageFile file)
    {
        try
        {
            using Windows.Storage.FileProperties.StorageItemThumbnail? thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, ChipSide);
            if (thumbnail is null || thumbnail.Type != Windows.Storage.FileProperties.ThumbnailType.Image)
            {
                return null;
            }

            using var copy = new MemoryStream();
            await thumbnail.AsStreamForRead().CopyToAsync(copy);
            byte[] bytes = copy.ToArray();
            return () => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
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

    // =========================================================================
    // PASTING
    // =========================================================================

    // The Clipboard (The Test's Under The Test Hooks, When It Set One); Null When Windows Won't Hand It Over (Another App Holds It)
    private static DataPackageView? ClipboardView()
    {
        if (TestHooks.PasteContent() is { } test)
        {
            return test;
        }

        try
        {
            return Clipboard.GetContent();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // Media On The Clipboard: Files (Explorer, The Emoji Panel's GIFs), Or A Picture With No Text Beside It (A Screenshot,
    // A Browser's Copy Image). Text With Pictures (A Copied Web Page Or Spreadsheet) Pastes As Text
    private static bool CarriesMedia(DataPackageView view) =>
        view.Contains(StandardDataFormats.StorageItems)
        || (!view.Contains(StandardDataFormats.Text) && (view.Contains(StandardDataFormats.Bitmap) || PastedPictures.Any(p => view.Contains(p.Format))));

    // The Paste Is Ours When It Carries Media; Under The Test Hooks Every Paste Is (The Box Would Read The Real Clipboard)
    private static bool OwnsPaste(DataPackageView view) => CarriesMedia(view) || TestHooks.BlockLaunches;

    // Ctrl + V And Shift + Insert
    private static bool IsPasteKey(VirtualKey key) =>
        (key == VirtualKey.V && IsDown(VirtualKey.Control)) || (key == VirtualKey.Insert && IsShiftDown());

    // The Box's Own Paste (Its Menu's Paste, Or Any Paste The Keys Above Didn't Take): Media Becomes Chips, Text Is Left To It
    private void OnDraftPaste(object sender, TextControlPasteEventArgs e)
    {
        if (ClipboardView() is not { } view)
        {
            return;
        }

        e.Handled = OwnsPaste(view);
        Paste(view, e.Handled);
    }

    // Pastes What The Clipboard Holds, Logging Its Formats Only (Never Its Content): Media Becomes Chips; Under The Test
    // Hooks Text Goes Into The Box As The Box Would Paste It
    private void Paste(DataPackageView view, bool owned)
    {
        if (Chat is not { } chat)
        {
            return;
        }

        bool media = CarriesMedia(view);
        AppLog.PasteFormats(App.ShellLog, chat.ConversationId, view.AvailableFormats, media);
        if (!owned)
        {
            return;
        }

        _ = media ? AttachPastedAsync(chat, view) : PasteTextAsync(view);
    }

    // Test Run: The Hook's Text, Typed Over The Selection
    private async Task PasteTextAsync(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.Text))
        {
            DraftBox.SelectedText = await view.GetTextAsync();
            DraftBox.Select(DraftBox.SelectionStart + DraftBox.SelectionLength, 0);
        }
    }

    // Pasted Media As Chips, Under The Same Limits As Picked Files: The Files Each As Picked; Else A Picture In Its Own
    // Format (Kept Byte For Byte, So A GIF Still Moves On The Phone); Else The Bitmap, Saved As A PNG. A Failure Is Logged
    // By Its Error Type Only
    private async Task AttachPastedAsync(ChatViewModel chat, DataPackageView view)
    {
        try
        {
            chat.ClearAttachError();
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await view.GetStorageItemsAsync();
                await AttachFilesAsync(chat, items.Where(i => i.IsOfType(StorageItemTypes.File)).Select(i => i.As<StorageFile>()).ToList());
            }
            else if ((await PastedPictureAsync(view) ?? await PastedWebGifAsync(view)) is { } picture)
            {
                (int width, int height) = await PictureSizeAsync(picture.Bytes);
                chat.Attach(picture.Name, picture.Mime, picture.Bytes, width, height);
            }
            else if (view.Contains(StandardDataFormats.Bitmap))
            {
                (byte[] png, int width, int height) = await BitmapToPngAsync(await view.GetBitmapAsync());
                chat.Attach(PastedBitmapName, "image/png", png, width, height);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "paste", chat.ConversationId, ex.GetType().Name);
        }

        DraftBox.Focus(FocusState.Programmatic);
    }

    // The First Picture On The Clipboard In Its Own Format (See PastedPictures), Or Null
    private static async Task<(string Name, string Mime, byte[] Bytes)?> PastedPictureAsync(DataPackageView view)
    {
        foreach ((string format, string mime, string name) in PastedPictures)
        {
            if (!view.Contains(format))
            {
                continue;
            }

            using IRandomAccessStream stream = (await view.GetDataAsync(format)).As<IRandomAccessStream>();
            using var copy                   = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(copy);
            return (name, mime, copy.ToArray());
        }

        return null;
    }

    // The GIF A Pasted Web Picture Links To, Or Null. The Emoji Panel's GIFs Come As HTML (An <img> Of The GIF On Tenor) Beside
    // A Bitmap Of Its First Frame, With No GIF Or File Format, So Pasting The Bitmap Sent A Still PNG. The Picture Is Fetched As
    // A Link Preview Is (Public Hosts Only, No Cookies, No Redirects) And Kept Only When It Is A GIF Within The Attachment Limit
    private static async Task<(string Name, string Mime, byte[] Bytes)?> PastedWebGifAsync(DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.Html))
        {
            return null;
        }

        string html = HtmlFormatHelper.GetStaticFragment(await view.GetHtmlFormatAsync());
        if (ImageSource().Match(html) is not { Success: true } match
            || !Uri.TryCreate(System.Net.WebUtility.HtmlDecode(match.Groups[1].Value), UriKind.Absolute, out Uri? url)
            || !LinkPreviewer.IsAllowed(url, TestHooks.AllowLoopbackPreviews))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await WebPictures.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType is not "image/gif"
                || response.Content.Headers.ContentLength > ChatStore.MaxAttachmentBytes)
            {
                return null;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync();
            using var copy          = new MemoryStream();
            byte[] buffer           = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                copy.Write(buffer, 0, read);
                if (copy.Length > ChatStore.MaxAttachmentBytes)
                {
                    return null;
                }
            }

            // A GIF By Its Bytes Too ("GIF8"), Whatever The Server Said
            byte[] bytes = copy.ToArray();
            return bytes is [0x47, 0x49, 0x46, 0x38, ..] ? (PastedGifName, "image/gif", bytes) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            AppLog.MediaFailed(App.ShellLog, "paste-gif", string.Empty, ex.GetType().Name);
            return null;
        }
    }

    // An <img>'s src In Pasted HTML
    [System.Text.RegularExpressions.GeneratedRegex("""<img\b[^>]*?\bsrc\s*=\s*["']([^"']+)["']""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ImageSource();

    // A Pasted Bitmap, Encoded As A PNG, With Its Size
    private static async Task<(byte[] Png, int Width, int Height)> BitmapToPngAsync(RandomAccessStreamReference bitmap)
    {
        using IRandomAccessStreamWithContentType source = await bitmap.OpenReadAsync();
        BitmapDecoder decoder                           = await BitmapDecoder.CreateAsync(source);
        using SoftwareBitmap pixels                     = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        using var output      = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(pixels);
        await encoder.FlushAsync();

        using var copy = new MemoryStream();
        output.Seek(0);
        await output.AsStreamForRead().CopyToAsync(copy);
        return (copy.ToArray(), pixels.PixelWidth, pixels.PixelHeight);
    }

    // A Pasted Picture's Size, Read From Its Header; 0 x 0 When It Won't Decode
    private static async Task<(int Width, int Height)> PictureSizeAsync(byte[] bytes)
    {
        try
        {
            using IRandomAccessStream stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            BitmapDecoder decoder            = await BitmapDecoder.CreateAsync(stream);
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (0, 0);
        }
    }

    // The Box's Menu: Windows Shows Its Paste Only For Text, So With Media On The Clipboard Windows' Own Paste Command Is
    // Added (Its Name And Icon Come From Windows), And Taken Away Again When The Menu Closes
    private void OnDraftMenuOpening(object? sender, object e)
    {
        // The Menu Is Windows' Shared Text Menu: Only When It Opens On The Message Box
        if (DraftBox.ContextFlyout is not { } menu || menu.Target != DraftBox || DraftBox.CanPasteClipboardContent || ClipboardView() is not { } view || !CarriesMedia(view))
        {
            return;
        }

        var command = new StandardUICommand(StandardUICommandKind.Paste);
        command.ExecuteRequested += (_, _) =>
        {
            if (ClipboardView() is { } now)
            {
                Paste(now, owned: CarriesMedia(now));
            }
        };

        var paste = new AppBarButton { Command = command };
        AutomationProperties.SetAutomationId(paste, "Chat.PasteMedia");
        CommandBarFlyout bar = menu.As<CommandBarFlyout>();
        bar.PrimaryCommands.Add(paste);
        menu.Closed += OnClosed;

        void OnClosed(object? s, object a)
        {
            menu.Closed -= OnClosed;
            bar.PrimaryCommands.Remove(paste);
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
    // Windows Opens Nothing For Win + Ctrl + Shift + Period. True When Windows Took Every Key
    private static bool SendEmojiShortcut()
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
        _emojiSearch!.Text = string.Empty;
        FilterEmoji();

        view.KeepShown(true);
        picker.Closed += OnClosed;
        picker.ShowAt(anchor);

        // Timed For Two Seconds From The Open: The Longest Gap Between Frames Is How Long The App Stood Still
        long last  = System.Diagnostics.Stopwatch.GetTimestamp();
        long stall = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        void OnRendering(object? sender, object e)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            stall    = Math.Max(stall, (long)System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last     = now;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start, now).TotalSeconds < 2)
            {
                return;
            }

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            long build_ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(start, built).TotalMilliseconds;
            int rows      = _emojiFactory?.Made ?? 0;
            AppLog.EmojiPickerShown(App.ShellLog, open, build_ms, stall, rows);
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
        Style button_style = ReactionButtons.Resources["ReactionButtonStyle"].As<Style>();
        Style note_style   = ReactionButtons.Resources["EmojiPickerNoteStyle"].As<Style>();
        var flyout         = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };

        // Search Box
        _emojiSearch = new TextBox { PlaceholderText = ChatCopy.SearchEmoji, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(_emojiSearch, "EmojiPicker.Search");
        AutomationProperties.SetName(_emojiSearch, ChatCopy.SearchEmoji);
        _emojiSearch.TextChanged += (_, _) => FilterEmoji();

        // Empty Search
        _emojiEmpty = new TextBlock { Text = ChatCopy.NoEmojiFound, Margin = new Thickness(4, 8, 0, 8), Style = note_style, Visibility = Visibility.Collapsed };

        // Rows: About Half A Screen Above And Below The View Is Made Ahead
        _emojiFactory = new EmojiRowFactory(EmojiPickerColumns, EmojiCellSize, button_style, note_style, PickEmoji);
        _emojiRows    = new ItemsRepeater { ItemTemplate = _emojiFactory, VerticalCacheLength = 1 };
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
                rows.Add(new EmojiRow(null, emoji.Skip(i).Take(EmojiPickerColumns).ToList()));
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

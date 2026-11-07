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
/// Shows the open conversation: the header with the lock, the message bubbles, the typing dots and the compose area.
/// The reply chip and the attachment chips join the top of the message box.
/// </summary>
/// <remarks>
/// <para>
/// The pane shows the chat in <see cref="Chat"/>, which the main window sets from <see cref="MainViewModel.Chat"/>.
/// <see cref="BindableList"/> wraps the bubbles, and that class tells why. The list stays at the newest bubble as
/// messages arrive. A scroll to the top loads the older page. A page too short to scroll loads the next older page.
/// That repeats until the list can scroll or no older pages are left.
/// </para>
/// <para>
/// A chat that opens puts the cursor in the message box. Enter sends the draft, and Shift + Enter adds a line. The
/// emoji button opens the Windows emoji panel on the message box.
/// </para>
/// <para>
/// The paperclip <c>Chat.Attach</c> and Ctrl + Shift + a open the file picker. Ctrl + Shift + p opens it for photos.
/// Each picked file becomes a chip above the box, and Send reads it. The mic <c>Chat.Record</c> records a voice
/// message. The recording bar then covers the box with the time, the live waveform, Cancel recording and Done. A
/// finished recording waits as the first chip, which plays and seeks, and it sends first.
/// </para>
/// <para>
/// One reaction picker <c>ReactionPicker</c> serves every bubble, with the buttons <c>ReactionPicker.1</c> to
/// <c>.7</c>. The menu of a bubble or Shift + + on its row opens the picker over that bubble. The React button of the
/// hover strip opens it over that button. Reply or Ctrl + Enter on a row starts the reply and moves to the message box.
/// </para>
/// <para>
/// One bubble menu <c>BubbleMenu</c> also serves every bubble. A right-click opens it at the pointer. The Application
/// key or Shift + F10 on a row opens it over the back of the bubble. The ⋮ of the hover strip opens it at the ⋮. Its
/// items act on the bubble that it opened on. A link preview card opens its own menu <c>PreviewMenu</c> with Copy link.
/// </para>
/// <para>
/// The ⋮ <c>Chat.More</c> of the header or a right-click on the header raises <see cref="MenuRequested"/>. The chats
/// then show the menu of the conversation.
/// </para>
/// <para>
/// While the other person types, the typing bubble <c>Chat.Typing</c> shows 3 dots that bounce in turn. Google
/// Messages and Teams do the same. A Composition loop moves them only while the bubble shows. The loop does not run
/// when the animation effects of Windows are off.
/// </para>
/// </remarks>
public sealed partial class ChatPane : UserControl
{
    /// <summary>Identifies the <see cref="Chat"/> property.</summary>
    public static readonly DependencyProperty ChatProperty =
        DependencyProperty.Register(nameof(Chat), typeof(ChatViewModel), typeof(ChatPane), new PropertyMetadata(null, OnChatChanged));

    // The reactions of LibGM are EmojiType 1 to 7.
    private const int ReactionCount = 7;

    // A picker button, its hover tint and its selected fill share these corners. See ReactionButtonStyle in the XAML.
    private const double ReactionCorner = 8;

    private const double MoreReactionsSize = 40;
    private const int EmojiPickerColumns   = 8;
    private const double EmojiPickerHeight = 280;
    private const double EmojiCellSize     = 44;

    // The corners of the message box match the control corner radius of Windows. ComposeInset and ComposeTop are the
    // padding of the compose area.
    private const double DraftCorner  = 4;
    private const double ComposeInset = 12;
    private const double ComposeTop   = 8;

    // An icon that keeps its own color shows at this opacity while its button is disabled.
    private const double DisabledOpacity = 0.4;

    // The side of a photo chip, in effective pixels.
    private const int ChipSide = 64;

    // The name of the recording preview in the voice player.
    private const string PreviewKey = "Chat.VoicePreview";

    private static readonly TimeSpan RecordTick = TimeSpan.FromMilliseconds(50);

    // The photo types of the spec, with their MIME types. Windows does not always know them. A PC whose registry has
    // no content type for .png reports none.
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

    // Video and audio types for a PC whose registry has no content type for them. With these, the phone shows a video
    // as a video. MediaFormatsMap falls back on the part before the slash.
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

    // A pasted picture in its own clipboard format, with the MIME type and the name that it sends as. The paste checks
    // these formats before the plain bitmap. Thus a GIF keeps its frames and a PNG keeps its transparency.
    private static readonly (string Format, string Mime, string Name)[] PastedPictures =
    [
        ("GIF", "image/gif", "image.gif"),
        ("image/gif", "image/gif", "image.gif"),
        ("PNG", "image/png", "image.png"),
        ("image/png", "image/png", "image.png"),
    ];

    // A pasted bitmap such as a screenshot sends as a PNG with this name. A web GIF from the emoji panel sends as a GIF
    // with the second name.
    private const string PastedBitmapName = "image.png";
    private const string PastedGifName    = "image.gif";

    // Each typing dot rises this far and back within its beat.
    private const float TypingRise = 4;

    // A chat switch fades in over the faster control animation time of WinUI. The normal 250 ms felt slow.
    private static readonly TimeSpan ChatFadeIn = TimeSpan.FromMilliseconds(83);

    // Each typing dot rises within its beat of every cycle. The next dot starts its beat after the stagger.
    private static readonly TimeSpan TypingCycle   = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan TypingStagger = TimeSpan.FromMilliseconds(150);

    // The focus visual of Windows has a 2 px outer ring and a 1 px inner ring. High contrast puts it back on a
    // focused row.
    private static readonly Thickness SystemFocusPrimary   = new(2);
    private static readonly Thickness SystemFocusSecondary = new(1);

    // Reads the animation setting of Windows live: Settings > Accessibility > Visual effects.
    private static readonly UISettings Ui = new();

    // Reads the high contrast state: Settings > Accessibility > Contrast themes.
    private static readonly AccessibilitySettings Access = new();

    private ScrollViewer? _scroller;

    // The bottom fade of the list behind the message box. The height is how much of the list the typing bubble, the
    // chips and the message box cover.
    private ScrollFade? _fade;
    private double _composerHeight = -1;

    // OnBubbleBringIntoView keeps a bubble that takes focus this far from the edge of the view.
    private const double BringMargin = 8;
    private bool _bringHooked;

    // True while the list stays at the newest message. See ChatScroll.StaysPinned.
    private bool _pinned = true;

    // The opened chat whose first drawn bubbles the log still waits for, or null.
    private ChatViewModel? _firstDrawFor;

    // The content and view heights that the list last had.
    private double _lastExtent;
    private double _lastViewport;

    // The bubble that the reaction picker is open for, and its view. The hover strip of the view stays while the picker
    // is open. The anchor is the element that the picker opened over.
    private MessageBubbleViewModel? _reactTarget;
    private MessageBubble? _reactView;
    private FrameworkElement? _reactAnchor;

    // The emoji picker, which the pane makes on first use. Its rows are virtualized, so it makes only the rows in view.
    // The pane counts the opens for the timing log.
    private Flyout? _emojiPicker;
    private TextBox? _emojiSearch;
    private Muxc.ItemsRepeater? _emojiRows;
    private ScrollViewer? _emojiScroller;
    private EmojiRowFactory? _emojiFactory;
    private TextBlock? _emojiEmpty;
    private MessageBubbleViewModel? _emojiTarget;
    private int _emojiOpens;

    // The bubble that More reactions picked. The emoji picker opens for it after the reaction picker closes.
    private (MessageBubbleViewModel Bubble, MessageBubble View, FrameworkElement Anchor)? _emojiPending;

    // The bubble that the bubble menu or the preview card menu last opened on. The menu items act on it. A chat switch
    // clears it.
    private MessageBubble? _menuView;

    // The picker buttons with the fill behind each button and its emoji. The button that matches my reaction shows
    // selected.
    private readonly List<(Button Button, Border Back, string Emoji)> _reactionButtons = [];

    // True while the list scrolls to the newest message. See ScrollToNewest.
    private bool _toNewest;

    // True when the arrow keys of the list open the next chat. The focus then stays on the list.
    private bool _keepFocus;

    // True while a file picker is open or the pane adds its files. Another request waits until it ends.
    private bool _picking;

    // Plays the voice messages of the chat, one at a time. The first play makes it. A chat switch or an unload
    // releases it.
    private VoicePlayer? _voice;

    // The recorder and its file while a recording runs, and the timer that shows its time and level. The time is the
    // length before the last pause, plus the run since the start or the resume.
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

        // The scroll-to-bottom button floats over the bubbles with its shadow. XAML cannot set Translation on a custom
        // button, so the code sets it.
        ScrollToBottomButton.Translation = new Vector3(0, 0, 16);

        // The reaction picker has a button per reaction in enum order, over a fill that shows my reaction selected. The
        // panel styles the buttons. Love shows the red heart of Google instead of heart eyes.
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

        // More reactions opens the emoji picker of the app, which offers any emoji.
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

        // When the bubble menu closes, the hover strip of its bubble hides unless the pointer is on it.
        BubbleMenu.Closed += (_, _) => _menuView?.KeepShown(false);

        ReactionPicker.Closed += (_, _) =>
        {
            _reactView?.KeepShown(false);
            _reactView = null;

            // When More reactions closed the picker, the emoji picker opens now. See OnMoreReactionsClick.
            if (_emojiPending is { } pending)
            {
                _emojiPending = null;
                ShowEmojiPicker(pending.Bubble, pending.View, pending.Anchor);
            }
        };

        // The Paste of the right-click menu of the message box also takes a copied picture or file.
        TextMenu.Attach(DraftBox, PasteMediaFromMenu);

        // The typing dots bounce only while their bubble shows.
        TypingBubble.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => AnimateTypingDots(TypingBubble.Visibility == Visibility.Visible));

        // The events of the static settings hold the pane only while it is loaded. Thus they do not keep the pane of a
        // closed window alive.
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

        MessageList.ContainerContentChanging += OnMessageContainerChanging;

        // The recording bar shows the time and the input level 20 times a second. The live waveform fills the bar.
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
    /// A user control has no automation peer of its own. Thus UI Automation could not read the bounds of the pane. The
    /// bounds are those of the chat card inside its 1 px border. The layout tests align the conversation list with
    /// them.
    /// </remarks>
    /// <returns>The peer of the pane.</returns>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    /// <summary>Gets the name of the close button of the reply chip.</summary>
    public static string CancelReplyText => ChatCopy.CancelReply;

    /// <summary>Gets the tooltip and name of the attach button.</summary>
    public static string AttachText => ChatCopy.Attach;

    /// <summary>Gets the tooltip and name of the remove button of a chip.</summary>
    public static string RemoveAttachmentText => ChatCopy.RemoveAttachment;

    /// <summary>Gets the tooltip and name of the ⋮ of the header.</summary>
    public static string MoreOptionsText => ChatCopy.MoreOptions;

    /// <summary>Gets the tooltip and name of the mute button of the header, which tell what a click does.</summary>
    /// <param name="isMuted">True while the chat is muted.</param>
    /// <returns>Unmute while muted, else Mute.</returns>
    public static string MuteText(bool isMuted) => isMuted ? ChatCopy.Unmute : ChatCopy.Mute;

    /// <summary>Gets the icon of the mute button. It shows the state of the chat as the ⋮ menu does.</summary>
    /// <param name="isMuted">True while the chat is muted.</param>
    /// <returns>The crossed-out bell while muted, else the bell.</returns>
    public static string MuteGlyph(bool isMuted) => isMuted ? "" : "";

    /// <summary>Occurs on a click of the mute button. The chats then mute or unmute the open chat.</summary>
    public event EventHandler? MuteRequested;

    /// <summary>Gets the tooltip and name of the mic button.</summary>
    public static string RecordText => ChatCopy.RecordVoiceMessage;

    /// <summary>Gets the tooltip and name of the Done button of the recording.</summary>
    public static string DoneText => ChatCopy.Done;

    /// <summary>Gets the tooltip and name of the Cancel recording button.</summary>
    public static string CancelRecordingText => ChatCopy.CancelRecording;

    /// <summary>
    /// Occurs on a click of the header ⋮ or a right-click on the header. The Application key and Shift + F10 there also
    /// raise it. The chats show the menu of the conversation at the target, and at the point when there is one.
    /// </summary>
    public event EventHandler<(FrameworkElement Target, Windows.Foundation.Point? Position)>? MenuRequested;

    /// <summary>Gets or sets the open conversation, or null.</summary>
    public ChatViewModel? Chat
    {
        get => (ChatViewModel?)GetValue(ChatProperty);
        set => SetValue(ChatProperty, value);
    }

    /// <summary>
    /// Puts the cursor in the message box for the "r" shortcut.
    /// </summary>
    /// <returns>True when the box took focus. False while no chat shows.</returns>
    public bool FocusDraft() => DraftBox.Focus(FocusState.Keyboard);

    /// <summary>
    /// Moves focus to the message bubbles for the Ctrl + j shortcut.
    /// </summary>
    /// <returns>True when the list took focus. False while no chat shows.</returns>
    public bool FocusMessages() => MessageList.Focus(FocusState.Keyboard);

    /// <summary>
    /// Opens the reaction picker over the bubble whose row has focus, for the Shift + + shortcut.
    /// </summary>
    /// <returns>True when a bubble row had focus.</returns>
    public bool OpenReactionsForFocused() => FocusedBubbleView() is { } view && OpenReactionPicker(view, view.Anchor);

    /// <summary>
    /// Starts a reply to the bubble whose row has focus and moves to the message box. Ctrl + Enter calls it.
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
    /// Opens the Windows emoji panel on the message box, or closes it on a second press. The emoji button and the
    /// Ctrl + Shift + e shortcut call this method.
    /// </summary>
    /// <remarks>
    /// The panel types into the element that has focus, so the method focuses the message box first. The method sends
    /// the Win + Period shortcut. <c>CoreInputView.GetForCurrentView().TryShow(Emoji)</c> needs a CoreWindow, which a
    /// desktop window does not have, and it showed nothing in a test. The shortcut waits 1 dispatcher turn, because
    /// Windows ignores it before the box has focus.
    /// </remarks>
    /// <returns>True when the method sent the request. False while no chat shows.</returns>
    public bool ToggleEmojiPanel()
    {
        if (Chat is null || !DraftBox.Focus(FocusState.Programmatic))
        {
            return false;
        }

        return XamlHost.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => SendEmojiShortcut());
    }

    /// <summary>
    /// Opens the file picker over this window and adds the picked files as chips. The attach button,
    /// Ctrl + Shift + a and Ctrl + Shift + p call this method.
    /// </summary>
    /// <remarks>
    /// The user can pick several files. The method checks the size of each file, and Send reads the files. The method
    /// refuses a file over 100 MB with a note. It also refuses a file that takes the chips past 200 MB. Only 1 picker
    /// opens at a time.
    /// Under the test hooks, the test gives the files through <see cref="TestHooks.PickFiles"/> and no picker opens.
    /// </remarks>
    /// <param name="photos">True for the photo picker, which offers only the photo types of the spec.</param>
    /// <returns>True when the picker opened. False while no chat shows or a picker is already open.</returns>
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
    /// Gets the AutomationId of a chip part: the prefix and the number of the chip.
    /// </summary>
    /// <param name="prefix">The prefix, such as <c>Chat.Attachment.</c>.</param>
    /// <param name="id">The number of the chip.</param>
    /// <returns>The AutomationId.</returns>
    public static string ChipIdOf(string prefix, int id) => prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets the image of a chip. A photo chip decodes its picture small from the file and never reads it whole. A video
    /// chip shows the thumbnail of Windows when there is one, and a file chip shows none.
    /// </summary>
    /// <remarks>The image fills in when the decode ends.</remarks>
    /// <param name="open">Opens the picked file. See <see cref="PendingAttachment.Open"/>.</param>
    /// <param name="thumbnail">Opens the thumbnail, or null. See <see cref="PendingAttachment.Thumbnail"/>.</param>
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
    /// Gets the icon of a chip without a picture: the icon of a video, an audio file or a document.
    /// </summary>
    /// <param name="kind">The kind that the attachment sends as.</param>
    /// <returns>The Segoe Fluent Icons glyph.</returns>
    public static string ChipGlyph(MessageKind kind) => kind switch
    {
        MessageKind.Video => "",
        MessageKind.Audio => "",
        _                 => "",
    };

    /// <summary>
    /// Shows the chip strip while a recording waits in the preview chip or an attachment waits.
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
    /// Gets the padding of the compose area. The top padding is 0 while chips sit over the message box, so the box
    /// meets them.
    /// </summary>
    /// <param name="isReplying">True while the user writes a reply.</param>
    /// <param name="hasAttachments">True while attachments wait.</param>
    /// <param name="isPreview">True while a recording waits.</param>
    /// <returns>The padding.</returns>
    public static Thickness ComposePadding(bool isReplying, bool hasAttachments, bool isPreview) =>
        new(ComposeInset, isReplying || hasAttachments || isPreview ? 0 : ComposeTop, ComposeInset, ComposeInset);

    /// <summary>
    /// Gets the corners of the message box. The top corners are square while chips sit over the box. Thus the chips
    /// and the box look like 1 control.
    /// </summary>
    /// <param name="isReplying">True while the user writes a reply.</param>
    /// <param name="hasAttachments">True while attachments wait.</param>
    /// <param name="isPreview">True while a recording waits.</param>
    /// <returns>The corners.</returns>
    public static CornerRadius DraftCorners(bool isReplying, bool hasAttachments, bool isPreview) =>
        isReplying || hasAttachments || isPreview ? new CornerRadius(0, 0, DraftCorner, DraftCorner) : new CornerRadius(DraftCorner);

    /// <summary>
    /// Gets the margin of the strip of attachment chips. While a reply waits, the strip joins the bottom of the reply
    /// chip. Otherwise the gap of the compose area sits above it.
    /// </summary>
    /// <param name="isReplying">True while the user writes a reply.</param>
    /// <returns>The margin.</returns>
    public static Thickness StripMargin(bool isReplying) => new(ComposeInset, isReplying ? 0 : ComposeTop, ComposeInset, 0);

    /// <summary>
    /// Gets the corners of the strip of attachment chips. They are square under the reply chip, else rounded on top.
    /// </summary>
    /// <param name="isReplying">True while the user writes a reply.</param>
    /// <returns>The corners.</returns>
    public static CornerRadius StripCorners(bool isReplying) =>
        isReplying ? new CornerRadius(0) : new CornerRadius(DraftCorner, DraftCorner, 0, 0);

    /// <summary>
    /// Dims an icon that keeps its own color while its button is disabled, such as the accent icon of Send.
    /// </summary>
    /// <param name="isEnabled">True while the button is enabled.</param>
    /// <returns>The opacity of the icon.</returns>
    public static double EnabledOpacity(bool isEnabled) => isEnabled ? 1 : DisabledOpacity;

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // Windows raises this event off the UI thread. The typing dots follow the animation setting as it changes.
    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        XamlHost.Dispatcher.TryEnqueue(() => AnimateTypingDots(TypingBubble.Visibility == Visibility.Visible));

    // When high contrast turns on or off, the focused row swaps between its bubble ring and the system visual.
    // Windows changes its colors then and raises this event off the UI thread. The app cannot use
    // AccessibilitySettings.HighContrastChanged, because a subscription crashes the window of a desktop app. The high
    // contrast flag can lag the colors, so a low-priority pass checks it again.
    private void OnColorValuesChanged(UISettings sender, object args) =>
        XamlHost.Dispatcher.TryEnqueue(() =>
        {
            RecheckFocusRing();
            XamlHost.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, RecheckFocusRing);
        });

    private void RecheckFocusRing()
    {
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused)
        {
            ShowFocusRing(focused.As<DependencyObject>());
        }
    }

    // A row in the recycle queue goes back below its neighbors, because a focused row rises for its ring. It also
    // releases its media. A row that gets a message loads its media in phase 1, after x:Bind gives it the message. A
    // row that gets the same message back sees no bubble change, so its released media loads here.
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

    // Shows the bubbles of the new chat and follows their changes.
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

        // The content height of the last chat means nothing here. With no height, the first look counts as a content
        // change, not as a scroll. Otherwise 2 chats of the same height unpin when the offset resets to the top.
        pane._lastExtent   = -1;
        pane._lastViewport = -1;
        pane._reactTarget  = null;
        pane._reactAnchor  = null;

        // An open reaction picker or emoji picker closes. Thus a pick never reacts to a bubble of the last chat.
        pane.ReactionPicker.Hide();
        pane._emojiPicker?.Hide();
        pane._emojiTarget  = null;
        pane._emojiPending = null;

        if (chat is not null)
        {
            chat.Messages.CollectionChanged += pane.OnMessagesChanged;
            chat.PropertyChanged            += pane.OnChatPropertyChanged;
            pane._firstDrawFor               = chat;
            pane.LogFirstDraw();

            // The chat is on screen, so its toasts count as read.
            (Application.Current as App)?.Notifications?.RemoveConversation(chat.ConversationId);

            // The message box takes focus 1 dispatcher turn later, when the pane shows. A hidden box cannot take focus.
            // This focus stays inside the window: it never activates the window or takes the foreground. Thus a window
            // that opened in the background stays there. When the user activates the window, the cursor shows in the
            // box. The arrow keys of the list open each chat but keep the focus on the list.
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

    // Waits for the frame that draws the first bubbles of the opened chat. The bubbles fade in from that frame. The
    // open-timing log then takes its last step, once per chat, with milliseconds and counts only. The pane calls this
    // method on open and on each change of the bubbles until that frame.
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

    // The bubbles of the new chat stay clear until their first frame. Thus no frame shows the last chat or the
    // layout pass.
    private void HideMessages()
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(MessageList);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Opacity = 0;
    }

    // The bubbles fade in from clear, from the frame that draws them. The list is already at the newest message then.
    // A Composition animation starts in that same frame, but the Opacity property does not. The gray frame that the
    // owner saw came from the scrolling placeholders of the list. The XAML now turns them off. A chat with no bubbles
    // stays clear until its first bubble comes.
    private void FadeInMessages()
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(MessageList);
        ScalarKeyFrameAnimation fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = ChatFadeIn;
        visual.StartAnimation(nameof(Visual.Opacity), fade);
    }

    // Starts the loop of the dots, or stops it and puts the dots back at rest. Each dot rises, falls and brightens on
    // the way up, 1 beat after the dot before it. With the animation effects off, the dots stay still.
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
    /// Leaves the focus where it is when the next chat opens.
    /// </summary>
    /// <remarks>The arrow keys of the conversation list open each chat that they pass.</remarks>
    public void KeepFocusOnNextOpen() => _keepFocus = true;

    // A bubble added at the bottom comes into view: a sent or arrived message, or the first page. Older pages go in
    // above. The list scrolls to its end instead of ScrollIntoView. On this bottom-aligned list, ScrollIntoView made
    // the panel realize its rows again. Every bubble above the new bubble then replayed its entrance, so the chat
    // seemed to reload on Send. When the list is already at the end, KeepPinned did the work and nothing moves.
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        LogFirstDraw();

        if (e.Action != NotifyCollectionChangedAction.Add || sender is not ICollection<MessageBubbleViewModel> messages
            || e.NewStartingIndex + e.NewItems!.Count != messages.Count)
        {
            return;
        }

        // This runs after the list measures the new bubble.
        XamlHost.Dispatcher.TryEnqueue(() =>
        {
            MessageList.UpdateLayout();
            if (Scroller() is { } scroller && scroller.VerticalOffset < scroller.ScrollableHeight)
            {
                ScrollToNewest(scroller);
            }
        });
    }

    // The pane hooks the scroller when the list has its template. Thus a scroll up loads older pages at once.
    private void OnMessageListLoaded(object sender, RoutedEventArgs e)
    {
        _ = Scroller();
        FillWithOlder();
    }

    // The scroller of the list tells when the list reaches the top. The pane finds it on first need. While the pane is
    // still collapsed at load, the list has no template and thus no scroller. The edges of the list fade as in the
    // conversation list. The owner chose that the bottom always fades, behind the message box.
    private ScrollViewer? Scroller()
    {
        if (_scroller is null && UiHelpers.FindScroller(MessageList) is { } found)
        {
            _scroller                  = found;
            _scroller.ViewChanged     += OnScrolled;
            MessageList.LayoutUpdated += OnMessageListLayoutUpdated;
            PaneGrid.LayoutUpdated    += (_, _) => KeepComposerClear();
        }

        // The fade needs the template of the scroller, which can come after the scroller. Each call tries until the
        // fade attaches.
        if (_scroller is not null && _fade is null && ScrollFade.Attach(_scroller, bottomAlways: true) is { } fade)
        {
            _fade           = fade;
            _composerHeight = -1;
            KeepComposerClear();
        }

        // A bubble that takes focus from the keyboard scrolls into view smoothly. The request reaches the scroller
        // before the list. Thus the pane must take it below the scroller, on the panel of the rows.
        if (_scroller is not null && !_bringHooked && MessageList.ItemsPanelRoot is { } panel)
        {
            _bringHooked                  = true;
            panel.BringIntoViewRequested += OnBubbleBringIntoView;
        }

        return _scroller;
    }

    // A bubble that took focus asks to show. The shared smooth scroll replaces the jump of the scroller. It scrolls
    // only far enough to show the whole bubble, 8 px clear of the edge.
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

    // When a page load ends and the list is too short to scroll, there is no top to reach. Thus older pages load here.
    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsLoading) && sender == Chat && Chat is { IsLoading: false })
        {
            XamlHost.Dispatcher.TryEnqueue(FillWithOlder);
        }

        // While an older page goes in above, the last shown bubble holds its place. See the panel of the rows in the
        // XAML. After the list lays out the page, the first shown bubble holds its place again. The pane measures the
        // page here, before the mode goes back.
        if (e.PropertyName == nameof(ChatViewModel.IsLoadingOlder) && sender == Chat && Chat is { } loading
            && MessageList.ItemsPanelRoot?.As<ItemsStackPanel>() is { } panel)
        {
            if (!loading.IsLoadingOlder)
            {
                MessageList.UpdateLayout();
            }

            panel.ItemsUpdatingScrollMode = loading.IsLoadingOlder ? ItemsUpdatingScrollMode.KeepLastItemInView : ItemsUpdatingScrollMode.KeepItemsInView;
        }

        // When the recording changes state, the preview chip shows its waveform. A send or a remove stops its playback.
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

    // Each older page ends in another IsLoading change. Thus this method repeats while the list is too short to scroll
    // or still near its top. It stops when no pages are left, a page adds nothing or a load fails. See
    // ChatViewModel.CanFillOlder.
    private void FillWithOlder()
    {
        if (Chat is not { IsLoading: false, CanFillOlder: true } chat)
        {
            return;
        }

        // The layout measures the new bubbles now, so the scroll height is current.
        MessageList.UpdateLayout();
        if (Scroller() is { } scroller && ChatScroll.ShouldLoadOlder(scroller.VerticalOffset, scroller.ScrollableHeight, scroller.ViewportHeight))
        {
            _ = chat.LoadOlderAsync();
        }
    }

    // A scroll near the top starts the older page, also during the scroll. Thus the page is usually in before the top.
    // The scroll-to-bottom button follows the distance from the bottom. LoadOlderAsync ignores calls during a load.
    private void OnScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroller is not { } scroller || Chat is null)
        {
            return;
        }

        // With the same content height, the user or the arrow moved the list. A new height comes from bubbles that
        // measure or from a page.
        bool contentChanged  = ContentChanged(scroller);
        bool wasPinned       = _pinned;
        _pinned              = ChatScroll.StaysPinned(_pinned, contentChanged, scroller.VerticalOffset, scroller.ScrollableHeight);

        if (contentChanged)
        {
            KeepPinned(scroller);
        }

        // When the scroll to the newest message stops, the list pins there. Rows that measured on the way can grow the
        // content past the target. KeepPinned then puts the list at the end.
        if (_toNewest && !e.IsIntermediate)
        {
            _toNewest = false;
            _pinned   = true;
            KeepPinned(scroller);
        }

        // When the list comes back to the newest message, the chat drops the older pages above the newest 200 bubbles.
        // Another scroll up loads them again.
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

    // Bubbles measure their real heights after they show, because the list first estimates them. Thus a chat that just
    // opened can stop a few pixels short of its newest message. While pinned, each change in content height puts the
    // list back at the bottom. OnScrolled handles a scroll, which keeps the same height.
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

    // Enter or Space on a focused bubble shows or hides its time, as a click does. The retry link has its own focus, so
    // the keys do not reach this method there. The method matches the row as a ContentControl that holds a bubble. In
    // the Native AOT build the row arrives as a ContentControl, not a ListViewItem. See UiHelpers.FindScroller.
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

    // When a send or a clear empties the box, the text scroller of the box goes back to the top. The owner reported
    // that a scroller left down from lines now gone showed only the top of the cursor.
    private void OnDraftTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DraftBox.Text.Length == 0 && UiHelpers.FindPart<ScrollViewer>(DraftBox, "ContentElement") is { } text)
        {
            text.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    // Enter sends. The box handles Shift + Enter and adds a line. With media on the clipboard, Ctrl + V or
    // Shift + Insert pastes the media as chips. The box ignores a picture.
    private void OnDraftKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // In a test run, the pane takes every paste at once. The test hook builds its clipboard after this event.
        if (IsPasteKey(e.Key) && TestHooks.BlockLaunches)
        {
            e.Handled = true;
            _ = PasteFromTestHookAsync();
            return;
        }

        if (IsPasteKey(e.Key) && ClipboardView() is { } view && CarriesMedia(view))
        {
            e.Handled = true;
            Paste(view, owned: true);
            return;
        }

        if (e.Key != VirtualKey.Enter || UiHelpers.IsDown(VirtualKey.Shift) || Chat is null)
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

    // A play, pause or seek of a bubble. Only 1 message plays at a time.
    private void OnVoiceRequested(object? sender, VoiceRequest request)
    {
        _voice ??= new VoicePlayer(XamlHost.Dispatcher);
        _voice.Handle(request);
    }

    /// <summary>
    /// Stops the voice message, the inline video and a recording. The window calls this method when it hides.
    /// </summary>
    internal void StopMedia()
    {
        StopVoice();
        MessageBubble.StopInlineVideo();
        DropRecorder();
    }

    private void StopVoice()
    {
        _voice?.Dispose();
        _voice = null;
    }

    // The React item of the bubble menu or the React button of the hover strip. The picker opens over the anchor.
    private void OnReactRequested(object? sender, FrameworkElement anchor)
    {
        if (sender is MessageBubble view)
        {
            OpenReactionPicker(view, anchor);
        }
    }

    private void OnReplyRequested(object? sender, EventArgs e)
    {
        if (sender is MessageBubble { Bubble: { } bubble })
        {
            StartReply(bubble);
        }
    }

    // A click, Enter or Space on a reply quote scrolls to the quoted message. The chat loads older pages until the
    // message shows, up to a cap. The owner chose no pulse in Plan 10. When the chat does not find the message or the
    // chat changes meanwhile, nothing happens. The method is async void as a click handler is. Thus a failure reaches
    // the unhandled error log of the app.
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

        // The view leaves the newest messages, so nothing pins it back down.
        _pinned = false;

        // The target offset centers the message in the view when the message fits. The pane finds the offset with a
        // jump to the row. The view then goes back to its start before anything draws, and scrolls there smoothly.
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

    // The close button of the reply chip goes with the chip, so focus goes back to the message box.
    private void OnCancelReplyClick(object sender, RoutedEventArgs e)
    {
        Chat?.CancelReply();
        DraftBox.Focus(FocusState.Programmatic);
    }

    private void OnAttachClick(object sender, RoutedEventArgs e) => OpenAttachPicker(photos: false);

    // The remove button of a chip drops that attachment and puts the cursor back in the message box.
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

    // A right-click on a bubble opens the bubble menu at the pointer. The Application key or Shift + F10 in its row
    // opens the menu over the back of the bubble. On a link preview card, the menu of the card opens instead.
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

    // The ⋮ of the hover strip of a bubble, or its ShowMenu.
    private void OnMenuRequested(object? sender, FrameworkElement anchor)
    {
        if (sender is MessageBubble view)
        {
            ShowBubbleMenu(view, anchor, null);
        }
    }

    // Opens the bubble menu with the items that the bubble takes. The hover strip of the bubble stays until the menu
    // closes.
    private void ShowBubbleMenu(MessageBubble view, FrameworkElement anchor, Windows.Foundation.Point? at)
    {
        // A system line such as a group rename has no menu.
        if (view.Bubble is not { IsSystemLine: false } bubble)
        {
            return;
        }

        if (!ReferenceEquals(view, _menuView))
        {
            _menuView?.KeepShown(false);
        }

        _menuView = view;
        view.KeepShown(true);

        // Open on a photo, video or voice message uses the viewer or player of the app. On a file, Open shows only for
        // a type that is safe to launch.
        bool canOpen = bubble.HasPhoto || bubble.HasVideo || bubble.HasVoice || bubble.CanOpenMedia;

        BubbleMenuCopy.Visibility     = bubble.HasCopyText ? Visibility.Visible : Visibility.Collapsed;
        BubbleMenuOpen.Visibility     = canOpen ? Visibility.Visible : Visibility.Collapsed;
        BubbleMenuOpenWith.Visibility = bubble.HasMediaFile ? Visibility.Visible : Visibility.Collapsed;
        BubbleMenuSaveAs.Visibility   = bubble.HasMediaFile ? Visibility.Visible : Visibility.Collapsed;
        BubbleMenuDownload.Visibility = bubble.HasMediaFile ? Visibility.Visible : Visibility.Collapsed;
        BubbleMenuDelete.Visibility   = bubble.CanAct ? Visibility.Visible : Visibility.Collapsed;
        ShowMenuAt(BubbleMenu, anchor, at);
    }

    // Opens a menu at a point on the element for a right-click, else at the element.
    private static void ShowMenuAt(MenuFlyout menu, FrameworkElement anchor, Windows.Foundation.Point? at)
    {
        if (at is { } point)
        {
            menu.ShowAt(anchor, new Windows.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
            return;
        }

        menu.ShowAt(anchor);
    }

    // Finds the bubble that an element sits in, and the link preview card that the element sits on. The card is null
    // off a card. The search goes up by name, as UiHelpers.FindPart goes down, so it is AOT safe. A link in the text
    // is not in the visual tree, so the search starts from its text.
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

    // The menu items act on the bubble that the menu opened on. The bubble stays set after the menu closes, because the
    // click of an item can come after the close.
    private void OnBubbleMenuCopyClick(object sender, RoutedEventArgs e) => _menuView?.Copy();

    private void OnBubbleMenuOpenClick(object sender, RoutedEventArgs e) => _menuView?.Open();

    private void OnBubbleMenuOpenWithClick(object sender, RoutedEventArgs e) => _menuView?.OpenWith();

    private void OnBubbleMenuSaveAsClick(object sender, RoutedEventArgs e) => _menuView?.SaveAs();

    private void OnBubbleMenuDownloadClick(object sender, RoutedEventArgs e) => _menuView?.Download();

    private void OnBubbleMenuReactClick(object sender, RoutedEventArgs e) => _menuView?.React();

    private void OnBubbleMenuReplyClick(object sender, RoutedEventArgs e) => _menuView?.Reply();

    // Delete runs after a confirm. Enter picks Cancel, so no message goes by accident. Only the copy on this side goes.
    private async void OnBubbleMenuDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_menuView?.Bubble is not { } bubble || Chat is not { } chat || XamlRoot is not { } root)
        {
            return;
        }

        var text = new TextBlock { Text = ChatCopy.DeleteMessageText, TextWrapping = TextWrapping.Wrap };
        if (await ConversationMenuFlyout.ShowAsync(ConversationMenuFlyout.NewDialog(root, "Dialog.DeleteMessage", ChatCopy.DeleteMessageTitle, text, ChatCopy.Delete)))
        {
            await chat.DeleteAsync(bubble);
        }
    }

    private void OnPreviewMenuCopyLinkClick(object sender, RoutedEventArgs e) => _menuView?.CopyLink();

    // The menu opens below the ⋮ of the header.
    private void OnMoreClick(object sender, RoutedEventArgs e) => MenuRequested?.Invoke(this, (MoreButton, null));

    private void OnMuteClick(object sender, RoutedEventArgs e) => MuteRequested?.Invoke(this, EventArgs.Empty);

    // A right-click on the header opens the menu at the pointer. The keyboard on the focused ⋮ opens it below the ⋮.
    private void OnHeaderContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        MenuRequested?.Invoke(this, e.TryGetPosition(HeaderBar, out Windows.Foundation.Point at) ? (HeaderBar, at) : (MoreButton, null));
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e) => ToggleEmojiPanel();

    // Presses and releases Win + Period. Windows opens nothing for Win + Ctrl + Shift + Period. Thus the method first
    // releases Ctrl and Shift that the Ctrl + Shift + e shortcut still holds. True when Windows took every key.
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

    // Presses and releases the keys in 1 call. When another app holds the input, Windows refuses the send and nothing
    // happens. After a partial send, the method releases each key that it pressed but did not release, Win above all.
    // Thus no key stays down. True when Windows took every key.
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

    // Notes the content and view heights. True when either height differs from the last height seen. The view height
    // changes when the typing dots, the reply chip or a longer draft show or hide. That change is not a
    // scroll, so a pinned list stays on the newest message.
    private bool ContentChanged(ScrollViewer scroller)
    {
        bool changed  = Math.Abs(scroller.ExtentHeight - _lastExtent) >= 0.5 || Math.Abs(scroller.ViewportHeight - _lastViewport) >= 0.5;
        _lastExtent   = scroller.ExtentHeight;
        _lastViewport = scroller.ViewportHeight;
        return changed;
    }

    // A row gained or lost focus. ShowFocusRing ignores an element inside a row.
    private void OnMessageListFocusChanged(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not null)
        {
            ShowFocusRing(e.OriginalSource.As<DependencyObject>());
        }
    }

    // A row with keyboard or programmatic focus rings its bubble. The row rises over its neighbors, so they do not
    // cover the ring. A click does not ring the bubble. In high contrast the system focus visual of the row shows
    // instead. See MessageBubbleViewModel.ShowsFocusRing.
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

    // The bubble of the row with keyboard focus. Null when focus is elsewhere, a link inside a row included.
    private MessageBubble? FocusedBubbleView() =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused ? RowBubbleView(focused.As<DependencyObject>()) : null;

    // The bubble of a row, or null when the element is not a row of the list. The search goes by name. See
    // UiHelpers.FindPart.
    private MessageBubble? RowBubbleView(DependencyObject element) =>
        MessageList.IndexFromContainer(element) >= 0 ? UiHelpers.FindPart<MessageBubble>(element, "BubbleView") : null;

    // Opens the reaction picker over a part of a bubble. A bubble that the phone did not report yet has nothing to
    // react to, so the picker does not open. The hover strip of the bubble stays until the picker closes.
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

        // My reaction shows selected with the accent fill of the pill behind its button. Narrator reads "Selected".
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

    // A picker button closes the picker and sends the reaction: an add, a switch or a remove. See
    // ChatViewModel.ReactAsync.
    private void React(string emoji)
    {
        ReactionPicker.Hide();
        if (_reactTarget is { } bubble && Chat is { } chat)
        {
            _ = chat.ReactAsync(bubble, emoji);
        }
    }

    // More reactions closes the picker. After the close, the emoji picker opens in the same place. The pane does not
    // use the Windows emoji panel, which types into a text box. The owner reported that the panel neither reacted nor
    // closed when it aimed at a hidden box.
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

        // The emoji picker opens after the Closed handler of the reaction picker runs. Before that, the close hides the
        // hover strip that the picker opens over. On a cold first run the emoji picker then did not show.
        _emojiPending = (bubble, view, anchor);
    }

    // Shows the emoji picker EmojiPicker in a flyout where the reaction picker was. A search box sits over the emoji of
    // the catalog, by category, in a scroller. The buttons are EmojiPicker.{code points in hex}. A click sends that
    // emoji as the reaction and closes the picker. Escape or a click outside also closes it. Focus starts in the search
    // box. The hover strip of the bubble stays until the picker closes.
    private void ShowEmojiPicker(MessageBubbleViewModel bubble, MessageBubble view, FrameworkElement anchor)
    {
        // The log times the open in milliseconds only. The first open froze the app when it drew every emoji at once.
        // See EmojiRowFactory.
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

        // The timing runs for 2 seconds from the open. The longest gap between frames is how long the app stood still.
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

    // Builds the emoji picker once: its search box, its empty note and its rows in a scroller. An ItemsRepeater
    // virtualizes the rows, so it makes only the rows in view and reuses them as they scroll out. The rows fill the
    // width of the picker, edge to edge with the search box. The scroll bar overlays them, as in Windows.
    private Flyout BuildEmojiPicker()
    {
        Style buttonStyle  = ReactionButtons.Resources["ReactionButtonStyle"].As<Style>();
        Style noteStyle    = ReactionButtons.Resources["EmojiPickerNoteStyle"].As<Style>();
        var flyout         = new Flyout { Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };

        _emojiSearch = new TextBox { PlaceholderText = ChatCopy.SearchEmoji, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(_emojiSearch, "EmojiPicker.Search");
        AutomationProperties.SetName(_emojiSearch, ChatCopy.SearchEmoji);
        _emojiSearch.TextChanged += (_, _) => FilterEmoji();

        _emojiEmpty = new TextBlock { Text = ChatCopy.NoEmojiFound, Margin = new Thickness(4, 8, 0, 8), Style = noteStyle, Visibility = Visibility.Collapsed };

        // The repeater makes about half a screen of rows ahead, above and below the view.
        _emojiFactory  = new EmojiRowFactory(EmojiPickerColumns, EmojiCellSize, buttonStyle, noteStyle, PickEmoji);
        _emojiRows     = new Muxc.ItemsRepeater { ItemTemplate = _emojiFactory, VerticalCacheLength = 1 };
        _emojiScroller = new ScrollViewer { Content = _emojiRows, Height = EmojiPickerHeight, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var root = new StackPanel { Width = EmojiPickerColumns * EmojiCellSize, Children = { _emojiSearch, _emojiEmpty, _emojiScroller } };
        AutomationProperties.SetAutomationId(root, "EmojiPicker");
        AutomationProperties.SetName(root, ChatCopy.EmojiPicker);
        flyout.Content = root;
        return flyout;
    }

    // Shows the categories that match the words of the search box. See EmojiCatalog.Search. Each category is a name
    // row over rows of up to 8 emoji, from the top. An empty box shows all categories. No match shows the empty note.
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

    // The method closes the picker and sends the picked emoji as the reaction. It is a custom reaction when it is not
    // 1 of the 7.
    private void PickEmoji(string emoji)
    {
        MessageBubbleViewModel? bubble = _emojiTarget;
        _emojiPicker?.Hide();
        if (bubble is not null && Chat is { } chat)
        {
            _ = chat.ReactAsync(bubble, emoji);
        }
    }

    // Starts the reply, then moves to the message box 1 dispatcher turn later. A menu that closes first puts focus back
    // where it was.
    private void StartReply(MessageBubbleViewModel bubble)
    {
        if (Chat is not { } chat)
        {
            return;
        }

        chat.StartReply(bubble);
        XamlHost.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => DraftBox.Focus(FocusState.Keyboard));
    }

    // The top of the row of a bubble in the view, or null while the row is not realized.
    private double? RowTop(ScrollViewer scroller, MessageBubbleViewModel bubble) =>
        MessageList.ContainerFromItem(bubble)?.As<UIElement>().TransformToVisual(scroller).TransformPoint(default).Y;

    // The bottom padding of the list keeps the newest bubble above the elements that float over it. These are the
    // typing bubble, the chips and the message box, in the last 3 rows of the pane. The bottom fade covers only that
    // height. The method runs after every layout, so it sets values only on a change. The padding changes the content
    // height, which KeepPinned follows.
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

    // While pinned and short of the newest message, the list jumps there with no animation.
    private void KeepPinned(ScrollViewer scroller)
    {
        if (_pinned && !ChatScroll.IsAtBottom(scroller.VerticalOffset, scroller.ScrollableHeight))
        {
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
        }
    }

    // Scrolls with animation to the newest message. The list only estimates the heights of rows not yet shown. Thus the
    // content can grow past the target during the scroll. OnScrolled finishes the scroll where it stops.
    private void ScrollToNewest(ScrollViewer scroller)
    {
        _toNewest = true;
        _         = ScrollSmoothlyAsync(scroller, scroller.ScrollableHeight);
    }

    // Every jump that the chat makes by itself goes through here, such as to the newest or a quoted message.
    // The scroll is smooth, as with scroll-behavior: smooth in CSS. With the animation effects of Windows off, it jumps
    // at once. The task ends when the view stops, after 1.5 s at most. The first place of a chat at the bottom and the
    // quiet corrections of KeepPinned do not use this method. They stay instant.
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

    // Fades the scroll-to-bottom button in or out, which its OpacityTransition animates. A hidden button takes no
    // clicks and no focus.
    private void ShowScrollToBottom(bool show)
    {
        ScrollToBottomButton.Opacity          = show ? 1 : 0;
        ScrollToBottomButton.IsHitTestVisible = show;
        ScrollToBottomButton.IsTabStop        = show;
    }
}

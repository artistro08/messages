using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using GMessages.App.Media;
using GMessages.App.Windowing;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Text;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows 1 message bubble. Sent bubbles sit on the right, and received bubbles sit on the left in the card color.
/// </summary>
/// <remarks>
/// <para>
/// A sent RCS bubble has a lighter shade of the accent color. A sent SMS bubble has the accent color.
/// </para>
/// <para>
/// The text carries the AutomationId <c>Bubble.{id}</c>. Until the phone echoes a sent message, it carries
/// <c>Bubble.{tmpId}</c>. A failed bubble shows the "Not sent" link <c>Bubble.{id}.Retry</c> in place of the time. A
/// click on the link raises <see cref="RetryRequested"/>, and the chat sends the message again.
/// </para>
/// <para>
/// The time <c>Bubble.{id}.Time</c> is hidden until a click on the bubble, and a second click hides it. On the newest
/// bubble of the chat, the click hides the caption row instead, and a second click shows it. The bubble catches the
/// click also on the text. The text selection marks the pointer as handled. The chat pane does the same for Enter and
/// Space on a focused bubble.
/// </para>
/// <para>
/// The side is a visual state, so its brushes stay theme resources. They follow the light or dark theme of the window.
/// The accent fill of the reaction pill for my reaction is a visual state too. Both follow the changes of the bubble,
/// as when an echo makes a sending bubble a sent bubble. Their groups sit on the root element <c>Row</c> of the
/// control, because GoToState looks only there.
/// </para>
/// <para>
/// The reply quote <c>Quote.{id}</c> shows above the text. The reaction pill <c>Reactions.{id}</c> sits over the bottom
/// corner. Under the newest message of the chat, the caption row shows the time <c>Caption.{id}</c>. On my message, it
/// also shows the delivery icon of the owner. The icon is named <c>Status.{id}</c>: "Sent", "Delivered" or "Read". In
/// an encrypted chat, the caption row also shows the lock.
/// </para>
/// <para>
/// The preview card <c>Preview.{id}</c> of a link shows under the text after the fetch. Its name is its title and the
/// host of the link. The text then omits the address of that link. A message that was only the link shows the
/// card alone. A click on the card opens the link in the default browser. The own menu of the card has Copy link
/// <c>Preview.CopyLink</c>, which copies the address as sent. A right-click, the Application key or Shift + F10 opens
/// that menu.
/// </para>
/// <para>
/// In every chat, the pill opens the list of who reacted, <c>Reactors</c>. Its rows are <c>Reactors.{n}</c>, and my
/// row reads "You". React in the menu and in the hover strip raises <see cref="ReactRequested"/>. Reply in the menu
/// raises <see cref="ReplyRequested"/>. The chat acts on both.
/// </para>
/// <para>
/// Web links in the text show as underlined links in the own color of the text. See
/// <see cref="LinkPreviewer.FindLinks"/>. Thus they read on every bubble color and in high contrast. Each link is named
/// <c>Link.{id}.{n}</c>, with n from 0, and Tab reaches it. A click or Enter opens it in the default browser, for http
/// and https only. That click does not also show or hide the time. Selection and Copy work on the whole text.
/// </para>
/// <para>
/// Bubbles of 1 group sit close together with small corners where they meet, as in Google Messages. See
/// <see cref="ChatViewModel.GroupGap"/>. A bubble is as wide as its content, up to 70% of the chat and 560 px at most.
/// </para>
/// <para>
/// A bubble that can take them shows react <c>HoverReact.{id}</c>, reply <c>HoverReply.{id}</c> and the ⋮
/// <c>HoverMore.{id}</c>. See <see cref="MessageBubbleViewModel.CanAct"/>. They fade in beside its back while the
/// pointer is over the bubble, as in Google Messages for web. The <c>Hover</c> visual states run the fade. The space of
/// the buttons is always kept, so nothing moves.
/// </para>
/// <para>
/// A photo <c>Bubble.Photo.{id}</c> fills the bubble from edge to edge, with its caption under it. Its name is its
/// caption or "Photo". Its size comes from the dimensions of the photo before anything loads, so the chat never jumps.
/// Its thumbnail shows first, decoded tiny and stretched, so it looks blurred. A Composition blur needs Win2D, which
/// this app does not carry. Then the full image fades in over it.
/// </para>
/// <para>
/// A thumbnail-only photo asks the phone for the full size, which arrives as an edit of the message. While the phone
/// has not sent the photo, it reads "Waiting for the photo from your phone…". A failure or a full size that never comes
/// reads "Couldn't load this photo.", and a click tries again. Else a click shows the photo in the photo viewer of the
/// window. See <see cref="PhotoViewer"/>.
/// </para>
/// <para>
/// Open <c>Bubble.Open</c> in the menu also shows the photo in the viewer. Open with <c>Bubble.OpenWith</c> is on the
/// menu of every attachment. It opens the attachment with the app picker of Windows. Save as <c>Bubble.SaveAs</c> saves
/// a copy where the person picks. Download <c>Bubble.Download</c>, also on the menu of a file, saves it to the
/// Downloads folder.
/// </para>
/// <para>
/// A file shows as a card <c>Bubble.File.{id}</c>, named by the file name. The card shows the thumbnail of the file
/// when one came, else the icon of its type. It also shows the name and the size as Explorer writes it. A click shows
/// "Downloading…" during the download, then opens the file in its default app. A failure reads "Couldn't open this
/// file." until the next click.
/// </para>
/// <para>
/// A video fills the bubble as a photo does. Its first frame <c>Bubble.Video.{id}</c> shows the hand cursor, and its
/// name is its caption or "Video". A play button <c>Video.Play.{id}</c>, "Play video", sits over the frame. Play plays
/// the video in place with sound, as media, in <c>Video.Player.{id}</c>. That player is a
/// <see cref="VideoPlayback"/> with compact controls. Only 1 video plays at a time.
/// </para>
/// <para>
/// The bubble releases the player when the row recycles, unloads or scrolls out of sight. It also releases the player
/// on a chat switch and when the viewer opens. The frame and Open in the menu open the video in the viewer of the
/// window. Over a playing video, the expand button <c>Video.Expand.{id}</c>, "Open video", does the same. The viewer
/// starts where the player of the bubble stopped.
/// </para>
/// <para>
/// A bubble that the chat added live fades in once, on its composition visual. That is a sent message, or a message
/// that arrives while the chat is open. See <see cref="MessageBubbleViewModel.TakeNew"/>. Thus layout and the scroll
/// pin of the list never see it move. The first page, older pages, cached bubbles and recycled rows show with no fade.
/// With the animation effects of Windows off, no bubble fades.
/// </para>
/// <para>
/// When its row has keyboard focus, a bubble draws a ring around its back. The ring takes the place of the system
/// focus visual of the row. See <see cref="ShowFocusRing"/>. In high contrast, the system visual shows instead.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "ReleaseVideo disposes the inline player at recycle, unload, scroll and chat switch. A control has no Dispose.")]
public sealed partial class MessageBubble : UserControl
{
    /// <summary>Identifies the <see cref="Bubble"/> property.</summary>
    public static readonly DependencyProperty BubbleProperty =
        DependencyProperty.Register(nameof(Bubble), typeof(MessageBubbleViewModel), typeof(MessageBubble), new PropertyMetadata(null, OnBubbleChanged));

    // The top of the reaction pill overlaps the back by this much. PillHost has a top margin of -8.
    private const double PillOverlap = 8;

    // The gap between the reaction pill and the time beside it. PillHost has a 6 px margin on the side of the time.
    private const double PillGap = 6;

    // On a short bubble, the pill centers this far outward of the point 12 px in from the back edge. That puts its
    // middle 4 px in from the edge.
    private const double PillCornerReach = 8;

    // The image width of the preview card and its gap below the text, in effective pixels.
    private const int PreviewImageWidth = 240;
    private const double PreviewGap     = 8;

    // The list of who reacted scrolls past this height. The emoji of each row sits this far from the name.
    private const double ReactorsMaxHeight = 320;
    private const double ReactorsSpacing   = 8;
    private const double ReactorsRowHeight = 32;

    // A bubble grows with its content up to this share of the chat. It never grows past the cap in effective pixels.
    private const double MaxWidthShare  = 0.7;
    private const double MaxBubbleWidth = 560;

    // Large round corners, and small corners on the side of the sender where bubbles of a group meet.
    private const double LargeCorner = 18;
    private const double SmallCorner = 4;

    // Half the gap inside a group, and half the gap between groups.
    private const double InGroupGap = 1;
    private const double GroupGap   = 4;

    // The avatar lane beside received bubbles in a group chat.
    private const double AvatarLane = 36;

    // The hover strip has 3 buttons of 32 px with 4 px on each side. Its slot in MessageBubble.xaml binds StripWidth.
    private const double HoverStripWidth = 104;

    // For the blurred look, the thumbnail of a photo decodes this wide, then stretches to the size of the photo.
    private const int BlurDecodeWidth = 24;

    // The thumbnail of the file card is a 40 px square in logical pixels. Logical pixels already include the display
    // scale.
    private const int FileThumbWidth = 40;

    // The caption of a photo sits this far inside the bubble. Other text has the usual padding of the back.
    private static readonly Thickness CaptionInset = new(12, 6, 12, 8);
    private static readonly Thickness TextPadding  = new(12, 8, 12, 8);

    // A thumbnail-only photo waits this long for the full size from the phone. Then it offers to try again.
    private static readonly TimeSpan FullSizeWait = TimeSpan.FromSeconds(30);

    // The entrance of a new bubble is a fade over the normal control animation time of WinUI.
    private static readonly TimeSpan EntranceTime = TimeSpan.FromMilliseconds(250);

    // The pop of a reaction pill starts at half size, goes a little over full size, then settles.
    private const float PillPopFrom = 0.5f;
    private const float PillPopOver = 1.08f;
    private static readonly TimeSpan PillPopTime = TimeSpan.FromMilliseconds(180);

    // Reads the animation setting of Windows live: Settings > Accessibility > Visual effects.
    private static readonly UISettings Ui = new();

    // The first flag is true while the pointer is over the bubble. The second flag is true while the picker or the
    // menu from the strip is open. The strip then stays while the pointer is in that popup.
    private bool _pointerOver;
    private bool _keepShown;

    // The CanAct value that the bubble last had. Thus a change from sending to sent shows or hides the strip under the
    // pointer.
    private bool _canAct;

    // The preview that the card shows or decodes, so that a change decodes once. The card holds only the decoded image.
    private LinkPreview? _previewShown;

    // A click on a link in the text also reaches the back of the bubble. The back must not toggle the time for that
    // click. The link click and the tap of the back come in any order, so the tap waits a turn.
    private bool _linkClicked;

    // The text and the part key that the runs of the text were built for. The runs rebuild only when these change,
    // because a rebuild drops a selection.
    private string? _shownText;
    private string? _shownKey;

    // The links of the text, kept in the color of the text.
    private readonly List<Hyperlink> _links = [];

    // The fade of the full photo over the blurred photo.
    private readonly ScalarTransition _photoFade = new();

    // The photo that shows or loads, and its bubble. Each load gets a number, so a load that a newer load overtakes
    // stops. The bubble is the view model itself. It stays 1 object through its echo, when its ID arrives, and through
    // a retry with a new temporary ID. A recycled container gets another view model.
    private MediaPart? _photoPart;
    private MessageBubbleViewModel? _photoBubble;
    private int _photoLoad;
    private bool _photoFailed;

    // The first field tells whether the full image is a moving GIF: an Image clipped on its composition visual. Else
    // the image is a still photo in a brush. The second field is the visual of the GIF image. The bubble takes it once
    // while the image is in the live tree.
    private bool _photoAnimated;
    private Visual? _photoFullVisual;

    // Cancels the wait of a thumbnail-only photo for its full size when a newer load starts. It is null when no load
    // waits.
    private Action? _cancelPhotoWait;

    // The widest that a photo can be: the width cap of the bubble. Measure sets it, because the photo covers the edge
    // of the bubble.
    private double _photoMaxWidth = MaxBubbleWidth;

    private MediaPart? _filePart;
    private MessageBubbleViewModel? _fileBubble;
    private bool _fileOpening;
    private bool _fileFailed;

    // The voice message on show, its bubble and its player key, which the echo keeps. The load number goes up when
    // another message shows, so a late load or player update stops. Then come the load number whose audio fetch
    // started, and the waveform after the read. The bubble keeps no audio, because Play gets it from the media store
    // again.
    private MediaPart? _voicePart;
    private MessageBubbleViewModel? _voiceBubble;
    private string _voiceKey = string.Empty;
    private int _voiceLoad;
    private int _voiceFetched = -1;
    private VoiceWave? _voiceWave;

    // The bubble whose video plays in place. Only 1 video in the chat plays at a time.
    private static MessageBubble? s_playingVideo;

    // The video on show and its bubble. The load number goes up when the video stops or another message shows, so a
    // late download stops. The last field is the inline player while it plays.
    private MediaPart? _videoPart;
    private MessageBubbleViewModel? _videoBubble;
    private int _videoLoad;
    private VideoPlayback? _video;

    // The still frame of the video after it shows. The viewer grows it out of the bubble before its player replaces it.
    private ImageSource? _videoFrameImage;

    // The reactions that the pill shows, so that a change on the same bubble pops the pill in. The flag is true while a
    // new pill waits for its size to pop. The visual of the pill host has no implicit transitions, so Windows gives it
    // out. The bubble takes it once for each pill. The pill unloads with the last reaction and loads with the next.
    private string _reactionsShown = string.Empty;
    private bool _popPill;
    private Visual? _pillVisual;

    // ReleaseMedia sets this flag while the row waits for reuse. Updates of its message then load no media into it.
    private bool _released;

    // AnimateIn sets this flag, so that a recycle restores the visual of the row. A row that never animated makes no
    // visual.
    private bool _animatedIn;

    // The moving GIF on show, and whether this bubble paused it off screen. Only that pause plays it again.
    private BitmapImage? _gif;
    private bool _gifPaused;

    /// <summary>
    /// Creates the bubble.
    /// </summary>
    public MessageBubble()
    {
        InitializeComponent();

        // x:Load does not make the parts that a message does not use. Such parts are the quote, photo, video, file
        // card, voice, upload bar, preview card, pill, retry footer and caption row. Thus their own wiring sits on them
        // in the XAML and comes with them.
        Back.AddHandler(TappedEvent, new TappedEventHandler(OnBackTapped), handledEventsToo: true);
        Row.Tapped += OnRowTapped;

        // A video that plays stops when the bubble leaves the tree.
        Unloaded += (_, _) => ReleaseVideo();

        // The row under the bubble is at least as wide as the back, inside its margins. Thus the pill sits on the
        // corner of the back.
        Back.SizeChanged += (_, _) =>
        {
            Under.MinWidth = Math.Max(0, Back.ActualWidth - Under.Margin.Left - Under.Margin.Right);
            LayPill();
        };
        Under.SizeChanged    += (_, _) => LayPill();
        Captions.SizeChanged += (_, _) => LayPill();

        // The own menu of the text would win a right-click on it, so the text has no menu. The request goes to the chat
        // pane, whose bubble menu has Copy.
        Body.ContextFlyout = null;

        // The links follow the color of the text, which the side state and the theme set.
        Body.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => ColorLinks());
    }

    /// <summary>
    /// Asks the chat to send this failed bubble again.
    /// </summary>
    public event EventHandler? RetryRequested;

    /// <summary>
    /// Asks the chat to open the reaction picker on this bubble, over the given element. React in the menu gives the
    /// back, and React in the hover strip gives its button.
    /// </summary>
    public event EventHandler<FrameworkElement>? ReactRequested;

    /// <summary>
    /// Asks the chat to start a reply to this bubble. Reply in the menu and in the hover strip raise it.
    /// </summary>
    public event EventHandler? ReplyRequested;

    /// <summary>
    /// Asks the chat to open its bubble menu on this bubble at the given element: the back or the ⋮ of the hover strip.
    /// </summary>
    public event EventHandler<FrameworkElement>? MenuRequested;

    /// <summary>
    /// Asks the voice player of the chat to play, pause or seek the voice message of this bubble.
    /// </summary>
    public event EventHandler<VoiceRequest>? VoiceRequested;

    /// <summary>
    /// Asks the chat to jump to the message that the quote shows. A click, Enter or Space on the quote raises it.
    /// </summary>
    public event EventHandler? QuoteRequested;

    /// <summary>Gets the accessible name of the quote, which jumps to the quoted message.</summary>
    public static string QuoteJumpText => ChatCopy.GoToOriginalMessage;

    /// <summary>Gets the Copy text of the menu.</summary>
    public static string CopyText => ChatCopy.Copy;

    /// <summary>Gets the React text of the menu.</summary>
    public static string ReactText => ChatCopy.React;

    /// <summary>Gets the Reply text of the menu.</summary>
    public static string ReplyText => ChatCopy.Reply;

    /// <summary>Gets the name and tooltip of the ⋮ in the hover strip.</summary>
    public static string MoreOptionsText => ChatCopy.MoreOptions;

    /// <summary>Gets the Copy link text of the preview card menu.</summary>
    public static string CopyLinkText => ChatCopy.CopyLink;

    /// <summary>Gets the Open text of the menu.</summary>
    public static string OpenText => ChatCopy.Open;

    /// <summary>Gets the Open with text of the menu.</summary>
    public static string OpenWithText => ChatCopy.OpenWith;

    /// <summary>Gets the Save as text of the menu.</summary>
    public static string SaveAsText => ChatCopy.SaveAs;

    /// <summary>Gets the Download text of the menu.</summary>
    public static string DownloadText => ChatCopy.Download;

    /// <summary>Gets the name and tooltip of the play button of a video.</summary>
    public static string PlayVideoText => ChatCopy.PlayVideo;

    /// <summary>Gets the name and tooltip of the expand button over a playing video.</summary>
    public static string OpenVideoText => ChatCopy.OpenVideo;

    /// <summary>
    /// Gets the width of the hover strip slot, which the XAML binds. See <see cref="LanePadding"/>.
    /// </summary>
    public static double StripWidth => HoverStripWidth;

    /// <summary>Gets the tooltip and name of the lock in the caption row.</summary>
    public static string EncryptedText => ChatCopy.EndToEndEncrypted;

    /// <summary>Gets the word in the caption row for a message sent as SMS.</summary>
    public static string SmsText => ChatCopy.Sms;

    /// <summary>Gets the Delete text of the bubble menu.</summary>
    public static string DeleteText => ChatCopy.Delete;

    /// <summary>Gets the colored back, which the reaction picker opens over.</summary>
    internal FrameworkElement Anchor => Back;

    /// <summary>Gets or sets the message that the bubble shows.</summary>
    public MessageBubbleViewModel? Bubble
    {
        get => (MessageBubbleViewModel?)GetValue(BubbleProperty);
        set => SetValue(BubbleProperty, value);
    }

    /// <summary>
    /// Opens the bubble menu of the chat over the back.
    /// </summary>
    public void ShowMenu() => ShowMenu(Back);

    /// <summary>
    /// Opens the bubble menu of the chat at an element, such as the ⋮ of the hover strip. See
    /// <see cref="MenuRequested"/>.
    /// </summary>
    /// <remarks>The chat keeps the hover strip shown until the menu closes.</remarks>
    /// <param name="anchor">The element that the menu opens at.</param>
    public void ShowMenu(FrameworkElement anchor) => MenuRequested?.Invoke(this, anchor);

    /// <summary>
    /// Shows or hides the ring around the back of the bubble for the keyboard focus of its row.
    /// </summary>
    /// <remarks>
    /// The chat pane decides. See <see cref="MessageBubbleViewModel.ShowsFocusRing"/>. A test run also puts the result
    /// in the item status of the text: <c>FocusRing</c> or <c>NoRing</c>. Then comes <c>+SystemFocus</c> while the
    /// system focus visual of the row draws too.
    /// </remarks>
    /// <param name="shown">True to show the ring.</param>
    /// <param name="systemVisual">True when the system focus visual of the row draws, as in high contrast.</param>
    internal void ShowFocusRing(bool shown, bool systemVisual)
    {
        // Keyboard focus loads the hover strip, as the pointer does. In high contrast, the system visual takes the
        // place of the ring.
        if (shown || systemVisual)
        {
            LoadHoverStrip();
        }

        VisualStateManager.GoToState(this, shown ? "RingShown" : "RingHidden", false);
        TestHooks.ShowState(Body, (shown ? "FocusRing" : "NoRing") + (systemVisual ? "+SystemFocus" : string.Empty));
    }

    /// <summary>
    /// Keeps the hover strip shown while the reaction picker or the menu from the strip is open. The pointer leaves the
    /// bubble for that popup.
    /// </summary>
    /// <remarks>With the keep off, the strip hides unless the pointer is over the bubble again.</remarks>
    /// <param name="keep">True while the picker or the menu is open.</param>
    internal void KeepShown(bool keep)
    {
        _keepShown = keep;
        if (!keep && !_pointerOver)
        {
            ShowHover(false, Ui.AnimationsEnabled);
        }
    }

    // Loads the hover strip once, and it then stays. Most bubbles never get the pointer.
    private void LoadHoverStrip()
    {
        if (HoverStrip is null)
        {
            FindName(nameof(HoverStrip));
        }
    }

    // Shows or hides the hover strip. Before the Shown state, the method loads the strip, because the storyboard and
    // the setters of that state target it.
    private void ShowHover(bool shown, bool animate)
    {
        if (shown)
        {
            LoadHoverStrip();
        }

        VisualStateManager.GoToState(this, shown ? "Shown" : "Hidden", animate);
    }

    // =========================================================================
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Gives the AutomationId of a part: the prefix, then the <see cref="MessageBubbleViewModel.PartKey"/> of the
    /// bubble. The key is the message ID, or the temporary ID until the phone reports the message.
    /// </summary>
    /// <param name="prefix">The prefix, such as "Status.".</param>
    /// <param name="key">The part key of the bubble.</param>
    /// <returns>The AutomationId.</returns>
    public static string PartIdOf(string prefix, string key) => prefix + key;

    /// <summary>
    /// Gives the AutomationId of the text: <c>Bubble.</c> and the part key of the bubble. The key is the message ID, or
    /// the temporary ID until the phone reports the message.
    /// </summary>
    /// <param name="key">The part key of the bubble.</param>
    /// <returns>The AutomationId.</returns>
    public static string AutomationIdOf(string key) => PartIdOf("Bubble.", key);

    /// <summary>
    /// Gives the AutomationId of the "Not sent" link.
    /// </summary>
    /// <param name="key">The part key of the bubble.</param>
    /// <returns>The AutomationId.</returns>
    public static string RetryIdOf(string key) => AutomationIdOf(key) + ".Retry";

    /// <summary>
    /// Gives the AutomationId of the time.
    /// </summary>
    /// <param name="key">The part key of the bubble.</param>
    /// <returns>The AutomationId.</returns>
    public static string TimeIdOf(string key) => AutomationIdOf(key) + ".Time";

    /// <summary>
    /// Shows the time after a click on the bubble. The time stays hidden when the retry link takes its place or the
    /// caption row shows it.
    /// </summary>
    /// <param name="isTimeShown">True after a click.</param>
    /// <param name="isFailed">True when the send failed.</param>
    /// <param name="showsStatusRow">True when the caption row with the time shows.</param>
    /// <returns>The visibility of the time.</returns>
    public static Visibility TimeVisibility(bool isTimeShown, bool isFailed, bool showsStatusRow) =>
        isTimeShown && !isFailed && !showsStatusRow ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Shows a part of the delivery icon for some of the icons.
    /// </summary>
    /// <param name="icon">The icon of the bubble.</param>
    /// <param name="icons">
    /// The icons that the part belongs to, as their numbers. "123" is sending, sent and delivered.
    /// </param>
    /// <returns>Visible when the icon of the bubble is one of them.</returns>
    public static Visibility ShownFor(MessageStatusIcon icon, string icons) =>
        icon != MessageStatusIcon.None && icons.Contains((char)('0' + (int)icon), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Rounds a bubble as Google Messages does, with large corners. Where it meets the bubble above or below in its
    /// group, the side of the sender gets small corners: the right for sent, the left for received.
    /// </summary>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <param name="isFirst">True for the first bubble of its group.</param>
    /// <param name="isLast">True for the last bubble of its group.</param>
    /// <returns>The corners.</returns>
    public static CornerRadius CornersOf(bool fromMe, bool isFirst, bool isLast)
    {
        double top    = isFirst ? LargeCorner : SmallCorner;
        double bottom = isLast ? LargeCorner : SmallCorner;

        return fromMe
            ? new CornerRadius(LargeCorner, top, bottom, LargeCorner)
            : new CornerRadius(top, LargeCorner, LargeCorner, bottom);
    }

    /// <summary>
    /// Rounds the focus ring at a distance outside the back. Each corner of the back grows by that distance, so the
    /// ring follows the corners evenly. See <see cref="CornersOf"/>.
    /// </summary>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <param name="isFirst">True for the first bubble of its group.</param>
    /// <param name="isLast">True for the last bubble of its group.</param>
    /// <param name="outset">How far outside the back the edge of the ring sits, in effective pixels.</param>
    /// <returns>The corners.</returns>
    public static CornerRadius RingCorners(bool fromMe, bool isFirst, bool isLast, double outset)
    {
        CornerRadius back = CornersOf(fromMe, isFirst, isLast);
        return new CornerRadius(back.TopLeft + outset, back.TopRight + outset, back.BottomRight + outset, back.BottomLeft + outset);
    }

    /// <summary>
    /// Spaces bubbles 2 px apart inside a group and further apart between groups.
    /// </summary>
    /// <param name="isFirst">True for the first bubble of its group.</param>
    /// <param name="isLast">True for the last bubble of its group.</param>
    /// <returns>The margin of the row.</returns>
    public static Thickness GroupMargin(bool isFirst, bool isLast) =>
        new(0, isFirst ? GroupGap : InGroupGap, 0, isLast ? GroupGap : InGroupGap);

    /// <summary>
    /// Puts the reaction pill in the column under the row toward the middle of the chat. That is column 2 under a
    /// received bubble and column 0 under a sent bubble. Thus the time of a sent bubble keeps the right end of the row.
    /// </summary>
    /// <remarks>
    /// The XAML binds it with <c>x:Bind</c>, not with a setter of the Side state. A setter would force a deferred pill
    /// to load.
    /// </remarks>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <returns>The column of the pill.</returns>
    public static int PillColumn(bool fromMe) => fromMe ? 0 : 2;

    /// <summary>
    /// Gives the margin of the reaction pill: its top 8 px over the back, and a 6 px gap on the side of the time. The
    /// gap is on the left of a received pill and on the right of a sent pill.
    /// </summary>
    /// <remarks>
    /// The XAML binds it with <c>x:Bind</c>, not with a setter of the Side state. A setter would force a deferred pill
    /// to load.
    /// </remarks>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <returns>The margin of the pill.</returns>
    public static Thickness PillMargin(bool fromMe) =>
        fromMe ? new(0, -PillOverlap, PillGap, 0) : new(PillGap, -PillOverlap, 0, 0);

    /// <summary>
    /// Puts the slot of the hover strip beside the back on the inner side of the bubble. That is column 2 beside a
    /// received back and column 0 beside a sent back.
    /// </summary>
    /// <remarks>
    /// The XAML binds it with <c>x:Bind</c>, not with a setter of the Side state. A setter would force the deferred
    /// strip to load.
    /// </remarks>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <returns>The column of the slot.</returns>
    public static int StripColumn(bool fromMe) => fromMe ? 0 : 2;

    /// <summary>
    /// Gives the width of the avatar lane. Received bubbles in a group chat keep the lane, so their avatars align.
    /// </summary>
    /// <param name="hasLane">True for a received bubble in a group chat.</param>
    /// <returns>The width of the lane.</returns>
    public static double LaneWidth(bool hasLane) => hasLane ? AvatarLane : 0;

    /// <summary>
    /// Gives the padding of the bubble. The avatar lane width is on the left when the bubble has a lane. The hover
    /// strip width is on the strip side. Thus the name, the pill and the time align with the back of the bubble.
    /// </summary>
    /// <remarks>The strip is on the right of a received bubble and on the left of a sent bubble.</remarks>
    /// <param name="hasLane">True for a received bubble in a group chat.</param>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <returns>The padding.</returns>
    public static Thickness LanePadding(bool hasLane, bool fromMe) =>
        fromMe ? new(HoverStripWidth, 0, 0, 0) : new(LaneWidth(hasLane), 0, HoverStripWidth, 0);

    /// <summary>
    /// Gives the row of the back the negative margins of the padding. Thus the lane with the avatar and the hover strip
    /// sit in the padding, beside the back. See <see cref="LanePadding"/>.
    /// </summary>
    /// <param name="hasLane">True for a received bubble in a group chat.</param>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <returns>The margin.</returns>
    public static Thickness LaneOverhang(bool hasLane, bool fromMe)
    {
        Thickness padding = LanePadding(hasLane, fromMe);
        return new(-padding.Left, 0, -padding.Right, 0);
    }

    /// <summary>
    /// Spaces the preview card from the text above it. A message that was only the link has no text. The card then
    /// starts at the top of the bubble.
    /// </summary>
    /// <param name="hasText">True when the bubble shows text.</param>
    /// <returns>The margin of the card.</returns>
    public static Thickness PreviewMargin(bool hasText) => new(0, hasText ? PreviewGap : 0, 0, 0);

    /// <summary>
    /// Gives the padding of the back. A photo fills the bubble from edge to edge and gets none. Emoji alone have no
    /// bubble and get none, so they align with the edges of the other bubbles. Other content gets the text padding.
    /// </summary>
    /// <param name="hasPhoto">True when the bubble shows a photo.</param>
    /// <param name="emojiOnly">True when the message is only emoji.</param>
    /// <returns>The padding.</returns>
    public static Thickness BackPadding(bool hasPhoto, bool emojiOnly) => hasPhoto || emojiOnly ? default : TextPadding;

    /// <summary>
    /// Gives the margin of the text. Under a photo, the caption keeps the usual inset of the bubble. Else the padding
    /// of the back gives the inset.
    /// </summary>
    /// <param name="hasPhoto">True when the bubble shows a photo.</param>
    /// <returns>The margin.</returns>
    public static Thickness CaptionMargin(bool hasPhoto) => hasPhoto ? CaptionInset : default;

    /// <summary>
    /// Shows the fill and the edge of the bubble, except behind a photo with no caption and no quote. The edge of that
    /// photo is then the edge of the bubble, with no fill or stroke around it.
    /// </summary>
    /// <param name="hasPhoto">True when the bubble shows a photo.</param>
    /// <param name="hasCaption">True when a caption shows under the photo.</param>
    /// <param name="hasQuote">True when the bubble quotes the message that it replies to.</param>
    /// <param name="emojiOnly">
    /// True when the message is only emoji. They show big with no bubble around them, as issue #11 asked.
    /// </param>
    /// <returns>Collapsed for a photo alone or for emoji alone.</returns>
    public static Visibility FillShown(bool hasPhoto, bool hasCaption, bool hasQuote, bool emojiOnly) =>
        emojiOnly || (hasPhoto && !hasCaption && !hasQuote) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Rounds a photo to the exact corners of its bubble, because the photo covers the edge of the bubble. The bottom
    /// is square when a caption follows.
    /// </summary>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <param name="isFirst">True for the first bubble of its group.</param>
    /// <param name="isLast">True for the last bubble of its group.</param>
    /// <param name="hasCaption">True when a caption shows under the photo.</param>
    /// <returns>The corners.</returns>
    public static CornerRadius PhotoCorners(bool fromMe, bool isFirst, bool isLast, bool hasCaption)
    {
        CornerRadius outer = CornersOf(fromMe, isFirst, isLast);
        return new CornerRadius(
            outer.TopLeft,
            outer.TopRight,
            hasCaption ? 0 : outer.BottomRight,
            hasCaption ? 0 : outer.BottomLeft);
    }

    /// <summary>
    /// Gives the weight of the sender line in the quote. It is semibold beside the thumbnail of quoted media, else the
    /// weight of the caption style.
    /// </summary>
    /// <param name="hasMedia">True when the quote shows the thumbnail or the icon of the quoted media.</param>
    /// <returns>The weight.</returns>
    public static Windows.UI.Text.FontWeight QuoteSenderWeight(bool hasMedia) => hasMedia ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>
    /// Sets a media label such as "Image", "Video" or "GIF" in italic. A file name or text stays upright.
    /// </summary>
    /// <param name="isLabel">True for a label.</param>
    /// <returns>The style.</returns>
    public static Windows.UI.Text.FontStyle LabelStyle(bool isLabel) => isLabel ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

    /// <summary>
    /// Gives the icon of the file card: the video icon for a video, or the document icon for any other file.
    /// </summary>
    /// <param name="kind">The kind of the message.</param>
    /// <returns>The Segoe Fluent Icons glyph.</returns>
    public static string FileGlyph(MessageKind kind) => kind == MessageKind.Video ? "\uE714" : "\uE8A5";

    /// <summary>
    /// Writes a file size as Explorer does, such as "1.20 MB" or "940 bytes", with the formatter of Windows.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>The size, or an empty string for a size of 0, which means unknown.</returns>
    public static string SizeText(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[32];
        int result    = NativeMethods.StrFormatByteSizeEx((ulong)bytes, NativeMethods.SFBS_FLAGS_ROUND_TO_NEAREST_DISPLAYED_DIGIT, buffer, (uint)buffer.Length);
        return result < 0 ? string.Empty : new string(buffer).TrimEnd('\0');
    }

    /// <summary>
    /// Caps the bubble at 70% of the chat width and 560 px at most before the measure. Thus the bubble has the right
    /// width from its first layout, and short messages stay narrow.
    /// </summary>
    /// <remarks>
    /// The kept space of the hover strip comes on top of the cap. Thus the strip takes nothing from the bubble.
    /// </remarks>
    /// <param name="availableSize">The width and the height of the row.</param>
    /// <returns>The size that the bubble wants.</returns>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        if (double.IsFinite(availableSize.Width))
        {
            double cap       = Math.Min(MaxBubbleWidth, Math.Floor(availableSize.Width * MaxWidthShare));
            double maxWidth  = cap + HoverStripWidth;
            if (Root.MaxWidth != maxWidth)
            {
                Root.MaxWidth = maxWidth;
            }

            // The photo fills the bubble from edge to edge, beside the avatar lane.
            _photoMaxWidth = cap - LaneWidth(Bubble?.HasAvatarLane == true);
            SizePhoto();
        }

        return base.MeasureOverride(availableSize);
    }

    // =========================================================================
    // DEFERRED PARTS
    // =========================================================================

    // A part that loads or returns to the tree gets its states again. A new part has none of the setters of its
    // group.
    private void OnPartLoaded(object sender, RoutedEventArgs e) => ShowPartStates(refresh: true);

    // Sets the own states of the parts: the on-accent brushes on the parts of a sent SMS bubble, and the fill of my
    // reaction on the pill. The method sets states only on a loaded part, because the setter of a state would load its
    // part. With refresh, the method goes to a state again through the plain state. That is for a new part whose group
    // already sat in the state. A second go to the same state does nothing.
    private void ShowPartStates(bool refresh)
    {
        bool sms = Bubble is { FromMe: true, IsRcs: false };
        GoToPartState(QuoteButton, sms ? "QuoteOnAccent" : "QuotePlain", "QuotePlain", refresh);
        GoToPartState(FileCard, sms ? "FileOnAccent" : "FilePlain", "FilePlain", refresh);
        GoToPartState(Voice, sms ? "VoiceOnAccent" : "VoicePlain", "VoicePlain", refresh);
        GoToPartState(Preview, sms ? "PreviewOnAccent" : "PreviewPlain", "PreviewPlain", refresh);
        GoToPartState(PillHost, Bubble?.HasMyReaction == true ? "Mine" : "NotMine", "NotMine", refresh);
    }

    private void GoToPartState(FrameworkElement? part, string state, string plain, bool refresh)
    {
        if (part is null)
        {
            return;
        }

        if (refresh && state != plain)
        {
            VisualStateManager.GoToState(this, plain, false);
        }

        VisualStateManager.GoToState(this, state, false);
    }

    // Returns true when the element sits in the quote, photo, video, file card, voice message or preview card. Those
    // parts handle their own taps.
    private bool InPart(DependencyObject? element)
    {
        for (DependencyObject? at = element; at is not null && !ReferenceEquals(at, Back); at = VisualTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, QuoteButton) || ReferenceEquals(at, Photo) || ReferenceEquals(at, Video)
                || ReferenceEquals(at, FileCard) || ReferenceEquals(at, Voice) || ReferenceEquals(at, Preview))
            {
                return true;
            }
        }

        return false;
    }

    // The full image of the photo gets its clip after layout, and the clip follows its size.
    private void OnPhotoFullLoaded(object sender, RoutedEventArgs e) => ClipPhotoFull(mayTake: true);

    private void OnPhotoFullSizeChanged(object sender, SizeChangedEventArgs e) => ClipPhotoFull();

    // The picture of a playing video keeps the corners of the bubble.
    private void OnVideoPictureSizeChanged(object sender, SizeChangedEventArgs e) => ClipVideoPicture();

    // A new pill pops when it is in the tree with its size. It grows from its middle.
    private void OnPillHostLoaded(object sender, RoutedEventArgs e)
    {
        ShowPartStates(refresh: true);
        TryPopPill();
    }

    private void OnPillHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        LayPill();
        TryPopPill();
    }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // A recycled container gets another message. The view then follows the changes of that message and takes its
    // side. No hover strip stays from the last message.
    private static void OnBubbleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MessageBubble)d;

        // The recycled row restores its visual, so no entrance of its last bubble still runs. Only a row whose
        // entrance ran does this, so a row that never animated makes no visual. The try is a guard, because this code
        // runs in the ProcessBindings of x:Bind. There Windows can refuse the visual with E_ACCESSDENIED, as it did for
        // the photo clip. A throw that escapes into XAML crashes the app.
        if (view._animatedIn)
        {
            view._animatedIn = false;
            try
            {
                Visual row = ElementCompositionPreview.GetElementVisual(view.Row);
                row.StopAnimation(nameof(Visual.Opacity));
                row.Opacity = 1;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.MediaFailed(App.ShellLog, "entrance", view.Bubble?.Id ?? string.Empty, ex.GetType().Name);
            }
        }

        view._pointerOver = false;
        view._keepShown   = false;
        view._canAct      = view.Bubble?.CanAct == true;
        view.ShowHover(false, false);
        view.ShowFocusRing(false, false);

        if (e.OldValue is MessageBubbleViewModel oldBubble)
        {
            oldBubble.PropertyChanged -= view.OnBubblePropertyChanged;
        }

        if (e.NewValue is MessageBubbleViewModel newBubble)
        {
            newBubble.PropertyChanged += view.OnBubblePropertyChanged;
        }

        // The audio of a voice message waits for phase 1, the LoadMedia call of the chat. Thus a row that a scroll
        // passes over fetches no audio.
        view.ShowText();
        view.ShowStates();
        view.LoadMedia(fetchVoice: false);

        // The reactions of another message show as they are, with no pop. A recycled row stops a pop that is half
        // done. With no reactions, the pill unloads with its visual.
        view._reactionsShown = view.Bubble?.ReactionsText ?? string.Empty;
        view._popPill        = false;
        view.RestPill();
        if (view._reactionsShown.Length == 0)
        {
            view._pillVisual = null;
        }

        // A bubble that the chat added live animates in once. TakeNew clears the flag, so a recycled row never plays
        // the entrance again.
        if (view.Bubble?.TakeNew() == true && Ui.AnimationsEnabled)
        {
            view.AnimateIn();
        }
    }

    // Fades the row in on its composition visual, with only opacity. The owner did not want a rise into place.
    // The ease matches the controls of WinUI: ControlFastOutSlowInKeySpline, 0,0,0,1. Layout never moves, so the
    // scroll pin does not change.
    private void AnimateIn()
    {
        Visual visual                  = ElementCompositionPreview.GetElementVisual(Row);
        Compositor compositor          = visual.Compositor;
        CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = EntranceTime;

        visual.StartAnimation(nameof(Visual.Opacity), fade);
        _animatedIn = true;

        AppLog.BubbleAnimatedIn(App.ShellLog, Bubble?.PartKey ?? string.Empty);
    }

    // The bubble raises each changed property by name, and the bindings follow by themselves. This method runs only the
    // work that the name touches. An echo can make a sending bubble RCS, my reaction comes and goes, and a preview card
    // arrives.
    private void OnBubblePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // The text, or the ID that names its links.
            case nameof(MessageBubbleViewModel.BodyText) or nameof(MessageBubbleViewModel.PartKey):
                ShowText();
                break;

            // The side, the fill of my reaction and the place of the pill. The corners of the photo and the playing
            // video follow the side and the group place of the bubble. A newer message from the same sender can change
            // that place.
            case nameof(MessageBubbleViewModel.FromMe) or nameof(MessageBubbleViewModel.IsRcs)
                or nameof(MessageBubbleViewModel.IsEmojiOnly) or nameof(MessageBubbleViewModel.HasMyReaction) or nameof(MessageBubbleViewModel.HasReactions)
                or nameof(MessageBubbleViewModel.IsFirstInGroup) or nameof(MessageBubbleViewModel.IsLastInGroup):
                ShowStates();
                ClipPhotoFull();
                ClipVideoPicture();
                break;

            // A change of the media or the preview card loads it. A row that released its media for reuse loads none.
            case nameof(MessageBubbleViewModel.Media) or nameof(MessageBubbleViewModel.Preview) or nameof(MessageBubbleViewModel.HasPhoto)
                or nameof(MessageBubbleViewModel.HasVideo) or nameof(MessageBubbleViewModel.HasFileCard) or nameof(MessageBubbleViewModel.HasVoice):
                if (!_released)
                {
                    LoadMedia();
                }

                break;

            case nameof(MessageBubbleViewModel.CanAct):
                ShowHoverStrip();
                break;

            case nameof(MessageBubbleViewModel.ReactionsText):
                ShowReactionChange();
                break;

            // The corners of the photo follow the caption, and the clip of a GIF follows them. The case above handles a
            // change of the group.
            case nameof(MessageBubbleViewModel.HasBodyText):
                ClipPhotoFull();
                break;
        }
    }

    // =========================================================================
    // REACTION PILL
    // =========================================================================

    // When the reactions of the same bubble change, the pill pops. It pops at once when it already shows, else when
    // it is in the tree with its size. It stays hidden until then, so it never shows a frame at full size first. The
    // pop runs only with the animation effects of Windows on. When the last reaction goes, the pill unloads with its
    // visual.
    private void ShowReactionChange()
    {
        string text = Bubble?.ReactionsText ?? string.Empty;
        if (text == _reactionsShown)
        {
            return;
        }

        _reactionsShown = text;
        if (text.Length == 0)
        {
            _popPill    = false;
            _pillVisual = null;
            return;
        }

        if (!Ui.AnimationsEnabled)
        {
            return;
        }

        FindName(nameof(PillHost));
        _popPill = true;
        TryPopPill();
    }

    // Pops a waiting pill when it has its size and Windows gives its visual in the live tree. Until then the pill is
    // hidden.
    private void TryPopPill()
    {
        if (!_popPill || TakePillVisual() is not { } visual)
        {
            return;
        }

        if (PillHost!.ActualWidth > 0)
        {
            _popPill = false;
            PopPill();
            return;
        }

        visual.Opacity = 0;
    }

    // Pops the pill in from its middle: from half size and clear to a little over full size, then it settles. The pop
    // takes 180 ms on the fast-out-slow-in curve of WinUI. Layout never moves.
    private void PopPill()
    {
        if (PillHost is null || TakePillVisual() is not { } visual)
        {
            return;
        }

        Compositor compositor          = visual.Compositor;
        CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
        visual.CenterPoint             = new Vector3(PillHost.ActualSize / 2, 0);

        Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(PillPopFrom, PillPopFrom, 1));
        scale.InsertKeyFrame(0.6f, new Vector3(PillPopOver, PillPopOver, 1), ease);
        scale.InsertKeyFrame(1, Vector3.One, ease);
        scale.Duration = PillPopTime;

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.5f, 1, ease);
        fade.Duration = PillPopTime;

        visual.StartAnimation(nameof(Visual.Scale), scale);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        TestHooks.NoteState("reaction-pop", Bubble?.PartKey ?? string.Empty);
    }

    // Stops a pop and shows the pill at full size.
    private void RestPill()
    {
        if (_pillVisual is not { } visual)
        {
            return;
        }

        visual.StopAnimation(nameof(Visual.Scale));
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Scale   = Vector3.One;
        visual.Opacity = 1;
    }

    // Puts the pill on the bottom corner of the back toward the middle of the chat. Its top is 8 px over the back.
    // On a received bubble, the pill sits bottom right and ends 12 px in from the right of the back. On a sent bubble,
    // it sits bottom left and starts 12 px in from the left of the back. The owner asked for this mirror of the pill.
    // A short bubble is under twice the width of the pill, and that place would be past the far corner. Thus the pill
    // centers 4 px in from the edge of the back, over the corner. The owner asked for this in Plan 10.
    // The pill sits at the end of its column: the right of the row on received, its left on sent. A shift moves it
    // from there to its place, wherever the width of the row put it.
    // The time sits on the left of a received row and the right of a sent row. It drops below the pill when the two
    // would meet. Only its top margin changes, never its width, so the layout cannot loop.
    // With no pill, the time returns to its place. That is when the pill is not loaded or its last reaction just
    // went.
    private void LayPill()
    {
        if (PillHost is null || PillShift is null || Bubble?.HasReactions != true)
        {
            if (Captions.Margin != default)
            {
                Captions.Margin = default;
            }

            return;
        }

        double pillWidth  = PillHost.ActualWidth;
        bool shown        = pillWidth > 0;
        bool sent         = Bubble.FromMe;
        bool shortBack    = Under.MinWidth < pillWidth * 2;

        // On received, the row starts 12 px in from the left of the back. Its MinWidth point is 12 px in from the
        // right. On sent, the row ends 12 px in from the right of the back. There, the point 12 px in from the left is
        // Under.ActualWidth - Under.MinWidth.
        double left = sent
            ? (shortBack ? (Under.ActualWidth - Under.MinWidth) - PillCornerReach - (pillWidth / 2) : Under.ActualWidth - Under.MinWidth)
            : (shortBack ? Under.MinWidth + PillCornerReach - (pillWidth / 2) : Under.MinWidth - pillWidth);

        double natural = sent ? 0 : Under.ActualWidth - pillWidth;
        double shiftX  = shown ? left - natural : 0;

        bool drop = shown && (sent
            ? left + pillWidth + PillGap > Under.ActualWidth - Captions.ActualWidth
            : left < Captions.ActualWidth + PillGap);

        var margin = new Thickness(0, drop ? Math.Max(0, PillHost.ActualHeight - PillOverlap) : 0, 0, 0);
        if (PillShift.X != shiftX)
        {
            PillShift.X = shiftX;
        }

        if (Captions.Margin != margin)
        {
            Captions.Margin = margin;
        }
    }

    // Takes the visual of the pill host once while the bubble is in the live tree. Returns null when Windows refuses
    // it, because no pop is better than a crash.
    private Visual? TakePillVisual()
    {
        if (_pillVisual is not null || PillHost is not { IsLoaded: true })
        {
            return _pillVisual;
        }

        try
        {
            _pillVisual = ElementCompositionPreview.GetElementVisual(PillHost);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "pill", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }

        return _pillVisual;
    }

    // A bubble under the pointer can start or stop taking the strip, for example after an echo. The bubble then shows
    // or hides the strip with no pointer move.
    private void ShowHoverStrip()
    {
        bool canAct = Bubble?.CanAct == true;
        if (canAct == _canAct)
        {
            return;
        }

        _canAct = canAct;
        ShowHover(canAct && (_pointerOver || _keepShown), Ui.AnimationsEnabled);
    }

    // Decodes the image of the card from its bytes. Bytes that Windows cannot read leave the card with no image. With
    // no preview, the card unloads with its image.
    private async void ShowPreviewImage()
    {
        LinkPreview? preview = Bubble?.Preview;
        if (ReferenceEquals(preview, _previewShown))
        {
            return;
        }

        _previewShown = preview;
        if (preview is null)
        {
            return;
        }

        FindName(nameof(Preview));
        PreviewImage.Source     = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        if (preview.Image is not { } bytes)
        {
            return;
        }

        // A recycled container can show another message after the decode.
        ImageSource? image = await Avatar.ToImageAsync(bytes, PreviewImageWidth);
        if (image is null || !ReferenceEquals(preview, _previewShown))
        {
            return;
        }

        PreviewImage.Source     = image;
        PreviewImage.Visibility = Visibility.Visible;
    }

    // =========================================================================
    // MEDIA
    // =========================================================================

    /// <summary>
    /// Shows the media of the message and loads only what changed. That is the preview card image, the photo, the file
    /// card thumbnail, the voice message and the video frame.
    /// </summary>
    /// <remarks>
    /// The bubble calls it when its message or the media of the message changes. The chat calls it again when a list
    /// row has its message, in phase 1. Thus a row that gets back its last message after <see cref="ReleaseMedia"/>
    /// loads the media again. The audio of a voice message downloads for its waveform and length only when
    /// <paramref name="fetchVoice"/> is true. The own call of the bubble on a new message passes false. Thus the audio
    /// waits for the phase 1 call of the chat.
    /// </remarks>
    /// <param name="fetchVoice">True to fetch the audio of a voice message for its waveform.</param>
    internal void LoadMedia(bool fetchVoice = true)
    {
        _released = false;
        ShowPreviewImage();
        ShowPhoto();
        ShowFile();
        ShowVoice(fetchVoice);
        ShowVideo();
    }

    /// <summary>
    /// Releases the media of a list row while the row waits for reuse. That is the player of the inline video, the
    /// waveform of the voice message, and every decoded image.
    /// </summary>
    /// <remarks>
    /// The decoded images are the preview card image, the photo, the file thumbnail and the video frame. Loads that
    /// still run stop, and the bubble forgets the shown media, so <see cref="LoadMedia"/> loads it again. The bubble
    /// keeps its message, because a clear would unload and reload every deferred <c>x:Load</c> part on each recycle.
    /// Parts that the message does not use are not loaded, so the method checks each part first.
    /// </remarks>
    internal void ReleaseMedia()
    {
        _released = true;

        ReleaseVideo();
        _videoPart       = null;
        _videoBubble     = null;
        _videoFrameImage = null;
        if (VideoThumb is not null)
        {
            VideoThumb.Background = null;
        }

        // A new load number stops a late load or player update of the voice message.
        _voicePart   = null;
        _voiceBubble = null;
        _voiceWave   = null;
        _voiceLoad++;

        // The photo releases its blurred and full images and any GIF. A load that still runs stops.
        _photoPart   = null;
        _photoBubble = null;
        _photoFailed = false;
        NextPhotoLoad();
        WatchGif(null);
        if (Photo is not null)
        {
            PhotoBlur.Background  = null;
            PhotoFull.Background  = null;
            PhotoFullImage.Source = null;
        }

        _filePart   = null;
        _fileBubble = null;
        if (FileThumb is not null)
        {
            FileThumb.Background = null;
        }

        _previewShown = null;
        if (PreviewImage is not null)
        {
            PreviewImage.Source     = null;
            PreviewImage.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStates()
    {
        // Emoji alone have no accent fill behind them. Thus a sent SMS of emoji takes the plain text color of the Sent
        // state, not the on-accent color.
        string side = Bubble switch
        {
            { FromMe: false } or null                => "Received",
            { IsRcs: true } or { IsEmojiOnly: true } => "Sent",
            _                                        => "SentSms",
        };

        VisualStateManager.GoToState(this, side, false);
        VisualStateManager.GoToState(this, Bubble?.IsEmojiOnly == true ? "BigEmoji" : "BodyText", false);
        ShowPartStates(refresh: false);

        // The pill gets its place again, so a recycled bubble on the other side does not keep the old shift.
        LayPill();
    }

    // Shows the text with its web links as links, or as plain text when it has none. While the card shows, the text
    // has no link of the card.
    private void ShowText()
    {
        string text = Bubble?.BodyText ?? string.Empty;
        string key  = Bubble?.PartKey ?? string.Empty;
        if (text == _shownText && key == _shownKey)
        {
            return;
        }

        _shownText = text;
        _shownKey  = key;
        _links.Clear();
        LinkBlockers.Children.Clear();

        IReadOnlyList<TextLink> links = LinkPreviewer.FindLinks(text);
        if (links.Count == 0)
        {
            Body.Text = text;
            return;
        }

        // The line ends are known after the layout of the new text.
        void OnLaidOut(object? sender, object e)
        {
            Body.LayoutUpdated -= OnLaidOut;
            BlockLinkEnds();
        }

        Body.LayoutUpdated += OnLaidOut;

        // Plain runs fill the text between the links.
        Body.Inlines.Clear();
        int at = 0;
        foreach (TextLink link in links)
        {
            if (link.Start > at)
            {
                Body.Inlines.Add(new Run { Text = text[at..link.Start] });
            }

            Body.Inlines.Add(NewLink(text.Substring(link.Start, link.Length), link.Url, _links.Count));
            at = link.Start + link.Length;
        }

        if (at < text.Length)
        {
            Body.Inlines.Add(new Run { Text = text[at..] });
        }
    }

    // Makes a link named by its text. Only a click or Enter opens it, never the app itself.
    private Hyperlink NewLink(string shown, Uri url, int number)
    {
        var link = new Hyperlink { Foreground = Body.Foreground, UnderlineStyle = UnderlineStyle.Single };
        link.Inlines.Add(new Run { Text = shown });
        AutomationProperties.SetAutomationId(link, PartIdOf("Link.", Bubble?.PartKey ?? string.Empty) + "." + number);
        link.Click += (_, _) =>
        {
            _linkClicked = true;
            XamlHost.Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => _linkClicked = false);
            OpenLink(url);
        };

        _links.Add(link);
        return link;
    }

    // A click, Enter or Space on the quote makes the chat jump to the quoted message.
    private void OnQuoteClick(object sender, RoutedEventArgs e) => QuoteRequested?.Invoke(this, EventArgs.Empty);

    // A new width moves the line ends.
    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => BlockLinkEnds();

    // The text maps the empty space at the end of a line to the last letter of the line. Thus past a link that ends or
    // wraps a line, the pointer still hovers the link and shows the hand. A clear strip covers that space from the
    // last letter of the line to the right edge of the text. It covers each line where the letters of a link end. Only
    // the letters count.
    private void BlockLinkEnds()
    {
        LinkBlockers.Children.Clear();
        double width = Body.ActualWidth;
        if (_links.Count == 0 || width <= 0)
        {
            return;
        }

        foreach (Hyperlink link in _links)
        {
            // The loop finds each line that the letters of the link sit on, with its top, height and rightmost edge.
            // GetCharacterRect gives the caret, which has no width. Thus the right edge of each letter is the caret
            // just after it, taken backward. At a wrap, that caret stays at the end of the line that it leaves.
            List<Rect> lines = [];
            TextPointer? at  = link.ContentStart.GetPositionAtOffset(1, LogicalDirection.Forward);
            while (at is not null && at.Offset <= link.ContentEnd.Offset)
            {
                Rect caret = at.GetCharacterRect(LogicalDirection.Backward);
                int last   = lines.Count - 1;
                if (last >= 0 && Math.Abs(lines[last].Y - caret.Y) < 1)
                {
                    lines[last] = new Rect(0, lines[last].Y, Math.Max(lines[last].Width, caret.X), Math.Max(lines[last].Height, caret.Height));
                }
                else if (caret.Height > 0)
                {
                    lines.Add(new Rect(0, caret.Y, caret.X, caret.Height));
                }

                at = at.GetPositionAtOffset(1, LogicalDirection.Forward);
            }

            // Every line but the last ends with the link. The last line also ends with it, unless text follows on that
            // line.
            for (int i = 0; i < lines.Count; i++)
            {
                Rect line = lines[i];
                if (i == lines.Count - 1 && FollowedOnLine(link, line))
                {
                    continue;
                }

                AddBlocker(line.Right, line.Y, width - line.Right, line.Height);
            }
        }
    }

    // Returns true when a letter after the link sits on the last line of the link. The letter is past the next run
    // start.
    private static bool FollowedOnLine(Hyperlink link, Rect line)
    {
        // The loop looks for the caret just after the next letter: the first caret past the end of the link that moved.
        // Element edges do not move the caret. When the text ends with the link, the loop finds none.
        Rect end           = link.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        TextPointer? after = link.ElementEnd;
        for (int step = 0; step < 4 && after is not null; step++)
        {
            after = after.GetPositionAtOffset(1, LogicalDirection.Forward);
            if (after is not null && after.GetCharacterRect(LogicalDirection.Backward) is { Height: > 0 } caret
                && (Math.Abs(caret.X - end.X) >= 0.5 || Math.Abs(caret.Y - end.Y) >= 0.5))
            {
                return Math.Abs(caret.Y - line.Y) < 1;
            }
        }

        return false;
    }

    // Adds a clear strip that takes the pointer through its transparent fill. A right-click on it goes to the bubble
    // menu of the chat, as a right-click on the text does.
    private void AddBlocker(double left, double top, double width, double height)
    {
        if (width < 1 || height < 1)
        {
            return;
        }

        var blocker = new Border { Width = width, Height = height, Background = new SolidColorBrush(Windows.UI.Colors.Transparent) };
        Canvas.SetLeft(blocker, left);
        Canvas.SetTop(blocker, top);
        LinkBlockers.Children.Add(blocker);
    }

    private void ColorLinks()
    {
        foreach (Hyperlink link in _links)
        {
            link.Foreground = Body.Foreground;
        }
    }

    // Only web addresses open. FindLinks gives no other kind, and the method checks again before any launch. A test run
    // only logs the click. The log gets only the scheme, never the address.
    private static void OpenLink(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        bool launch = !TestHooks.BlockLaunches;
        AppLog.LinkOpened(App.ShellLog, url.Scheme, launch);
        if (launch)
        {
            _ = Launcher.LaunchUriAsync(url);
        }
    }

    // The time toggles a turn later, after the click of a link. That click can come after the tap.
    private void OnBackTapped(object sender, TappedRoutedEventArgs e)
    {
        // The quote, photo, video, file card, voice message and preview card handle their own taps.
        if (InPart(e.OriginalSource as DependencyObject))
        {
            return;
        }

        XamlHost.Dispatcher.TryEnqueue(() =>
        {
            if (_linkClicked)
            {
                _linkClicked = false;
                return;
            }

            Bubble?.ToggleTime();
        });
    }

    // A tap on the row beside the bubble also toggles the time: on its empty width, the avatar or the time. The bubble
    // has its own handler above. A button such as the hover strip or the pill handles its own tap.
    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        for (DependencyObject? at = e.OriginalSource as DependencyObject; at is not null && !ReferenceEquals(at, Row); at = VisualTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, Back))
            {
                return;
            }
        }

        Bubble?.ToggleTime();
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (Bubble?.Preview is { } preview)
        {
            OpenLink(preview.Url);
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Runs Copy from the bubble menu. It copies the selected text, or the whole text when nothing is selected. The own
    /// Copy of the text did the same.
    /// </summary>
    /// <remarks>The copy is always the text as sent, without the VS16 that the bubble adds for color emoji.</remarks>
    public void Copy()
    {
        string original = Bubble?.Text ?? string.Empty;
        string text     = Body.SelectedText.Length > 0 ? ChatFormat.OriginalOf(original, Body.SelectedText) : original;
        if (text.Length == 0)
        {
            return;
        }

        UiHelpers.ToClipboard(text);
    }

    /// <summary>
    /// Runs Copy link from the preview card menu. It copies the whole address as sent, which the text no longer shows.
    /// </summary>
    public void CopyLink()
    {
        if (Bubble?.LinkUrl is { } url)
        {
            UiHelpers.ToClipboard(url.OriginalString);
        }
    }

    /// <summary>
    /// Runs React from the bubble menu, which asks the chat to open the reaction picker over the back.
    /// </summary>
    public void React() => ReactRequested?.Invoke(this, Back);

    // The pill shows who reacted, in every chat. It never opens the picker. The menu, the strip and Shift + + open the
    // picker.
    private void OnPillClick(object sender, RoutedEventArgs e)
    {
        if (Bubble is not { } bubble)
        {
            return;
        }

        ShowReactors(bubble.Reactors());
    }

    // Shows who reacted in a flyout list at the pill, with 1 row for each person, such as "👍 Bo". Narrator reads
    // the rows Reactors.{n}. The rows are only for reading, so they show no hover or press fill. The list is compact as
    // a menu: 32 px rows, 8 px from emoji to name, and the thin edge of a menu around the list. Names never reach the
    // log.
    private void ShowReactors(IReadOnlyList<(string Emoji, string Name)> reactors)
    {
        if (reactors.Count == 0)
        {
            return;
        }

        var list  = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = ReactorsMaxHeight };
        var clear = new SolidColorBrush(Windows.UI.Colors.Transparent);
        list.Resources["ListViewItemBackgroundPointerOver"] = clear;
        list.Resources["ListViewItemBackgroundPressed"]     = clear;
        AutomationProperties.SetAutomationId(list, "Reactors");

        for (int i = 0; i < reactors.Count; i++)
        {
            (string emoji, string name) = reactors[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = ReactorsSpacing };
            row.Children.Add(new TextBlock { Text = emoji, IsColorFontEnabled = true });
            row.Children.Add(new TextBlock { Text = name });

            var item = new ListViewItem { Content = row, MinHeight = ReactorsRowHeight };
            AutomationProperties.SetAutomationId(item, "Reactors." + i);
            AutomationProperties.SetName(item, emoji + " " + name);
            list.Items.Add(item);
        }

        new Flyout { Content = list, FlyoutPresenterStyle = Application.Current.Resources["ReactorsFlyoutPresenterStyle"].As<Style>() }.ShowAt(Pill);
    }

    // The buttons of the strip act only while the bubble takes them, because a click can race a change. React of the
    // strip opens the picker over the button.
    private void OnHoverReactClick(object sender, RoutedEventArgs e)
    {
        if (Bubble?.CanAct == true)
        {
            ReactRequested?.Invoke(this, HoverReact);
        }
    }

    private void OnHoverReplyClick(object sender, RoutedEventArgs e)
    {
        if (Bubble?.CanAct == true)
        {
            ReplyRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnHoverMoreClick(object sender, RoutedEventArgs e)
    {
        if (Bubble?.CanAct == true)
        {
            ShowMenu(HoverMore);
        }
    }

    // Only a bubble that can take them shows the strip.
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        if (Bubble?.CanAct == true)
        {
            ShowHover(true, Ui.AnimationsEnabled);
        }
    }

    // The pointer left the bubble. As on the conversation row, the method makes no bounds check. A pointer that leaves
    // the window straight from a bubble reports its last point inside the bubble. While the picker or the menu is
    // open, its overlay takes the pointer. The strip then waits for the popup to close.
    private void OnPointerLeft(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        if (!_keepShown)
        {
            ShowHover(false, Ui.AnimationsEnabled);
        }
    }

    /// <summary>
    /// Runs Reply from the bubble menu, which asks the chat to start a reply to this bubble.
    /// </summary>
    public void Reply() => ReplyRequested?.Invoke(this, EventArgs.Empty);
}

using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using GMessages.App.Media;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// One message bubble: sent ones on the right (RCS in a lighter shade of the accent, SMS in the accent color), received
/// ones on the left in the card color.
/// </summary>
/// <remarks>
/// <para>
/// The text carries the AutomationId <c>Bubble.{id}</c>, or <c>Bubble.{tmpId}</c> until the phone has echoed a sent
/// message. A failed bubble shows the "Not sent" link (<c>Bubble.{id}.Retry</c>) in place of the time; clicking it
/// raises <see cref="RetryRequested"/> for the chat to send it again.
/// </para>
/// <para>
/// The time (<c>Bubble.{id}.Time</c>) is hidden until the bubble is clicked, and a second click hides it; on the chat's
/// newest bubble the click hides its caption row instead, and a second one shows it again. The click is caught even
/// when it lands on the text, whose selection marks the pointer handled. The chat pane does the same for Enter and
/// Space on a focused bubble.
/// </para>
/// <para>
/// The side is a visual state so its brushes stay theme resources and follow the window's light or dark theme. So is
/// the reaction pill's accent border for my reaction. Both follow the bubble's changes (an echo turns a sending bubble
/// into the sent one). Their groups sit on the control's root element (<c>Row</c>), the only place GoToState looks.
/// </para>
/// <para>
/// The reply quote shows above the text (<c>Quote.{id}</c>), the reaction pill (<c>Reactions.{id}</c>) over the bottom
/// corner and, under the chat's newest message, the caption row: the time (<c>Caption.{id}</c>), on my message the
/// owner's delivery icon (named <c>Status.{id}</c>: "Sent", "Delivered" or "Read"), and the lock in an encrypted chat. A link's preview card (<c>Preview.{id}</c>), named by its
/// title and the link's host, shows under the text once fetched, and the text then leaves out that link's address (a
/// message that was only the link shows the card alone). Clicking the card opens the link in the default browser; its
/// own menu (right-click, the Application key or Shift + F10) has Copy link (<c>Preview.CopyLink</c>), which copies the
/// address as sent. In every chat the pill opens who reacted (<c>Reactors</c>, rows <c>Reactors.{n}</c>, mine as
/// "You"). The menu's React and the hover strip's React raise
/// <see cref="ReactRequested"/>, and the menu's Reply raises <see cref="ReplyRequested"/>, for the chat to act on.
/// </para>
/// <para>
/// Web links in the text (see <see cref="LinkPreviewer.FindLinks"/>) show as underlined links in the text's own color,
/// so they read on every bubble color and in high contrast. Each is named <c>Link.{id}.{n}</c> (n from 0) and reached
/// with Tab; a click or Enter opens it in the default browser (http and https only), and that click doesn't also
/// show or hide the time. Selection and Copy work on the whole text as before.
/// </para>
/// <para>
/// Bubbles of one group (see <see cref="ChatViewModel.GroupGap"/>) sit close together with small corners where they meet,
/// as in Google Messages. A bubble is as wide as its content, up to 70% of the chat and 560 px at most.
/// </para>
/// <para>
/// Pointing at a bubble that can take them (<see cref="MessageBubbleViewModel.CanAct"/>) fades in react
/// (<c>HoverReact.{id}</c>), reply (<c>HoverReply.{id}</c>) and the ⋮ (<c>HoverMore.{id}</c>) beside its back, as in
/// Google Messages for web, through the <c>Hover</c> visual states. Their space is always kept, so nothing moves.
/// </para>
/// <para>
/// A photo (<c>Bubble.Photo.{id}</c>, named by its caption or "Photo") fills the bubble edge to edge, with its caption
/// under it. Its size comes from the photo's dimensions before anything loads, so the chat never jumps. Its thumbnail
/// shows first, decoded tiny and stretched so it looks blurred (a Composition blur needs Win2D, which this app doesn't
/// carry), then the full image fades in over it. A thumbnail-only photo asks the phone for the full size, which arrives
/// as an edit of the message. While the phone hasn't sent the photo it reads "Waiting for the photo from your phone…";
/// a failure (or a full size that never comes) reads "Couldn't load this photo." and a click tries again. Otherwise a
/// click shows it in the window's photo viewer (see <see cref="PhotoViewer"/>), as does the menu's Open
/// (<c>Bubble.Open</c>); Open with (<c>Bubble.OpenWith</c>, on every attachment's menu) opens it with Windows' app
/// picker; Save as (<c>Bubble.SaveAs</c>) saves a copy where the person picks, and Download
/// (<c>Bubble.Download</c>, also on a file's menu) saves it to the Downloads folder.
/// </para>
/// <para>
/// A file shows as a card (<c>Bubble.File.{id}</c>, named by the file name): its thumbnail when one came, else its
/// type's icon, the name, and the size as Explorer writes it. A click shows "Downloading…" while it downloads, then
/// opens it in its default app; a failure reads "Couldn't open this file." until the next click.
/// </para>
/// <para>
/// A video fills the bubble like a photo: its first frame (<c>Bubble.Video.{id}</c>, named by its caption or "Video",
/// with the hand cursor) under a play button (<c>Video.Play.{id}</c>, "Play video"). Play plays it in place
/// (<c>Video.Player.{id}</c>, a <see cref="VideoPlayback"/> with compact controls), with sound, as media; only one
/// plays at a time. The player is let go when the row is recycled, unloads or scrolls out of sight, on a chat switch,
/// and when the viewer opens. The frame, the menu's Open, or the expand button over a playing video
/// (<c>Video.Expand.{id}</c>, "Open video") opens it in the window's viewer, from where it got to.
/// </para>
/// <para>
/// A bubble the chat added live (sent, or arrived while the chat is open; see
/// <see cref="MessageBubbleViewModel.TakeNew"/>) fades in and rises 12 px into place once, on its composition visual, so
/// layout and the list's scroll pin never see it move. The first page, older pages, cached bubbles and recycled rows
/// show without it, and so does everything when Windows' animation effects are off.
/// </para>
/// <para>
/// A bubble whose row has keyboard focus draws a ring around its back (see <see cref="ShowFocusRing"/>), in place of the
/// row's system focus visual; in high contrast the system visual shows instead.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "ReleaseVideo disposes the inline player on recycle, scroll away, unload and chat switch; a control has no Dispose of its own.")]
public sealed partial class MessageBubble : UserControl
{
    /// <summary>Identifies the <see cref="Bubble"/> property.</summary>
    public static readonly DependencyProperty BubbleProperty =
        DependencyProperty.Register(nameof(Bubble), typeof(MessageBubbleViewModel), typeof(MessageBubble), new PropertyMetadata(null, OnBubbleChanged));

    // A Bubble Still Being Sent Shows Faded
    private const double SendingFade = 0.6;

    // How Far The Reaction Pill's Top Overlaps The Back (PillHost's -8 Top Margin)
    private const double PillOverlap = 8;

    // The Gap Between The Reaction Pill And The Time Beside It (PillHost's 6 px Margin On The Time's Side)
    private const double PillGap = 6;

    // A Short Bubble's Pill Is Centered This Far Outward Of The Point 12 px In From The Back's Edge (4 px In From The Edge)
    private const double PillCornerReach = 8;

    // The Preview Card's Image Width, And Its Gap Below The Text, In Effective Pixels
    private const int PreviewImageWidth = 240;
    private const double PreviewGap     = 8;

    // Who Reacted: The List Scrolls Past This Height, And Each Row's Emoji Sits This Far From The Name
    private const double ReactorsMaxHeight = 320;
    private const double ReactorsSpacing   = 8;
    private const double ReactorsRowHeight = 32;

    // Width: A Bubble Grows With Its Content Up To This Share Of The Chat, And Never Past The Cap (Effective Pixels)
    private const double MaxWidthShare  = 0.7;
    private const double MaxBubbleWidth = 560;

    // Corners: Large Round Ones, And Small Ones Where Bubbles Of A Group Meet On The Sender's Side
    private const double LargeCorner = 18;
    private const double SmallCorner = 4;

    // Spacing: Half The Gap Inside A Group, And Half The Gap Between Groups
    private const double InGroupGap = 1;
    private const double GroupGap   = 4;

    // The Avatar Lane Beside Received Bubbles In A Group Chat
    private const double AvatarLane = 36;

    // The Hover Strip: Three 32 px Buttons With 4 px Either Side (Its Slot In MessageBubble.xaml Binds StripWidth)
    private const double HoverStripWidth = 104;

    // A Photo's Blurred Look: Its Thumbnail Decoded This Wide, Then Stretched To The Photo's Size
    private const int BlurDecodeWidth = 24;

    // The File Card's Thumbnail Width: Its 40 px Square, In Logical Pixels (The Display Scale Is Already In Them)
    private const int FileThumbWidth = 40;

    // A New Bubble's Entrance Rises From This Far Below (Over EntranceTime)
    private const float EntranceRise = 12;

    // A Photo's Caption Sits This Far Inside The Bubble; The Back's Usual Padding Otherwise
    private static readonly Thickness CaptionInset = new(12, 6, 12, 8);
    private static readonly Thickness TextPadding  = new(12, 8, 12, 8);

    // How Long A Thumbnail-Only Photo Waits For The Phone's Full Size Before It Offers To Try Again
    private static readonly TimeSpan FullSizeWait = TimeSpan.FromSeconds(30);

    // A New Bubble's Entrance: A Fade And A Rise (EntranceRise), Over WinUI's Normal Control Animation Time
    private static readonly TimeSpan EntranceTime = TimeSpan.FromMilliseconds(250);

    // A Reaction Pill's Pop: From Half Size, A Touch Over Full Size, Then Settling
    private const float PillPopFrom = 0.5f;
    private const float PillPopOver = 1.08f;
    private static readonly TimeSpan PillPopTime = TimeSpan.FromMilliseconds(180);

    // Reads The Windows Animation Setting Live (Settings > Accessibility > Visual Effects)
    private static readonly UISettings Ui = new();

    // The Pointer Is Over The Bubble; The Picker Or Menu Opened From The Strip Is Open, So The Strip Stays While The
    // Pointer Is In It
    private bool _pointerOver;
    private bool _keepShown;

    // Whether The Bubble Took The Strip When Last Seen, So A Change (Sending To Sent) Shows Or Hides It Under The Pointer
    private bool _canAct;

    // The Preview The Card Shows (Or Is Decoding), So A Change Decodes Once; Only Its Decoded Image Is Held Here
    private LinkPreview? _previewShown;

    // A Click On A Link In The Text Also Reaches The Bubble's Back, Which Must Not Toggle The Time For It. Its Click And
    // The Back's Tap Come In Either Order, So The Tap Waits A Turn
    private bool _linkClicked;

    // The Text And Part Key The Text's Runs Were Built For: Rebuilt Only When They Change (A Rebuild Drops A Selection)
    private string? _shownText;
    private string? _shownKey;

    // The Text's Links, Kept In The Text's Color
    private readonly List<Hyperlink> _links = [];

    // The Full Photo's Fade In Over The Blurred One
    private readonly ScalarTransition _photoFade = new();

    // The Photo Shown (Or Loading) And Its Bubble; Each Load Gets A Number, So One Overtaken By A Newer Load Stops. The
    // Bubble Is The View Model Itself: It Stays One Object Through Its Echo (Its ID Arrives) And A Retry (A New Temporary
    // ID), While A Recycled Container Gets Another One
    private MediaPart? _photoPart;
    private MessageBubbleViewModel? _photoBubble;
    private int _photoLoad;
    private bool _photoFailed;

    // Whether The Full Image Is A Moving GIF (An Image, Clipped On Its Composition Visual), Else A Still One (A Brush);
    // And The GIF Image's Visual, Taken Once While It Is In The Live Tree
    private bool _photoAnimated;
    private Visual? _photoFullVisual;

    // Cancels A Thumbnail-Only Photo's Wait For Its Full Size When A Newer Load Starts (Null When None Is Waiting)
    private Action? _cancelPhotoWait;

    // The Widest A Photo May Be: The Bubble's Width Cap (Set At Measure; The Photo Covers The Bubble's Edge)
    private double _photoMaxWidth = MaxBubbleWidth;

    // The File The Card Shows, Its Message, And Whether It Is Opening Or Its Last Open Failed
    private MediaPart? _filePart;
    private MessageBubbleViewModel? _fileBubble;
    private bool _fileOpening;
    private bool _fileFailed;

    // The Voice Message Shown, Its Bubble, Its Player Key (Kept Through The Echo), Its Load Number (Bumped When Another
    // Message Shows, So A Late Load Or Player Update Stops), The Load Number Whose Audio Fetch Started, And Its Waveform
    // Once Read. The Audio Itself Isn't Kept: Play Gets It From The Media Store Again
    private MediaPart? _voicePart;
    private MessageBubbleViewModel? _voiceBubble;
    private string _voiceKey = string.Empty;
    private int _voiceLoad;
    private int _voiceFetched = -1;
    private VoiceWave? _voiceWave;

    // The Bubble Whose Video Plays In Place: One At A Time Across The Chat
    private static MessageBubble? s_playingVideo;

    // The Video Shown, Its Bubble, Its Load Number (Bumped When It Stops Or Another Message Shows, So A Late Download
    // Stops), The Inline Player While It Plays, And The Player Host's Visual (For Its Rounded Clip; Taken Once)
    private MediaPart? _videoPart;
    private MessageBubbleViewModel? _videoBubble;
    private int _videoLoad;
    private VideoPlayback? _video;
    private Visual? _videoHostVisual;

    // The Video's Still Frame Once It Shows, Which The Viewer Grows Out Of The Bubble Before Its Player Takes Over
    private ImageSource? _videoFrameImage;

    // The Reactions The Pill Shows, So A Change On The Same Bubble Pops It In; Set When A Pill Just Shown Waits For Its
    // Size To Pop; And The Pill Host's Visual (No Implicit Transitions On It, So Windows Hands It Out), Taken Once Per
    // Pill (The Pill Unloads With The Last Reaction, And A New One Loads With The Next)
    private string _reactionsShown = string.Empty;
    private bool _popPill;
    private Visual? _pillVisual;

    // The Row Let Go Of Its Media And Waits To Be Used Again: Its Message's Updates Load None Into It Meanwhile
    private bool _released;

    // Whether The Entrance Ran On This Row, So A Recycle Puts Its Visual Back (A Row That Never Animated Makes None)
    private bool _animatedIn;

    // The Moving GIF Shown, And Whether It Was Paused For Being Off Screen (So Only That Pause Plays It Again)
    private BitmapImage? _gif;
    private bool _gifPaused;

    /// <summary>
    /// Creates the bubble.
    /// </summary>
    public MessageBubble()
    {
        InitializeComponent();

        // Behavior. The Parts A Message Doesn't Use (Quote, Photo, Video, File Card, Voice, Upload Bar, Preview Card, Pill,
        // Retry Footer, Caption Row) Aren't Made (x:Load), So Their Own Wiring Sits On Them In The XAML, Made With Them
        Back.AddHandler(TappedEvent, new TappedEventHandler(OnBackTapped), handledEventsToo: true);

        // A Playing Video Stops When The Bubble Leaves The Tree
        Unloaded += (_, _) => ReleaseVideo();

        // The Row Under The Bubble Is At Least As Wide As The Back (Inside Its Margins), So The Pill Sits On The Back's Corner
        Back.SizeChanged += (_, _) =>
        {
            Under.MinWidth = Math.Max(0, Back.ActualWidth - Under.Margin.Left - Under.Margin.Right);
            LayPill();
        };
        Under.SizeChanged    += (_, _) => LayPill();
        Captions.SizeChanged += (_, _) => LayPill();

        // The Text's Own Menu Would Win A Right-Click On It, So It Has None: The Request Goes On Up To The Chat Pane, Whose
        // Bubble Menu Has Copy
        Body.ContextFlyout = null;

        // Links Follow The Text's Color: Its Side's State And The Theme Set It
        Body.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => ColorLinks());
    }

    /// <summary>
    /// Asks the chat to send this failed bubble again.
    /// </summary>
    public event EventHandler? RetryRequested;

    /// <summary>
    /// Asks the chat to open the reaction picker on this bubble (the menu's React or the hover strip's React),
    /// over the element given: the back, or the strip's React button.
    /// </summary>
    public event EventHandler<FrameworkElement>? ReactRequested;

    /// <summary>
    /// Asks the chat to start a reply to this bubble (the menu's Reply or the hover strip's Reply).
    /// </summary>
    public event EventHandler? ReplyRequested;

    /// <summary>
    /// Asks the chat to open its bubble menu on this bubble, at the element given: the back, or the hover strip's ⋮.
    /// </summary>
    public event EventHandler<FrameworkElement>? MenuRequested;

    /// <summary>
    /// Asks the chat's voice player to play, pause or seek this bubble's voice message.
    /// </summary>
    public event EventHandler<VoiceRequest>? VoiceRequested;

    /// <summary>
    /// Asks the chat to jump to the message this bubble's quote shows (a click, Enter or Space on the quote).
    /// </summary>
    public event EventHandler? QuoteRequested;

    /// <summary>Gets the quote's accessible name: it jumps to the quoted message.</summary>
    public static string QuoteJumpText => ChatCopy.GoToOriginalMessage;

    /// <summary>Gets the menu's Copy text.</summary>
    public static string CopyText => ChatCopy.Copy;

    /// <summary>Gets the menu's React text.</summary>
    public static string ReactText => ChatCopy.React;

    /// <summary>Gets the menu's Reply text.</summary>
    public static string ReplyText => ChatCopy.Reply;

    /// <summary>Gets the hover strip ⋮'s name and tooltip.</summary>
    public static string MoreOptionsText => ChatCopy.MoreOptions;

    /// <summary>Gets the preview card menu's Copy link text.</summary>
    public static string CopyLinkText => ChatCopy.CopyLink;

    /// <summary>Gets the menu's Open text.</summary>
    public static string OpenText => ChatCopy.Open;

    /// <summary>Gets the menu's Open with text.</summary>
    public static string OpenWithText => ChatCopy.OpenWith;

    /// <summary>Gets the menu's Save as text.</summary>
    public static string SaveAsText => ChatCopy.SaveAs;

    /// <summary>Gets the menu's Download text.</summary>
    public static string DownloadText => ChatCopy.Download;

    /// <summary>Gets the video's play button name and tooltip.</summary>
    public static string PlayVideoText => ChatCopy.PlayVideo;

    /// <summary>Gets the playing video's expand button name and tooltip.</summary>
    public static string OpenVideoText => ChatCopy.OpenVideo;

    /// <summary>Gets the hover strip slot's width (see <see cref="LanePadding"/>), kept the same in the XAML.</summary>
    public static double StripWidth => HoverStripWidth;

    /// <summary>Gets the caption row lock's tooltip and name.</summary>
    public static string EncryptedText => ChatCopy.EndToEndEncrypted;

    /// <summary>Gets the colored back, which the reaction picker opens over.</summary>
    internal FrameworkElement Anchor => Back;

    /// <summary>Gets or sets the message the bubble shows.</summary>
    public MessageBubbleViewModel? Bubble
    {
        get => (MessageBubbleViewModel?)GetValue(BubbleProperty);
        set => SetValue(BubbleProperty, value);
    }

    /// <summary>
    /// Opens the chat's bubble menu over the back.
    /// </summary>
    public void ShowMenu() => ShowMenu(Back);

    /// <summary>
    /// Opens the chat's bubble menu at an element, such as the hover strip's ⋮ (see <see cref="MenuRequested"/>).
    /// </summary>
    /// <remarks>The chat keeps the hover strip shown until the menu closes.</remarks>
    /// <param name="anchor">The element the menu opens at.</param>
    public void ShowMenu(FrameworkElement anchor) => MenuRequested?.Invoke(this, anchor);

    /// <summary>
    /// Shows or hides the ring around the bubble's back, for its row's keyboard focus.
    /// </summary>
    /// <remarks>
    /// The chat pane decides (see <see cref="MessageBubbleViewModel.ShowsFocusRing"/>). A test run also puts the result in
    /// the text's item status: <c>FocusRing</c> or <c>NoRing</c>, then <c>+SystemFocus</c> while the row's system focus
    /// visual draws too.
    /// </remarks>
    /// <param name="shown">True to show the ring.</param>
    /// <param name="systemVisual">True when the row's system focus visual draws (high contrast).</param>
    internal void ShowFocusRing(bool shown, bool systemVisual)
    {
        // Keyboard Focus Loads The Hover Strip, As The Pointer Does; In High Contrast The System Visual Stands In For The Ring
        if (shown || systemVisual)
        {
            LoadHoverStrip();
        }

        VisualStateManager.GoToState(this, shown ? "RingShown" : "RingHidden", false);
        TestHooks.ShowState(Body, (shown ? "FocusRing" : "NoRing") + (systemVisual ? "+SystemFocus" : string.Empty));
    }

    /// <summary>
    /// Keeps the hover strip shown while the reaction picker or the menu opened from it is open (the pointer leaves the
    /// bubble for it).
    /// </summary>
    /// <remarks>Turned off, the strip hides unless the pointer is back over the bubble.</remarks>
    /// <param name="keep">True while the picker or menu is open.</param>
    internal void KeepShown(bool keep)
    {
        _keepShown = keep;
        if (!keep && !_pointerOver)
        {
            ShowHover(false, Ui.AnimationsEnabled);
        }
    }

    // Loads The Hover Strip Once (It Then Stays): Most Bubbles Are Never Hovered
    private void LoadHoverStrip()
    {
        if (HoverStrip is null)
        {
            FindName(nameof(HoverStrip));
        }
    }

    // Shows Or Hides The Hover Strip. Shown Loads It First: Shown's Storyboard And Setters Target It
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
    /// Gives a part's AutomationId: the prefix, then the bubble's <see cref="MessageBubbleViewModel.PartKey"/> (the message
    /// ID or, until the phone has reported the message, the temporary ID).
    /// </summary>
    /// <param name="prefix">The prefix, such as "Status.".</param>
    /// <param name="key">The bubble's part key.</param>
    /// <returns>The AutomationId.</returns>
    public static string PartIdOf(string prefix, string key) => prefix + key;

    /// <summary>
    /// Gives the text's AutomationId: <c>Bubble.</c> and the bubble's part key (the message ID, or the temporary ID until
    /// the phone has reported the message).
    /// </summary>
    /// <param name="key">The bubble's part key.</param>
    /// <returns>The AutomationId.</returns>
    public static string AutomationIdOf(string key) => PartIdOf("Bubble.", key);

    /// <summary>
    /// Gives the "Not sent" link's AutomationId.
    /// </summary>
    /// <param name="key">The bubble's part key.</param>
    /// <returns>The AutomationId.</returns>
    public static string RetryIdOf(string key) => AutomationIdOf(key) + ".Retry";

    /// <summary>
    /// Gives the time's AutomationId.
    /// </summary>
    /// <param name="key">The bubble's part key.</param>
    /// <returns>The AutomationId.</returns>
    public static string TimeIdOf(string key) => AutomationIdOf(key) + ".Time";

    /// <summary>
    /// Shows the time once the bubble was clicked, unless the retry link takes its place or the caption row already shows it.
    /// </summary>
    /// <param name="isTimeShown">True once clicked.</param>
    /// <param name="isFailed">True when the send failed.</param>
    /// <param name="showsStatusRow">True when the caption row (with the time) shows.</param>
    /// <returns>The time's visibility.</returns>
    public static Visibility TimeVisibility(bool isTimeShown, bool isFailed, bool showsStatusRow) =>
        isTimeShown && !isFailed && !showsStatusRow ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Shows a part of the delivery icon for some of the icons.
    /// </summary>
    /// <param name="icon">The bubble's icon.</param>
    /// <param name="icons">The icons the part belongs to, as their numbers ("123" for sending, sent and delivered).</param>
    /// <returns>Visible when the bubble's icon is one of them.</returns>
    public static Visibility ShownFor(MessageStatusIcon icon, string icons) =>
        icon != MessageStatusIcon.None && icons.Contains((char)('0' + (int)icon), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Rounds a bubble the way Google Messages does: large corners, with small ones on the sender's side where it meets the
    /// bubble above or below in its group (sent on the right, received on the left).
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
    /// Rounds the focus ring a distance outside the back: each of the back's corners (see <see cref="CornersOf"/>) grown
    /// by that distance, so the ring follows them evenly.
    /// </summary>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <param name="isFirst">True for the first bubble of its group.</param>
    /// <param name="isLast">True for the last bubble of its group.</param>
    /// <param name="outset">How far outside the back the ring's edge sits, in effective pixels.</param>
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
    /// <returns>The row's margin.</returns>
    public static Thickness GroupMargin(bool isFirst, bool isLast) =>
        new(0, isFirst ? GroupGap : InGroupGap, 0, isLast ? GroupGap : InGroupGap);

    /// <summary>
    /// Puts the reaction pill in the column under the row toward the chat's middle: the right one (2) under a received
    /// bubble, the left one (0) under a sent one, so a sent bubble's time keeps the row's right end.
    /// </summary>
    /// <remarks>Bound with <c>x:Bind</c>, not a Side state setter, so the setter never forces a deferred pill to load.</remarks>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <returns>The pill's column.</returns>
    public static int PillColumn(bool fromMe) => fromMe ? 0 : 2;

    /// <summary>
    /// Gives the reaction pill's margin: its top 8 px over the back, and a 6 px gap on the time's side (left of a received
    /// bubble's pill, right of a sent one's).
    /// </summary>
    /// <remarks>Bound with <c>x:Bind</c>, not a Side state setter, so the setter never forces a deferred pill to load.</remarks>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <returns>The pill's margin.</returns>
    public static Thickness PillMargin(bool fromMe) =>
        fromMe ? new(0, -PillOverlap, PillGap, 0) : new(PillGap, -PillOverlap, 0, 0);

    /// <summary>
    /// Puts the hover strip's slot beside the back on the bubble's inner side: the right column (2) beside a received
    /// back, the left one (0) beside a sent one.
    /// </summary>
    /// <remarks>Bound with <c>x:Bind</c>, not a Side state setter, so the setter never forces the deferred strip to load.</remarks>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <returns>The slot's column.</returns>
    public static int StripColumn(bool fromMe) => fromMe ? 0 : 2;

    /// <summary>
    /// Gives the avatar lane's width: received bubbles in a group chat keep it, so their avatar lines up.
    /// </summary>
    /// <param name="hasLane">True for a received bubble in a group chat.</param>
    /// <returns>The lane's width.</returns>
    public static double LaneWidth(bool hasLane) => hasLane ? AvatarLane : 0;

    /// <summary>
    /// Gives the bubble's padding: the avatar lane's width on the left when it has one, and the hover strip's width on the
    /// strip's side (right of a received bubble, left of a sent one), so the name, pill and time line up with the
    /// bubble's back.
    /// </summary>
    /// <param name="hasLane">True for a received bubble in a group chat.</param>
    /// <param name="fromMe">True for a sent bubble.</param>
    /// <returns>The padding.</returns>
    public static Thickness LanePadding(bool hasLane, bool fromMe) =>
        fromMe ? new(HoverStripWidth, 0, 0, 0) : new(LaneWidth(hasLane), 0, HoverStripWidth, 0);

    /// <summary>
    /// Gives the back's row negative margins of the padding (see <see cref="LanePadding"/>), so the lane (with the avatar)
    /// and the hover strip sit in the padding, beside the back.
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
    /// Spaces the preview card from the text above it; with no text (a message that was only the link) the card starts
    /// at the top of the bubble.
    /// </summary>
    /// <param name="hasText">True when the bubble shows text.</param>
    /// <returns>The card's margin.</returns>
    public static Thickness PreviewMargin(bool hasText) => new(0, hasText ? PreviewGap : 0, 0, 0);

    /// <summary>
    /// Gives the back's padding: none around a photo, which fills the bubble edge to edge; the text's padding otherwise.
    /// </summary>
    /// <param name="hasPhoto">True when the bubble shows a photo.</param>
    /// <returns>The padding.</returns>
    public static Thickness BackPadding(bool hasPhoto) => hasPhoto ? default : TextPadding;

    /// <summary>
    /// Gives the text's margin: under a photo the caption keeps the bubble's usual inset; otherwise the back's padding does.
    /// </summary>
    /// <param name="hasPhoto">True when the bubble shows a photo.</param>
    /// <returns>The margin.</returns>
    public static Thickness CaptionMargin(bool hasPhoto) => hasPhoto ? CaptionInset : default;

    /// <summary>
    /// Shows the bubble's fill and edge, except behind a photo with nothing else in its bubble (no caption, no quote):
    /// then the photo's own edge is the bubble's, with no fill or stroke around it.
    /// </summary>
    /// <param name="hasPhoto">True when the bubble shows a photo.</param>
    /// <param name="hasCaption">True when a caption shows under the photo.</param>
    /// <param name="hasQuote">True when the bubble quotes the message it replies to.</param>
    /// <returns>Collapsed for a photo alone.</returns>
    public static Visibility FillShown(bool hasPhoto, bool hasCaption, bool hasQuote) =>
        hasPhoto && !hasCaption && !hasQuote ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Rounds a photo to its bubble's corners exactly (the photo covers the bubble's edge), square at the bottom when its
    /// caption follows.
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
    /// Gives the quote's sender line its weight: semibold beside quoted media's thumbnail, the caption's own otherwise.
    /// </summary>
    /// <param name="hasMedia">True when the quote shows the quoted media's thumbnail or icon.</param>
    /// <returns>The weight.</returns>
    public static Windows.UI.Text.FontWeight QuoteSenderWeight(bool hasMedia) => hasMedia ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>
    /// Sets a media label ("Image", "Video", "GIF") in italic; a file name or text stays upright.
    /// </summary>
    /// <param name="isLabel">True for a label.</param>
    /// <returns>The style.</returns>
    public static Windows.UI.Text.FontStyle LabelStyle(bool isLabel) => isLabel ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

    /// <summary>
    /// Gives the file card's icon: a video's, or a document's for any other file.
    /// </summary>
    /// <param name="kind">The message's kind.</param>
    /// <returns>The Segoe Fluent Icons glyph.</returns>
    public static string FileGlyph(MessageKind kind) => kind == MessageKind.Video ? "\uE714" : "\uE8A5";

    /// <summary>
    /// Writes a file size the way Explorer does ("1.20 MB", "940 bytes"), through Windows' own formatter.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>The size, or empty when it is unknown (0).</returns>
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
    /// Fades a bubble that is still being sent.
    /// </summary>
    /// <param name="isSending">True while sending.</param>
    /// <returns>The opacity.</returns>
    public static double SendingOpacity(bool isSending) => isSending ? SendingFade : 1;

    /// <summary>
    /// Caps the bubble at 70% of the chat's width (560 px at most) before it is measured, so it is the right width from its
    /// first layout and short messages stay narrow. The hover strip's kept space comes on top, so it takes nothing from
    /// the bubble.
    /// </summary>
    /// <param name="availableSize">The row's width and height.</param>
    /// <returns>The size the bubble wants.</returns>
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

            // The Photo Fills The Bubble Edge To Edge, Beside The Avatar Lane
            _photoMaxWidth = cap - LaneWidth(Bubble?.HasAvatarLane == true);
            SizePhoto();
        }

        return base.MeasureOverride(availableSize);
    }

    // =========================================================================
    // DEFERRED PARTS
    // =========================================================================

    // A Part Loaded (Or Back In The Tree): Its States Again, Since One Made Afresh Has None Of Its Group's Setters
    private void OnPartLoaded(object sender, RoutedEventArgs e) => ShowPartStates(refresh: true);

    // The Parts' Own States: The On-Accent Brushes On A Sent SMS Bubble's Parts, And My Reaction's Fill On The Pill. Only
    // On A Loaded Part: A State's Setter Would Load Its Part. With Refresh, A State Is Gone To Again Through The Plain One,
    // For A Part Made Afresh While Its Group Already Sat In The State (Going To The Same State Again Does Nothing)
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

    // True When The Element Sits In The Quote, Photo, Video, File Card, Voice Or Preview Card, Whose Taps Are Their Own
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

    // The Photo's Full Image: Clipped Once Laid Out, Reshaped With Its Size
    private void OnPhotoFullLoaded(object sender, RoutedEventArgs e) => ClipPhotoFull(mayTake: true);

    private void OnPhotoFullSizeChanged(object sender, SizeChangedEventArgs e) => ClipPhotoFull();

    // The Playing Video Keeps The Bubble's Corners
    private void OnVideoHostSizeChanged(object sender, SizeChangedEventArgs e) => ClipVideoHost();

    // A Pill That Just Appeared Pops In Once It Is In The Tree With Its Size (Its Middle Is Where It Grows From)
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

    // A Recycled Container Gets Another Message: Follow Its Changes Instead, And Put It On Its Side, With No Hover Strip
    // Left Over From The Last One
    private static void OnBubbleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MessageBubble)d;

        // No Entrance Left Running From The Container's Last Bubble; Only When One Ran, So A Row That Never Animated Makes
        // No Visual. Guarded: This Runs In x:Bind's ProcessBindings, Where Windows Can Refuse The Visual (E_ACCESSDENIED, As
        // It Did The Photo's Clip), And A Throw Escaping Into XAML Crashes The App
        if (view._animatedIn)
        {
            view._animatedIn = false;
            try
            {
                Visual row = ElementCompositionPreview.GetElementVisual(view.Row);
                row.StopAnimation(nameof(Visual.Opacity));
                row.StopAnimation("Translation");
                row.Opacity = 1;
                row.Properties.InsertVector3("Translation", Vector3.Zero);
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

        // The Voice Message's Audio Waits For Phase 1 (The Chat's LoadMedia), So A Row Passed Over While Scrolling Fetches None
        view.ShowText();
        view.ShowStates();
        view.LoadMedia(fetchVoice: false);

        // Another Message's Reactions Show As They Are, With No Pop (A Recycled Row's Half-Run Pop Is Put Back). With None,
        // The Pill Unloads, Its Visual With It
        view._reactionsShown = view.Bubble?.ReactionsText ?? string.Empty;
        view._popPill        = false;
        view.RestPill();
        if (view._reactionsShown.Length == 0)
        {
            view._pillVisual = null;
        }

        // A Bubble Added Live Animates In, Once (The Flag Clears, So A Recycled Row Never Replays It)
        if (view.Bubble?.TakeNew() == true && Ui.AnimationsEnabled)
        {
            view.AnimateIn();
        }
    }

    // Fades In And Rises Into Place On The Row's Composition Visual (Opacity And Translation), Easing Like WinUI's Own
    // Controls (ControlFastOutSlowInKeySpline: 0,0,0,1). Layout Never Moves, So The Scroll Pin Is Unaffected
    private void AnimateIn()
    {
        Visual visual                  = ElementCompositionPreview.GetElementVisual(Row);
        Compositor compositor          = visual.Compositor;
        CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
        ElementCompositionPreview.SetIsTranslationEnabled(Row, true);

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = EntranceTime;

        Vector3KeyFrameAnimation rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, EntranceRise, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, ease);
        rise.Duration = EntranceTime;

        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation("Translation", rise);
        _animatedIn = true;

        AppLog.BubbleAnimatedIn(App.ShellLog, Bubble?.PartKey ?? string.Empty);
    }

    // The Bubble Raises Each Property It Changed By Name (Bindings Follow On Their Own); Only The Work The Name Touches
    // Runs Here. An Echo Can Make A Sending Bubble RCS, My Reaction Comes And Goes, And A Preview Card Arrives
    private void OnBubblePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // The Text, Or The ID Its Links Are Named By
            case nameof(MessageBubbleViewModel.BodyText) or nameof(MessageBubbleViewModel.PartKey):
                ShowText();
                break;

            // The Side, My Reaction's Fill, And Where The Pill Sits; The Photo's Corners Follow The Side
            case nameof(MessageBubbleViewModel.FromMe) or nameof(MessageBubbleViewModel.IsRcs)
                or nameof(MessageBubbleViewModel.HasMyReaction) or nameof(MessageBubbleViewModel.HasReactions):
                ShowStates();
                ClipPhotoFull();
                break;

            // The Media Or The Preview Card Changed: Loaded, Unless The Row Let Go Of Its Media While Waiting To Be Used Again
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

            // The Photo's Corners Follow The Group (A Bubble Added Below) And The Caption, And A GIF's Clip With Them
            case nameof(MessageBubbleViewModel.IsFirstInGroup) or nameof(MessageBubbleViewModel.IsLastInGroup) or nameof(MessageBubbleViewModel.HasBodyText):
                ClipPhotoFull();
                break;
        }
    }

    // =========================================================================
    // REACTION PILL
    // =========================================================================

    // The Same Bubble's Reactions Changed: The Pill Pops In, At Once When It Already Shows, Else Once It Is In The Tree
    // With Its Size (Hidden Until Then, So It Never Shows A Frame At Full Size First). Only With Windows' Animation
    // Effects On. The Last Reaction Gone, The Pill Unloads With Its Visual
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

    // Pops A Waiting Pill Once Windows Hands Out Its Visual (In The Live Tree) And It Has Its Size; Until Then It Is Hidden
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

    // Pops The Pill In From Its Middle: From Half Size And Clear To A Touch Over Full Size, Then Settling, In 180 ms On
    // WinUI's Fast-Out-Slow-In Curve. Layout Never Moves
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

    // Puts The Pill Back At Rest (Full Size, Shown), Stopping A Pop
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

    // The Pill On The Back's Bottom Corner Toward The Chat's Middle, Its Top 8 px Over The Back: Bottom-Right On A Received
    // Bubble, Ending 12 px In From The Back's Right; Bottom-Left On A Sent One, Starting 12 px In From The Back's Left (Owner,
    // The Received Pill Mirrored). On A Short Bubble (Under Twice The Pill's Width) That Would Put It Past The Far Corner, So
    // It Is Centered 4 px In From The Back's Edge Instead, Over The Corner (Owner, Plan 10). The Pill Sits At Its Column's
    // End (The Row's Right On Received, Its Left On Sent), So It Is Moved There From Wherever The Row's Width Put It. The
    // Time (On The Received Row's Left, The Sent Row's Right) Drops Below The Pill When The Two Would Meet; Only Its Top
    // Margin Changes, Never Its Width, So The Layout Can't Loop. No Pill (Not Loaded, Or Its Last Reaction Just Went):
    // The Time Sits Back In Its Place
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

        // Received: The Row Starts 12 px In From The Back's Left; Its MinWidth Point Is 12 px In From The Back's Right
        // Sent: The Row Ends 12 px In From The Back's Right; 12 px In From The Back's Left Is Under.ActualWidth - Under.MinWidth
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

    // The Pill Host's Visual, Taken Once While The Bubble Is In The Live Tree; Null When Windows Refuses It (No Pop Beats
    // A Crash)
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

    // A Bubble Under The Pointer That Starts Or Stops Taking The Strip (An Echo Turns Sending Into Sent) Shows Or Hides
    // It Without The Pointer Moving
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

    // Decodes The Card's Image From Its Bytes; Bytes Windows Can't Read Leave The Card Without One. No Card: It Unloads,
    // Its Image With It
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

        // A Recycled Container May Have Moved On While Decoding
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
    /// Shows the message's media: the preview card's image, the photo, the file card's thumbnail, the voice message and
    /// the video's frame, loading only what changed.
    /// </summary>
    /// <remarks>
    /// The bubble calls it when its message or the message's media changes; the chat calls it again once a list row has
    /// its message (phase 1), so a row given back the message it showed before its media was let go
    /// (<see cref="ReleaseMedia"/>) loads it again. A voice message's audio is fetched (for its waveform and length) only
    /// when <paramref name="fetchVoice"/> is true: the bubble's own call on a new message passes false, so the audio
    /// waits for the chat's phase 1 call.
    /// </remarks>
    /// <param name="fetchVoice">True to fetch a voice message's audio for its waveform.</param>
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
    /// Lets go of the media a list row holds while it waits to be used again: the inline video's player, the voice
    /// message's waveform, and every decoded image (preview card, photo, file thumbnail, video frame).
    /// </summary>
    /// <remarks>
    /// Loads still running stop, and the media shown is forgotten, so <see cref="LoadMedia"/> loads it again. The bubble
    /// keeps its message: clearing it would unload and reload every deferred (<c>x:Load</c>) part on each recycle. Parts
    /// the message doesn't use aren't loaded, so each is checked first.
    /// </remarks>
    internal void ReleaseMedia()
    {
        _released = true;

        // Video: The Inline Player And Its Frame
        ReleaseVideo();
        _videoPart       = null;
        _videoBubble     = null;
        _videoFrameImage = null;
        if (VideoThumb is not null)
        {
            VideoThumb.Background = null;
        }

        // Voice: Its Waveform; A Late Load Or Player Update Stops
        _voicePart   = null;
        _voiceBubble = null;
        _voiceWave   = null;
        _voiceLoad++;

        // Photo: Its Blurred And Full Images, And Any GIF; A Load Still Running Stops
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

        // File Card: Its Thumbnail
        _filePart   = null;
        _fileBubble = null;
        if (FileThumb is not null)
        {
            FileThumb.Background = null;
        }

        // Preview Card: Its Image
        _previewShown = null;
        if (PreviewImage is not null)
        {
            PreviewImage.Source     = null;
            PreviewImage.Visibility = Visibility.Collapsed;
        }
    }

    // =========================================================================
    // PHOTO
    // =========================================================================

    // Another Photo, Or The Same One With Its Full Size Now (The Phone's Edit Swaps The Media Part): Load It. The Same
    // Message Keeps Its Blurred Look While The Full Image Loads; Another One Starts Clear. The Echo Of A Photo Sent From
    // Here Keeps What Shows: Its Bytes Are The Photo, So They Go In The Cache Under Its New Media ID Instead Of A Download
    private void ShowPhoto()
    {
        MediaPart? part = Bubble is { HasPhoto: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _photoPart) && ReferenceEquals(Bubble, _photoBubble))
        {
            return;
        }

        // No Photo: The Part Unloads With Its Images (x:Load), And A GIF's Clip Visual With It; A Load Still Running Stops
        if (part is null)
        {
            _photoPart       = null;
            _photoBubble     = Bubble;
            _photoFailed     = false;
            _photoAnimated   = false;
            _photoFullVisual = null;
            NextPhotoLoad();
            WatchGif(null);
            return;
        }

        FindName(nameof(Photo));
        bool sameMessage = Bubble is not null && ReferenceEquals(Bubble, _photoBubble);
        if (sameMessage && KeepSentMedia(_photoPart, part))
        {
            // An Echo With Only A Thumbnail (An MMS Photo's) Keeps The Sent Bytes On Show: Loading It Showed The Tiny
            // Thumbnail Blurred, Waiting For A Full Size That May Never Come
            _photoPart = part.MediaId.Length > 0 ? part : _photoPart;
            SizePhoto();
            return;
        }

        _photoPart   = part;
        _photoBubble = Bubble;
        _photoFailed = false;
        int load     = NextPhotoLoad();

        // Another Message, Or None (A Bubble Without A Photo): Let Go Of The Old Images
        if (!sameMessage)
        {
            PhotoBlur.Background        = null;
            PhotoFull.Background        = null;
            PhotoFullImage.Source       = null;
            PhotoFull.OpacityTransition = null;
            PhotoFull.Opacity           = 0;
            _photoAnimated              = false;
            WatchGif(null);
            ClipPhotoFull();
        }

        ShowPhotoNote(null, string.Empty);
        SizePhoto();
        _ = LoadPhotoAsync(part, load, again: false);
    }

    // The Same Media Though The Bubble's Update Built A New Part (A Status Or Reaction Change): Nothing To Load Again.
    // Inline Bytes Count By Reference, So Two Pending Parts With One Name Still Differ
    private static bool SameMedia(MediaPart? a, MediaPart? b) =>
        a is null || b is null ? a is null && b is null
            : a.MediaId == b.MediaId && a.ThumbnailId == b.ThumbnailId && a.Name == b.Name
              && (ReferenceEquals(a.InlineData, b.InlineData) || (a.InlineData.Length == 0 && b.InlineData.Length == 0));

    // The Echo Of An Attachment Sent From Here: The Shown Part Held Its Bytes, And The New One Has The Media ID They
    // Uploaded As (The Bytes Go In The Cache Under That ID, So Nothing Downloads Again), Or Only A Thumbnail (The Bytes
    // Keep Showing); True When So
    private static bool KeepSentMedia(MediaPart? shown, MediaPart? echo)
    {
        if (shown is not { InlineData.Length: > 0 } local || echo is not { InlineData.Length: 0 } || echo is { MediaId.Length: 0, ThumbnailId.Length: 0 })
        {
            return false;
        }

        (Application.Current as App)?.Media?.Seed(echo.MediaId, local.InlineData);
        return true;
    }

    // Starts A Photo Load: Its Number, With An Older Load's Wait For The Full Size Cancelled
    private int NextPhotoLoad()
    {
        _cancelPhotoWait?.Invoke();
        _cancelPhotoWait = null;
        return ++_photoLoad;
    }

    // The Photo's Or Video's Size From Its Dimensions, Fitted To The Bubble (See MessageBubbleViewModel.PhotoSize)
    private void SizePhoto()
    {
        if (Bubble is not { FillsBubble: true, Media: { } media } bubble)
        {
            return;
        }

        (double width, double height) = MessageBubbleViewModel.PhotoSize(media.Width, media.Height, _photoMaxWidth);
        FrameworkElement? shown       = bubble.HasVideo ? Video : Photo;
        if (shown is null)
        {
            return;
        }

        if (shown.Width != width || shown.Height != height)
        {
            shown.Width  = width;
            shown.Height = height;
        }
    }

    // Blurred First, Then Sharp. A Thumbnail-Only Photo Asks The Phone For Its Full Size Instead, Which Comes Back As A New
    // Media Part (ShowPhoto Then Loads Again); If It Never Comes, The Photo Offers To Try Again. Any Failure Keeps What
    // Shows And Says So; A Click Tries Again
    private async Task LoadPhotoAsync(MediaPart part, int load, bool again)
    {
        if (part.IsPending)
        {
            ShowPhotoNote(ChatCopy.PhotoPending, "Pending");
            return;
        }

        if (Bubble is not { } bubble || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        try
        {
            // Blurred: The Thumbnail (Or The Photo Itself When No Thumbnail Came), Decoded Tiny And Stretched
            if (PhotoBlur.Background is null)
            {
                byte[] small     = await media.GetAsync(part, thumbnail: true, CancellationToken.None);
                ImageSource blur = await Avatar.ToImageAsync(small, BlurDecodeWidth) ?? throw new InvalidDataException();
                if (load != _photoLoad)
                {
                    return;
                }

                PhotoBlur.Background = new ImageBrush { ImageSource = blur, Stretch = Stretch.UniformToFill };
                TestHooks.ShowState(Photo, "Blurred");
            }

            // Only The Thumbnail Came: Ask For The Full Size, And Offer To Try Again If It Doesn't Come
            if (part is { MediaId.Length: 0, ThumbnailId.Length: > 0 })
            {
                await bubble.RequestFullSizeAsync(media, again);
                if (load != _photoLoad)
                {
                    return;
                }

                using (var wait = new CancellationTokenSource())
                {
                    _cancelPhotoWait = wait.Cancel;
                    try
                    {
                        await Task.Delay(TestHooks.ShortWait("full-size-wait") ?? FullSizeWait, wait.Token);
                    }
                    finally
                    {
                        _cancelPhotoWait = load == _photoLoad ? null : _cancelPhotoWait;
                    }
                }

                if (load == _photoLoad)
                {
                    ShowPhotoFailed();
                }

                return;
            }

            // Sharp: Decoded At The Photo's Size. One Already In Memory (A Recycled Row Showing It Again) Shows At Once
            Task<byte[]> fullTask  = media.GetAsync(part, thumbnail: false, CancellationToken.None);
            bool atOnce            = fullTask.IsCompleted;
            byte[] full            = await fullTask;
            long decodeStart       = System.Diagnostics.Stopwatch.GetTimestamp();
            if (load != _photoLoad)
            {
                return;
            }

            // Its Size Read From The Same Stream It Decodes From, Then Decoded To Fill The Box
            double boxWidth  = Photo.Width;
            double boxHeight = Photo.Height;
            (ImageSource? fitted, int pixelWidth, int pixelHeight, int decodeWidth) =
                await Avatar.ToFittedImageAsync(full, (w, h) => MessageBubbleViewModel.FillDecodeWidth(w, h, boxWidth, boxHeight));
            ImageSource image = fitted ?? throw new InvalidDataException();
            long decodeMs     = (long)System.Diagnostics.Stopwatch.GetElapsedTime(decodeStart).TotalMilliseconds;
            AppLog.PhotoDecoded(App.ShellLog, bubble.Id, full.Length, part.Size, pixelWidth, pixelHeight, decodeWidth, decodeMs);
            if (load != _photoLoad)
            {
                return;
            }

            // A Moving GIF Plays In An Image (A Brush Shows Only Its First Frame; Test Runs Note It); A Still Photo Is The
            // Layer's Brush, Which The Border's Corners Clip With No Composition Clip
            BitmapImage? gif            = image.As<BitmapImage>() is { IsAnimatedBitmap: true } moving ? moving : null;
            bool animated               = gif is not null;
            _photoAnimated              = animated;
            WatchGif(gif);
            PhotoFullImage.Source       = animated ? image : null;
            PhotoFull.Background        = animated ? null : new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
            PhotoFull.OpacityTransition = atOnce || !Ui.AnimationsEnabled ? null : _photoFade;
            PhotoFull.Opacity           = 1;
            ClipPhotoFull(mayTake: true);
            TestHooks.ShowState(Photo, "Full");
            if (animated)
            {
                TestHooks.NoteState("animated-photo", bubble.Id);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (load != _photoLoad)
            {
                return;
            }

            AppLog.MediaFailed(App.ShellLog, "photo", bubble.Id, ex.GetType().Name);
            ShowPhotoFailed();
        }
    }

    // A GIF's Image Clipped To The Photo's Rounded Corners (An Image Isn't Clipped By Its Border's Corners); A Still
    // Photo's Brush Needs No Clip, So Any Left From A GIF This Container Showed Goes.
    // The Clip Goes On The Image, Never On PhotoFull: PhotoFull Fades In Through OpacityTransition, A XAML Implicit
    // Transition, And Windows Refuses The Composition Visual Of An Element That Uses One (E_ACCESSDENIED, Reproduced By
    // ManyPhotos_FastScroll_NoCrash: Every Failure Was A Faded-In GIF's PhotoFull, Loaded And Laid Out). In 0.6.2 That
    // Throw Escaped SizeChanged And x:Bind Callbacks Into XAML And Crashed The App (0xc000027b). The Visual Is Taken Once,
    // For A GIF, On Loaded Or When It Arrives; Size And Corner Changes Only Reshape The Clip
    private void ClipPhotoFull(bool mayTake = false)
    {
        if (PhotoFullImage is null || Bubble is not { } bubble || (_photoFullVisual is null && (!mayTake || !_photoAnimated || !PhotoFullImage.IsLoaded)))
        {
            return;
        }

        try
        {
            _photoFullVisual ??= ElementCompositionPreview.GetElementVisual(PhotoFullImage);
            Visual visual      = _photoFullVisual;

            // The Photo's Corners From The Bubble Itself, Not The Bound Ones, Which May Not Have Caught Up With A Change Yet.
            // Not Laid Out Yet: Its SizeChanged Clips It Later
            CornerRadius r = PhotoCorners(bubble.FromMe, bubble.IsFirstInGroup, bubble.IsLastInGroup, bubble.HasBodyText);
            if (!_photoAnimated || !ClipRounded(visual, PhotoFullImage, r))
            {
                visual.Clip = null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Square Corners Beat A Crash; The Next Size Change Tries Again
            AppLog.MediaFailed(App.ShellLog, "clip", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }
    }

    // Rounds A Visual To An Element's Size With The Given Corners; False When The Element Isn't Laid Out Yet (Nothing
    // Changed). A Corner Can't Be Rounder Than Half The Shorter Side, Or The Clip Is Refused (Which Crashed The App On
    // Opening A Chat)
    private static bool ClipRounded(Visual visual, FrameworkElement element, CornerRadius corners)
    {
        float width  = (float)element.ActualWidth;
        float height = (float)element.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        float most  = Math.Min(width, height) / 2;
        visual.Clip = visual.Compositor.CreateRectangleClip(
            0, 0, width, height,
            Corner(corners.TopLeft, most), Corner(corners.TopRight, most), Corner(corners.BottomRight, most), Corner(corners.BottomLeft, most));
        return true;

        static Vector2 Corner(double radius, float most) => new(Math.Min((float)radius, most));
    }

    // Follows Where The Moving GIF Shown Is (Null For None), So It Pauses Off Screen (A Realized Row Off Screen Isn't
    // Recycled At Once, And Its GIF Kept Playing)
    private void WatchGif(BitmapImage? gif)
    {
        if (ReferenceEquals(gif, _gif))
        {
            return;
        }

        if (_gif is null)
        {
            EffectiveViewportChanged += OnGifViewportChanged;
        }
        else if (gif is null)
        {
            EffectiveViewportChanged -= OnGifViewportChanged;
        }

        _gif       = gif;
        _gifPaused = false;
    }

    // Off Screen: The GIF Pauses; Back On Screen, One Paused Here Plays Again (One Windows Never Played, With Animation
    // Effects Off, Stays Still)
    private void OnGifViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (_gif is not { } gif)
        {
            return;
        }

        bool outOfSight = OutOfSight(args.EffectiveViewport);
        if (outOfSight && gif.IsPlaying)
        {
            gif.Stop();
            _gifPaused = true;
        }
        else if (!outOfSight && _gifPaused)
        {
            gif.Play();
            _gifPaused = false;
        }
    }

    // Whether None Of The Bubble Is In The Viewport Seen
    private bool OutOfSight(Windows.Foundation.Rect seen) =>
        seen.Width <= 0 || seen.Height <= 0 || seen.Bottom <= 0 || seen.Top >= ActualHeight || seen.Right <= 0 || seen.Left >= ActualWidth;

    private void ShowPhotoFailed()
    {
        _photoFailed = true;
        ShowPhotoNote(ChatCopy.PhotoLoadFailed, "Failed");
    }

    // The Note Over The Photo (Waiting, Or Failed), Or None
    private void ShowPhotoNote(string? text, string state)
    {
        PhotoNote.Text           = text ?? string.Empty;
        PhotoNoteBack.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        TestHooks.ShowState(Photo, state);
    }

    // After A Failure, Try Again (Asking The Phone Again For A Full Size That Never Came); While Waiting For The Phone,
    // Nothing; Otherwise Show It In The Window's Photo Viewer
    private void OnPhotoClick(object sender, RoutedEventArgs e)
    {
        if (_photoPart is not { IsPending: false } part || sender is not Control photo)
        {
            return;
        }

        if (_photoFailed)
        {
            _photoFailed = false;
            ShowPhotoNote(null, string.Empty);
            _ = LoadPhotoAsync(part, NextPhotoLoad(), again: true);
            return;
        }

        ShowInViewer(part, photo);
    }

    // The Photo Over The Whole Window, Named As The Bubble's Photo Is; Focus Goes Back To The Part It Opened From
    private void ShowInViewer(MediaPart part, Control from) =>
        (Application.Current as App)?.MainWindow?.ShowPhoto(part, Bubble?.Id ?? string.Empty, Bubble?.PhotoName ?? ChatCopy.Photo, from);

    // =========================================================================
    // FILE CARD
    // =========================================================================

    // Another File: Its Size, And Its Thumbnail When One Came
    private async void ShowFile()
    {
        MediaPart? part = Bubble is { HasFileCard: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _filePart) && ReferenceEquals(Bubble, _fileBubble))
        {
            return;
        }

        // The Echo Of A File Sent From Here: Keep The Card As It Is
        if (part is not null && Bubble is not null && ReferenceEquals(Bubble, _fileBubble) && KeepSentMedia(_filePart, part))
        {
            _filePart = part;
            return;
        }

        _filePart    = part;
        _fileBubble  = Bubble;
        _fileOpening = false;
        _fileFailed  = false;

        // No File: The Card Unloads (x:Load)
        if (part is null)
        {
            return;
        }

        FindName(nameof(FileCard));
        FileThumb.Background = null;
        ShowFileDetail();
        if (part is not { ThumbnailId.Length: > 0 } || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        // A Thumbnail That Won't Load Leaves The Icon
        try
        {
            byte[] bytes      = await media.GetAsync(part, thumbnail: true, CancellationToken.None);
            ImageSource image = await Avatar.ToImageAsync(bytes, FileThumbWidth) ?? throw new InvalidDataException();
            if (ReferenceEquals(part, _filePart))
            {
                FileThumb.Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "thumbnail", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }
    }

    // The Line Under The Name: Downloading, The Failure, Or The Size
    private void ShowFileDetail()
    {
        (FileDetail.Text, string state) = (_fileOpening, _fileFailed) switch
        {
            (true, _) => (ChatCopy.Downloading, "Downloading"),
            (_, true) => (ChatCopy.FileOpenFailed, "Failed"),
            _         => (SizeText(_filePart?.Size ?? 0), "Ready"),
        };
        TestHooks.ShowState(FileCard, state);
    }

    // Downloads, Then Opens In The Default App; Once At A Time
    private async void OnFileClick(object sender, RoutedEventArgs e)
    {
        if (_filePart is not { IsPending: false } part || _fileOpening)
        {
            return;
        }

        _fileOpening = true;
        _fileFailed  = false;
        ShowFileDetail();

        MessageBubbleViewModel? bubble = _fileBubble;
        bool opened                    = await OpenMediaAsync(part);
        if (!ReferenceEquals(bubble, _fileBubble))
        {
            return;
        }

        _fileOpening = false;
        _fileFailed  = !opened;
        ShowFileDetail();
    }

    // =========================================================================
    // VOICE
    // =========================================================================

    // Another Voice Message: Shown Stopped, Then (When Asked To Fetch) Downloaded And Its Waveform And Length Read. The
    // Echo Of One Sent From Here Keeps Its Waveform And Play State, So A Playing Message Plays On
    private void ShowVoice(bool fetch)
    {
        MediaPart? part = Bubble is { HasVoice: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _voicePart) && ReferenceEquals(Bubble, _voiceBubble))
        {
            FetchVoice(fetch);
            return;
        }

        if (part is not null && Bubble is not null && ReferenceEquals(Bubble, _voiceBubble) && KeepSentMedia(_voicePart, part))
        {
            _voicePart = part;
            FetchVoice(fetch);
            return;
        }

        _voicePart   = part;
        _voiceBubble = Bubble;
        _voiceKey    = Bubble is { } shown ? (shown.TmpId.Length > 0 ? shown.TmpId : shown.Id) : string.Empty;
        _voiceLoad++;
        _voiceWave   = null;

        // No Voice Message: Its Part Unloads (x:Load)
        if (part is null)
        {
            return;
        }

        FindName(nameof(Voice));
        VoiceLength.Text = string.Empty;
        VoiceBars.Show(null);
        VoiceBars.Length = TimeSpan.Zero;
        ShowVoicePlaying(false, 0);
        FetchVoice(fetch);
    }

    // The Shown Voice Message's Audio, Read For Its Waveform And Length Once Per Message Shown; The Audio Is Let Go After
    private async void FetchVoice(bool fetch)
    {
        if (!fetch || _voicePart is not { IsPending: false } part || _voiceWave is not null || _voiceFetched == _voiceLoad
            || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        int load      = _voiceLoad;
        _voiceFetched = load;
        try
        {
            VoiceWave wave = await WaveformReader.ReadAsync(await media.GetAsync(part, thumbnail: false, CancellationToken.None));
            if (load != _voiceLoad)
            {
                return;
            }

            _voiceWave       = wave;
            VoiceLength.Text = VoiceWave.LengthText(wave.Length);
            VoiceBars.Show(wave.Peaks);
            VoiceBars.Length = wave.Length;
            TestHooks.ShowState(VoiceLength, "Ready");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another Message Since: Its Part May Be Gone
            if (load != _voiceLoad)
            {
                return;
            }

            AppLog.MediaFailed(App.ShellLog, "voice", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            TestHooks.ShowState(VoiceLength, "Failed");
        }
    }

    // Play Or Pause: The Chat's Player Shows The State Back (See VoiceRequest)
    private void OnVoicePlayClick(object sender, RoutedEventArgs e) => RequestVoice(null);

    // A Click On The Waveform Seeks There
    private void OnVoiceSeekRequested(object? sender, double share) => RequestVoice(share);

    // The Audio Comes From The Media Store Again (Kept In Its Memory Cache Since The Waveform Was Read)
    private async void RequestVoice(double? seekTo)
    {
        if (_voicePart is not { IsPending: false } part || _voiceWave is not { } wave || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        // Only While This View Still Shows The Message (A Recycled Row Moves On; Its Echo Doesn't)
        int load = _voiceLoad;
        byte[] audio;
        try
        {
            audio = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "voice", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            return;
        }

        if (load != _voiceLoad)
        {
            return;
        }

        VoiceRequested?.Invoke(this, new VoiceRequest(_voiceKey, audio, wave.Length, seekTo, (playing, share) =>
        {
            if (load == _voiceLoad)
            {
                ShowVoicePlaying(playing, share);
            }
        }));
    }

    // The Play Button Becomes Pause While It Plays, And The Waveform Fills Up To How Much Has Played
    private void ShowVoicePlaying(bool playing, double share)
    {
        if (Voice is null)
        {
            return;
        }

        string name      = playing ? ChatCopy.Pause : ChatCopy.Play;
        VoiceGlyph.Glyph = playing ? "" : "";
        AutomationProperties.SetName(VoicePlay, name);
        ToolTipService.SetToolTip(VoicePlay, name);
        VoiceBars.ShowProgress(share, playing);
        TestHooks.ShowState(VoicePlay, playing ? "Playing" : "Paused");
    }

    // =========================================================================
    // VIDEO
    // =========================================================================

    /// <summary>
    /// Stops the video playing in place, wherever it is (a chat switch, the viewer opening).
    /// </summary>
    internal static void StopInlineVideo() => s_playingVideo?.ReleaseVideo();

    // Another Video: Stopped, On The Plain Fill With Its Play Button Until Its Frame Loads (The Phone's Thumbnail, Else
    // One Made Here; see VideoStill). The Echo Of One Sent From Here Keeps What Shows, Its Bytes Seeded Under The New
    // Media ID; Without A Frame Yet, It Loads One
    private void ShowVideo()
    {
        MediaPart? part = Bubble is { HasVideo: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _videoPart) && ReferenceEquals(Bubble, _videoBubble))
        {
            return;
        }

        if (part is not null && Bubble is not null && ReferenceEquals(Bubble, _videoBubble) && KeepSentMedia(_videoPart, part))
        {
            _videoPart = part;
            FindName(nameof(Video));
            if (VideoThumb.Background is null)
            {
                LoadVideoFrame(part);
            }

            return;
        }

        ReleaseVideo();
        _videoPart       = part;
        _videoBubble     = Bubble;
        _videoFrameImage = null;

        // No Video: Its Part Unloads (x:Load), The Player Host's Visual With It
        if (part is null)
        {
            _videoHostVisual = null;
            return;
        }

        FindName(nameof(Video));
        VideoThumb.Background = null;
        ShowVideoNote(null, string.Empty);
        SizePhoto();
        LoadVideoFrame(part);
    }

    // The Video's Frame, In The Background; One That Won't Load Leaves The Plain Fill. The Play Button's Item Status Says
    // Which Shows, For The Tests: Pending, Then Frame Or NoFrame
    private async void LoadVideoFrame(MediaPart part)
    {
        TestHooks.ShowState(VideoPlay, "Pending");
        if (part.IsPending || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        // Decoded As Wide As The Video Shows (Sized Before This Runs)
        int frameWidth = (int)Math.Ceiling(Video is { Width: > 0 } box ? box.Width : MaxBubbleWidth);
        try
        {
            byte[] bytes      = await VideoStill.GetAsync(media, part);
            ImageSource image = await Avatar.ToImageAsync(bytes, frameWidth) ?? throw new InvalidDataException();
            if (ReferenceEquals(part, _videoPart))
            {
                VideoThumb.Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
                _videoFrameImage      = image;
                TestHooks.ShowState(VideoPlay, "Frame");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "thumbnail", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            if (ReferenceEquals(part, _videoPart))
            {
                TestHooks.ShowState(VideoPlay, "NoFrame");
            }
        }
    }

    // Play: Downloads (Or Reads From Memory) The Video And Plays It Here, With Sound, Stopping Any Other One First
    private void OnVideoPlayClick(object sender, RoutedEventArgs e) => PlayInline(TimeSpan.Zero, play: true);

    // The Inline Player From A Position, Playing Or Paused There (The Viewer Hands Back Where It Got To On Close). Async
    // Void Like A Click Handler, So A Failure Reaches The App's Unhandled Error Log
    private async void PlayInline(TimeSpan start, bool play)
    {
        if (_videoPart is not { IsPending: false } part || _video is not null || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        if (!ReferenceEquals(s_playingVideo, this))
        {
            s_playingVideo?.ReleaseVideo();
        }

        s_playingVideo = this;
        int load       = ++_videoLoad;
        ShowVideoNote(ChatCopy.Downloading, "Downloading");
        try
        {
            byte[] bytes = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
            if (load != _videoLoad || !ReferenceEquals(s_playingVideo, this))
            {
                return;
            }

            string id = PartIdOf("Video.Player.", Bubble?.PartKey ?? string.Empty);
            _video         = new VideoPlayback(DispatcherQueue, bytes, part.Mime, start, id, Bubble?.VideoName ?? ChatCopy.Video);
            _video.Failed += OnVideoFailed;
            VideoHost.Children.Add(_video.Element);
            VideoPlay.Visibility   = Visibility.Collapsed;
            VideoExpand.Visibility = Visibility.Visible;
            ShowVideoNote(null, "Playing");
            EffectiveViewportChanged += OnVideoViewportChanged;
            ClipVideoHost(mayTake: true);
            if (play)
            {
                _video.Player.Play();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (load != _videoLoad)
            {
                return;
            }

            AppLog.MediaFailed(App.ShellLog, "video", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            ReleaseVideo();
            ShowVideoNote(ChatCopy.VideoPlayFailed, "Failed");
        }
    }

    // The Player Couldn't Play It: Back To The Frame, Saying So
    private void OnVideoFailed(object? sender, EventArgs e)
    {
        AppLog.MediaFailed(App.ShellLog, "video", Bubble?.Id ?? string.Empty, "MediaFailed");
        ReleaseVideo();
        ShowVideoNote(ChatCopy.VideoPlayFailed, "Failed");
    }

    // A Click On The Frame Opens The Viewer From The Start
    private void OnVideoFrameClick(object sender, RoutedEventArgs e) => OpenVideoInViewer(VideoFrame);

    // Expand: The Viewer Carries On Where The Inline Player Got To
    private void OnVideoExpandClick(object sender, RoutedEventArgs e) => OpenVideoInViewer(VideoExpand);

    // Stops The Inline Player And Shows The Video Over The Whole Window, Where It Got To And Playing Or Paused As It Was
    // (From The Frame: From The Start, Playing). Opened From The Inline Player, The Viewer Hands Back Its Own Position And
    // State On Close, And The Inline Player Carries On From There (Owner, Plan 10). Focus Comes Back To The Frame On
    // Close, The Way The Clicked Button Had It (A Click Brings No Focus Ring Back)
    private void OpenVideoInViewer(Control from)
    {
        if (_videoPart is not { IsPending: false } part)
        {
            return;
        }

        VideoPlayback? inline              = _video;
        TimeSpan start                     = inline?.Position ?? TimeSpan.Zero;
        bool play                          = inline?.IsPlaying ?? true;
        FocusState how                     = from.FocusState;
        MessageBubbleViewModel? bubble     = Bubble;
        Action<TimeSpan, bool>? handBack   = inline is null ? null : (position, playing) =>
        {
            if (ReferenceEquals(Bubble, bubble) && ReferenceEquals(_videoPart, part) && IsLoaded)
            {
                PlayInline(position, playing);
            }
        };

        ReleaseVideo();
        ShowVideoNote(null, string.Empty);
        (Application.Current as App)?.MainWindow?.ShowVideo(part, Bubble?.Id ?? string.Empty, Bubble?.VideoName ?? ChatCopy.Video, VideoFrame, how, start, play, _videoFrameImage, handBack);
    }

    // Scrolled Out Of Sight: The Video Stops (A Realized Row Off Screen Isn't Recycled At Once)
    private void OnVideoViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (OutOfSight(args.EffectiveViewport))
        {
            ReleaseVideo();
        }
    }

    // Stops The Inline Player And Lets Go Of It And The Bytes, Back To The Frame And Its Play Button
    private void ReleaseVideo()
    {
        _videoLoad++;
        if (ReferenceEquals(s_playingVideo, this))
        {
            s_playingVideo = null;
        }

        if (_video is not { } video)
        {
            return;
        }

        _video                    = null;
        EffectiveViewportChanged -= OnVideoViewportChanged;
        video.Failed              -= OnVideoFailed;
        video.Dispose();

        // The Bubble Moved On To A Message Without A Video: Its Part Is Already Gone (x:Load)
        if (Video is null)
        {
            return;
        }

        VideoHost.Children.Clear();
        VideoPlay.Visibility   = Visibility.Visible;
        VideoExpand.Visibility = Visibility.Collapsed;
        TestHooks.ShowState(VideoFrame, "Stopped");
    }

    // The Note Over The Frame (Downloading, Or Failed), Or None; Its State Goes On The Frame For The Tests
    private void ShowVideoNote(string? text, string state)
    {
        VideoNote.Text           = text ?? string.Empty;
        VideoNoteBack.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        TestHooks.ShowState(VideoFrame, state);
    }

    // The Player's Host Rounded To The Bubble's Corners (The Video's Swap Chain Isn't Clipped By The Grid's Corners). The
    // Host Uses No Implicit Transitions, So Windows Hands Out Its Visual; It Is Taken Once, When A Video Starts
    private void ClipVideoHost(bool mayTake = false)
    {
        if (VideoHost is null || (_videoHostVisual is null && (!mayTake || !VideoHost.IsLoaded)))
        {
            return;
        }

        try
        {
            _videoHostVisual ??= ElementCompositionPreview.GetElementVisual(VideoHost);
            ClipRounded(_videoHostVisual, VideoHost, Video.CornerRadius);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "clip", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }
    }

    // =========================================================================
    // OPEN AND SAVE
    // =========================================================================

    // Opens The File In Its Default App (See MediaActions.OpenAsync). True When Opened Or Offered For Saving
    private Task<bool> OpenMediaAsync(MediaPart part) => MediaActions.OpenAsync(XamlRoot, part, Bubble?.Id ?? string.Empty);

    /// <summary>
    /// The bubble menu's Open: as a click on the photo (the viewer), the video's frame, the voice message, or the file
    /// card (its default app).
    /// </summary>
    public void Open()
    {
        if (Bubble?.HasFileCard == true)
        {
            OnFileClick(this, new RoutedEventArgs());
        }
        else if (Bubble?.HasVideo == true)
        {
            OpenVideoInViewer(VideoFrame);
        }
        else if (Bubble?.Media is { IsPending: false } part && ((Control?)Photo ?? VoicePlay) is { } from)
        {
            // A Photo, Or A Voice Message (Whose Bubble Has No Photo Part, So Focus Goes Back To Its Play Button)
            ShowInViewer(part, from);
        }
    }

    /// <summary>
    /// The bubble menu's Open with: the photo, video, voice message or file with Windows' app picker.
    /// </summary>
    public void OpenWith()
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.OpenWithAsync(part, Bubble.Id);
        }
    }

    /// <summary>
    /// The bubble menu's Save as: the Windows Save dialog for the photo or file.
    /// </summary>
    public void SaveAs()
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.SaveAsAsync(XamlRoot, part, Bubble.Id);
        }
    }

    /// <summary>
    /// The bubble menu's Download: into the Downloads folder, with the window's confirmation.
    /// </summary>
    public void Download()
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.DownloadAsync(part, Bubble.Id);
        }
    }

    private void ShowStates()
    {
        string side = Bubble switch
        {
            { FromMe: false } or null => "Received",
            { IsRcs: true }           => "Sent",
            _                         => "SentSms",
        };

        VisualStateManager.GoToState(this, side, false);
        ShowPartStates(refresh: false);

        // Re-Lay The Pill, So A Recycled Bubble That Switched Sides Doesn't Keep The Old Side's Shift
        LayPill();
    }

    // The Text, With Its Web Links As Links (Plain Text When It Has None); Without The Card's Link While The Card Shows
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

        // The Line Ends Are Known Once The New Text Is Laid Out
        void OnLaidOut(object? sender, object e)
        {
            Body.LayoutUpdated -= OnLaidOut;
            BlockLinkEnds();
        }

        Body.LayoutUpdated += OnLaidOut;

        // Plain Runs Between The Links
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

    // A Link Named By Its Text, Opened On Click Or Enter (Never By Itself)
    private Hyperlink NewLink(string shown, Uri url, int number)
    {
        var link = new Hyperlink { Foreground = Body.Foreground, UnderlineStyle = UnderlineStyle.Single };
        link.Inlines.Add(new Run { Text = shown });
        AutomationProperties.SetAutomationId(link, PartIdOf("Link.", Bubble?.PartKey ?? string.Empty) + "." + number);
        link.Click += (_, _) =>
        {
            _linkClicked = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _linkClicked = false);
            OpenLink(url);
        };

        _links.Add(link);
        return link;
    }

    // The Quote's Click, Enter Or Space: The Chat Jumps To The Quoted Message
    private void OnQuoteClick(object sender, RoutedEventArgs e) => QuoteRequested?.Invoke(this, EventArgs.Empty);

    // A New Width Moves The Line Ends
    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => BlockLinkEnds();

    // The Text Maps The Empty Space At The End Of A Line To The Line's Last Letter, So Past A Link That Ends A Line (Or
    // Wraps) The Pointer Still Hovers The Link And Shows The Hand. A Clear Strip Covers That Space From The Line's Last
    // Letter To The Text's Right Edge, Over Every Line Where A Link's Letters End; Only The Letters Themselves Count
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
            // Each Line The Link's Letters Sit On: Its Top, Height And Rightmost Edge. GetCharacterRect Gives The Caret (No
            // Width), So Each Letter's Right Edge Is The Caret Just After It, Taken Backward (At A Wrap That Caret Stays At
            // The End Of The Line It Leaves)
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

            // Every Line But The Last Ends With The Link; The Last Does Too Unless Text Follows On The Same Line
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

    // True When A Letter After The Link (Past The Next Run's Start) Sits On The Link's Last Line
    private static bool FollowedOnLine(Hyperlink link, Rect line)
    {
        // The Caret Just After The Next Letter: The First Past The Link's End That Moved (Element Edges Don't Move It);
        // None When The Text Ends With The Link
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

    // A Clear Strip That Takes The Pointer (A Transparent Fill); Its Right-Click Goes On Up To The Chat's Bubble Menu, Like The Text's
    private void AddBlocker(double left, double top, double width, double height)
    {
        if (width < 1 || height < 1)
        {
            return;
        }

        var blocker = new Border { Width = width, Height = height, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
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

    // Only Web Addresses Open (FindLinks Gives No Other, Checked Again Before Anything Launches); A Test Run Only Logs
    // The Click. Never The Address: Only Its Scheme Is Logged
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

    // The Time Toggles A Turn Later, Once A Link's Click (Which Can Come After The Tap) Has Had Its Say
    private void OnBackTapped(object sender, TappedRoutedEventArgs e)
    {
        // A Tap On The Quote, A Photo, Video, File Card, Voice Message Or Preview Card Is That Part's Own
        if (InPart(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_linkClicked)
            {
                _linkClicked = false;
                return;
            }

            Bubble?.ToggleTime();
        });
    }

    // Opens The Link In The Default Browser
    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (Bubble?.Preview is { } preview)
        {
            OpenLink(preview.Url);
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The bubble menu's Copy: the selected text, or the whole text when nothing is selected, as the text's own Copy did.
    /// </summary>
    /// <remarks>Always as sent, without the VS16 added for showing color emoji.</remarks>
    public void Copy()
    {
        string original = Bubble?.Text ?? string.Empty;
        string text     = Body.SelectedText.Length > 0 ? ChatFormat.OriginalOf(original, Body.SelectedText) : original;
        if (text.Length == 0)
        {
            return;
        }

        ToClipboard(text);
    }

    /// <summary>
    /// The preview card menu's Copy link: the whole address as sent, though the text no longer shows it.
    /// </summary>
    public void CopyLink()
    {
        if (Bubble?.LinkUrl is { } url)
        {
            ToClipboard(url.OriginalString);
        }
    }

    // Another App Holding The Clipboard Throws; Copy Then Does Nothing, Logged
    private static void ToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);

        try
        {
            Clipboard.SetContent(package);
        }
        catch (COMException ex)
        {
            AppLog.CopyFailed(App.ShellLog, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The bubble menu's React: asks the chat to open the reaction picker over the back.
    /// </summary>
    public void React() => ReactRequested?.Invoke(this, Back);

    // The Pill: Who Reacted, In Every Chat (Never The Picker; The Menu, The Strip And Shift + + Open That)
    private void OnPillClick(object sender, RoutedEventArgs e)
    {
        if (Bubble is not { } bubble)
        {
            return;
        }

        ShowReactors(bubble.Reactors());
    }

    // Who Reacted: A List In A Flyout At The Pill, One Row Per Person ("👍 Bo"), Named For Narrator (Reactors.{n}). The
    // Rows Only Read, So They Show No Hover Or Press Fill. Compact Like A Menu: 32 px Rows, 8 px From Emoji To Name, And A
    // Menu's Thin Edge Around The List. Names Never Reach The Log
    private void ShowReactors(IReadOnlyList<(string Emoji, string Name)> reactors)
    {
        if (reactors.Count == 0)
        {
            return;
        }

        var list  = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = ReactorsMaxHeight };
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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

    // The Strip's Buttons Act Only While The Bubble Takes Them (A Click Can Race A Change). Its React Opens The Picker
    // Over The Button
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

    // Only A Bubble That Can Take Them Shows The Strip
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        if (Bubble?.CanAct == true)
        {
            ShowHover(true, Ui.AnimationsEnabled);
        }
    }

    // The Pointer Left The Bubble. As On The Conversation Row, No Bounds Check Is Made: Leaving The Window Straight From A
    // Bubble Reports Its Last Point Inside It. While The Picker Or Menu Is Open (Its Overlay Takes The Pointer) The Strip
    // Waits For It To Close
    private void OnPointerLeft(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        if (!_keepShown)
        {
            ShowHover(false, Ui.AnimationsEnabled);
        }
    }

    /// <summary>
    /// The bubble menu's Reply: asks the chat to start a reply to this bubble.
    /// </summary>
    public void Reply() => ReplyRequested?.Invoke(this, EventArgs.Empty);
}

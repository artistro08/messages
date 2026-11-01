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
/// (<c>Bubble.Open</c>); Save as (<c>Bubble.SaveAs</c>) saves a copy where the person picks, and Download
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

    // The Hover Strip: Three 32 px Buttons With 4 px Either Side (MessageBubble.xaml)
    private const double HoverStripWidth = 104;

    // A Photo's Blurred Look: Its Thumbnail Decoded This Wide, Then Stretched To The Photo's Size
    private const int BlurDecodeWidth = 24;

    // The File Card's Thumbnail Width (Its 40 px Square, Sharp At 200% Scale)
    private const int FileThumbWidth = 80;

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

    // The Image Bytes The Card Shows (Or Is Decoding), So A Change Decodes Once
    private byte[]? _previewImage;

    // A Click On The Preview Card, The Photo Or The File Card Also Reaches The Bubble's Back, Which Must Not Toggle The
    // Time For It
    private bool _previewTapped;

    // Same For A Link In The Text: Its Click And The Back's Tap Come In Either Order, So The Tap Waits A Turn
    private bool _linkClicked;

    // The Text And ID The Text's Runs Were Built For: Rebuilt Only When They Change (A Rebuild Drops A Selection)
    private string? _shownText;

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
    // Message Shows, So A Late Load Or Player Update Stops), And Its Audio And Waveform Once Read
    private MediaPart? _voicePart;
    private MessageBubbleViewModel? _voiceBubble;
    private string _voiceKey = string.Empty;
    private int _voiceLoad;
    private byte[]? _voiceAudio;
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

    // The Reactions The Pill Shows, So A Change On The Same Bubble Pops It In; Set When A Pill Just Shown Waits For Its
    // Size To Pop; And The Pill Host's Visual (No Implicit Transitions On It, So Windows Hands It Out), Taken Once
    private string _reactionsShown = string.Empty;
    private bool _popPill;
    private Visual? _pillVisual;

    /// <summary>
    /// Creates the bubble.
    /// </summary>
    public MessageBubble()
    {
        InitializeComponent();

        // Behavior
        Back.AddHandler(TappedEvent, new TappedEventHandler(OnBackTapped), handledEventsToo: true);
        Preview.AddHandler(TappedEvent, new TappedEventHandler((_, _) => _previewTapped = true), handledEventsToo: true);
        Photo.AddHandler(TappedEvent, new TappedEventHandler((_, _) => _previewTapped = true), handledEventsToo: true);
        FileCard.AddHandler(TappedEvent, new TappedEventHandler((_, _) => _previewTapped = true), handledEventsToo: true);
        Voice.AddHandler(TappedEvent, new TappedEventHandler((_, _) => _previewTapped = true), handledEventsToo: true);
        Video.AddHandler(TappedEvent, new TappedEventHandler((_, _) => _previewTapped = true), handledEventsToo: true);

        // A Playing Video Stops When The Bubble Leaves The Tree, And Its Player Keeps The Bubble's Corners
        Unloaded               += (_, _) => ReleaseVideo();
        VideoHost.SizeChanged  += (_, _) => ClipVideoHost();

        // The Row Under The Bubble Is At Least As Wide As The Back (Inside Its Margins), So The Pill Sits On The Back's Corner
        Back.SizeChanged += (_, _) => Under.MinWidth = Math.Max(0, Back.ActualWidth - Under.Margin.Left - Under.Margin.Right);

        // A Pill That Just Appeared Pops In Once It Has Its Size (Its Middle Is Where It Grows From)
        PillHost.SizeChanged += (_, _) =>
        {
            if (_popPill)
            {
                _popPill = false;
                PopPill();
            }
        };

        // The Text's Own Menu Would Win A Right-Click On It, So It Gets The Bubble Menu Too (Which Has Copy)
        Body.ContextFlyout = Menu;
        Menu.Closed       += (_, _) => KeepShown(false);

        // Copy
        AutomationProperties.SetName(HoverReact, ChatCopy.React);
        ToolTipService.SetToolTip(HoverReact, ChatCopy.React);
        AutomationProperties.SetName(HoverReply, ChatCopy.Reply);
        ToolTipService.SetToolTip(HoverReply, ChatCopy.Reply);
        AutomationProperties.SetName(HoverMore, ChatCopy.MoreOptions);
        ToolTipService.SetToolTip(HoverMore, ChatCopy.MoreOptions);

        // The Photo's Layers Take The Photo's Rounded Corners (Bound Once, On The Button). A Still Photo Is The Full
        // Layer's Brush, Which Its Corners Clip; Only A GIF's Image Needs The Composition Clip (See ClipPhotoFull)
        Photo.RegisterPropertyChangedCallback(Control.CornerRadiusProperty, (_, _) =>
        {
            PhotoBlur.CornerRadius = PhotoFull.CornerRadius = Photo.CornerRadius;
            ClipPhotoFull();
        });
        PhotoFullImage.SizeChanged += (_, _) => ClipPhotoFull();
        PhotoFullImage.Loaded      += (_, _) => ClipPhotoFull(mayTake: true);

        // Links Follow The Text's Color: Its Side's State And The Theme Set It
        Body.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => ColorLinks());
        ShowVoicePlaying(false, 0);
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
    /// Asks the chat's voice player to play, pause or seek this bubble's voice message.
    /// </summary>
    public event EventHandler<VoiceRequest>? VoiceRequested;

    /// <summary>Gets the menu's Copy text.</summary>
    public static string CopyText => ChatCopy.Copy;

    /// <summary>Gets the menu's React text.</summary>
    public static string ReactText => ChatCopy.React;

    /// <summary>Gets the menu's Reply text.</summary>
    public static string ReplyText => ChatCopy.Reply;

    /// <summary>Gets the preview card menu's Copy link text.</summary>
    public static string CopyLinkText => ChatCopy.CopyLink;

    /// <summary>Gets the menu's Open text.</summary>
    public static string OpenText => ChatCopy.Open;

    /// <summary>Gets the menu's Save as text.</summary>
    public static string SaveAsText => ChatCopy.SaveAs;

    /// <summary>Gets the menu's Download text.</summary>
    public static string DownloadText => ChatCopy.Download;

    /// <summary>Gets the video's play button name and tooltip.</summary>
    public static string PlayVideoText => ChatCopy.PlayVideo;

    /// <summary>Gets the playing video's expand button name and tooltip.</summary>
    public static string OpenVideoText => ChatCopy.OpenVideo;

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
    /// Opens the bubble menu over the back (the Application key or Shift + F10 on the bubble's row).
    /// </summary>
    public void ShowMenu() => ShowMenu(Back);

    /// <summary>
    /// Opens the bubble menu at an element, such as the hover strip's ⋮.
    /// </summary>
    /// <remarks>The hover strip stays shown until the menu closes.</remarks>
    /// <param name="anchor">The element the menu opens at.</param>
    public void ShowMenu(FrameworkElement anchor)
    {
        KeepShown(true);
        Menu.ShowAt(anchor);
    }

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
            VisualStateManager.GoToState(this, "Hidden", Ui.AnimationsEnabled);
        }
    }

    // =========================================================================
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Gives a part's AutomationId: the prefix, then the message ID or, until the phone has reported the message, the
    /// temporary ID.
    /// </summary>
    /// <param name="prefix">The prefix, such as "Status.".</param>
    /// <param name="id">The message ID, or empty.</param>
    /// <param name="tmpId">The temporary ID, or empty.</param>
    /// <returns>The AutomationId.</returns>
    public static string PartIdOf(string prefix, string id, string tmpId) => prefix + (id.Length > 0 ? id : tmpId);

    /// <summary>
    /// Shows an element only when there is text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Visible for non-empty text.</returns>
    public static Visibility ShownIfText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Gives the text's AutomationId: the message ID, or the temporary ID until the phone has reported the message.
    /// </summary>
    /// <param name="id">The message ID, or empty.</param>
    /// <param name="tmpId">The temporary ID, or empty.</param>
    /// <returns>The AutomationId.</returns>
    public static string AutomationIdOf(string id, string tmpId) => PartIdOf("Bubble.", id, tmpId);

    /// <summary>
    /// Gives the "Not sent" link's AutomationId.
    /// </summary>
    /// <param name="id">The message ID, or empty.</param>
    /// <param name="tmpId">The temporary ID, or empty.</param>
    /// <returns>The AutomationId.</returns>
    public static string RetryIdOf(string id, string tmpId) => AutomationIdOf(id, tmpId) + ".Retry";

    /// <summary>
    /// Gives the time's AutomationId.
    /// </summary>
    /// <param name="id">The message ID, or empty.</param>
    /// <param name="tmpId">The temporary ID, or empty.</param>
    /// <returns>The AutomationId.</returns>
    public static string TimeIdOf(string id, string tmpId) => AutomationIdOf(id, tmpId) + ".Time";

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
            double max_width = cap + HoverStripWidth;
            if (Root.MaxWidth != max_width)
            {
                Root.MaxWidth = max_width;
            }

            // The Photo Fills The Bubble Edge To Edge, Beside The Avatar Lane
            _photoMaxWidth = cap - LaneWidth(Bubble?.HasAvatarLane == true);
            SizePhoto();
        }

        return base.MeasureOverride(availableSize);
    }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // A Recycled Container Gets Another Message: Follow Its Changes Instead, And Put It On Its Side, With No Hover Strip
    // Left Over From The Last One
    private static void OnBubbleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MessageBubble)d;

        // No Entrance Left Running From The Container's Last Bubble. Guarded: This Runs In x:Bind's ProcessBindings, Where
        // Windows Can Refuse The Visual (E_ACCESSDENIED, As It Did The Photo's Clip), And A Throw Escaping Into XAML Crashes
        // The App
        try
        {
            Visual row = ElementCompositionPreview.GetElementVisual(view.Row);
            ElementCompositionPreview.SetIsTranslationEnabled(view.Row, true);
            row.StopAnimation(nameof(Visual.Opacity));
            row.StopAnimation("Translation");
            row.Opacity = 1;
            row.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "entrance", view.Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }

        view._pointerOver = false;
        view._keepShown   = false;
        view._canAct      = view.Bubble?.CanAct == true;
        VisualStateManager.GoToState(view, "Hidden", false);
        view.ShowFocusRing(false, false);

        if (e.OldValue is MessageBubbleViewModel old_bubble)
        {
            old_bubble.PropertyChanged -= view.OnBubblePropertyChanged;
        }

        if (e.NewValue is MessageBubbleViewModel new_bubble)
        {
            new_bubble.PropertyChanged += view.OnBubblePropertyChanged;
        }

        view.ShowText();
        view.ShowStates();
        view.ShowPreviewImage();
        view.ShowPhoto();
        view.ShowFile();
        view.ShowVoice();
        view.ShowVideo();

        // Another Message's Reactions Show As They Are, With No Pop (A Recycled Row's Half-Run Pop Is Put Back)
        view._reactionsShown = view.Bubble?.ReactionsText ?? string.Empty;
        view._popPill        = false;
        view.RestPill();

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

        string id = PartIdOf(string.Empty, Bubble?.Id ?? string.Empty, Bubble?.TmpId ?? string.Empty);
        AppLog.BubbleAnimatedIn(App.ShellLog, id);
    }

    // An Echo Can Make A Sending Bubble RCS, My Reaction Comes And Goes, And A Preview Card Arrives
    private void OnBubblePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowText();
        ShowStates();
        ShowPreviewImage();
        ShowPhoto();
        ShowFile();
        ShowVoice();
        ShowVideo();
        ShowHoverStrip();
        ShowReactionChange();
    }

    // =========================================================================
    // REACTION PILL
    // =========================================================================

    // The Same Bubble's Reactions Changed: The Pill Pops In, At Once When It Already Shows, Else Once It Has Its Size
    // (Hidden Until Then, So It Never Shows A Frame At Full Size First). Only With Windows' Animation Effects On
    private void ShowReactionChange()
    {
        string text = Bubble?.ReactionsText ?? string.Empty;
        if (text == _reactionsShown)
        {
            return;
        }

        _reactionsShown = text;
        if (text.Length == 0 || !Ui.AnimationsEnabled || TakePillVisual() is not { } visual)
        {
            return;
        }

        if (PillHost.ActualWidth > 0)
        {
            PopPill();
            return;
        }

        _popPill       = true;
        visual.Opacity = 0;
    }

    // Pops The Pill In From Its Middle: From Half Size And Clear To A Touch Over Full Size, Then Settling, In 180 ms On
    // WinUI's Fast-Out-Slow-In Curve. Layout Never Moves
    private void PopPill()
    {
        if (TakePillVisual() is not { } visual)
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
        TestHooks.NoteState("reaction-pop", PartIdOf(string.Empty, Bubble?.Id ?? string.Empty, Bubble?.TmpId ?? string.Empty));
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

    // The Pill Host's Visual, Taken Once While The Bubble Is In The Live Tree; Null When Windows Refuses It (No Pop Beats
    // A Crash)
    private Visual? TakePillVisual()
    {
        if (_pillVisual is not null || !PillHost.IsLoaded)
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
        bool can_act = Bubble?.CanAct == true;
        if (can_act == _canAct)
        {
            return;
        }

        _canAct = can_act;
        VisualStateManager.GoToState(this, can_act && (_pointerOver || _keepShown) ? "Shown" : "Hidden", Ui.AnimationsEnabled);
    }

    // Decodes The Card's Image From Its Bytes; Bytes Windows Can't Read Leave The Card Without One
    private async void ShowPreviewImage()
    {
        byte[]? bytes = Bubble?.Preview?.Image;
        if (ReferenceEquals(bytes, _previewImage))
        {
            return;
        }

        _previewImage           = bytes;
        PreviewImage.Source     = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        if (bytes is null)
        {
            return;
        }

        // A Recycled Container May Have Moved On While Decoding
        ImageSource? image = await Avatar.ToImageAsync(bytes, PreviewImageWidth);
        if (image is null || !ReferenceEquals(bytes, _previewImage))
        {
            return;
        }

        PreviewImage.Source     = image;
        PreviewImage.Visibility = Visibility.Visible;
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

        bool same_message = part is not null && Bubble is not null && ReferenceEquals(Bubble, _photoBubble);
        if (same_message && KeepSentMedia(_photoPart, part))
        {
            // An Echo With Only A Thumbnail (An MMS Photo's) Keeps The Sent Bytes On Show: Loading It Showed The Tiny
            // Thumbnail Blurred, Waiting For A Full Size That May Never Come
            _photoPart = part!.MediaId.Length > 0 ? part : _photoPart;
            SizePhoto();
            return;
        }

        _photoPart   = part;
        _photoBubble = Bubble;
        _photoFailed = false;
        int load     = NextPhotoLoad();

        // Another Message, Or None (A Bubble Without A Photo): Let Go Of The Old Images
        if (!same_message)
        {
            PhotoBlur.Background        = null;
            PhotoFull.Background        = null;
            PhotoFullImage.Source       = null;
            PhotoFull.OpacityTransition = null;
            PhotoFull.Opacity           = 0;
            _photoAnimated              = false;
            ClipPhotoFull();
        }

        ShowPhotoNote(null, string.Empty);
        SizePhoto();
        if (part is not null)
        {
            _ = LoadPhotoAsync(part, load, again: false);
        }
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
        FrameworkElement shown        = bubble.HasVideo ? Video : Photo;
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
            Task<byte[]> full_task = media.GetAsync(part, thumbnail: false, CancellationToken.None);
            bool at_once           = full_task.IsCompleted;
            byte[] full            = await full_task;
            (int pixel_width, int pixel_height) = await PixelSizeAsync(full);
            int decode_width                    = MessageBubbleViewModel.FillDecodeWidth(pixel_width, pixel_height, Photo.Width, Photo.Height);
            ImageSource image                   = await Avatar.ToImageAsync(full, decode_width) ?? throw new InvalidDataException();
            AppLog.PhotoDecoded(App.ShellLog, bubble.Id, full.Length, part.Size, pixel_width, pixel_height, decode_width);
            if (load != _photoLoad)
            {
                return;
            }

            // A Moving GIF Plays In An Image (A Brush Shows Only Its First Frame; Test Runs Note It); A Still Photo Is The
            // Layer's Brush, Which The Border's Corners Clip With No Composition Clip
            bool animated               = image.As<BitmapImage>() is { IsAnimatedBitmap: true };
            _photoAnimated              = animated;
            PhotoFullImage.Source       = animated ? image : null;
            PhotoFull.Background        = animated ? null : new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
            PhotoFull.OpacityTransition = at_once || !Ui.AnimationsEnabled ? null : _photoFade;
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

    // A Picture's Own Size As Shown (After Its Orientation), From Its Header; 0 x 0 When It Won't Decode
    private static async Task<(int Width, int Height)> PixelSizeAsync(byte[] bytes)
    {
        try
        {
            using Windows.Storage.Streams.IRandomAccessStream stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            Windows.Graphics.Imaging.BitmapDecoder decoder           = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (0, 0);
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
        if (_photoFullVisual is null && (!mayTake || !_photoAnimated || !PhotoFullImage.IsLoaded))
        {
            return;
        }

        try
        {
            _photoFullVisual ??= ElementCompositionPreview.GetElementVisual(PhotoFullImage);
            Visual visual      = _photoFullVisual;

            // Not Laid Out Yet: Its SizeChanged Clips It Later. A Corner Can't Be Rounder Than Half The Shorter Side, Or
            // The Clip Is Refused (Which Crashed The App On Opening A Chat)
            float width  = (float)PhotoFullImage.ActualWidth;
            float height = (float)PhotoFullImage.ActualHeight;
            if (!_photoAnimated || width <= 0 || height <= 0)
            {
                visual.Clip = null;
                return;
            }

            float most     = Math.Min(width, height) / 2;
            CornerRadius r = Photo.CornerRadius;
            visual.Clip    = visual.Compositor.CreateRectangleClip(
                0, 0, width, height,
                Corner(r.TopLeft, most), Corner(r.TopRight, most), Corner(r.BottomRight, most), Corner(r.BottomLeft, most));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Square Corners Beat A Crash; The Next Size Change Tries Again
            AppLog.MediaFailed(App.ShellLog, "clip", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }

        static Vector2 Corner(double radius, float most) => new(Math.Min((float)radius, most));
    }

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
        if (_photoPart is not { IsPending: false } part)
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

        ShowInViewer(part);
    }

    // The Photo Over The Whole Window, Named As The Bubble's Photo Is
    private void ShowInViewer(MediaPart part) =>
        (Application.Current as App)?.MainWindow?.ShowPhoto(part, Bubble?.Id ?? string.Empty, Bubble?.PhotoName ?? ChatCopy.Photo, Photo);

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

        _filePart            = part;
        _fileBubble          = Bubble;
        _fileOpening         = false;
        _fileFailed          = false;
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

    // Another Voice Message: Shown Stopped, Then Downloaded And Its Waveform And Length Read (Once Per File, In Memory).
    // The Echo Of One Sent From Here Keeps Its Audio, Waveform And Play State, So A Playing Message Plays On
    private async void ShowVoice()
    {
        MediaPart? part = Bubble is { HasVoice: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _voicePart) && ReferenceEquals(Bubble, _voiceBubble))
        {
            return;
        }

        if (part is not null && Bubble is not null && ReferenceEquals(Bubble, _voiceBubble) && KeepSentMedia(_voicePart, part))
        {
            _voicePart = part;
            return;
        }

        _voicePart        = part;
        _voiceBubble      = Bubble;
        _voiceKey         = Bubble is { } shown ? (shown.TmpId.Length > 0 ? shown.TmpId : shown.Id) : string.Empty;
        _voiceLoad++;
        _voiceAudio       = null;
        _voiceWave        = null;
        VoiceLength.Text  = string.Empty;
        VoiceBars.Show(null);
        VoiceBars.Length  = TimeSpan.Zero;
        ShowVoicePlaying(false, 0);
        if (part is not { IsPending: false } || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        try
        {
            int load       = _voiceLoad;
            byte[] audio   = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
            VoiceWave wave = await WaveformReader.ReadAsync(audio);
            if (load != _voiceLoad)
            {
                return;
            }

            _voiceAudio      = audio;
            _voiceWave       = wave;
            VoiceLength.Text = VoiceWave.LengthText(wave.Length);
            VoiceBars.Show(wave.Peaks);
            VoiceBars.Length = wave.Length;
            TestHooks.ShowState(VoiceLength, "Ready");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "voice", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            TestHooks.ShowState(VoiceLength, "Failed");
        }
    }

    // Play Or Pause: The Chat's Player Shows The State Back (See VoiceRequest)
    private void OnVoicePlayClick(object sender, RoutedEventArgs e) => RequestVoice(null);

    // A Click On The Waveform Seeks There
    private void OnVoiceSeekRequested(object? sender, double share) => RequestVoice(share);

    private void RequestVoice(double? seekTo)
    {
        if (_voiceAudio is not { } audio || _voiceWave is not { } wave)
        {
            return;
        }

        // Only While This View Still Shows The Message (A Recycled Row Moves On; Its Echo Doesn't)
        int load = _voiceLoad;
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
        string name      = playing ? ChatCopy.Pause : ChatCopy.Play;
        VoiceGlyph.Glyph = playing ? "" : "";
        AutomationProperties.SetName(VoicePlay, name);
        ToolTipService.SetToolTip(VoicePlay, name);
        VoiceBars.ShowProgress(share);
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
            if (VideoThumb.Background is null)
            {
                LoadVideoFrame(part);
            }

            return;
        }

        ReleaseVideo();
        _videoPart            = part;
        _videoBubble          = Bubble;
        VideoThumb.Background = null;
        ShowVideoNote(null, string.Empty);
        SizePhoto();
        if (part is not null)
        {
            LoadVideoFrame(part);
        }
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

        try
        {
            byte[] bytes      = await VideoStill.GetAsync(media, part);
            ImageSource image = await Avatar.ToImageAsync(bytes, (int)Math.Ceiling(MaxBubbleWidth)) ?? throw new InvalidDataException();
            if (ReferenceEquals(part, _videoPart))
            {
                VideoThumb.Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
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
    private async void OnVideoPlayClick(object sender, RoutedEventArgs e)
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

            string id = PartIdOf("Video.Player.", Bubble?.Id ?? string.Empty, Bubble?.TmpId ?? string.Empty);
            _video    = new VideoPlayback(DispatcherQueue, bytes, part.Mime, TimeSpan.Zero, compact: true, id, Bubble?.VideoName ?? ChatCopy.Video);
            _video.Failed              += OnVideoFailed;
            _video.FullWindowRequested += OnVideoFullWindowRequested;
            VideoHost.Children.Add(_video.Element);
            VideoPlay.Visibility   = Visibility.Collapsed;
            VideoExpand.Visibility = Visibility.Visible;
            ShowVideoNote(null, "Playing");
            EffectiveViewportChanged += OnVideoViewportChanged;
            ClipVideoHost(mayTake: true);
            _video.Player.Play();
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

    // Windows' Full Window Button Does What Expand Does (After Its Click Has Finished With The Controls)
    private void OnVideoFullWindowRequested(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => OpenVideoInViewer(VideoExpand));

    // Stops The Inline Player (Keeping Its Position) And Shows The Video Over The Whole Window. Focus Comes Back To The
    // Frame On Close, The Way The Clicked Button Had It (A Click Brings No Focus Ring Back)
    private void OpenVideoInViewer(Control from)
    {
        if (_videoPart is not { IsPending: false } part)
        {
            return;
        }

        TimeSpan start  = _video?.Position ?? TimeSpan.Zero;
        FocusState how  = from.FocusState;
        ReleaseVideo();
        ShowVideoNote(null, string.Empty);
        (Application.Current as App)?.MainWindow?.ShowVideo(part, Bubble?.Id ?? string.Empty, Bubble?.VideoName ?? ChatCopy.Video, VideoFrame, how, start);
    }

    // Scrolled Out Of Sight: The Video Stops (A Realized Row Off Screen Isn't Recycled At Once)
    private void OnVideoViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        Windows.Foundation.Rect seen = args.EffectiveViewport;
        if (seen.Width <= 0 || seen.Height <= 0 || seen.Bottom <= 0 || seen.Top >= ActualHeight || seen.Right <= 0 || seen.Left >= ActualWidth)
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
        video.FullWindowRequested -= OnVideoFullWindowRequested;
        VideoHost.Children.Clear();
        video.Dispose();
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
        if (_videoHostVisual is null && (!mayTake || !VideoHost.IsLoaded))
        {
            return;
        }

        try
        {
            _videoHostVisual ??= ElementCompositionPreview.GetElementVisual(VideoHost);
            float width        = (float)VideoHost.ActualWidth;
            float height       = (float)VideoHost.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            float most               = Math.Min(width, height) / 2;
            CornerRadius r           = Video.CornerRadius;
            _videoHostVisual.Clip    = _videoHostVisual.Compositor.CreateRectangleClip(
                0, 0, width, height,
                Corner(r.TopLeft, most), Corner(r.TopRight, most), Corner(r.BottomRight, most), Corner(r.BottomLeft, most));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "clip", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }

        static Vector2 Corner(double radius, float most) => new(Math.Min((float)radius, most));
    }

    // =========================================================================
    // OPEN AND SAVE
    // =========================================================================

    // Opens The File In Its Default App (See MediaActions.OpenAsync). True When Opened Or Offered For Saving
    private Task<bool> OpenMediaAsync(MediaPart part) => MediaActions.OpenAsync(XamlRoot, part, Bubble?.Id ?? string.Empty);

    // The Menu's Open: As A Click On The Photo (The Viewer) Or The Card
    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Bubble?.HasFileCard == true)
        {
            OnFileClick(sender, e);
        }
        else if (Bubble?.HasVideo == true)
        {
            OpenVideoInViewer(VideoFrame);
        }
        else if (Bubble?.Media is { IsPending: false } part)
        {
            ShowInViewer(part);
        }
    }

    // The Menu's Save As
    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.SaveAsAsync(XamlRoot, part, Bubble.Id);
        }
    }

    // The Menu's Download: Into The Downloads Folder, With The Window's Confirmation
    private void OnDownloadClick(object sender, RoutedEventArgs e)
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
        VisualStateManager.GoToState(this, Bubble?.HasMyReaction == true ? "Mine" : "NotMine", false);
    }

    // The Text, With Its Web Links As Links (Plain Text When It Has None); Without The Card's Link While The Card Shows
    private void ShowText()
    {
        string text = Bubble?.BodyText ?? string.Empty;
        string key  = text + "|" + PartIdOf("Link.", Bubble?.Id ?? string.Empty, Bubble?.TmpId ?? string.Empty);
        if (key == _shownText)
        {
            return;
        }

        _shownText = key;
        _links.Clear();

        IReadOnlyList<TextLink> links = LinkPreviewer.FindLinks(text);
        if (links.Count == 0)
        {
            Body.Text = text;
            return;
        }

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
        AutomationProperties.SetAutomationId(link, PartIdOf("Link.", Bubble?.Id ?? string.Empty, Bubble?.TmpId ?? string.Empty) + "." + number);
        link.Click += (_, _) =>
        {
            _linkClicked = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _linkClicked = false);
            OpenLink(url);
        };

        _links.Add(link);
        return link;
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
        if (_previewTapped)
        {
            _previewTapped = false;
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

    // Copies The Selected Text, Or The Whole Text When Nothing Is Selected, As The Text's Own Copy Did; Always As Sent,
    // Without The VS16 Added For Showing Color Emoji
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string original = Bubble?.Text ?? string.Empty;
        string text     = Body.SelectedText.Length > 0 ? ChatFormat.OriginalOf(original, Body.SelectedText) : original;
        if (text.Length == 0)
        {
            return;
        }

        ToClipboard(text);
    }

    // The Card's Copy Link: The Whole Address As Sent, Though The Text No Longer Shows It
    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
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

    // The Menu's React: The Picker Opens Over The Back
    private void OnReactClick(object sender, RoutedEventArgs e) => ReactRequested?.Invoke(this, Back);

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
            VisualStateManager.GoToState(this, "Shown", Ui.AnimationsEnabled);
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
            VisualStateManager.GoToState(this, "Hidden", Ui.AnimationsEnabled);
        }
    }

    private void OnReplyClick(object sender, RoutedEventArgs e) => ReplyRequested?.Invoke(this, EventArgs.Empty);
}

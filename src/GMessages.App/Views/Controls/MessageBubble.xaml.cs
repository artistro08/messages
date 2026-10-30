using System.ComponentModel;
using System.Runtime.InteropServices;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.ViewManagement;

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
/// title, shows under the text once fetched; clicking it opens the link in the default browser. The pill and the menu's React raise
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
/// </remarks>
public sealed partial class MessageBubble : UserControl
{
    /// <summary>Identifies the <see cref="Bubble"/> property.</summary>
    public static readonly DependencyProperty BubbleProperty =
        DependencyProperty.Register(nameof(Bubble), typeof(MessageBubbleViewModel), typeof(MessageBubble), new PropertyMetadata(null, OnBubbleChanged));

    // A Bubble Still Being Sent Shows Faded
    private const double SendingFade = 0.6;

    // The Preview Card's Image Width, In Effective Pixels
    private const int PreviewImageWidth = 240;

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

    // A Click On The Card Also Reaches The Bubble's Back, Which Must Not Toggle The Time For It
    private bool _previewTapped;

    // Same For A Link In The Text: Its Click And The Back's Tap Come In Either Order, So The Tap Waits A Turn
    private bool _linkClicked;

    // The Text And ID The Text's Runs Were Built For: Rebuilt Only When They Change (A Rebuild Drops A Selection)
    private string? _shownText;

    // The Text's Links, Kept In The Text's Color
    private readonly List<Hyperlink> _links = [];

    /// <summary>
    /// Creates the bubble.
    /// </summary>
    public MessageBubble()
    {
        InitializeComponent();

        // Behavior
        Back.AddHandler(TappedEvent, new TappedEventHandler(OnBackTapped), handledEventsToo: true);
        Preview.AddHandler(TappedEvent, new TappedEventHandler((_, _) => _previewTapped = true), handledEventsToo: true);

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

        // Links Follow The Text's Color: Its Side's State And The Theme Set It
        Body.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => ColorLinks());
    }

    /// <summary>
    /// Asks the chat to send this failed bubble again.
    /// </summary>
    public event EventHandler? RetryRequested;

    /// <summary>
    /// Asks the chat to open the reaction picker on this bubble (the pill, the menu's React or the hover strip's React),
    /// over the element given: the back, or the strip's React button.
    /// </summary>
    public event EventHandler<FrameworkElement>? ReactRequested;

    /// <summary>
    /// Asks the chat to start a reply to this bubble (the menu's Reply or the hover strip's Reply).
    /// </summary>
    public event EventHandler? ReplyRequested;

    /// <summary>Gets the "Not sent" link text.</summary>
    public static string NotSentText => ChatCopy.NotSentRetry;

    /// <summary>Gets the menu's Copy text.</summary>
    public static string CopyText => ChatCopy.Copy;

    /// <summary>Gets the menu's React text.</summary>
    public static string ReactText => ChatCopy.React;

    /// <summary>Gets the menu's Reply text.</summary>
    public static string ReplyText => ChatCopy.Reply;

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
            double max_width = Math.Min(MaxBubbleWidth, Math.Floor(availableSize.Width * MaxWidthShare)) + HoverStripWidth;
            if (Root.MaxWidth != max_width)
            {
                Root.MaxWidth = max_width;
            }
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
        var view          = (MessageBubble)d;
        view._pointerOver = false;
        view._keepShown   = false;
        view._canAct      = view.Bubble?.CanAct == true;
        VisualStateManager.GoToState(view, "Hidden", false);

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
    }

    // An Echo Can Make A Sending Bubble RCS, My Reaction Comes And Goes, And A Preview Card Arrives
    private void OnBubblePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowText();
        ShowStates();
        ShowPreviewImage();
        ShowHoverStrip();
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

    // The Text, With Its Web Links As Links (Plain Text When It Has None)
    private void ShowText()
    {
        string text = Bubble?.DisplayText ?? string.Empty;
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

        var package = new DataPackage();
        package.SetText(text);

        // Another App Holding The Clipboard Throws; Copy Then Does Nothing, Logged
        try
        {
            Clipboard.SetContent(package);
        }
        catch (COMException ex)
        {
            AppLog.CopyFailed(App.ShellLog, ex.GetType().Name);
        }
    }

    // The Pill Or The Menu's React: The Picker Opens Over The Back
    private void OnReactClick(object sender, RoutedEventArgs e) => ReactRequested?.Invoke(this, Back);

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

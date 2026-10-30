using System.ComponentModel;
using System.Runtime.InteropServices;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

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
/// Bubbles of one group (see <see cref="ChatViewModel.GroupGap"/>) sit close together with small corners where they meet,
/// as in Google Messages. A bubble is as wide as its content, up to 70% of the chat and 560 px at most.
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

    // The Image Bytes The Card Shows (Or Is Decoding), So A Change Decodes Once
    private byte[]? _previewImage;

    // A Click On The Card Also Reaches The Bubble's Back, Which Must Not Toggle The Time For It
    private bool _previewTapped;

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
    }

    /// <summary>
    /// Asks the chat to send this failed bubble again.
    /// </summary>
    public event EventHandler? RetryRequested;

    /// <summary>
    /// Asks the chat to open the reaction picker on this bubble (the pill or the menu's React).
    /// </summary>
    public event EventHandler? ReactRequested;

    /// <summary>
    /// Asks the chat to start a reply to this bubble (the menu's Reply).
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
    /// Opens the bubble menu (the Application key or Shift + F10 on the bubble's row).
    /// </summary>
    public void ShowMenu() => Menu.ShowAt(Back);

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
    /// Gives the bubble's left padding: the avatar lane's width when it has one, so the name, pill and time line up with the
    /// bubble's back.
    /// </summary>
    /// <param name="hasLane">True for a received bubble in a group chat.</param>
    /// <returns>The padding.</returns>
    public static Thickness LanePadding(bool hasLane) => new(LaneWidth(hasLane), 0, 0, 0);

    /// <summary>
    /// Gives the back's row a negative left margin of the lane's width, so the lane (with the avatar) sits in the padding,
    /// beside the back.
    /// </summary>
    /// <param name="hasLane">True for a received bubble in a group chat.</param>
    /// <returns>The margin.</returns>
    public static Thickness LaneOverhang(bool hasLane) => new(-LaneWidth(hasLane), 0, 0, 0);

    /// <summary>
    /// Fades a bubble that is still being sent.
    /// </summary>
    /// <param name="isSending">True while sending.</param>
    /// <returns>The opacity.</returns>
    public static double SendingOpacity(bool isSending) => isSending ? SendingFade : 1;

    /// <summary>
    /// Caps the bubble at 70% of the chat's width (560 px at most) before it is measured, so it is the right width from its
    /// first layout and short messages stay narrow.
    /// </summary>
    /// <param name="availableSize">The row's width and height.</param>
    /// <returns>The size the bubble wants.</returns>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        if (double.IsFinite(availableSize.Width))
        {
            double max_width = Math.Min(MaxBubbleWidth, Math.Floor(availableSize.Width * MaxWidthShare));
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

    // A Recycled Container Gets Another Message: Follow Its Changes Instead, And Put It On Its Side
    private static void OnBubbleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MessageBubble)d;

        if (e.OldValue is MessageBubbleViewModel old_bubble)
        {
            old_bubble.PropertyChanged -= view.OnBubblePropertyChanged;
        }

        if (e.NewValue is MessageBubbleViewModel new_bubble)
        {
            new_bubble.PropertyChanged += view.OnBubblePropertyChanged;
        }

        view.ShowStates();
        view.ShowPreviewImage();
    }

    // An Echo Can Make A Sending Bubble RCS, My Reaction Comes And Goes, And A Preview Card Arrives
    private void OnBubblePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowStates();
        ShowPreviewImage();
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

    private void OnBackTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_previewTapped)
        {
            _previewTapped = false;
            return;
        }

        Bubble?.ToggleTime();
    }

    // Opens The Link In The Default Browser
    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (Bubble?.Preview is { } preview)
        {
            _ = Launcher.LaunchUriAsync(preview.Url);
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

    private void OnReactClick(object sender, RoutedEventArgs e) => ReactRequested?.Invoke(this, EventArgs.Empty);

    private void OnReplyClick(object sender, RoutedEventArgs e) => ReplyRequested?.Invoke(this, EventArgs.Empty);
}

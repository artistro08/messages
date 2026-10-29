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
/// One message bubble: sent ones on the right (RCS in the accent color, SMS in a shade of it), received ones on the left
/// in the card color.
/// </summary>
/// <remarks>
/// <para>
/// The text carries the AutomationId <c>Bubble.{id}</c>, or <c>Bubble.{tmpId}</c> until the phone has echoed a sent
/// message. A failed bubble shows the "Not sent" link (<c>Bubble.{id}.Retry</c>) in place of the time; clicking it
/// raises <see cref="RetryRequested"/> for the chat to send it again.
/// </para>
/// <para>
/// The time (<c>Bubble.{id}.Time</c>) is hidden until the bubble is clicked, and a second click hides it. The click is
/// caught even when it lands on the text, whose selection marks the pointer handled. The chat pane does the same for
/// Enter and Space on a focused bubble.
/// </para>
/// <para>
/// The side is a visual state so its brushes stay theme resources and follow the window's light or dark theme. So is
/// the reaction pill's accent border for my reaction. Both follow the bubble's changes (an echo turns a sending bubble
/// into the sent one).
/// </para>
/// <para>
/// The reply quote shows above the text (<c>Quote.{id}</c>), the reaction pill (<c>Reactions.{id}</c>) over the bottom
/// corner and the status line (<c>Status.{id}</c>) under it. A link's preview card (<c>Preview.{id}</c>), named by its
/// title, shows under the text once fetched; clicking it opens the link in the default browser. The pill and the menu's React raise
/// <see cref="ReactRequested"/>, and the menu's Reply raises <see cref="ReplyRequested"/>, for the chat to act on.
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
    /// Shows the time once the bubble was clicked, unless the retry link takes its place.
    /// </summary>
    /// <param name="isTimeShown">True once clicked.</param>
    /// <param name="isFailed">True when the send failed.</param>
    /// <returns>The time's visibility.</returns>
    public static Visibility TimeVisibility(bool isTimeShown, bool isFailed) =>
        isTimeShown && !isFailed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Fades a bubble that is still being sent.
    /// </summary>
    /// <param name="isSending">True while sending.</param>
    /// <returns>The opacity.</returns>
    public static double SendingOpacity(bool isSending) => isSending ? SendingFade : 1;

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

    // Copies The Selected Text, Or The Whole Text When Nothing Is Selected, As The Text's Own Copy Did
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string text = Body.SelectedText.Length > 0 ? Body.SelectedText : Body.Text;
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

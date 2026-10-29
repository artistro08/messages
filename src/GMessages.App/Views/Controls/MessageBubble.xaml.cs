using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GMessages.App.Views.Controls;

/// <summary>
/// One message bubble: sent ones on the right in the accent color, received ones on the left in the card color.
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
/// The side is a visual state so its brushes stay theme resources and follow the window's light or dark theme.
/// </para>
/// </remarks>
public sealed partial class MessageBubble : UserControl
{
    /// <summary>Identifies the <see cref="Bubble"/> property.</summary>
    public static readonly DependencyProperty BubbleProperty =
        DependencyProperty.Register(nameof(Bubble), typeof(MessageBubbleViewModel), typeof(MessageBubble), new PropertyMetadata(null, OnBubbleChanged));

    // A Bubble Still Being Sent Shows Faded
    private const double SendingFade = 0.6;

    /// <summary>
    /// Creates the bubble.
    /// </summary>
    public MessageBubble()
    {
        InitializeComponent();

        // Behavior
        Back.AddHandler(TappedEvent, new TappedEventHandler(OnBackTapped), handledEventsToo: true);
    }

    /// <summary>
    /// Asks the chat to send this failed bubble again.
    /// </summary>
    public event EventHandler? RetryRequested;

    /// <summary>Gets the "Not sent" link text.</summary>
    public static string NotSentText => ChatCopy.NotSentRetry;

    /// <summary>Gets or sets the message the bubble shows.</summary>
    public MessageBubbleViewModel? Bubble
    {
        get => (MessageBubbleViewModel?)GetValue(BubbleProperty);
        set => SetValue(BubbleProperty, value);
    }

    // =========================================================================
    // X:BIND HELPERS
    // =========================================================================

    /// <summary>
    /// Gives the text's AutomationId: the message ID, or the temporary ID until the phone has reported the message.
    /// </summary>
    /// <param name="id">The message ID, or empty.</param>
    /// <param name="tmpId">The temporary ID, or empty.</param>
    /// <returns>The AutomationId.</returns>
    public static string AutomationIdOf(string id, string tmpId) => "Bubble." + (id.Length > 0 ? id : tmpId);

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

    // A Recycled Container Gets Another Message: Put It On Its Side
    private static void OnBubbleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MessageBubble)d;
        bool sent = view.Bubble?.FromMe == true;
        VisualStateManager.GoToState(view, sent ? "Sent" : "Received", false);
    }

    private void OnBackTapped(object sender, TappedRoutedEventArgs e) => Bubble?.ToggleTime();

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);
}

using System.ComponentModel;
using System.Globalization;
using GMessages.Presentation.Connection;
using GMessages.Protocol.Http;
using GMessages.Protocol.Pairing;

namespace GMessages.Presentation.Pairing;

/// <summary>
/// Specifies the step of the pairing page in the sign-in and pairing flow.
/// </summary>
public enum PairingStep
{
    /// <summary>The page shows the Google sign-in.</summary>
    SigningIn,

    /// <summary>The user signed in, and the page waits for the phone to send the emoji.</summary>
    WaitingForPhone,

    /// <summary>The page shows the emoji and waits for the user to tap it on the phone.</summary>
    ShowingEmoji,

    /// <summary>The pairing failed. <see cref="PairingViewModel.Message"/> gives the reason.</summary>
    Failed,

    /// <summary>The phone is paired and connected. The main window then shows the chats.</summary>
    Done,
}

/// <summary>
/// Holds the state of the pairing page: the sign-in, the emoji to tap, and a plain-English message for a failure.
/// </summary>
/// <remarks>
/// <para>
/// The window calls <see cref="OnCookiesCapturedAsync"/> on the UI thread when it captures the sign-in cookies. Google
/// refuses cookies that are a few hours old, so the pairing starts at once. Every later change goes through
/// <c>post</c>, so <see cref="PropertyChanged"/> always occurs on the UI thread. The later changes are the emoji from
/// the thread of the client, Done and Failed.
/// </para>
/// <para>
/// Each change raises <see cref="PropertyChanged"/> once with an empty property name. That name tells <c>x:Bind</c> to
/// read every property again.
/// </para>
/// </remarks>
public sealed class PairingViewModel : INotifyPropertyChanged
{
    // The owner wrote these messages. They must keep the exact words.
    private const string WrongEmojiText  = "That wasn't the matching emoji. Try again.";
    private const string CanceledText    = "Pairing on phone was cancelled";
    private const string DeclinedText    = "Pairing was declined on your phone.";
    private const string TimeoutText     = "Your phone didn't respond in time. Make sure Google Messages is open on your phone, then try again.";
    private const string NoPhoneText     = "No phone with Google Messages was found on this Google account.";
    private const string NotAcceptedText = "Google didn't accept the sign-in. Try again.";
    private const string GenericText     = "Something went wrong while pairing. Try again.";

    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    private readonly ConnectionController _controller;
    private readonly Action<Action> _post;

    private CancellationTokenSource? _cts;

    /// <summary>
    /// Creates the view model at the <see cref="PairingStep.SigningIn"/> step.
    /// </summary>
    /// <param name="controller">Pairs the phone and gives the signed-out reason.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PairingViewModel(ConnectionController controller, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(post);

        _controller     = controller;
        _post           = post;
        SignedOutReason = controller.SignedOutReason;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the current step.</summary>
    public PairingStep Step { get; private set; } = PairingStep.SigningIn;

    /// <summary>Gets the emoji to tap on the phone, or null before the phone answers.</summary>
    public string? Emoji { get; private set; }

    /// <summary>
    /// Gives the file name of the bundled Noto Color Emoji image for a pairing emoji. The image makes the emoji look as
    /// messages.google.com and the phone draw it.
    /// </summary>
    /// <remarks>
    /// The images are in <c>Assets/Emoji</c> of the app, with the Noto names. Each code point is lower-case hex with at
    /// least 4 digits, and "_" joins the code points. The name leaves out VS16.
    /// </remarks>
    /// <param name="emoji">The emoji.</param>
    /// <returns>The file name, such as <c>emoji_u1f62e_200d_1f4a8.png</c>.</returns>
    public static string EmojiFileName(string emoji)
    {
        ArgumentException.ThrowIfNullOrEmpty(emoji);

        IEnumerable<string> points = emoji.EnumerateRunes().Where(r => r.Value != 0xFE0F).Select(r => r.Value.ToString("x4", CultureInfo.InvariantCulture));
        return "emoji_u" + string.Join('_', points) + ".png";
    }

    /// <summary>Gets the plain-English failure message, or null when nothing failed.</summary>
    public string? Message { get; private set; }

    /// <summary>
    /// Gets the reason that Google signed the user out, or null for a first pairing. The page shows the reason above
    /// the sign-in.
    /// </summary>
    public string? SignedOutReason { get; }

    /// <summary>Gets whether the sign-in page shows.</summary>
    public bool IsSigningIn => Step == PairingStep.SigningIn;

    /// <summary>Gets whether the window waits for the phone to send the emoji.</summary>
    public bool IsWaitingForPhone => Step == PairingStep.WaitingForPhone;

    /// <summary>Gets whether the emoji shows.</summary>
    public bool IsShowingEmoji => Step == PairingStep.ShowingEmoji;

    /// <summary>Gets whether the failure message and Try again show.</summary>
    public bool IsFailed => Step == PairingStep.Failed;

    /// <summary>Gets whether the signed-out reason shows. The reason shows only above the sign-in.</summary>
    public bool ShowSignedOutReason => IsSigningIn && SignedOutReason is not null;

    /// <summary>
    /// Pairs with the captured sign-in cookies. The page shows the emoji when the phone answers, then Done or Failed.
    /// </summary>
    /// <remarks>
    /// Call this method on the UI thread. Only the first call at the <see cref="PairingStep.SigningIn"/> step pairs.
    /// Later calls do nothing before <see cref="TryAgain"/>. A <see cref="Cancel"/> ends the attempt and shows no
    /// failure. An unpair during the pairing also cancels the pairing. When the controller then reports
    /// <see cref="ConnectionState.NotPaired"/>, the window goes back to <see cref="PairingStep.SigningIn"/> and shows
    /// no failure. Every other error gives <see cref="PairingStep.Failed"/> with the text from
    /// <see cref="MessageFor"/>.
    /// </remarks>
    /// <param name="cookies">The Google sign-in cookies.</param>
    /// <returns>A task that completes when the attempt has ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cookies"/> is null.</exception>
    public async Task OnCookiesCapturedAsync(IReadOnlyDictionary<string, string> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        if (Step != PairingStep.SigningIn)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts          = cts;
        Update(PairingStep.WaitingForPhone, null, null);

        try
        {
            await _controller.PairAsync(cookies, emoji => _post(() => ShowEmoji(emoji)), cts.Token).ConfigureAwait(false);
            _post(() => Update(PairingStep.Done, Emoji, null));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The window cancels the attempt when the window closes, so the page shows nothing.
        }
        catch (OperationCanceledException) when (_controller.State == ConnectionState.NotPaired)
        {
            // An unpair cancels the pairing here. The page shows a new sign-in and no failure.
            _post(() => Update(PairingStep.SigningIn, null, null));
        }
#pragma warning disable CA1031 // Each failure becomes a message, so no caller must handle an error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            string message = MessageFor(ex);
            _post(() => Update(PairingStep.Failed, null, message));
        }
        finally
        {
            Interlocked.CompareExchange(ref _cts, null, cts);
        }
    }

    /// <summary>
    /// Shows the generic failure when the sign-in page cannot load. A missing WebView2 runtime is an example.
    /// </summary>
    /// <remarks>Call this method on the UI thread.</remarks>
    public void SignInFailed() => Update(PairingStep.Failed, null, GenericText);

    /// <summary>
    /// Goes back to the sign-in after a failure.
    /// </summary>
    public void TryAgain() => Update(PairingStep.SigningIn, null, null);

    /// <summary>
    /// Cancels a running pairing when the window closes. With no running pairing, the method does nothing.
    /// </summary>
    public void Cancel()
    {
        try
        {
            Volatile.Read(ref _cts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt ends by itself at the same time.
        }
    }

    /// <summary>
    /// Gives the plain-English text for a pairing error.
    /// </summary>
    /// <remarks>
    /// A <see cref="PairingException"/> maps by its <see cref="PairingFailure"/>. A <see cref="GmRequestException"/>
    /// with 401 or 403 means that Google refused the sign-in cookies. Any other error gives the generic message.
    /// </remarks>
    /// <param name="error">The error from <see cref="ConnectionController.PairAsync"/>.</param>
    /// <returns>The text to show.</returns>
    public static string MessageFor(Exception error) => error switch
    {
        PairingException { Failure: PairingFailure.IncorrectEmoji }                                 => WrongEmojiText,
        PairingException { Failure: PairingFailure.Canceled }                                       => CanceledText,
        PairingException { Failure: PairingFailure.NotMe }                                          => DeclinedText,
        PairingException { Failure: PairingFailure.Timeout or PairingFailure.InitTimeout }          => TimeoutText,
        PairingException { Failure: PairingFailure.NoDevicesFound or PairingFailure.PhoneNotFound } => NoPhoneText,
        PairingException { Failure: PairingFailure.NoCookies }                                      => NotAcceptedText,
        GmRequestException { StatusCode: 401 or 403 }                                               => NotAcceptedText,
        _                                                                                           => GenericText,
    };

    // The method ignores a late emoji that comes after a cancel or a failure.
    private void ShowEmoji(string emoji)
    {
        if (Step != PairingStep.WaitingForPhone)
        {
            return;
        }

        Update(PairingStep.ShowingEmoji, emoji, null);
    }

    private void Update(PairingStep step, string? emoji, string? message)
    {
        Step    = step;
        Emoji   = emoji;
        Message = message;
        PropertyChanged?.Invoke(this, AllChanged);
    }
}

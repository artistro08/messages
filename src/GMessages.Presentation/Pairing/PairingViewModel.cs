using System.ComponentModel;
using GMessages.Presentation.Connection;
using GMessages.Protocol.Http;
using GMessages.Protocol.Pairing;

namespace GMessages.Presentation.Pairing;

/// <summary>
/// Where the pairing window is in the sign-in and pairing flow.
/// </summary>
public enum PairingStep
{
    /// <summary>The Google sign-in page is showing.</summary>
    SigningIn,

    /// <summary>Signed in; waiting for the phone to answer before the emoji is known.</summary>
    WaitingForPhone,

    /// <summary>The emoji is showing; waiting for the user to tap it on the phone.</summary>
    ShowingEmoji,

    /// <summary>Pairing failed; <see cref="PairingViewModel.Message"/> says why.</summary>
    Failed,

    /// <summary>Paired and connected; the window closes.</summary>
    Done,
}

/// <summary>
/// The pairing window's state: sign-in, the emoji to tap, and a plain-English message when pairing fails.
/// </summary>
/// <remarks>
/// <para>
/// The window calls <see cref="OnCookiesCapturedAsync"/> on the UI thread as soon as the sign-in cookies are captured
/// (Google rejects cookies that are a few hours old, so pairing starts right away). Every later change (the emoji from
/// the client's thread, Done, Failed) goes through <c>post</c>, so <see cref="PropertyChanged"/> is always raised on
/// the UI thread.
/// </para>
/// <para>
/// Each change raises <see cref="PropertyChanged"/> once with an empty property name, which tells <c>x:Bind</c> to
/// re-read every property.
/// </para>
/// </remarks>
public sealed class PairingViewModel : INotifyPropertyChanged
{
    // Failure Copy (Owner Wording, Verbatim)
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

    // The Running Attempt's Cancel Source, Or Null
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Creates the view model at <see cref="PairingStep.SigningIn"/>.
    /// </summary>
    /// <param name="controller">Does the pairing; also gives the signed-out reason.</param>
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

    /// <summary>Gets the plain-English failure message, or null when nothing failed.</summary>
    public string? Message { get; private set; }

    /// <summary>Gets why Google signed the user out, shown above the sign-in; null for a first pairing.</summary>
    public string? SignedOutReason { get; }

    /// <summary>Gets whether the sign-in page shows.</summary>
    public bool IsSigningIn => Step == PairingStep.SigningIn;

    /// <summary>Gets whether the window waits for the phone before the emoji is known.</summary>
    public bool IsWaitingForPhone => Step == PairingStep.WaitingForPhone;

    /// <summary>Gets whether the emoji shows.</summary>
    public bool IsShowingEmoji => Step == PairingStep.ShowingEmoji;

    /// <summary>Gets whether the failure message and Try again show.</summary>
    public bool IsFailed => Step == PairingStep.Failed;

    /// <summary>Gets whether the signed-out reason shows (only above the sign-in).</summary>
    public bool ShowSignedOutReason => IsSigningIn && SignedOutReason is not null;

    // =========================================================================
    // PAIRING
    // =========================================================================

    /// <summary>
    /// Pairs with the captured sign-in cookies: shows the emoji when the phone answers, then Done or Failed.
    /// </summary>
    /// <remarks>
    /// Call on the UI thread. Only the first call while <see cref="PairingStep.SigningIn"/> pairs; later calls do
    /// nothing until <see cref="TryAgain"/>. A <see cref="Cancel"/> ends the attempt without showing a failure.
    /// Every other error becomes <see cref="PairingStep.Failed"/> with <see cref="MessageFor"/>'s text.
    /// </remarks>
    /// <param name="cookies">The Google sign-in cookies.</param>
    /// <returns>A task that completes when the attempt has ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cookies"/> is null.</exception>
    public async Task OnCookiesCapturedAsync(IReadOnlyDictionary<string, string> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        // Capture Once Per Attempt
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
            // Canceled Here: The Window Is Closing, So Show Nothing
        }
#pragma warning disable CA1031 // Any Failure Becomes A Message; Nothing Is Left For A Caller To Handle
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
    /// Shows the generic failure when the sign-in page itself can't load (for example, no WebView2 runtime).
    /// </summary>
    /// <remarks>Call on the UI thread.</remarks>
    public void SignInFailed() => Update(PairingStep.Failed, null, GenericText);

    /// <summary>
    /// Goes back to the sign-in after a failure.
    /// </summary>
    public void TryAgain() => Update(PairingStep.SigningIn, null, null);

    /// <summary>
    /// Cancels a running pairing (the window is closing or Cancel was clicked). Does nothing when none runs.
    /// </summary>
    public void Cancel()
    {
        try
        {
            Volatile.Read(ref _cts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The Attempt Just Ended On Its Own
        }
    }

    /// <summary>
    /// Gives the plain-English text for a pairing error.
    /// </summary>
    /// <remarks>
    /// <see cref="PairingException"/> maps by <see cref="PairingFailure"/>; a <see cref="GmRequestException"/> with
    /// 401 or 403 means Google refused the sign-in cookies. Anything else is the generic message.
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

    // =========================================================================
    // STATE
    // =========================================================================

    // A Late Emoji (After A Cancel Or Failure) Is Ignored
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

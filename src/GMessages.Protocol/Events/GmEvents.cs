using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Events;

// Ported from the event types in
// https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/events/ready.go
// and the protobuf values event_handler.go passes to the event handler. Every event is raised as a plain object
// through an Action<object> event; subscribers switch on the record type.

/// <summary>
/// A message arrived from the phone, or an existing message changed.
/// </summary>
/// <remarks>
/// Ported from <c>WrappedMessage</c> in event_handler.go. <see cref="IsOld"/> is true for messages the server
/// replays while the client catches up after connecting; the app uses it to skip notifications for them.
/// </remarks>
/// <param name="Message">The message.</param>
/// <param name="IsOld">True when the message was part of the catch-up batch.</param>
public sealed record MessageReceived(Message Message, bool IsOld);

/// <summary>
/// A conversation was created or changed.
/// </summary>
/// <remarks>Raised only for live updates; Go drops conversation updates from the catch-up batch.</remarks>
/// <param name="Conversation">The conversation.</param>
public sealed record ConversationUpdated(Conversation Conversation);

/// <summary>
/// Someone started or stopped typing.
/// </summary>
/// <remarks>Raised only for live updates.</remarks>
/// <param name="Data">The typing state.</param>
public sealed record TypingChanged(TypingData Data);

/// <summary>
/// The phone's settings changed.
/// </summary>
/// <remarks>Raised for live and catch-up updates alike, as in Go.</remarks>
/// <param name="Settings">The new settings.</param>
public sealed record SettingsChanged(Settings Settings);

/// <summary>
/// The phone sent a status alert (battery, data connection, browser active, and so on).
/// </summary>
/// <remarks>Raised only for live updates.</remarks>
/// <param name="Alert">The alert.</param>
public sealed record UserAlert(UserAlertEvent Alert);

/// <summary>
/// The Google account on the phone changed.
/// </summary>
/// <remarks>
/// Ported from <c>events.AccountChange</c>. <see cref="IsFake"/> is true when it came from the second encrypted field
/// at startup; Go raises that "fake" one so the app re-checks the account.
/// </remarks>
/// <param name="Change">The account change.</param>
/// <param name="IsFake">True for the startup notice from the second encrypted field.</param>
public sealed record AccountChanged(AccountChangeOrSomethingEvent Change, bool IsFake);

/// <summary>
/// Listening stopped for good: the credentials were refused.
/// </summary>
/// <remarks>Ported from <c>events.ListenFatalError</c>. The long-poll loop has exited; the user must sign in again.</remarks>
/// <param name="Error">What went wrong.</param>
public sealed record ListenFatalError(Exception Error);

/// <summary>
/// Listening failed, and the loop will retry after a delay.
/// </summary>
/// <remarks>Ported from <c>events.ListenTemporaryError</c>.</remarks>
/// <param name="Error">What went wrong.</param>
public sealed record ListenTemporaryError(Exception Error);

/// <summary>
/// Listening works again after one or more errors (and once on the first connect, as in Go).
/// </summary>
/// <remarks>Ported from <c>events.ListenRecovered</c>.</remarks>
public sealed record ListenRecovered;

/// <summary>
/// The phone has stopped answering pings.
/// </summary>
/// <remarks>Ported from <c>events.PhoneNotResponding</c>.</remarks>
public sealed record PhoneNotResponding;

/// <summary>
/// The phone answers again after <see cref="PhoneNotResponding"/> or failed pings.
/// </summary>
/// <remarks>Ported from <c>events.PhoneRespondingAgain</c>.</remarks>
public sealed record PhoneRespondingAgain;

/// <summary>
/// A ping to the phone could not be sent.
/// </summary>
/// <remarks>Ported from <c>events.PingFailed</c>.</remarks>
/// <param name="Error">What went wrong.</param>
/// <param name="ErrorCount">How many sends failed in a row.</param>
public sealed record PingFailed(Exception Error, int ErrorCount);

/// <summary>
/// No data came from the phone for a while, so an extra update request was sent.
/// </summary>
/// <remarks>Ported from <c>events.NoDataReceived</c>.</remarks>
public sealed record NoDataReceived;

/// <summary>
/// The Google account was signed out on the phone.
/// </summary>
/// <remarks>Ported from <c>events.GaiaLoggedOut</c>.</remarks>
public sealed record GaiaLoggedOut;

/// <summary>
/// The Tachyon auth token was refreshed; the session should be saved.
/// </summary>
/// <remarks>Ported from <c>events.AuthTokenRefreshed</c>.</remarks>
public sealed record AuthTokenRefreshed;

/// <summary>
/// Pairing with the phone finished.
/// </summary>
/// <remarks>Ported from <c>events.PairSuccessful</c>, which Go's <c>DoGaiaPairing</c> raises before reconnecting.</remarks>
/// <param name="PhoneId">The phone ID: the phone's source ID, a slash, and its timestamp (or unknown int).</param>
public sealed record PairSuccessful(string PhoneId);

/// <summary>
/// The startup skip count was still above zero when the update request went out, so it may not take effect.
/// </summary>
/// <remarks>Ported from <c>events.HackySetActiveMayFail</c>, raised by <c>postConnect</c>.</remarks>
public sealed record HackySetActiveMayFail;

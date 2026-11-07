using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Events;

// Ported from the event types in
// https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/events/ready.go
// and the protobuf values that event_handler.go gives to the event handler. Each event goes out as a plain object
// through an Action<object> event. Subscribers switch on the record type.

/// <summary>
/// A message arrived from the phone, or a known message changed.
/// </summary>
/// <remarks>
/// Ported from <c>WrappedMessage</c> in event_handler.go. <see cref="IsOld"/> is true for the messages that the server
/// replays while the client catches up after a connect. The app uses it to skip notifications for them.
/// </remarks>
/// <param name="Message">The message.</param>
/// <param name="IsOld">True when the message is part of the catch-up batch.</param>
public sealed record MessageReceived(Message Message, bool IsOld);

/// <summary>
/// A conversation is new or changed.
/// </summary>
/// <remarks>Occurs only for live updates. Go drops the conversation updates of the catch-up batch.</remarks>
/// <param name="Conversation">The conversation.</param>
public sealed record ConversationUpdated(Conversation Conversation);

/// <summary>
/// A person started or stopped typing.
/// </summary>
/// <remarks>Occurs only for live updates.</remarks>
/// <param name="Data">The typing state.</param>
public sealed record TypingChanged(TypingData Data);

/// <summary>
/// The settings of the phone changed.
/// </summary>
/// <remarks>Occurs for live updates and catch-up updates, as in Go.</remarks>
/// <param name="Settings">The new settings.</param>
public sealed record SettingsChanged(Settings Settings);

/// <summary>
/// The phone sent a status alert, such as battery, data connection or browser active.
/// </summary>
/// <remarks>Occurs only for live updates.</remarks>
/// <param name="Alert">The alert.</param>
public sealed record UserAlert(UserAlertEvent Alert);

/// <summary>
/// The Google account on the phone changed.
/// </summary>
/// <remarks>
/// Ported from <c>events.AccountChange</c>. <see cref="IsFake"/> is true for the notice from the second encrypted
/// field at startup. Go raises that "fake" notice, so that the app checks the account again.
/// </remarks>
/// <param name="Change">The account change.</param>
/// <param name="IsFake">True for the startup notice from the second encrypted field.</param>
public sealed record AccountChanged(AccountChangeOrSomethingEvent Change, bool IsFake);

/// <summary>
/// Listening stopped permanently, because the server refused the credentials.
/// </summary>
/// <remarks>Ported from <c>events.ListenFatalError</c>. The long-poll loop ended. The user must sign in again.</remarks>
/// <param name="Error">The cause.</param>
public sealed record ListenFatalError(Exception Error);

/// <summary>
/// Listening failed, and the loop tries again after a delay.
/// </summary>
/// <remarks>Ported from <c>events.ListenTemporaryError</c>.</remarks>
/// <param name="Error">The cause.</param>
public sealed record ListenTemporaryError(Exception Error);

/// <summary>
/// Listening works again after 1 or more errors. As in Go, it also occurs 1 time on the first connect.
/// </summary>
/// <remarks>Ported from <c>events.ListenRecovered</c>.</remarks>
public sealed record ListenRecovered;

/// <summary>
/// The phone stopped answering pings.
/// </summary>
/// <remarks>Ported from <c>events.PhoneNotResponding</c>.</remarks>
public sealed record PhoneNotResponding;

/// <summary>
/// The phone answers again after <see cref="PhoneNotResponding"/> or failed pings.
/// </summary>
/// <remarks>Ported from <c>events.PhoneRespondingAgain</c>.</remarks>
public sealed record PhoneRespondingAgain;

/// <summary>
/// A ping to the phone did not go out.
/// </summary>
/// <remarks>Ported from <c>events.PingFailed</c>.</remarks>
/// <param name="Error">The cause.</param>
/// <param name="ErrorCount">How many sends failed in a row.</param>
public sealed record PingFailed(Exception Error, int ErrorCount);

/// <summary>
/// No data came from the phone for some time, so an extra update request went out.
/// </summary>
/// <remarks>Ported from <c>events.NoDataReceived</c>.</remarks>
public sealed record NoDataReceived;

/// <summary>
/// The Google account signed out on the phone.
/// </summary>
/// <remarks>Ported from <c>events.GaiaLoggedOut</c>.</remarks>
public sealed record GaiaLoggedOut;

/// <summary>
/// The Tachyon auth token is new. The app must save the session.
/// </summary>
/// <remarks>Ported from <c>events.AuthTokenRefreshed</c>.</remarks>
public sealed record AuthTokenRefreshed;

/// <summary>
/// The pairing with the phone finished.
/// </summary>
/// <remarks>Ported from <c>events.PairSuccessful</c>. Go's <c>DoGaiaPairing</c> raises it before it reconnects.</remarks>
/// <param name="PhoneId">The phone ID: the source ID of the phone, a slash, and its timestamp or its unknown int.</param>
public sealed record PairSuccessful(string PhoneId);

/// <summary>
/// The startup skip count was above 0 when the update request went out. The request can fail to take effect.
/// </summary>
/// <remarks>Ported from <c>events.HackySetActiveMayFail</c>, which <c>postConnect</c> raises.</remarks>
public sealed record HackySetActiveMayFail;

/// <summary>
/// The session save to disk failed. The app uses it to warn the user that a restart can require a new pairing.
/// </summary>
/// <remarks>
/// C#-only, because Go keeps the session in the bridge database. It occurs for each failed save, also for the save
/// right after pairing. It carries only the type name of the exception, never its message. The message can hold a
/// path or session data.
/// </remarks>
/// <param name="ErrorType">The type name of the exception, such as <c>IOException</c>.</param>
public sealed record SessionSaveFailed(string ErrorType);

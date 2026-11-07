namespace GMessages.Protocol.Pairing;

/// <summary>
/// Why a Google account pairing failed.
/// </summary>
/// <remarks>
/// Ported from the error values in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go">pair_google.go</see>.
/// Go has no separate "not me" error, because it wraps <c>ErrPairingCancelled</c>. Go also has no "unknown" error,
/// because it formats the code. Here, both have their own value, so that the app can show a plain message for each.
/// </remarks>
public enum PairingFailure
{
    /// <summary>The account has no cookies. Ported from <c>ErrNoCookies</c>.</summary>
    NoCookies,

    /// <summary>No phone on the account can pair. Ported from <c>ErrNoDevicesFound</c>.</summary>
    NoDevicesFound,

    /// <summary>The user tapped the wrong emoji on the phone. Ported from <c>ErrIncorrectEmoji</c>.</summary>
    IncorrectEmoji,

    /// <summary>The user canceled on the phone. Ported from <c>ErrPairingCancelled</c>.</summary>
    Canceled,

    /// <summary>The user chose "this is not me" on the phone. Go wraps <c>ErrPairingCancelled</c> for it.</summary>
    NotMe,

    /// <summary>The phone or the server stopped the wait. Ported from <c>ErrPairingTimeout</c>.</summary>
    Timeout,

    /// <summary>The phone did not answer the first step in time. Ported from <c>ErrPairingInitTimeout</c>.</summary>
    InitTimeout,

    /// <summary>The phone of the earlier pairing is no longer on the account. Ported from <c>ErrPhoneNotFound</c>.</summary>
    PhoneNotFound,

    /// <summary>The phone sent an error code that Go does not name.</summary>
    Unknown,
}

/// <summary>
/// The exception that is thrown when a Google account pairing fails for a known reason.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> is the error text of Go, so the logs read the same as the logs of the Go bridge.
/// This type does not wrap network and protocol problems. Those problems keep their own exceptions.
/// </remarks>
/// <param name="failure">Why the pairing failed.</param>
/// <param name="message">The error text of Go.</param>
public sealed class PairingException(PairingFailure failure, string message) : Exception(message)
{
    /// <summary>
    /// Gets why the pairing failed.
    /// </summary>
    public PairingFailure Failure { get; } = failure;
}

namespace GMessages.Protocol.Pairing;

/// <summary>
/// Why a Google account pairing failed.
/// </summary>
/// <remarks>
/// Mirrors the error values in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go">pair_google.go</see>.
/// Go has no separate "not me" error (it wraps <c>ErrPairingCancelled</c>) and no "unknown" one (it formats the
/// code); both get their own value here so the app can show a plain message for each.
/// </remarks>
public enum PairingFailure
{
    /// <summary>The account has no cookies (Go: <c>ErrNoCookies</c>).</summary>
    NoCookies,

    /// <summary>No phone on the account can be paired (Go: <c>ErrNoDevicesFound</c>).</summary>
    NoDevicesFound,

    /// <summary>The user tapped the wrong emoji on the phone (Go: <c>ErrIncorrectEmoji</c>).</summary>
    IncorrectEmoji,

    /// <summary>The user canceled on the phone (Go: <c>ErrPairingCancelled</c>).</summary>
    Canceled,

    /// <summary>The user chose "this is not me" on the phone (Go: <c>ErrPairingCancelled</c>, wrapped).</summary>
    NotMe,

    /// <summary>The phone or server gave up waiting (Go: <c>ErrPairingTimeout</c>).</summary>
    Timeout,

    /// <summary>The phone did not answer the first step in time (Go: <c>ErrPairingInitTimeout</c>).</summary>
    InitTimeout,

    /// <summary>The previously paired phone is no longer on the account (Go: <c>ErrPhoneNotFound</c>).</summary>
    PhoneNotFound,

    /// <summary>The phone reported an error code Go does not name.</summary>
    Unknown,
}

/// <summary>
/// A Google account pairing failed for a known reason.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> is Go's error text, so logs read the same as the Go bridge's. Network and
/// protocol problems are not wrapped in this type; they surface as their own exceptions.
/// </remarks>
/// <param name="failure">Why the pairing failed.</param>
/// <param name="message">Go's error text.</param>
public sealed class PairingException(PairingFailure failure, string message) : Exception(message)
{
    /// <summary>
    /// Gets why the pairing failed.
    /// </summary>
    public PairingFailure Failure { get; } = failure;
}

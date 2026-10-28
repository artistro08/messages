using GMessages.Protocol.Proto;
using Google.Protobuf;

namespace GMessages.Protocol.Rpc;

/// <summary>
/// An incoming message from the long poll, with its inner payload decoded and decrypted.
/// </summary>
/// <remarks>
/// Ported from <c>IncomingRPCMessage</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/event_handler.go">event_handler.go</see>.
/// Which of <see cref="Pair"/>, <see cref="Gaia"/> and <see cref="Message"/> is set depends on the route of
/// <see cref="Raw"/>; the other two stay null. <see cref="DecryptedMessage"/> is only set for data events whose
/// action has a known response type.
/// </remarks>
public sealed class IncomingRpc
{
    /// <summary>Gets the message as it came off the wire.</summary>
    public required IncomingRPCMessage Raw { get; init; }

    /// <summary>Gets or sets whether the message was delivered before the client finished catching up.</summary>
    public bool IsOld { get; set; }

    /// <summary>Gets the decoded data event, or null for other routes.</summary>
    public RPCMessageData? Message { get; init; }

    /// <summary>Gets the decoded Google account pairing event, or null for other routes.</summary>
    public RPCGaiaData? Gaia { get; init; }

    /// <summary>Gets the decoded QR pairing event, or null for other routes.</summary>
    public RPCPairData? Pair { get; init; }

    /// <summary>Gets the decrypted payload bytes, or null when the data event had none.</summary>
    public byte[]? DecryptedData { get; init; }

    /// <summary>Gets the payload decoded as the response type of its action, or null when unknown or not encrypted.</summary>
    public IMessage? DecryptedMessage { get; init; }
}

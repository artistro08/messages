using GMessages.Protocol.Proto;
using Google.Protobuf;

namespace GMessages.Protocol.Rpc;

/// <summary>
/// An incoming message from the long poll, with its inner payload decoded and decrypted.
/// </summary>
/// <remarks>
/// Ported from <c>IncomingRPCMessage</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/event_handler.go">event_handler.go</see>.
/// Only a data event sets <see cref="Message"/>. The client decodes a pairing event only to check it.
/// <see cref="DecryptedMessage"/> is set only for data events with an action that has a known response type.
/// </remarks>
public sealed class IncomingRpc
{
    /// <summary>Gets the message as it came off the wire.</summary>
    public required IncomingRPCMessage Raw { get; init; }

    /// <summary>Gets or sets whether the message came before the client finished the catch-up.</summary>
    public bool IsOld { get; set; }

    /// <summary>Gets the decoded data event, or null for other routes.</summary>
    public RPCMessageData? Message { get; init; }

    /// <summary>Gets the decrypted payload bytes, or null when the data event had none.</summary>
    public byte[]? DecryptedData { get; init; }

    /// <summary>
    /// Gets the payload decoded as the response type of its action, or null when the type is unknown. An event with
    /// no encrypted data gets an empty message of that type, as in Go.
    /// </summary>
    public IMessage? DecryptedMessage { get; init; }
}

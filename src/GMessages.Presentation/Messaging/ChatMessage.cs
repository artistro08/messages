using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// One message as the chat view shows it.
/// </summary>
/// <param name="Id">The message ID.</param>
/// <param name="TmpId">The temporary ID the sender made, or empty.</param>
/// <param name="ConversationId">The conversation it belongs to.</param>
/// <param name="ParticipantId">Who sent it.</param>
/// <param name="Time">When it was sent.</param>
/// <param name="FromMe">True when the owner sent it (from here or from the phone).</param>
/// <param name="Kind">What it shows.</param>
/// <param name="Text">The text (a media message's caption), or empty when there is none.</param>
/// <param name="Status">The phone's delivery status.</param>
public sealed record ChatMessage(
    string Id,
    string TmpId,
    string ConversationId,
    string ParticipantId,
    DateTimeOffset Time,
    bool FromMe,
    MessageKind Kind,
    string Text,
    MessageStatusType Status)
{
    /// <summary>True when the message failed to send or was canceled.</summary>
    public bool IsFailed => Status is MessageStatusType.OutgoingFailedGeneric
        or MessageStatusType.OutgoingFailedEmergencyNumber
        or MessageStatusType.OutgoingCanceled
        or MessageStatusType.OutgoingFailedTooLarge
        or MessageStatusType.OutgoingFailedRecipientLostRcs
        or MessageStatusType.OutgoingFailedNoRetryNoFallback
        or MessageStatusType.OutgoingFailedRecipientDidNotDecrypt
        or MessageStatusType.OutgoingFailedRecipientLostEncryption
        or MessageStatusType.OutgoingFailedRecipientDidNotDecryptNoMoreRetry
        or MessageStatusType.OutgoingFailedRecipientNegativeDelivery
        or MessageStatusType.MessageStatusOutgoingFailedEmergencyProtocolDeterminationMessage
        or MessageStatusType.OutgoingFailedToEncrypt;

    /// <summary>Gets the reactions on the message, one per emoji.</summary>
    public IReadOnlyList<ReactionInfo> Reactions { get; init; } = [];

    /// <summary>Gets the ID of the message this one replies to, or empty.</summary>
    public string ReplyToId { get; init; } = string.Empty;

    /// <summary>Gets whether the message went over RCS (chat) rather than SMS or MMS (text).</summary>
    public bool IsRcs { get; init; }

    /// <summary>Gets the encryption this system line switches the chat to, or null when it says nothing about encryption.</summary>
    public bool? EncryptionChange { get; init; }

    /// <summary>Gets the photo, video, voice message or file the message carries, or null for none.</summary>
    public MediaPart? Media { get; init; }

    /// <summary>
    /// Copies the fields the app needs out of a protocol message.
    /// </summary>
    /// <remarks>
    /// The first media part becomes <see cref="Media"/> and decides the kind (by its format); the first text part is the
    /// text, which on a media message is its caption. Text alone is Text; neither is Other. A status of 200 to 299 is a
    /// system line (Tombstone). Outgoing statuses are 1 to 99, which includes messages sent from the phone.
    /// Message types 1, 2 and 3 are SMS and MMS, anything else RCS (<c>IsSMSOrMMS</c> in the Go connector's
    /// <c>dbmeta.go</c>). Reactions are read as the connector's <c>GetReactions</c> does.
    /// </remarks>
    /// <param name="m">The protocol message.</param>
    /// <returns>The chat record.</returns>
    public static ChatMessage From(Message m)
    {
        ArgumentNullException.ThrowIfNull(m);

        MessageStatusType status = m.MessageStatus?.Status ?? MessageStatusType.StatusUnknown;
        MessageKind kind         = MessageKind.Other;
        string text              = string.Empty;

        // Media Decides The Kind; Text Is The Message, Or The Media's Caption
        MessageInfo? text_part  = m.MessageInfo.FirstOrDefault(i => i.DataCase == MessageInfo.DataOneofCase.MessageContent);
        MessageInfo? media_part = m.MessageInfo.FirstOrDefault(i => i.DataCase == MessageInfo.DataOneofCase.MediaContent);
        MediaPart? media        = media_part is null ? null : MediaPart.From(media_part.MediaContent);

        if (text_part is not null)
        {
            kind = MessageKind.Text;
            text = text_part.MessageContent.Content;
        }

        if (media is not null)
        {
            kind = media.Kind;
        }

        // System Lines Win
        if ((int)status is >= 200 and <= 299)
        {
            kind = MessageKind.Tombstone;
        }

        return new ChatMessage(
            m.MessageID,
            m.TmpID,
            m.ConversationID,
            m.ParticipantID,
            DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp / 1000),
            (int)status is >= 1 and <= 99,
            kind,
            text,
            status)
        {
            Reactions        = ReactionsOf(m),
            ReplyToId        = m.ReplyMessage?.MessageID ?? string.Empty,
            IsRcs            = m.Type is not (1 or 2 or 3),
            EncryptionChange = ChatFormat.EncryptionOf(status),
            Media            = media,
        };
    }

    // Like GetReactions In The Go Connector: Custom Uses Its Text, Emotify Is Skipped, Any Other Type Uses Its Fixed Emoji
    private static List<ReactionInfo> ReactionsOf(Message m)
    {
        List<ReactionInfo> reactions = [];

        foreach (ReactionEntry entry in m.Reactions)
        {
            EmojiType type = entry.Data?.Type ?? EmojiType.ReactionTypeUnspecified;
            string emoji   = type switch
            {
                EmojiType.Emotify => string.Empty,
                EmojiType.Custom  => entry.Data?.Unicode ?? string.Empty,
                _                 => EmojiTypes.Unicode(type),
            };

            if (emoji.Length > 0)
            {
                reactions.Add(new ReactionInfo(emoji, [.. entry.ParticipantIDs]));
            }
        }

        return reactions;
    }
}

using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds 1 message as the chat view shows it.
/// </summary>
/// <param name="TmpId">The temporary ID that the sender made, or empty.</param>
/// <param name="ConversationId">The conversation that the message belongs to.</param>
/// <param name="ParticipantId">The participant who sent the message.</param>
/// <param name="Time">When the message was sent.</param>
/// <param name="FromMe">True when the owner sent the message, from here or from the phone.</param>
/// <param name="Kind">What the message shows.</param>
/// <param name="Text">The text, which is the caption on a media message, or empty when there is none.</param>
/// <param name="Status">The delivery status from the phone.</param>
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
    /// <summary>Gets whether the message failed to send or was canceled.</summary>
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

    /// <summary>
    /// Gets whether the message is a stand-in that the phone shows nowhere. A stand-in is an MMS that the phone still
    /// downloads, with no text or media yet, or a deletion notice.
    /// </summary>
    /// <remarks>
    /// A group MMS first lands, not downloaded, in the one-on-one chat of the sender. After the download, the phone
    /// moves it with the same ID into the group. Or the phone turns it into a reaction, such as an iPhone tapback.
    /// The phone then drops the stand-in and does not say so.
    /// </remarks>
    public bool IsPlaceholder => Status == MessageStatusType.MessageDeleted
        || (Kind == MessageKind.Other && Status is MessageStatusType.IncomingAutoDownloading
            or MessageStatusType.IncomingRetryingAutoDownload
            or MessageStatusType.IncomingAwaitingAutoDownload);

    /// <summary>Gets the reactions on the message, 1 for each emoji.</summary>
    public IReadOnlyList<ReactionInfo> Reactions { get; init; } = [];

    /// <summary>Gets the ID of the message that this message replies to, or empty.</summary>
    public string ReplyToId { get; init; } = string.Empty;

    /// <summary>Gets whether the message went over RCS chat and not over SMS or MMS text.</summary>
    public bool IsRcs { get; init; }

    /// <summary>Gets the encryption state that this system line sets for the chat, or null when it sets none.</summary>
    public bool? EncryptionChange { get; init; }

    /// <summary>Gets the photo, video, voice message or file that the message carries, or null for none.</summary>
    public MediaPart? Media { get; init; }

    /// <summary>
    /// Makes a message of mine that is on its way to the phone. It has no ID yet, and its status is sending.
    /// </summary>
    /// <param name="tmpId">The temporary ID that the send carries.</param>
    /// <param name="conversationId">The conversation that the message goes to.</param>
    /// <param name="outgoingId">The outgoing participant of the conversation, or empty when not known.</param>
    /// <param name="now">The time of the send.</param>
    /// <param name="kind">What the message shows.</param>
    /// <param name="text">The text, or the caption of the media.</param>
    /// <returns>The pending message. The caller sets the reply, the RCS flag and the media with <c>with</c>.</returns>
    public static ChatMessage Pending(string tmpId, string conversationId, string outgoingId, DateTimeOffset now, MessageKind kind, string text) =>
        new(string.Empty, tmpId, conversationId, outgoingId, now, true, kind, text, MessageStatusType.OutgoingSending);

    /// <summary>
    /// Copies the fields that the app needs from a protocol message.
    /// </summary>
    /// <remarks>
    /// The first media part becomes <see cref="Media"/> and its format decides the kind. The first text part is the
    /// text, which is the caption on a media message. Text alone is Text, and no part is Other. A status from 200 to
    /// 299 is a system line, which is Tombstone. Outgoing statuses are 1 to 99, which includes messages sent from the
    /// phone. Message types 1, 2 and 3 are SMS and MMS, and all other types are RCS. <c>IsSMSOrMMS</c> in the
    /// <c>dbmeta.go</c> file of the Go connector decides the same. The reactions follow <c>GetReactions</c> in the
    /// connector.
    /// </remarks>
    /// <param name="m">The protocol message.</param>
    /// <returns>The chat record.</returns>
    public static ChatMessage From(Message m)
    {
        ArgumentNullException.ThrowIfNull(m);

        MessageStatusType status = m.MessageStatus?.Status ?? MessageStatusType.StatusUnknown;
        MessageKind kind         = MessageKind.Other;
        string text              = string.Empty;

        MessageInfo? textPart  = m.MessageInfo.FirstOrDefault(i => i.DataCase == MessageInfo.DataOneofCase.MessageContent);
        MessageInfo? mediaPart = m.MessageInfo.FirstOrDefault(i => i.DataCase == MessageInfo.DataOneofCase.MediaContent);
        MediaPart? media       = mediaPart is null ? null : MediaPart.From(mediaPart.MediaContent) with { ActionMessageId = mediaPart.ActionMessageID };

        if (textPart is not null)
        {
            kind = MessageKind.Text;
            text = textPart.MessageContent.Content;
        }

        if (media is not null)
        {
            kind = media.Kind;
        }

        // A system line wins over the text and the media.
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

    // As GetReactions in the Go connector does: Custom uses its text, Emotify is skipped, and all other types use their
    // fixed emoji.
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

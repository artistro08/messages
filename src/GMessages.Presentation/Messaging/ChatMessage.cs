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
/// <param name="Text">The text, or empty when there is none.</param>
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

    /// <summary>
    /// Copies the fields the app needs out of a protocol message.
    /// </summary>
    /// <remarks>
    /// Kind comes from the first text part, else the first media part (by its format), else Other. A status of
    /// 200 to 299 is a system line (Tombstone). Outgoing statuses are 1 to 99, which includes messages sent from the phone.
    /// </remarks>
    /// <param name="m">The protocol message.</param>
    /// <returns>The chat record.</returns>
    public static ChatMessage From(Message m)
    {
        ArgumentNullException.ThrowIfNull(m);

        MessageStatusType status = m.MessageStatus?.Status ?? MessageStatusType.StatusUnknown;
        MessageKind kind         = MessageKind.Other;
        string text              = string.Empty;

        // Text First, Then Media
        MessageInfo? text_part  = m.MessageInfo.FirstOrDefault(i => i.DataCase == MessageInfo.DataOneofCase.MessageContent);
        MessageInfo? media_part = m.MessageInfo.FirstOrDefault(i => i.DataCase == MessageInfo.DataOneofCase.MediaContent);

        if (text_part is not null)
        {
            kind = MessageKind.Text;
            text = text_part.MessageContent.Content;
        }
        else if (media_part is not null)
        {
            kind = KindOf(media_part.MediaContent.Format);
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
            status);
    }

    // Media Kind From The Format Name
    private static MessageKind KindOf(MediaFormats format)
    {
        string name = format.ToString();

        if (name.StartsWith("Image", StringComparison.Ordinal))
        {
            return MessageKind.Photo;
        }

        if (name.StartsWith("Video", StringComparison.Ordinal))
        {
            return MessageKind.Video;
        }

        return name.StartsWith("Audio", StringComparison.Ordinal) ? MessageKind.Audio : MessageKind.File;
    }
}

using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// The slim version of a conversation the chat list needs.
/// </summary>
/// <param name="Id">The conversation ID.</param>
/// <param name="Name">The conversation name.</param>
/// <param name="IsGroup">True for a group chat.</param>
/// <param name="Status">Where the phone keeps the conversation (active, archived, spam...).</param>
/// <param name="Unread">True when there are unread messages.</param>
/// <param name="Pinned">True when pinned.</param>
/// <param name="LastMessageTime">When the newest message arrived.</param>
/// <param name="Snippet">The text of the newest message.</param>
/// <param name="SnippetFromMe">True when the newest message is the owner's.</param>
/// <param name="AvatarHexColor">The avatar color as hex, or empty.</param>
/// <param name="Participants">Everyone in the conversation.</param>
/// <param name="DefaultOutgoingId">The participant ID messages are sent as.</param>
/// <param name="LatestMessageId">The ID of the newest message.</param>
public sealed record ConversationInfo(
    string Id,
    string Name,
    bool IsGroup,
    ConversationStatus Status,
    bool Unread,
    bool Pinned,
    DateTimeOffset LastMessageTime,
    string Snippet,
    bool SnippetFromMe,
    string AvatarHexColor,
    IReadOnlyList<ParticipantInfo> Participants,
    string DefaultOutgoingId,
    string LatestMessageId)
{
    /// <summary>True for conversations in the main list (not archived, spam or deleted).</summary>
    public bool IsMainView => Status == ConversationStatus.Active;

    /// <summary>
    /// Copies the fields the app needs out of a protocol conversation.
    /// </summary>
    /// <remarks>The phone's timestamps are in microseconds (<c>time.UnixMicro</c> in the Go connector).</remarks>
    /// <param name="c">The protocol conversation.</param>
    /// <returns>The slim record.</returns>
    public static ConversationInfo From(Conversation c)
    {
        ArgumentNullException.ThrowIfNull(c);

        return new ConversationInfo(
            c.ConversationID,
            c.Name,
            c.IsGroupChat,
            c.Status,
            c.Unread,
            c.Pinned,
            DateTimeOffset.FromUnixTimeMilliseconds(c.LastMessageTimestamp / 1000),
            c.LatestMessage?.DisplayContent ?? string.Empty,
            c.LatestMessage?.FromMe == 1,
            c.AvatarHexColor,
            [.. c.Participants.Select(ParticipantInfo.From)],
            c.DefaultOutgoingID,
            c.LatestMessageID);
    }

    /// <summary>
    /// Finds a participant by ID.
    /// </summary>
    /// <param name="id">The participant ID.</param>
    /// <returns>The participant, or null when not in this conversation.</returns>
    public ParticipantInfo? FindParticipant(string id) => Participants.FirstOrDefault(p => p.Id == id);
}

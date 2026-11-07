using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds the slim copy of a conversation that the chat list needs.
/// </summary>
/// <param name="IsGroup">True for a group chat.</param>
/// <param name="Status">Where the phone keeps the conversation: active, archived, spam and so on.</param>
/// <param name="Unread">True when the conversation has unread messages.</param>
/// <param name="LastMessageTime">When the newest message arrived.</param>
/// <param name="Snippet">The text of the newest message.</param>
/// <param name="SnippetFromMe">True when the newest message is from the owner.</param>
/// <param name="AvatarHexColor">The avatar color as hex, or empty.</param>
/// <param name="Participants">All people in the conversation.</param>
/// <param name="DefaultOutgoingId">The participant ID that messages go as.</param>
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
    /// <summary>Gets whether the conversation is in the main list: active and not <see cref="IsEmpty"/>.</summary>
    public bool IsMainView => Status == ConversationStatus.Active && !IsEmpty;

    /// <summary>
    /// Gets whether a one-on-one chat holds no message, only its "conversation created" system line. The phone keeps
    /// the conversation but shows it nowhere, and the app also does not show it.
    /// </summary>
    /// <remarks>
    /// A person not on RCS sends a group MMS. The MMS first lands, not downloaded, in a one-on-one chat that the phone
    /// makes for that person. That chat holds only this line and the stand-in. The phone then moves the message into
    /// the group. The chat stays active, with the line as its newest message. The created line of a new group keeps
    /// the group visible, as on the phone.
    /// </remarks>
    public bool IsEmpty => LatestMessageStatus is MessageStatusType.TombstoneOneOnOneSmsCreated
        or MessageStatusType.TombstoneOneOnOneRcsCreated
        or MessageStatusType.TombstoneEncryptedOneOnOneRcsCreated;

    /// <summary>Gets the phone status of the newest message: its delivery state, its system line, or unknown.</summary>
    public MessageStatusType LatestMessageStatus { get; init; }

    /// <summary>Gets whether the conversation is an RCS chat conversation and not SMS.</summary>
    public bool IsRcs { get; init; }

    /// <summary>
    /// Gets whether the phone says that the chat is end-to-end encrypted, or null when the phone did not say. The flag
    /// comes from field 39 of the conversation.
    /// </summary>
    public bool? IsEncrypted { get; init; }

    /// <summary>
    /// Gets the name that the phone gives the sender of the newest message, or empty. A group snippet shows it first.
    /// </summary>
    public string SnippetSender { get; init; } = string.Empty;

    /// <summary>Gets whether the conversation is muted on the phone, so its messages do not notify.</summary>
    public bool IsMuted { get; init; }

    /// <summary>
    /// Gets the label of the media of the newest message: "Image", "Video", "GIF", "Voice message" or the file name.
    /// The list shows it when the phone sent no snippet text. It is empty when unknown or not media.
    /// </summary>
    /// <remarks>
    /// The phone snippet is empty for media with no caption, so the store fills this from the saved message.
    /// </remarks>
    public string SnippetMedia { get; init; } = string.Empty;

    /// <summary>
    /// Copies the fields that the app needs from a protocol conversation.
    /// </summary>
    /// <remarks>
    /// The timestamps of the phone are in microseconds, as <c>time.UnixMicro</c> in the Go connector reads them.
    /// </remarks>
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
            c.LatestMessageID)
        {
            IsRcs               = c.Type == ConversationType.Rcs,
            IsEncrypted         = c.EncryptionState?.Encrypted,
            SnippetSender       = c.LatestMessage?.DisplayName ?? string.Empty,
            IsMuted             = MutedOf(c),
            LatestMessageStatus = c.LatestMessage?.LatestMessageStatus?.Status ?? MessageStatusType.StatusUnknown,
        };
    }

    /// <summary>
    /// Gets whether the phone says that a conversation is muted.
    /// </summary>
    /// <remarks>
    /// The flag is field 7 of the conversation, which LibGM does not name. In the Plan 5 probe, the phone set it to 1
    /// on mute. The phone dropped it on unmute.
    /// </remarks>
    /// <param name="c">The protocol conversation.</param>
    /// <returns>True when the conversation is muted.</returns>
    internal static bool MutedOf(Conversation c) => c.Muted == ConversationMuteStatus.Mute;

    /// <summary>
    /// Finds a participant by ID.
    /// </summary>
    /// <returns>The participant, or null when the participant is not in this conversation.</returns>
    public ParticipantInfo? FindParticipant(string id) => Participants.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Tells whether a participant ID is the owner: the default outgoing ID, or a participant marked as the owner.
    /// An empty ID is never the owner.
    /// </summary>
    /// <param name="participantId">The participant ID to check.</param>
    /// <returns>True when the ID is the owner in this conversation.</returns>
    public bool IsMine(string participantId) =>
        participantId.Length > 0 && (participantId == DefaultOutgoingId || FindParticipant(participantId)?.IsMe == true);
}

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds 1 emoji on a message and the people who reacted with it.
/// </summary>
/// <param name="Emoji">The emoji.</param>
/// <param name="ParticipantIds">The participant IDs of all people who reacted with it.</param>
public sealed record ReactionInfo(string Emoji, IReadOnlyList<string> ParticipantIds);

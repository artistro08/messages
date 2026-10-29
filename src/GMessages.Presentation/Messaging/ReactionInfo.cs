namespace GMessages.Presentation.Messaging;

/// <summary>
/// One emoji on a message and who reacted with it.
/// </summary>
/// <param name="Emoji">The emoji.</param>
/// <param name="ParticipantIds">The participant IDs of everyone who reacted with it.</param>
public sealed record ReactionInfo(string Emoji, IReadOnlyList<string> ParticipantIds);

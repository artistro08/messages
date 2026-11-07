namespace GMessages.Presentation.Messaging;

/// <summary>
/// Names what a message shows: text, a kind of attachment, or a system line.
/// </summary>
public enum MessageKind
{
    Text,
    Photo,
    Video,
    Audio,
    File,
    Other,
    Tombstone,
}

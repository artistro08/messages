namespace GMessages.Presentation.Main;

/// <summary>
/// The delivery icon under a sent bubble, matching Google Messages' read receipts.
/// </summary>
/// <remarks>
/// Google's icon set (2024 redesign): a single check in a ring for sent, a double check in a ring for delivered and a
/// double check in a filled circle for read; a message still sending shows a clock (the "timer" of Google's help page).
/// </remarks>
public enum MessageStatusIcon
{
    /// <summary>No icon.</summary>
    None,

    /// <summary>Still being sent: a clock.</summary>
    Sending,

    /// <summary>Sent: a single check in a ring.</summary>
    Sent,

    /// <summary>Delivered: a double check in a ring.</summary>
    Delivered,

    /// <summary>Read: a double check in a filled circle.</summary>
    Read,
}

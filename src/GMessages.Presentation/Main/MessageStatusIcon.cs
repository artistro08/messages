namespace GMessages.Presentation.Main;

/// <summary>
/// Names the delivery icon under a sent bubble, as the read receipts of Google Messages show it.
/// </summary>
/// <remarks>
/// The icons follow the 2024 icon set of Google. Sent shows a single check in a ring, and delivered a double check in a
/// ring. Read shows a double check in a filled circle. A message that still sends shows a clock, the "timer" of the
/// help page of Google.
/// </remarks>
public enum MessageStatusIcon
{
    /// <summary>The bubble shows no icon.</summary>
    None,

    /// <summary>The message still sends: a clock.</summary>
    Sending,

    /// <summary>The message went: a single check in a ring.</summary>
    Sent,

    /// <summary>The message arrived: a double check in a ring.</summary>
    Delivered,

    /// <summary>The other side read the message: a double check in a filled circle.</summary>
    Read,
}

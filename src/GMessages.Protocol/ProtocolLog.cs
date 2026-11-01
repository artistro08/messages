using Microsoft.Extensions.Logging;

namespace GMessages.Protocol;

/// <summary>
/// Log messages that several protocol classes share.
/// </summary>
internal static partial class ProtocolLog
{
    /// <summary>
    /// Logs that a subscriber of an event threw; the event is still delivered to the others.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">What the subscriber threw.</param>
    /// <param name="eventType">The event's type name.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "An event subscriber threw for {EventType}")]
    public static partial void LogHandlerFailed(ILogger logger, Exception exception, string eventType);
}

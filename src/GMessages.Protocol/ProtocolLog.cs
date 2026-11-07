using Microsoft.Extensions.Logging;

namespace GMessages.Protocol;

/// <summary>
/// Log messages and the event raise that several protocol classes share.
/// </summary>
internal static partial class ProtocolLog
{
    /// <summary>
    /// Gives an event to every subscriber. A subscriber that throws goes in the log and does not stop the caller.
    /// </summary>
    /// <param name="log">The logger.</param>
    /// <param name="handlers">The subscribers, or null when there are none.</param>
    /// <param name="evt">The event.</param>
    public static void Raise(ILogger log, Action<object>? handlers, object evt)
    {
        try
        {
            handlers?.Invoke(evt);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(log, ex, evt.GetType().Name);
        }
    }

    /// <summary>
    /// Logs that a subscriber of an event threw. That subscriber stops the delivery to the subscribers after it.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">What the subscriber threw.</param>
    /// <param name="eventType">The event's type name.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "An event subscriber threw for {EventType}")]
    public static partial void LogHandlerFailed(ILogger logger, Exception exception, string eventType);
}

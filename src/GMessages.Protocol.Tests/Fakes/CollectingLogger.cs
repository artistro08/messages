using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Tests.Fakes;

/// <summary>
/// An <see cref="ILogger"/> that keeps every formatted line, for tests that check what was logged.
/// </summary>
internal sealed class CollectingLogger : ILogger
{
    private readonly List<string> _messages = [];

    /// <summary>Gets a snapshot of the lines logged so far.</summary>
    public string[] Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_messages)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}

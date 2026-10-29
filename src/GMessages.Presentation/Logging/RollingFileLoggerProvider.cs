using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Logging;

/// <summary>
/// Writes log lines to {DisplayName}.log and keeps the newest files. Ported from Magic Buds.
/// </summary>
/// <remarks>
/// When the current file would pass <c>maxFileBytes</c>, it becomes {DisplayName}.1.log, the
/// older numbered files shift up by one, and anything past <c>maxFiles</c> total is deleted.
/// The file name comes from <see cref="AppInfo.DisplayName"/>. Line format: <c>2026-09-23T15:04:05.123Z [Information] Category: message</c>.
/// </remarks>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private static readonly string BaseName = AppInfo.DisplayName;

    private readonly string _directory;
    private readonly LogLevelSwitch _levelSwitch;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    /// <summary>
    /// Creates the provider and its log folder.
    /// </summary>
    /// <param name="directory">Folder for the log files.</param>
    /// <param name="levelSwitch">Minimum level, changeable at runtime.</param>
    /// <param name="timeProvider">Clock for line timestamps.</param>
    /// <param name="maxFileBytes">Size at which the current file rolls.</param>
    /// <param name="maxFiles">Total files kept, including the current one.</param>
    public RollingFileLoggerProvider(string directory, LogLevelSwitch levelSwitch, TimeProvider timeProvider, long maxFileBytes = 1_048_576, int maxFiles = 5)
    {
        _directory = directory;
        _levelSwitch = levelSwitch;
        _timeProvider = timeProvider;
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// The file being written.
    /// </summary>
    public string CurrentFilePath => Path.Combine(_directory, BaseName + ".log");

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // Every write opens and closes the file, so there's nothing to release.
    }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _levelSwitch.MinimumLevel;

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(_timeProvider.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(category).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }
        line.AppendLine();
        var text = line.ToString();

        lock (_gate)
        {
            try
            {
                RollIfNeeded(Encoding.UTF8.GetByteCount(text));
                File.AppendAllText(CurrentFilePath, text, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private string NumberedPath(int number) => Path.Combine(_directory, $"{BaseName}.{number}.log");

    private void RollIfNeeded(int incomingBytes)
    {
        var current = new FileInfo(CurrentFilePath);
        if (!current.Exists || current.Length + incomingBytes <= _maxFileBytes)
        {
            return;
        }

        var oldest = NumberedPath(_maxFiles - 1);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (var number = _maxFiles - 2; number >= 1; number--)
        {
            var source = NumberedPath(number);
            if (File.Exists(source))
            {
                File.Move(source, NumberedPath(number + 1));
            }
        }
        File.Move(CurrentFilePath, NumberedPath(1));
    }

    private sealed class RollingFileLogger : ILogger
    {
        private readonly RollingFileLoggerProvider _provider;
        private readonly string _category;

        public RollingFileLogger(RollingFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }
            _provider.Write(logLevel, _category, formatter(state, exception), exception);
        }
    }
}

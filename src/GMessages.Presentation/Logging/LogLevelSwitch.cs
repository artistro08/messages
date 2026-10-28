using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Logging;

/// <summary>
/// Minimum level the file logger writes. Changed at runtime by the log level setting. Ported from Magic Buds.
/// </summary>
public sealed class LogLevelSwitch
{
    /// <summary>
    /// Lowest level that gets written. Defaults to Information.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}

using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Logging;

/// <summary>
/// Holds the minimum level that the file logger writes. The log level setting changes that level at run time. Ported
/// from Magic Buds.
/// </summary>
public sealed class LogLevelSwitch
{
    /// <summary>
    /// Gets or sets the lowest level that the logger writes. The default is Information.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}

using Microsoft.Extensions.Logging;

namespace GMessages.App;

/// <summary>
/// Every log message the app shell writes, as source-generated LoggerMessage delegates. Same pattern as Magic Buds' <c>AppLog</c>.
/// </summary>
internal static partial class AppLog
{
    [LoggerMessage(EventId = 901, Level = LogLevel.Information, Message = "{Name} {Version} started (packaged: {Packaged}, test run: {TestRun})")]
    public static partial void Started(ILogger logger, string name, string version, bool packaged, bool testRun);

    [LoggerMessage(EventId = 902, Level = LogLevel.Information, Message = "Quit")]
    public static partial void Quit(ILogger logger);

    [LoggerMessage(EventId = 903, Level = LogLevel.Information, Message = "Tray icon added: {Added}")]
    public static partial void TrayAdded(ILogger logger, bool added);

    [LoggerMessage(EventId = 904, Level = LogLevel.Information, Message = "Open requested ({Source})")]
    public static partial void OpenRequested(ILogger logger, string source);

    [LoggerMessage(EventId = 905, Level = LogLevel.Information, Message = "Settings requested")]
    public static partial void SettingsRequested(ILogger logger);
}

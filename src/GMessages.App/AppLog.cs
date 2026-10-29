using GMessages.Presentation.Connection;
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

    [LoggerMessage(EventId = 906, Level = LogLevel.Error, Message = "Sign-in page could not start ({ErrorType})")]
    public static partial void SignInStartFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 907, Level = LogLevel.Warning, Message = "Sign-in cookies could not be read ({ErrorType})")]
    public static partial void CookieReadFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 908, Level = LogLevel.Information, Message = "Sign-in cookies captured: {Names}")]
    public static partial void CookiesCaptured(ILogger logger, string names);

    [LoggerMessage(EventId = 909, Level = LogLevel.Warning, Message = "Sign-in profile folder could not be deleted; it holds a live Google session: {Folder}")]
    public static partial void ProfileNotDeleted(ILogger logger, string folder);

    [LoggerMessage(EventId = 910, Level = LogLevel.Error, Message = "Connection start failed")]
    public static partial void ConnectionStartFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 911, Level = LogLevel.Information, Message = "Connection state: {State}")]
    public static partial void ConnectionStateChanged(ILogger logger, ConnectionState state);

    [LoggerMessage(EventId = 912, Level = LogLevel.Information, Message = "Reconnecting ({Reason})")]
    public static partial void Reconnecting(ILogger logger, string reason);

    [LoggerMessage(EventId = 913, Level = LogLevel.Warning, Message = "Reconnect failed ({Reason})")]
    public static partial void ReconnectFailed(ILogger logger, string reason, Exception ex);

    [LoggerMessage(EventId = 914, Level = LogLevel.Warning, Message = "Notification {Action} failed ({ErrorType} 0x{HResult:X8})")]
    public static partial void NotificationFailed(ILogger logger, string action, string errorType, int hResult);

    [LoggerMessage(EventId = 915, Level = LogLevel.Information, Message = "Notification invoked ({Action})")]
    public static partial void NotificationInvoked(ILogger logger, string action);

    [LoggerMessage(EventId = 916, Level = LogLevel.Information, Message = "Notifications registered")]
    public static partial void NotificationsRegistered(ILogger logger);

    [LoggerMessage(EventId = 918, Level = LogLevel.Information, Message = "Toast shown for {MessageId} in {ConversationId}")]
    public static partial void ToastShown(ILogger logger, string messageId, string conversationId);

    [LoggerMessage(EventId = 917, Level = LogLevel.Error, Message = "Unhandled error ({ErrorType} 0x{HResult:X8}, handled: {Handled})")]
    public static partial void UnhandledError(ILogger logger, string errorType, int hResult, bool handled);

    [LoggerMessage(EventId = 919, Level = LogLevel.Information, Message = "Sign-in device-bound session headers: {Headers}")]
    public static partial void BoundSessionHeaders(ILogger logger, string headers);

    [LoggerMessage(EventId = 951,Level = LogLevel.Information, Message = "Test endpoint {Endpoint}, data folder {DataFolder}")]
    public static partial void TestEndpoint(ILogger logger, string endpoint, string dataFolder);
}

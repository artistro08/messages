using Microsoft.Extensions.Logging;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace GMessages.App;

/// <summary>
/// Shows a number on the app's taskbar button: how many chats are unread (see <c>UnreadBadge</c>).
/// </summary>
/// <remarks>
/// Windows 11 shows a packaged app's badge on its taskbar button whenever the button is visible. The unpackaged Debug
/// app has no package identity, so it shows nothing. A failure is logged and dropped; only the count is ever logged.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/apps/design/shell/tiles-and-notifications/badges"/>
internal static class TaskbarBadge
{
    /// <summary>
    /// Shows a count on the badge, or clears it for 0.
    /// </summary>
    /// <param name="count">The number of unread chats.</param>
    /// <param name="logger">The app log.</param>
    public static void Show(int count, ILogger logger)
    {
        if (!AppIdentity.IsPackaged)
        {
            return;
        }

        try
        {
            BadgeUpdater updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
            if (count <= 0)
            {
                updater.Clear();
            }
            else
            {
                var badge = new XmlDocument();
                badge.LoadXml($"<badge value=\"{count}\"/>");
                updater.Update(new BadgeNotification(badge));
            }

            AppLog.BadgeSet(logger, count);
        }
#pragma warning disable CA1031 // A missing badge is better than a failed event handler; the failure is logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.BadgeFailed(logger, ex.GetType().Name, ex.HResult);
        }
    }
}

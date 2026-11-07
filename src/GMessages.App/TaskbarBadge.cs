using Microsoft.Extensions.Logging;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace GMessages.App;

/// <summary>
/// Shows the number of unread chats on the taskbar button of the app (see <c>UnreadBadge</c>).
/// </summary>
/// <remarks>
/// Windows 11 shows the badge of a packaged app on its taskbar button whenever the button is visible. The unpackaged
/// Debug app has no package identity, so it shows no badge. The app logs a failure and drops it. The log of a badge
/// holds only the count.
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
        catch (Exception ex)
        {
            // A missing badge is better than a failed event handler. The log holds the failure.
            AppLog.BadgeFailed(logger, ex.GetType().Name, ex.HResult);
        }
    }
}

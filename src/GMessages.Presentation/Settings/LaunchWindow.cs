namespace GMessages.Presentation.Settings;

/// <summary>
/// Decides whether the main window opens when the app starts.
/// </summary>
/// <remarks>
/// <para>
/// A launch by the user (the Start menu, a shortcut, or right after install) shows the main window. Windows starting
/// the app at sign-in through the startup task ("Start with Windows") keeps it in the tray, with no window. A toast
/// click handles itself: an open shows the window on that chat, and a reply sends without one.
/// </para>
/// <para>
/// Pairing is separate: with no session the window always opens on pairing, whatever started the app.
/// </para>
/// </remarks>
public static class LaunchWindow
{
    /// <summary>
    /// Gets whether the main window shows at launch.
    /// </summary>
    /// <param name="byStartupTask">Whether Windows started the app through its startup task at sign-in.</param>
    /// <param name="byNotification">Whether a toast click started the app.</param>
    /// <returns>True for a launch by the user.</returns>
    public static bool ShowsAtLaunch(bool byStartupTask, bool byNotification) => !byStartupTask && !byNotification;
}

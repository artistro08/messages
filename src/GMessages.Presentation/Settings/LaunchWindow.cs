namespace GMessages.Presentation.Settings;

/// <summary>
/// Decides whether the main window opens when the app starts, and whether closing it quits.
/// </summary>
/// <remarks>
/// <para>
/// A launch by the user (the Start menu, a shortcut, or right after install) shows the main window. Windows starting
/// the app at sign-in through the startup task ("Start with Windows") keeps it in the background with no window, unless
/// it doesn't run in the background (Settings): then the window opens. A toast click handles itself: an open shows the
/// window on that chat, and a reply sends without one.
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
    /// <param name="runInBackground">The setting that keeps the app running with no window.</param>
    /// <returns>True for a launch by the user, or by the startup task when the app doesn't run in the background.</returns>
    public static bool ShowsAtLaunch(bool byStartupTask, bool byNotification, bool runInBackground = true) =>
        !byNotification && (!byStartupTask || !runInBackground);

    /// <summary>
    /// Gets whether closing the main window quits the app.
    /// </summary>
    /// <remarks>
    /// It quits when the app doesn't run in the background, and also with no tray icon and no phone paired: then
    /// nothing runs in the background, and the account menu (which holds Quit while the icon is off) isn't shown.
    /// </remarks>
    /// <param name="runInBackground">The setting that keeps the app running with no window.</param>
    /// <param name="showTrayIcon">The tray icon setting.</param>
    /// <param name="needsPairing">Whether no phone is paired (not paired yet, or signed out).</param>
    /// <returns>True to quit.</returns>
    public static bool QuitsOnClose(bool runInBackground, bool showTrayIcon, bool needsPairing) =>
        !runInBackground || (!showTrayIcon && needsPairing);
}

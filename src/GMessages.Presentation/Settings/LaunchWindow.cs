namespace GMessages.Presentation.Settings;

/// <summary>
/// Decides whether the main window opens when the app starts, and whether a close of the window quits the app.
/// </summary>
/// <remarks>
/// <para>
/// A launch by the user shows the main window. The Start menu, a shortcut and the first start after the install are
/// such launches. At sign-in, Windows can start the app through the startup task ("Start with Windows"). Then the app
/// stays in the background with no window. With the run in background setting off, the window opens instead. A toast
/// click has its own handling. An open shows the window on that chat, and a reply goes out with no window.
/// </para>
/// <para>
/// Pairing is separate. With no session, the window always opens on the pairing page, whatever started the app.
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
    /// <returns>
    /// True for a launch by the user, or for a launch by the startup task when the app does not run in the background.
    /// </returns>
    public static bool ShowsAtLaunch(bool byStartupTask, bool byNotification, bool runInBackground) =>
        !byNotification && (!byStartupTask || !runInBackground);

    /// <summary>
    /// Gets whether a close of the main window quits the app.
    /// </summary>
    /// <remarks>
    /// The app quits when the run in background setting is off. The app also quits with no tray icon and no paired
    /// phone. Then no work runs in the background. While the icon is off, the account menu holds Quit. Without a paired
    /// phone, that menu does not show.
    /// </remarks>
    /// <param name="runInBackground">The setting that keeps the app running with no window.</param>
    /// <param name="showTrayIcon">The tray icon setting.</param>
    /// <param name="needsPairing">Whether no phone is paired, as before the first pairing or after a sign-out.</param>
    /// <returns>True to quit.</returns>
    public static bool QuitsOnClose(bool runInBackground, bool showTrayIcon, bool needsPairing) =>
        !runInBackground || (!showTrayIcon && needsPairing);
}

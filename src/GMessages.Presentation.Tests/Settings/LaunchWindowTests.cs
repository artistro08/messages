using GMessages.Presentation.Settings;

namespace GMessages.Presentation.Tests.Settings;

/// <summary>
/// Checks <see cref="LaunchWindow"/>: the user's launch shows the window, the startup task and toast clicks don't (the
/// startup task does when the app doesn't run in the background), and when closing the window quits.
/// </summary>
[TestClass]
public sealed class LaunchWindowTests
{
    [TestMethod]
    [DataRow(false, false, true, true)]
    [DataRow(true, false, true, false)]
    [DataRow(false, true, true, false)]
    [DataRow(true, false, false, true)]
    [DataRow(false, true, false, false)]
    public void ShowsAtLaunch_ForTheUsersLaunch_OrStartupWithoutBackground(bool byStartupTask, bool byNotification, bool runInBackground, bool expected) =>
        Assert.AreEqual(expected, LaunchWindow.ShowsAtLaunch(byStartupTask, byNotification, runInBackground));

    [TestMethod]
    [DataRow(true, true, false, false)]
    [DataRow(true, true, true, false)]
    [DataRow(true, false, false, false)]
    [DataRow(true, false, true, true)]
    [DataRow(false, true, false, true)]
    [DataRow(false, false, false, true)]
    public void QuitsOnClose_WithoutBackground_OrWithNoIconAndNoPhone(bool runInBackground, bool showTrayIcon, bool needsPairing, bool expected) =>
        Assert.AreEqual(expected, LaunchWindow.QuitsOnClose(runInBackground, showTrayIcon, needsPairing));
}

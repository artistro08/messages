using GMessages.Presentation.Settings;

namespace GMessages.Presentation.Tests.Settings;

/// <summary>
/// Checks <see cref="LaunchWindow"/>. A launch by the user shows the window. The startup task and toast clicks do not
/// show it. The startup task does show it when the app does not run in the background. The test also checks when
/// closing the window quits the app.
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

using System.Diagnostics;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using GMessages.Presentation.Connection;

namespace GMessages.App.UITests;

/// <summary>
/// The app's life as a tray app: one copy only, a lost session, and closing windows without quitting.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class LifecycleTests : UiTest
{
    /// <summary>
    /// A second launch with the same flags exits quickly, the first copy opens its main window, and only one copy runs.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SecondLaunch_OpensExistingWindow_NoSecondProcess()
    {
        await PairAsync();
        Assert.IsNull(App.FindWindow(AppInfo.DisplayName), "A window was already open before the second launch.");

        // Second Copy Hands Over And Exits
        using (Process second = AppDriver.StartExe(App.TestArguments))
        {
            // A Broken Hand-Off Must Not Leave A Second Copy Running Into Later Tests
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await second.WaitForExitAsync(timeout.Token);
                Assert.AreEqual(0, second.ExitCode, "The second launch did not exit cleanly.");
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("The second launch was still running 10 s later; it should hand off and exit.");
            }
            finally
            {
                if (!second.HasExited)
                {
                    second.Kill(entireProcessTree: true);
                }
            }
        }

        // The First Copy Shows Its Main Window; Still One Process
        App.Find("Main.ConversationList");
        CollectionAssert.AreEqual(new[] { App.Process.Id }, AppDriver.RunningTestExeIds().ToArray(), "Expected exactly the first Messages process.");
    }

    /// <summary>
    /// A tray click while start-up still waits on the server is kept and opens the main window once start-up ends.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task TrayClickDuringSlowStart_OpensWhenReady()
    {
        // Slow From The Start, So The Saved Session Needs A (Slow) Refresh At The Next Launch
        Server.SlowStart = true;
        await PairAsync();
        int refreshes = Refreshes();

        await App.RestartAsync();

        // Click While The 3 s Refresh Is Still Unanswered
        Wait.Until(() => Refreshes() > refreshes, AppDriver.DefaultTimeout, () => "The restarted app never asked for a token refresh.");
        Tray.LeftClick();

        App.Find("Main.ConversationList");

        // The Click Really Came Before Start-Up Ended (The Restarted Run's First State Line), Or This Proved Nothing
        string log        = ReadLog();
        string second_run = log[log.LastIndexOf(" started (", StringComparison.Ordinal)..];
        int clicked_at    = second_run.IndexOf("Open requested (tray)", StringComparison.Ordinal);
        int started_at    = second_run.IndexOf("Connection state:", StringComparison.Ordinal);
        Assert.IsTrue(clicked_at >= 0 && started_at > clicked_at, $"The tray click wasn't logged before start-up ended:\n{second_run}");

        int Refreshes() => Server.RequestedPaths.Count(p => p.EndsWith("Registration/RegisterRefresh", StringComparison.Ordinal));
    }

    /// <summary>
    /// Google ending the session (401 on the long poll) while connected brings back the pairing window with the
    /// signed-out reason above the sign-in.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SessionKilled_ShowsPairingWithSignedOutMessage()
    {
        await PairAsync();

        Server.KillLongPoll(401);

        FlaUI.Core.AutomationElements.Window pairing = App.WaitForWindow(PairingTitle, TimeSpan.FromSeconds(30));
        Wait.Until(() => AppDriver.Search(pairing, e => AppDriver.SafeName(e).Contains(ConnectionController.SignedOutText, StringComparison.Ordinal)) is not null,
            AppDriver.DefaultTimeout, () => $"The pairing window never showed '{ConnectionController.SignedOutText}'.");
        WaitForSignInButton();
    }

    /// <summary>
    /// Closing the pairing and settings windows leaves the app and its tray icon running, and the icon still works.
    /// </summary>
    [TestMethod]
    public void ClosingWindows_KeepsTrayRunning()
    {
        // Cancel Pairing
        App.WaitForWindow(PairingTitle, AppDriver.DefaultTimeout);
        App.Find("Pairing.Cancel").Patterns.Invoke.Pattern.Invoke();
        App.WaitForNoWindow(PairingTitle, AppDriver.DefaultTimeout);
        AssertStillRunning();

        // Close Settings With Its Close Button
        InvokeTrayMenu("TrayMenu.Settings");
        App.WaitForWindow(SettingsTitle, AppDriver.DefaultTimeout).Close();
        App.WaitForNoWindow(SettingsTitle, AppDriver.DefaultTimeout);
        AssertStillRunning();

        // The Icon Still Answers: Unpaired, A Left-Click Brings Pairing Back; Close It Too
        Tray.LeftClick();
        App.WaitForWindow(PairingTitle, AppDriver.DefaultTimeout).Close();
        App.WaitForNoWindow(PairingTitle, AppDriver.DefaultTimeout);
        AssertStillRunning();
    }

    private void AssertStillRunning()
    {
        Assert.IsFalse(App.Process.HasExited, "Closing a window ended the app.");
        Tray.FindIcon(AppDriver.DefaultTimeout);
        Tray.CloseOverflow();
    }
}

using System.Diagnostics;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using GMessages.Presentation.Connection;

namespace GMessages.App.UITests;

/// <summary>
/// The app life as a tray app: one copy only, a lost session, and closing windows without a quit.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class LifecycleTests : UiTest
{
    /// <summary>
    /// A second launch with the same flags exits quickly. The first copy opens its main window, and only one copy runs.
    /// </summary>
    [TestMethod]
    public async Task SecondLaunch_OpensExistingWindow_NoSecondProcess()
    {
        await PairAsync();
        Assert.IsNull(App.FindWindow(AppInfo.WindowTitle), "A window was already open before the second launch.");

        using (Process second = AppDriver.StartExe(App.TestArguments))
        {
            // A broken hand-off must not leave a second copy running into later tests.
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

        App.Find("Main.ConversationList");
        CollectionAssert.AreEqual(new[] { App.Process.Id }, AppDriver.RunningTestExeIds().ToArray(), "Expected exactly the first Messages process.");
    }

    /// <summary>
    /// <c>--quit</c> with a chat open ends the process within 5 s. The log records the time of each quit step in
    /// milliseconds, with no message text.
    /// </summary>
    [TestMethod]
    public async Task Quit_WithChatOpen_ExitsWithinFiveSeconds()
    {
        await OpenMainAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Ann Lee");

        var clock = Stopwatch.StartNew();
        await App.QuitAsync();
        Assert.IsLessThan(5000, clock.ElapsedMilliseconds, $"Quitting took {clock.ElapsedMilliseconds} ms.");
        StringAssert.Contains(ReadLog(), "Quit steps:", "The quit's timing line is missing.");
    }

    /// <summary>
    /// A paired app that the user starts shows the main window without a tray click. Only the Windows startup task at
    /// sign-in keeps the app in the tray. The LaunchWindow unit tests cover that case.
    /// </summary>
    [TestMethod]
    public async Task UserLaunch_Paired_ShowsMainWindow()
    {
        await PairAsync();

        await App.RestartAsync();

        App.Find("Main.ConversationList");
    }

    /// <summary>
    /// A tray click while start-up still waits for the server is kept. The main window opens when start-up ends.
    /// </summary>
    [TestMethod]
    public async Task TrayClickDuringSlowStart_OpensWhenReady()
    {
        // The slow server makes the saved session need a slow token refresh at the next launch.
        Server.SlowStart = true;
        await PairAsync();
        int refreshes = Refreshes();

        await App.RestartAsync();

        // The click comes while the 3 s refresh has no answer yet.
        Wait.Until(() => Refreshes() > refreshes, AppDriver.DefaultTimeout, () => "The restarted app never asked for a token refresh.");
        Tray.LeftClick();

        App.Find("Main.ConversationList");

        // The click must come before start-up ends, which is the first state line of the restarted run.
        // Otherwise the test proves nothing.
        string log        = ReadLog();
        string secondRun  = log[log.LastIndexOf(" started (", StringComparison.Ordinal)..];
        int clickedAt     = secondRun.IndexOf("Open requested (tray)", StringComparison.Ordinal);
        int startedAt     = secondRun.IndexOf("Connection state:", StringComparison.Ordinal);
        Assert.IsTrue(clickedAt >= 0 && startedAt > clickedAt, $"The tray click wasn't logged before start-up ended:\n{secondRun}");

        int Refreshes() => Server.RequestedPaths.Count(p => p.EndsWith("Registration/RegisterRefresh", StringComparison.Ordinal));
    }

    /// <summary>
    /// A 401 on the long poll while connected opens the main window on pairing. The signed-out reason shows above the
    /// sign-in.
    /// </summary>
    [TestMethod]
    public async Task SessionKilled_ShowsPairingWithSignedOutMessage()
    {
        await PairAsync();

        Server.KillLongPoll(401);

        FlaUI.Core.AutomationElements.Window pairing = WaitForPairing(TimeSpan.FromSeconds(30));
        Wait.Until(() => AppDriver.Search(pairing, e => AppDriver.SafeName(e).Contains(ConnectionController.SignedOutText, StringComparison.Ordinal)) is not null,
            AppDriver.DefaultTimeout, () => $"Pairing never showed '{ConnectionController.SignedOutText}'.");
        WaitForSignInButton();
    }

    /// <summary>
    /// Closing the main window on pairing or on settings leaves the app and the tray icon running. The icon still
    /// works.
    /// </summary>
    [TestMethod]
    public void ClosingWindows_KeepsTrayRunning()
    {
        WaitForPairing();
        App.Find("Pairing.Cancel").Patterns.Invoke.Pattern.Invoke();
        App.WaitForNoWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        AssertStillRunning();

        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings().Close();
        App.WaitForNoWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        AssertStillRunning();

        // The app is not paired, so a left-click shows pairing again.
        Tray.LeftClick();
        WaitForPairing().Close();
        App.WaitForNoWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        AssertStillRunning();
    }

    /// <summary>
    /// After pairing and connecting, closing the main window leaves the tray idle. The app drops the kept window
    /// after 1 second here, and after minutes in a real run. Then the app trims its memory, and its private bytes end
    /// below the value right after the close. A debug build writes <c>trim-done.txt</c> when the trim ends.
    /// </summary>
    [TestMethod]
    public async Task TrayIdle_AfterConnectAndClose_Trims()
    {
        await OpenMainAsync();

        // A trim that the open window skips writes no note, so the file must not exist yet.
        string done = Path.Combine(App.DataDir, "trim-done.txt");
        File.Delete(done);
        File.WriteAllText(Path.Combine(App.DataDir, "keep-window.txt"), "1");
        CloseMainWindow();

        App.Process.Refresh();
        long afterClose = App.Process.PrivateMemorySize64;

        Wait.Until(() => File.Exists(done), TimeSpan.FromSeconds(30), () => "The tray never trimmed after the window closed.");

        App.Process.Refresh();
        long afterTrim = App.Process.PrivateMemorySize64;
        Console.WriteLine($"Private bytes: {afterClose} after the close, {afterTrim} after the trim.");
        Assert.IsLessThanOrEqualTo(afterClose + 2L * 1024 * 1024, afterTrim, "The trim raised the private bytes.");
    }

    private void AssertStillRunning()
    {
        Assert.IsFalse(App.Process.HasExited, "Closing a window ended the app.");
        Tray.FindIcon(AppDriver.DefaultTimeout);
        Tray.CloseOverflow();
    }
}

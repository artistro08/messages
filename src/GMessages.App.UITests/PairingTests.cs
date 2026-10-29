using GMessages.App.UITests.Driver;

namespace GMessages.App.UITests;

/// <summary>
/// The first-run pairing flow, driven in the real app against the fake Google server and fake phone.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class PairingTests : UiTest
{
    // Failure Copy (Owner Wording, Verbatim)
    private const string WrongEmojiText  = "That wasn't the matching emoji. Try again.";
    private const string NotAcceptedText = "Google didn't accept the sign-in. Try again.";

    // Controller Copy (The Owner May Edit It)
    private const string SignInPendingText = "Sign in to Google in the window that opened.";
    private const string SignInClosedText  = "Sign-in was closed. Try again.";

    /// <summary>
    /// A first run opens pairing; the fake sign-in page loads in the sign-in helper's window (a second process); the app
    /// shows the phone's emoji; a correct tap closes the window, saves the session, leaves the tray icon and no helper.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task FirstRun_ShowsPairing_SignIn_Emoji_TapPairs_WindowCloses()
    {
        // Pairing Window Points To The Sign-In Window
        FlaUI.Core.AutomationElements.Window pairing = App.WaitForWindow(PairingTitle, AppDriver.DefaultTimeout);
        AssertTitleBar("Pairing.TitleBar", PairingTitle);
        App.Find("Pairing.SignInPending");
        App.WaitForText("Pairing.SignInPendingText", SignInPendingText);

        // Sign-In Window: Its Own Process, With The Fake Sign-In Page
        FlaUI.Core.AutomationElements.Window sign_in = App.WaitForWindow(SignInTitle, AppDriver.DefaultTimeout);
        AssertTitleBar("SignIn.TitleBar", SignInTitle);
        Assert.AreNotEqual(pairing.Properties.ProcessId.Value, sign_in.Properties.ProcessId.Value, "The sign-in window runs in the app process.");
        WaitForSignInButton();
        CollectionAssert.Contains(Server.RequestedPaths.ToList(), "/web/authentication", "The WebView2 never asked the fake server for the sign-in page.");

        // Emoji Matches The Phone
        string emoji = await SignInAndWaitForEmojiAsync();
        Assert.AreEqual(Server.Phone.ShownEmoji, emoji);

        // Correct Tap: Window Closes, Session Saved, Tray Stays
        Server.Phone.Tap(true);
        App.WaitForNoWindow(PairingTitle, AppDriver.DefaultTimeout);
        Wait.Until(() => File.Exists(SessionFile), AppDriver.DefaultTimeout, () => $"No session.bin in {App.DataDir}.");
        Tray.FindIcon(AppDriver.DefaultTimeout);
        Tray.CloseOverflow();
        App.WaitForNoHelper();

        // Which Cookies WebView2 Kept Over http://127.0.0.1 (Is __Secure-1PSIDTS Among Them?)
        string captured = ReadLog().Split('\n').FirstOrDefault(l => l.Contains("Sign-in cookies captured", StringComparison.Ordinal)) ?? "(not logged)";
        TestContext.WriteLine(captured.Trim());
    }

    /// <summary>
    /// A wrong tap shows the wrong-emoji message; Try again brings back a fresh sign-in page.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task WrongEmoji_ShowsMessage_TryAgain_RestartsSignIn()
    {
        App.WaitForWindow(PairingTitle, AppDriver.DefaultTimeout);
        await SignInAndWaitForEmojiAsync();

        // Wrong Tap
        Server.Phone.Tap(false);
        App.WaitForText("Pairing.Message", WrongEmojiText);

        // Try Again: A New Browser Loads The Sign-In Page Again
        int page_loads = Server.RequestedPaths.Count(p => p == "/web/authentication");
        App.Find("Pairing.TryAgain").Patterns.Invoke.Pattern.Invoke();
        WaitForSignInButton();
        Wait.Until(() => Server.RequestedPaths.Count(p => p == "/web/authentication") > page_loads, AppDriver.DefaultTimeout,
            () => "Try again did not load the sign-in page again.");
        Assert.IsFalse(File.Exists(SessionFile), "A failed pairing saved a session.");
    }

    /// <summary>
    /// Closing the sign-in window shows the closed message; Try again opens a new sign-in window with the page.
    /// </summary>
    [TestMethod]
    public void SignInWindowClosed_ShowsMessage_TryAgain_ReopensSignIn()
    {
        App.WaitForWindow(PairingTitle, AppDriver.DefaultTimeout);
        WaitForSignInButton();

        // Close The Sign-In Window
        App.WaitForWindow(SignInTitle, AppDriver.DefaultTimeout).Close();
        App.WaitForText("Pairing.Message", SignInClosedText);
        App.WaitForNoHelper();

        // Try Again: A New Sign-In Window With The Page
        int page_loads = Server.RequestedPaths.Count(p => p == "/web/authentication");
        App.Find("Pairing.TryAgain").Patterns.Invoke.Pattern.Invoke();
        App.WaitForWindow(SignInTitle, AppDriver.DefaultTimeout);
        WaitForSignInButton();
        Wait.Until(() => Server.RequestedPaths.Count(p => p == "/web/authentication") > page_loads, AppDriver.DefaultTimeout,
            () => "Try again did not load the sign-in page again.");
        Assert.IsFalse(File.Exists(SessionFile), "A closed sign-in saved a session.");
    }

    /// <summary>
    /// Google refusing the cookies (401 on sign-in) shows the not-accepted message and saves nothing.
    /// </summary>
    [TestMethod]
    public void RejectedCookies_ShowsGoogleDidntAccept()
    {
        Server.RejectCookies = true;
        App.WaitForWindow(PairingTitle, AppDriver.DefaultTimeout);
        ClickSignIn();

        App.WaitForText("Pairing.Message", NotAcceptedText);
        App.Find("Pairing.TryAgain");
        Assert.IsFalse(File.Exists(SessionFile), "A rejected sign-in saved a session.");
    }
}

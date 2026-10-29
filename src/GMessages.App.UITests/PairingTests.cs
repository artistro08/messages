using System.Drawing;
using FlaUI.Core.AutomationElements;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;

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

    /// <summary>
    /// A first run opens the main window on pairing, with no ☰ or profile button; the fake sign-in page loads in the
    /// WebView2, about as wide as the window; the app shows the phone's emoji; a correct tap switches the same window to
    /// the chats, saves the session and leaves the tray icon.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task FirstRun_ShowsPairing_SignIn_Emoji_TapPairs_SwitchesToChats()
    {
        // Main Window On Pairing With The Fake Sign-In Page, Nearly As Wide As The Window
        Window window = WaitForPairing();
        AssertTitleBar("Main.TitleBar", AppInfo.DisplayName);
        Assert.IsNull(App.TryFind("Main.MenuButton"), "☰ shows while pairing.");
        Assert.IsNull(App.TryFind("Main.ProfileButton"), "The profile button shows while pairing.");
        WaitForSignInButton();
        CollectionAssert.Contains(Server.RequestedPaths.ToList(), "/web/authentication", "The WebView2 never asked the fake server for the sign-in page.");
        Rectangle sign_in = App.Find("Pairing.SignIn").BoundingRectangle;
        Assert.IsTrue(sign_in.Width >= window.BoundingRectangle.Width - 40, $"The sign-in is {sign_in.Width} px wide in a {window.BoundingRectangle.Width} px window.");

        // Emoji Matches The Phone
        string emoji = await SignInAndWaitForEmojiAsync();
        Assert.AreEqual(Server.Phone.ShownEmoji, emoji);

        // Correct Tap: The Same Window Shows The Chats (☰ And Profile Back), Session Saved, Tray Stays
        Server.Phone.Tap(true);
        App.Find("Main.ConversationList");
        WaitForGone("Pairing.Cancel");
        App.Find("Main.MenuButton");
        App.Find("Main.ProfileButton");
        Assert.AreEqual(window.Properties.NativeWindowHandle.Value, App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).Properties.NativeWindowHandle.Value, "Pairing opened another window for the chats.");
        Wait.Until(() => File.Exists(SessionFile), AppDriver.DefaultTimeout, () => $"No session.bin in {App.DataDir}.");
        Tray.FindIcon(AppDriver.DefaultTimeout);
        Tray.CloseOverflow();

        // Which Cookies WebView2 Kept Over http://127.0.0.1
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
        WaitForPairing();
        await SignInAndWaitForEmojiAsync();

        // Wrong Tap
        Server.Phone.Tap(false);
        App.WaitForText("Pairing.Message", WrongEmojiText);

        // Try Again: A New Browser Loads The Sign-In Page Again. UI Automation Waits For The Load: A WebView2 Asked
        // While Its Browser Is Still Starting Stays An Empty Pane For Good
        int page_loads = Server.RequestedPaths.Count(p => p == "/web/authentication");
        App.Find("Pairing.TryAgain").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => Server.RequestedPaths.Count(p => p == "/web/authentication") > page_loads, AppDriver.DefaultTimeout,
            () => "Try again did not load the sign-in page again.");
        WaitForSignInButton();
        Assert.IsFalse(File.Exists(SessionFile), "A failed pairing saved a session.");
    }

    /// <summary>
    /// Google refusing the cookies (401 on sign-in) shows the not-accepted message and saves nothing.
    /// </summary>
    [TestMethod]
    public void RejectedCookies_ShowsGoogleDidntAccept()
    {
        Server.RejectCookies = true;
        WaitForPairing();
        ClickSignIn();

        App.WaitForText("Pairing.Message", NotAcceptedText);
        App.Find("Pairing.TryAgain");
        Assert.IsFalse(File.Exists(SessionFile), "A rejected sign-in saved a session.");
    }
}

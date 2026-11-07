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
    // The owner wrote these failure messages. They must stay verbatim.
    private const string WrongEmojiText  = "That wasn't the matching emoji. Try again.";
    private const string NotAcceptedText = "Google didn't accept the sign-in. Try again.";

    /// <summary>
    /// A first run opens the main window on pairing, with no menu or profile button. The fake sign-in page loads in
    /// the WebView2 at nearly the window width, and the app shows the phone emoji. A correct tap switches the same
    /// window to the chats, saves the session and keeps the tray icon.
    /// </summary>
    [TestMethod]
    public async Task FirstRun_ShowsPairing_SignIn_Emoji_TapPairs_SwitchesToChats()
    {
        Window window = WaitForPairing();
        AssertTitleBar("Main.TitleBar", AppInfo.WindowTitle);
        Assert.IsNull(App.TryFind("Main.MenuButton"), "☰ shows while pairing.");
        Assert.IsNull(App.TryFind("Main.ProfileButton"), "The profile button shows while pairing.");
        WaitForSignInButton();
        CollectionAssert.Contains(Server.RequestedPaths.ToList(), "/web/authentication", "The WebView2 never asked the fake server for the sign-in page.");
        Rectangle signIn = App.Find("Pairing.SignIn").BoundingRectangle;
        Assert.IsTrue(signIn.Width >= window.BoundingRectangle.Width - 40, $"The sign-in is {signIn.Width} px wide in a {window.BoundingRectangle.Width} px window.");

        string emoji = await SignInAndWaitForEmojiAsync();
        Assert.AreEqual(Server.Phone.ShownEmoji, emoji);

        Server.Phone.Tap(true);
        App.Find("Main.ConversationList");
        WaitForGone("Pairing.Cancel");
        App.Find("Main.MenuButton");
        App.Find("Main.ProfileButton");
        Assert.AreEqual(window.Properties.NativeWindowHandle.Value, App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).Properties.NativeWindowHandle.Value, "Pairing opened another window for the chats.");
        Wait.Until(() => File.Exists(SessionFile), AppDriver.DefaultTimeout, () => $"No session.bin in {App.DataDir}.");
        Tray.FindIcon(AppDriver.DefaultTimeout);
        Tray.CloseOverflow();

        // The test log records which cookies WebView2 keeps over http://127.0.0.1, for example __Secure-1PSIDTS.
        string captured = ReadLog().Split('\n').FirstOrDefault(l => l.Contains("Sign-in cookies captured", StringComparison.Ordinal)) ?? "(not logged)";
        TestContext.WriteLine(captured.Trim());
    }

    /// <summary>
    /// A wrong tap shows the wrong-emoji message. Try again loads a fresh sign-in page.
    /// </summary>
    [TestMethod]
    public async Task WrongEmoji_ShowsMessage_TryAgain_RestartsSignIn()
    {
        WaitForPairing();
        await SignInAndWaitForEmojiAsync();

        Server.Phone.Tap(false);
        App.WaitForText("Pairing.Message", WrongEmojiText);

        // Try again starts a new browser. The test waits for the page load through UI Automation:
        // a WebView2 that gets a request while its browser still starts stays an empty pane for good.
        int pageLoads = Server.RequestedPaths.Count(p => p == "/web/authentication");
        App.Find("Pairing.TryAgain").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => Server.RequestedPaths.Count(p => p == "/web/authentication") > pageLoads, AppDriver.DefaultTimeout,
            () => "Try again did not load the sign-in page again.");
        WaitForSignInButton();
        Assert.IsFalse(File.Exists(SessionFile), "A failed pairing saved a session.");
    }

    /// <summary>
    /// A 401 from Google on sign-in shows the not-accepted message and saves no session.
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

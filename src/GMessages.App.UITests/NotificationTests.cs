using FlaUI.Core.AutomationElements;
using GMessages.App.UITests.Driver;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;

namespace GMessages.App.UITests;

/// <summary>
/// Windows notifications: which messages raise a toast, the toast's words, its reply box and a click on its body.
/// </summary>
/// <remarks>
/// These show real toasts on the desktop, so Windows notifications must be turned on. The app removes its toasts when
/// it quits in a test run, so none carry over to the next test. A test that expects no toast follows up with a live
/// message that must toast, so it can't pass just because toasts never show.
/// </remarks>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class NotificationTests : UiTest
{
    // How Long A Message That Must Not Notify Is Watched For A Toast
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);

    private ToastDriver _toasts = null!;

    /// <summary>
    /// Sets up the toast driver on the app's UI Automation client. With Windows notifications turned off no toast ever
    /// shows, so the test is inconclusive (the owner's setting is never changed).
    /// </summary>
    [TestInitialize]
    public void StartToasts()
    {
        if (ToastDriver.ToastsDisabled())
        {
            Assert.Inconclusive("Windows notifications are turned off on this PC; turn them on to run the toast tests.");
        }

        _toasts = new ToastDriver(App.Automation);
    }

    /// <summary>
    /// A live message in Ann's chat shows a toast titled with her name and the text as its body.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task LiveIncoming_ShowsToast_TitleAndBody()
    {
        await PairAsync();

        Server.Phone.PushIncoming("c-ann", "hey there");

        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");
    }

    /// <summary>
    /// A group message's toast is titled with the group and its body starts with the sender's name.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task GroupIncoming_BodyHasSenderPrefix()
    {
        await PairAsync();

        Server.Phone.PushIncoming("c-team", "lunch?");

        _toasts.WaitFor(AppDriver.DefaultTimeout, "Team", "Bo: lunch?");
    }

    /// <summary>
    /// A message that comes in with the catch-up after a reconnect doesn't notify; the next live one does.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task CatchUp_NoToast()
    {
        await PairAsync();

        Server.Phone.PushIncoming("c-ann", "from earlier", old: true);
        Server.KillLongPoll(0);
        _toasts.AssertNone("from earlier", QuietPeriod);

        AssertLiveStillToasts();
    }

    /// <summary>
    /// A message in a spam conversation doesn't notify; the next live one in the inbox does.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Spam_NoToast()
    {
        await PairAsync();

        Server.Phone.PushIncoming("c-spam", "claim now");
        _toasts.AssertNone("Prize Bot", QuietPeriod);

        AssertLiveStillToasts();
    }

    /// <summary>
    /// Replying from the toast sends the text and marks the chat read, without opening the main window.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ToastReply_SendsAndMarksRead_NoWindow()
    {
        await PairAsync();
        Server.Phone.PushIncoming("c-ann", "hey there");
        AutomationElement toast = _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");

        ToastDriver.Reply(toast, "on my way");

        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the reply.");
        Assert.AreEqual("on my way", FakePhone.SentText(Server.Phone.Sent[0]), "The phone got other text.");
        Assert.AreEqual("c-ann", Server.Phone.Sent[0].ConversationID, "The reply went to another conversation.");
        Wait.Until(() => Server.Phone.Reads.Any(r => r.ConversationID == "c-ann"), AppDriver.DefaultTimeout, () => "The phone never got a read mark for c-ann.");
        Assert.IsNull(App.FindWindow(AppInfo.DisplayName), "Replying opened a window.");
    }

    /// <summary>
    /// Clicking a toast's body opens the main window on that conversation.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ToastBodyClick_OpensWindowOnConversation()
    {
        await PairAsync();
        Server.Phone.PushIncoming("c-ann", "hey there");
        AutomationElement toast = _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");

        ToastDriver.ClickBody(toast);

        App.WaitForText("Chat.Header", "Ann Lee");
    }

    /// <summary>
    /// The chat that's open in the focused window doesn't notify, until "Notify for the chat I'm viewing" is on.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ViewedFocusedChat_NoToast_UnlessSettingOn()
    {
        // Ann's Chat Open In The Focused Window
        await OpenMainAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Ann Lee");
        FocusMainWindow();

        Server.Phone.PushIncoming("c-ann", "you there?");
        _toasts.AssertNone("you there?", QuietPeriod);

        // Turn The Setting On, Then Back To The Chat
        InvokeTrayMenu("TrayMenu.Settings");
        App.Find("Settings.NotifyViewedChat").Patterns.Toggle.Pattern.Toggle();
        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Settings.Back");
        App.WaitForText("Chat.Header", "Ann Lee");
        FocusMainWindow();

        Server.Phone.PushIncoming("c-ann", "hello?");
        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hello?");
    }

    /// <summary>
    /// A toast reply the phone refuses shows a second toast, titled with the chat, saying the reply wasn't sent.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ToastReply_PhoneFails_ShowsFailureToast()
    {
        await PairAsync();
        Server.Phone.FailSends = true;
        Server.Phone.PushIncoming("c-ann", "hey there");
        AutomationElement toast = _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");

        ToastDriver.Reply(toast, "on my way");

        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", ChatCopy.ReplyNotSent);
        Assert.AreEqual(1, Server.Phone.Sent.Count, "The phone didn't get the reply once.");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // A Live Message Still Toasts, So A "No Toast" Check Before It Meant Something
    private void AssertLiveStillToasts()
    {
        Server.Phone.PushIncoming("c-ann", "live one");
        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "live one");
    }

    // The Main Window Takes The Foreground, So The App Sees It Focused
    private void FocusMainWindow()
    {
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
    }
}

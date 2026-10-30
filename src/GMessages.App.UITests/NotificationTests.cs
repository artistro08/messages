using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using GMessages.App.UITests.Driver;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

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

    // How Long A Sign-Out May Take To Clear A Toast
    private static readonly TimeSpan SignOutTime = TimeSpan.FromSeconds(10);

    private ToastDriver _toasts = null!;

    /// <summary>
    /// Sets up the toast driver on the app's UI Automation client.
    /// </summary>
    [TestInitialize]
    public void StartToasts() => _toasts = new ToastDriver(App.Automation);

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
    /// Clicking a toast's body while the main window shows Settings closes Settings and shows that conversation.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ToastBodyClick_WhileSettingsOpen_ShowsConversation()
    {
        await OpenMainAsync();
        InvokeTrayMenu("TrayMenu.Settings");
        WaitForSettings();

        Server.Phone.PushIncoming("c-ann", "hey there");
        AutomationElement toast = _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");
        ToastDriver.ClickBody(toast);

        WaitForGone("Settings.Back");
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

    /// <summary>
    /// Updates of one incoming message (new statuses, as the real phone sends) while its toast shows don't show it
    /// again: the app logs one toast for the message.
    /// </summary>
    /// <remarks>
    /// A repeat show reuses the toast's tag, so it would replace the toast on screen rather than add one. The app's
    /// "Toast shown" log line is what tells them apart. The live message after the updates arrives behind them, so once
    /// it toasts, the updates were handled.
    /// </remarks>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task RepeatedUpdates_ShowOneToast()
    {
        await PairAsync();

        Message message = Server.Phone.PushIncoming("c-ann", "hey there");
        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");

        // Updates While It Shows
        Server.Phone.PushStatusUpdate(message, MessageStatusType.IncomingDelivered);
        Server.Phone.PushStatusUpdate(message, MessageStatusType.IncomingComplete);
        AssertLiveStillToasts();

        string shown = $"Toast shown for {message.MessageID} ";
        Assert.AreEqual(1, ReadLog().Split('\n').Count(l => l.Contains(shown, StringComparison.Ordinal)), "An update showed the message's toast again.");
    }

    /// <summary>
    /// Signing out (Google ends the session) removes the toasts that were showing.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SignOut_RemovesToasts()
    {
        await PairAsync();
        Server.Phone.PushIncoming("c-ann", "sign-out marker");
        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "sign-out marker");

        Server.KillLongPoll(401);

        Wait.Until(() => _toasts.TryFind("sign-out marker") is null, SignOutTime, () => "The toast stayed after sign-out.");
    }

    /// <summary>
    /// A snoozed toast's banner is removed by a sign-out.
    /// </summary>
    /// <remarks>
    /// That the clear also cancels the waiting snooze is covered by <c>ToastMemoryTests</c>: the shortest snooze the
    /// settings accept is 5 minutes, too long to watch for here.
    /// </remarks>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task SignOut_AfterSnooze_RemovesToast()
    {
        await PairAsync();
        Server.Phone.PushIncoming("c-ann", "snoozed marker");
        AutomationElement toast = _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "snoozed marker");

        // Snooze It, Then Sign Out
        AppDriver.Search(toast, e => e.ControlType == ControlType.Button && AppDriver.SafeName(e) == ChatCopy.Snooze)!.Patterns.Invoke.Pattern.Invoke();
        Server.KillLongPoll(401);

        // The Banner Of An Unattended Machine Stays Until The Sign-Out Clears It
        Wait.Until(() => _toasts.TryFind("snoozed marker") is null, SignOutTime, () => "The toast stayed after sign-out.");
    }

    /// <summary>
    /// Archiving a chat on the phone removes its toast, as reading it does.
    /// </summary>
    /// <remarks>
    /// Muting, blocking and deleting take the same path (<c>NotificationPolicyTests</c>); that it also cancels a waiting
    /// snooze is covered by <c>ToastMemoryTests</c>.
    /// </remarks>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Archived_RemovesToast()
    {
        await PairAsync();
        Server.Phone.PushIncoming("c-ann", "archive marker");
        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "archive marker");

        Server.Phone.PushConversationStatus("c-ann", ConversationStatus.Archived);

        Wait.Until(() => _toasts.TryFind("archive marker") is null, AppDriver.DefaultTimeout, () => "The toast stayed after the chat was archived.");
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

/// <summary>
/// Toast checks that need a person at the machine, so they're left out of the <c>TestCategory=UI</c> run.
/// </summary>
/// <remarks>
/// Windows keeps a toast's banner on screen while the user is away (no real keyboard or mouse input; UI Automation
/// and a cursor moved from code don't count), so on an unattended machine the banner never leaves. Run these with
/// <c>--filter TestCategory=Manual</c> while using the mouse or keyboard now and then.
/// </remarks>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class ManualNotificationTests : UiTest
{
    // How Long A Toast's Banner May Stay On Screen Before It Moves To The Notification Center
    private static readonly TimeSpan BannerTime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A message's toast leaves the screen (for the Notification Center) within 10 s, as other apps' toasts do. Needs
    /// real user input during the run (see the class remarks).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Toast_LeavesScreenWithin10Seconds()
    {
        var toasts = new ToastDriver(App.Automation);
        await PairAsync();

        Server.Phone.PushIncoming("c-ann", "hey there");
        toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");

        Wait.Until(() => toasts.TryFind("Ann Lee", "hey there") is null, BannerTime, () => $"The toast was still on screen after {BannerTime.TotalSeconds} s.");
    }
}

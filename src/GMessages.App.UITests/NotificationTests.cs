using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using GMessages.App.UITests.Driver;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.App.UITests;

/// <summary>
/// Windows notifications: which messages raise a toast, the toast text, its reply box and a click on its body.
/// </summary>
/// <remarks>
/// These tests show real toasts on the desktop, so Windows notifications must be on. The app removes its toasts when
/// it quits in a test run, so no toast carries over to the next test. A test that expects no toast follows up with a
/// live message that must toast. That test cannot pass only because toasts never show.
/// </remarks>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class NotificationTests : UiTest
{
    // The time that a test watches for a toast from a message that must not notify.
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);

    // The longest time that a sign-out takes to clear a toast.
    private static readonly TimeSpan SignOutTime = TimeSpan.FromSeconds(10);

    private ToastDriver _toasts = null!;

    /// <summary>
    /// Sets up the toast driver on the UI Automation client of the app.
    /// </summary>
    [TestInitialize]
    public void StartToasts() => _toasts = new ToastDriver(App.Automation);

    /// <summary>
    /// A live message in the chat of Ann shows a toast. The title is her name and the body is the text.
    /// </summary>
    [TestMethod]
    public async Task LiveIncoming_ShowsToast_TitleAndBody()
    {
        await PairAsync();

        Server.Phone.PushIncoming("c-ann", "hey there");

        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");
    }

    /// <summary>
    /// The toast of a group message has the group name as title. The body starts with the name of the sender.
    /// </summary>
    [TestMethod]
    public async Task GroupIncoming_BodyHasSenderPrefix()
    {
        await PairAsync();

        Server.Phone.PushIncoming("c-team", "lunch?");

        _toasts.WaitFor(AppDriver.DefaultTimeout, "Team", "Bo: lunch?");
    }

    /// <summary>
    /// A message that arrives with the catch-up after a reconnect does not notify. The next live message does notify.
    /// </summary>
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
    /// A message in a spam conversation does not notify. The next live message in the inbox does notify.
    /// </summary>
    [TestMethod]
    public async Task Spam_NoToast()
    {
        await PairAsync();

        Server.Phone.PushIncoming("c-spam", "claim now");
        _toasts.AssertNone("Prize Bot", QuietPeriod);

        AssertLiveStillToasts();
    }

    /// <summary>
    /// A reply from the toast sends the text and marks the chat as read. The main window stays closed.
    /// </summary>
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
        Assert.IsNull(App.FindWindow(AppInfo.WindowTitle), "Replying opened a window.");
    }

    /// <summary>
    /// A click on the body of a toast opens the main window on that conversation.
    /// </summary>
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
    /// A click on the body of a toast while the main window shows Settings closes Settings and shows that
    /// conversation.
    /// </summary>
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
    /// The chat that is open in the focused window does not notify until the setting "Notify for the chat I'm
    /// viewing" is on.
    /// </summary>
    [TestMethod]
    public async Task ViewedFocusedChat_NoToast_UnlessSettingOn()
    {
        await OpenMainAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Ann Lee");
        FocusMainWindow();

        Server.Phone.PushIncoming("c-ann", "you there?");
        _toasts.AssertNone("you there?", QuietPeriod);

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
    /// A toast reply that the phone refuses shows a second toast. Its title is the chat name, and it says that the
    /// reply was not sent.
    /// </summary>
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
    /// Status updates of one incoming message, as the real phone sends them, do not show its toast again while the
    /// toast shows. The app logs one toast for the message.
    /// </summary>
    /// <remarks>
    /// A repeat show reuses the tag of the toast, so it replaces the toast on screen and does not add one. The
    /// "Toast shown" log line of the app tells the two cases apart. The live message arrives behind the updates, so
    /// when it toasts, the app has handled the updates. The test dismisses the first banner before then. Windows shows
    /// the next banner only when the first one leaves, and on an unattended machine it never leaves by itself.
    /// </remarks>
    [TestMethod]
    public async Task RepeatedUpdates_ShowOneToast()
    {
        await PairAsync();

        Message message = Server.Phone.PushIncoming("c-ann", "hey there");
        AutomationElement toast = _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");

        Server.Phone.PushStatusUpdate(message, MessageStatusType.IncomingDelivered);
        Server.Phone.PushStatusUpdate(message, MessageStatusType.IncomingComplete);

        // Windows queues the live toast behind a toast that is still on screen. On an unattended machine,
        // that toast never leaves, so the test dismisses its banner.
        ToastDriver.Dismiss(toast);
        Wait.Until(() => _toasts.TryFind("Ann Lee", "hey there") is null, AppDriver.DefaultTimeout, () => "The toast stayed after Dismiss.");
        AssertLiveStillToasts();

        string shown = $"Toast shown for {message.MessageID} ";
        Assert.AreEqual(1, ReadLog().Split('\n').Count(l => l.Contains(shown, StringComparison.Ordinal)), "An update showed the message's toast again.");
    }

    /// <summary>
    /// A reaction made in the app, which the phone echoes back on the reacted message, never toasts. This holds with
    /// the setting that notifies for the chat on screen. A live message still toasts.
    /// </summary>
    [TestMethod]
    public async Task MyReaction_NoToast_LiveStillToasts()
    {
        // The setting that notifies for the open chat is on, so only the fix keeps the echo quiet.
        await OpenMainAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        App.WaitForText("Chat.Header", "Ann Lee");
        FocusMainWindow();
        InvokeTrayMenu("TrayMenu.Settings");
        App.Find("Settings.NotifyViewedChat").Patterns.Toggle.Pattern.Toggle();
        App.Find("Settings.Back").Patterns.Invoke.Pattern.Invoke();
        WaitForGone("Settings.Back");
        App.WaitForText("Chat.Header", "Ann Lee");
        FocusMainWindow();

        string id = Server.Phone.Chats.Page("c-ann", 1, null).Page[0].MessageID;
        App.Find("Bubble." + id).RightClick();
        App.Find("Bubble.React").Patterns.Invoke.Pattern.Invoke();
        App.Find("ReactionPicker.1").Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => Server.Phone.Reactions.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the reaction.");

        _toasts.AssertNone("Are you free later?", QuietPeriod);
        AssertLiveStillToasts();
    }

    /// <summary>
    /// A sign-out, which is Google ending the session, removes the toasts that show.
    /// </summary>
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
    /// A sign-out removes the banner of a snoozed toast.
    /// </summary>
    /// <remarks>
    /// <c>ToastMemoryTests</c> covers that the clear also cancels the waiting snooze. The shortest snooze that the
    /// settings accept is 5 minutes, which is too long to watch here.
    /// </remarks>
    [TestMethod]
    public async Task SignOut_AfterSnooze_RemovesToast()
    {
        await PairAsync();
        Server.Phone.PushIncoming("c-ann", "snoozed marker");
        AutomationElement toast = _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "snoozed marker");

        AppDriver.Search(toast, e => e.ControlType == ControlType.Button && AppDriver.SafeName(e) == ChatCopy.Snooze)!.Patterns.Invoke.Pattern.Invoke();
        Server.KillLongPoll(401);

        // On an unattended machine, the banner stays until the sign-out clears it.
        Wait.Until(() => _toasts.TryFind("snoozed marker") is null, SignOutTime, () => "The toast stayed after sign-out.");

        // The Snooze button gives the shell window of the toast the foreground, and the window keeps it when empty.
        // Code cannot take the foreground back, so the window of every later test would stay behind.
        // A real click on the title bar of the pairing window returns the foreground.
        // The next click of a person does the same.
        Window pairing                  = WaitForPairing(TimeSpan.FromSeconds(30));
        System.Drawing.Rectangle bounds = pairing.BoundingRectangle;
        int appProcess                  = pairing.Properties.ProcessId.Value;
        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(bounds.X + (bounds.Width / 2), bounds.Y + 16));
        Wait.Until(() => App.Automation.FocusedElement()?.Properties.ProcessId.ValueOrDefault == appProcess, AppDriver.DefaultTimeout,
            () => $"The click on the pairing window's title bar ({bounds}) didn't bring it forward.");
    }

    /// <summary>
    /// Archiving a chat on the phone removes its toast, as reading the chat does.
    /// </summary>
    /// <remarks>
    /// Mute, block and delete use the same path, and <c>NotificationPolicyTests</c> covers them.
    /// <c>ToastMemoryTests</c> covers that the path also cancels a waiting snooze.
    /// </remarks>
    [TestMethod]
    public async Task Archived_RemovesToast()
    {
        await PairAsync();
        Server.Phone.PushIncoming("c-ann", "archive marker");
        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "archive marker");

        Server.Phone.PushConversationStatus("c-ann", ConversationStatus.Archived);

        Wait.Until(() => _toasts.TryFind("archive marker") is null, AppDriver.DefaultTimeout, () => "The toast stayed after the chat was archived.");
    }

    // A live message must still toast. This proves that a "no toast" check before it was meaningful.
    private void AssertLiveStillToasts()
    {
        Server.Phone.PushIncoming("c-ann", "live one");
        _toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "live one");
    }

    // Gives the main window the foreground, so the app sees it as focused.
    private void FocusMainWindow()
    {
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).SetForeground();
    }
}

/// <summary>
/// Toast checks that need a person at the machine. The <c>TestCategory=UI</c> run leaves them out.
/// </summary>
/// <remarks>
/// Windows keeps the banner of a toast on screen while the user is away. Real keyboard or mouse input ends that.
/// UI Automation, a cursor that code moves and a mouse move sent through <c>SendInput</c> do not count as input.
/// On an unattended machine, the banner never leaves. Run these tests with <c>--filter TestCategory=Manual</c>
/// and use the mouse or keyboard now and then. An unattended run ends inconclusive, not failed.
/// </remarks>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class ManualNotificationTests : UiTest
{
    // The Windows default, in seconds, for "Dismiss notifications after" in Settings, Accessibility, Visual effects.
    private const int DefaultMessageSeconds = 5;

    // Extra time after that setting for the slide-out of the banner and for the polling.
    private static readonly TimeSpan BannerMargin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The toast of a message leaves the screen and goes to the Notification Center. This happens within the Windows
    /// "Dismiss notifications after" time plus 5 s, which is 10 s at the default of 5 s. Toasts of other apps do the
    /// same. The run needs real user input, as the remarks of the class explain.
    /// </summary>
    /// <remarks>
    /// The test reads the setting and never writes it. If no keyboard or mouse input comes after the toast shows,
    /// a banner that stays proves nothing. The test is then inconclusive, not failed.
    /// </remarks>
    [TestMethod]
    public async Task Toast_LeavesScreenWithinDismissTime()
    {
        var toasts = new ToastDriver(App.Automation);
        await PairAsync();

        int seconds         = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Accessibility", "MessageDuration", null) is int set ? set : DefaultMessageSeconds;
        TimeSpan bannerTime = TimeSpan.FromSeconds(seconds) + BannerMargin;

        Server.Phone.PushIncoming("c-ann", "hey there");
        toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", "hey there");

        // The banner must leave in time, unless nobody was there to let Windows take it down.
        try
        {
            Wait.Until(() => toasts.TryFind("Ann Lee", "hey there") is null, bannerTime, () => $"The toast was still on screen after {bannerTime.TotalSeconds} s (Windows' setting: {seconds} s).");
        }
        catch (AssertFailedException)
        {
            // The read happens once, outside a filter, so a failed read shows its own message.
            TimeSpan idle = PointerInput.SinceLastInput();
            if (idle < bannerTime)
            {
                throw;
            }

            Assert.Inconclusive($"No keyboard or mouse input for {idle.TotalSeconds:0} s: Windows keeps a banner up while the user is away. Run this while using the machine.");
        }
    }
}

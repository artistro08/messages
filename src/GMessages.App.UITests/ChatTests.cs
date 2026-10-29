using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using GMessages.App.UITests.Driver;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation.Messaging;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace GMessages.App.UITests;

/// <summary>
/// The chat pane: opening a conversation, sending, the failed send and its retry, live messages, and Start chat.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class ChatTests : UiTest
{
    /// <summary>
    /// Opening Ann's unread chat shows her three messages, tells the phone it was read, and her row stops being bold.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task OpenChat_ShowsMessagesAndMarksRead()
    {
        await OpenAnnAsync();

        Wait.Until(() => Server.Phone.Reads.Count(r => r.ConversationID == "c-ann") == 1, AppDriver.DefaultTimeout,
            () => $"The phone got {Server.Phone.Reads.Count(r => r.ConversationID == "c-ann")} reads for c-ann, not 1.");
        Wait.Until(() => !AppDriver.SafeName(App.TryFind("Row.c-ann")).Contains("Unread", StringComparison.Ordinal), AppDriver.DefaultTimeout,
            () => "Ann's row stayed unread after opening her chat.");
        App.WaitForText("Chat.Header", "Ann Lee");
    }

    /// <summary>
    /// Typing a message (Shift + Enter for a new line) and pressing Enter shows the bubble, the phone gets the text,
    /// and the phone's echo turns the same bubble into the sent message (no second bubble).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task TypeAndSend_ShowsBubble_PhoneReceives()
    {
        await OpenAnnAsync();

        TypeInDraft("See you", "at six");

        Wait.Until(() => Server.Phone.Sent.Count == 1, AppDriver.DefaultTimeout, () => "The phone never got the message.");
        string sent = FakePhone.SentText(Server.Phone.Sent[^1]);
        StringAssert.StartsWith(sent, "See you", "The phone got other text.");
        StringAssert.EndsWith(sent, "at six", "Shift + Enter didn't keep the second line in the same message.");
        Assert.IsTrue(sent.Contains('\r', StringComparison.Ordinal) || sent.Contains('\n', StringComparison.Ordinal), "Shift + Enter didn't make a new line.");

        // The Echo Gives The Bubble The Phone's Message ID
        string echo_id = Server.Phone.Chats.Page("c-ann", 1, null).Page[0].MessageID;
        Wait.Until(() => Bubbles().Any(b => b.Id == "Bubble." + echo_id), AppDriver.DefaultTimeout,
            () => $"No bubble Bubble.{echo_id}; bubbles: [{string.Join(", ", Bubbles().Select(b => b.Id))}].");
        Assert.AreEqual(4, Bubbles().Count, "The echo added a second bubble instead of updating the sent one.");
    }

    /// <summary>
    /// A send the phone refuses shows "Not sent. Click to try again."; clicking it sends the text again.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Send_PhoneFails_ShowsNotSent_RetrySends()
    {
        await OpenAnnAsync();
        Server.Phone.FailSends = true;

        TypeInDraft("Running late");
        AutomationElement retry = WaitForRetry();

        // Retry With The Phone Taking It
        Server.Phone.FailSends = false;
        retry.Patterns.Invoke.Pattern.Invoke();

        Wait.Until(() => Server.Phone.Sent.Count == 2, AppDriver.DefaultTimeout, () => $"The phone got {Server.Phone.Sent.Count} sends, not 2.");
        Assert.AreEqual("Running late", FakePhone.SentText(Server.Phone.Sent[^1]), "The retry sent other text.");
        Assert.AreNotEqual(Server.Phone.Sent[0].TmpID, Server.Phone.Sent[^1].TmpID, "The retry reused the failed temporary ID.");
        Wait.Until(() => FindRetry() is null, AppDriver.DefaultTimeout, () => "'Not sent' stayed after the retry went through.");
    }

    /// <summary>
    /// A message arriving in the open chat appends a bubble.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task LiveIncoming_InOpenChat_AppendsBubble()
    {
        await OpenAnnAsync();

        Server.Phone.PushIncoming("c-ann", "Still on?");

        Wait.Until(() => Bubbles().Count == 4 && Bubbles()[^1].Text == "Still on?", AppDriver.DefaultTimeout,
            () => $"Bubbles: [{string.Join(" | ", Bubbles().Select(b => b.Text))}].");
    }

    /// <summary>
    /// Start chat: typing a name filters the contacts, picking Ann and pressing Go opens her conversation in place
    /// of the Start chat pane.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task StartChat_PickContact_OpensConversation()
    {
        // From An Open Chat: Start Chat Takes Its Place
        await OpenAnnAsync();
        App.Find("Main.StartChat").Patterns.Invoke.Pattern.Invoke();
        App.Find("StartChat.Query").Patterns.Value.Pattern.SetValue("Ann");
        Wait.Until(() => App.TryFind("Chat.Draft") is null && App.TryFind("Main.EmptyState") is null, AppDriver.DefaultTimeout,
            () => "The chat pane or the empty state still showed under Start chat.");

        // Only Ann Matches
        Wait.Until(() => AppDriver.SafeName(App.TryFind("Contact.0")).Contains("Ann Lee", StringComparison.Ordinal) && App.TryFind("Contact.1") is null,
            AppDriver.DefaultTimeout, () => $"Contact.0 was '{AppDriver.SafeName(App.TryFind("Contact.0"))}', Contact.1 was '{AppDriver.SafeName(App.TryFind("Contact.1"))}'.");
        App.Find("Contact.0").Click();

        // Go Opens Her Conversation
        AutomationElement go = App.Find("StartChat.Go");
        Wait.Until(() => go.IsEnabled, AppDriver.DefaultTimeout, () => "Go stayed disabled after picking Ann.");
        go.Patterns.Invoke.Pattern.Invoke();

        App.WaitForText("Chat.Header", "Ann Lee");
        Wait.Until(() => App.TryFind("StartChat.Query") is null, AppDriver.DefaultTimeout, () => "The Start chat pane stayed open.");
        Wait.Until(() => Bubbles().Count == 3, AppDriver.DefaultTimeout, () => $"Ann's chat showed {Bubbles().Count} bubbles, not 3.");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Opens The Main Window, Then Ann's Chat, And Waits For Her Three Messages
    private async Task OpenAnnAsync()
    {
        await OpenMainAsync();
        App.Find("Row.c-ann").Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => Bubbles().Count == 3, AppDriver.DefaultTimeout, () => $"Ann's chat showed {Bubbles().Count} bubbles, not 3.");
    }

    // Types Each Line In The Message Box, Shift + Enter Between Them, Then Enter To Send
    private void TypeInDraft(params string[] lines)
    {
        App.Find("Chat.Draft").Focus();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.RETURN);
            }

            Keyboard.Type(lines[i]);
        }

        Keyboard.Type(VirtualKeyShort.RETURN);
    }

    // The Bubbles In The Open Chat, Top To Bottom: AutomationId And Text
    private List<(string Id, string Text)> Bubbles()
    {
        AutomationElement? list = App.TryFind("Chat.Messages");
        if (list is null)
        {
            return [];
        }

        try
        {
            return [.. list.FindAllDescendants()
                .Select(e => (Id: e.Properties.AutomationId.ValueOrDefault ?? "", Text: AppDriver.SafeName(e)))
                .Where(b => b.Id.StartsWith("Bubble.", StringComparison.Ordinal) && !b.Id.EndsWith(".Retry", StringComparison.Ordinal))];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            // The List Changed Mid-Walk; Read Again
            return [];
        }
    }

    // The Failed Bubble's "Not sent" Button, Or Null
    private AutomationElement? FindRetry() =>
        App.TryFind("Chat.Messages") is { } list ? AppDriver.Search(list, e => AppDriver.SafeName(e) == ChatCopy.NotSentRetry) : null;

    private AutomationElement WaitForRetry()
    {
        AutomationElement? retry = null;
        Wait.Until(() => (retry = FindRetry()) is not null, AppDriver.DefaultTimeout, () => $"'{ChatCopy.NotSentRetry}' never showed.");
        return retry!;
    }
}

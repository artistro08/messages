using GMessages.Presentation.Connection;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;

namespace GMessages.Presentation.Tests.Main;

[TestClass]
public sealed class MainViewModelTests
{
    private static async Task<MainViewModel> NewMainAsync(MainRig rig)
    {
        rig.Page(MainRig.Conv("a", "Ann", 1), MainRig.Conv("b", "Bo", 2), MainRig.Conv("c", "Cy", 3));
        var vm = new MainViewModel(rig.Controller, rig.Store, TimeProvider.System, a => a());
        await vm.Inbox.LoadAsync();
        return vm;
    }

    [TestMethod]
    public async Task Escape_ClearsSelection()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        vm.Select("a");

        vm.ExitConversation();

        Assert.IsNull(vm.SelectedConversationId);
    }

    [TestMethod]
    public async Task Narrow_SelectShowsChat_EscapeShowsList()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        vm.IsNarrow = true;
        Assert.IsFalse(vm.ShowChatPane);

        vm.Select("a");
        Assert.IsTrue(vm.ShowChatPane);

        vm.ExitConversation();
        Assert.IsFalse(vm.ShowChatPane);

        vm.OpenStartChat();
        Assert.IsTrue(vm.IsStartChatOpen);
        Assert.IsTrue(vm.ShowChatPane);

        // Wide: Never Hides The List
        vm.IsNarrow = false;
        Assert.IsFalse(vm.ShowChatPane);
    }

    [TestMethod]
    public async Task NextPrevious_MoveThroughRows_StopAtEnds()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);

        vm.NextConversation();
        Assert.AreEqual("a", vm.SelectedConversationId);

        vm.PreviousConversation();
        Assert.AreEqual("a", vm.SelectedConversationId);

        vm.NextConversation();
        vm.NextConversation();
        vm.NextConversation();
        Assert.AreEqual("c", vm.SelectedConversationId);

        vm.PreviousConversation();
        Assert.AreEqual("b", vm.SelectedConversationId);
    }

    [TestMethod]
    public async Task SpamView_SwitchesCurrentList_BackReturns()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        Assert.IsNull(vm.SpamBlocked);
        Assert.AreSame(vm.Inbox, vm.CurrentList);

        vm.OpenSpamBlocked();

        Assert.IsTrue(vm.IsSpamView);
        Assert.IsNotNull(vm.SpamBlocked);
        Assert.AreSame(vm.SpamBlocked, vm.CurrentList);

        vm.BackToInbox();

        Assert.IsFalse(vm.IsSpamView);
        Assert.AreSame(vm.Inbox, vm.CurrentList);
    }

    /// <summary>
    /// The archived list is made and loaded once, shows in place of the inbox, reloads with the other lists, and Back
    /// returns to the inbox.
    /// </summary>
    [TestMethod]
    public async Task OpenArchived_LoadsArchiveFolder_BackToInbox()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        rig.Page(MainRig.Conv("a", "Ann", 1), MainRig.Conv("z", "Zed", 2, status: ConversationStatus.Archived));
        Assert.IsFalse(vm.IsFolderView);

        vm.OpenArchived();

        Assert.AreEqual(ConversationFolder.Archived, vm.Folder);
        Assert.IsTrue(vm.IsArchivedView);
        Assert.IsTrue(vm.IsFolderView);
        Assert.IsFalse(vm.IsSpamView);
        Assert.AreNotSame(vm.Inbox, vm.CurrentList);
        Assert.AreEqual("z", string.Join(",", vm.CurrentList.Rows.Select(r => r.Id)));
        Assert.AreEqual(ListConversationsRequest.Types.Folder.Archive, rig.Client.ListCalls.Last().Folder);

        // Opening Again Keeps The List And Loads Nothing
        ConversationListViewModel archived = vm.CurrentList;
        int calls                          = rig.Client.ListCalls.Count;
        vm.BackToInbox();
        Assert.AreEqual(ConversationFolder.Inbox, vm.Folder);
        Assert.IsFalse(vm.IsArchivedView);
        Assert.IsFalse(vm.IsFolderView);
        Assert.AreSame(vm.Inbox, vm.CurrentList);

        vm.OpenArchived();
        Assert.AreSame(archived, vm.CurrentList);
        Assert.AreEqual(calls, rig.Client.ListCalls.Count);

        // The Spam List Is Its Own
        vm.OpenSpamBlocked();
        Assert.IsTrue(vm.IsSpamView);
        Assert.IsFalse(vm.IsArchivedView);
        Assert.AreSame(vm.SpamBlocked, vm.CurrentList);
        Assert.AreNotSame(archived, vm.CurrentList);

        // Back Online: The Inbox, Archived And Spam Lists Reload, Once Each
        calls = rig.Client.ListCalls.Count;
        vm.ApplyState(ConnectionState.Reconnecting);
        vm.ApplyState(ConnectionState.Connected);
        Assert.AreEqual(calls + 3, rig.Client.ListCalls.Count);
    }

    /// <summary>
    /// An action that moves the open conversation out of its folder closes the chat; one on another conversation
    /// leaves the open chat alone.
    /// </summary>
    [TestMethod]
    [DataRow(ConversationAction.Archive)]
    [DataRow(ConversationAction.Delete)]
    [DataRow(ConversationAction.BlockAndReport)]
    public async Task Run_MovesOpenChatOut_ClosesIt(ConversationAction action)
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        vm.Select("b");

        Assert.IsTrue(await vm.RunAsync("c", action));
        Assert.AreEqual("b", vm.SelectedConversationId);
        Assert.IsNotNull(vm.Chat);

        Assert.IsTrue(await vm.RunAsync("b", action));
        Assert.IsNull(vm.SelectedConversationId);
        Assert.IsNull(vm.Chat);
        Assert.AreEqual("a", string.Join(",", vm.Inbox.Rows.Select(r => r.Id)));
        Assert.IsNull(vm.ActionError);
    }

    /// <summary>
    /// Mute and unmute keep the open chat, whose mute state follows.
    /// </summary>
    [TestMethod]
    public async Task Run_Mute_KeepsChatOpen()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        vm.Select("a");
        ChatViewModel chat = vm.Chat!;
        Assert.IsFalse(chat.IsMuted);
        Assert.AreEqual("a", chat.Conversation!.Id);

        Assert.IsTrue(await vm.RunAsync("a", ConversationAction.Mute));
        Assert.AreSame(chat, vm.Chat);
        Assert.IsTrue(chat.IsMuted);
        Assert.IsTrue(chat.Conversation!.IsMuted);
        Assert.IsTrue(vm.FindConversation("a")!.IsMuted);

        Assert.IsTrue(await vm.RunAsync("a", ConversationAction.Unmute));
        Assert.AreSame(chat, vm.Chat);
        Assert.IsFalse(chat.IsMuted);
        Assert.IsNull(vm.FindConversation("nobody"));
    }

    /// <summary>
    /// A refused or impossible update shows the failure banner and keeps the chat open; the next success or Dismiss
    /// clears it.
    /// </summary>
    [TestMethod]
    public async Task Run_Fails_SetsActionError_ChatStays()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        vm.Select("a");
        rig.Client.RefuseUpdates = true;

        Assert.IsFalse(await vm.RunAsync("a", ConversationAction.Archive));
        Assert.AreEqual(ChatCopy.ActionFailed, vm.ActionError);
        Assert.IsTrue(vm.HasActionError);
        Assert.AreEqual("a", vm.SelectedConversationId);
        Assert.AreEqual("a,b,c", string.Join(",", vm.Inbox.Rows.Select(r => r.Id)));

        // Success Clears It
        rig.Client.RefuseUpdates = false;
        Assert.IsTrue(await vm.RunAsync("a", ConversationAction.Mute));
        Assert.IsNull(vm.ActionError);
        Assert.IsFalse(vm.HasActionError);

        // Not Connected: Logged Once, By Error Type Only
        rig.Connected = false;
        int logged    = rig.Log.Messages.Length;
        Assert.IsFalse(await vm.RunAsync("a", ConversationAction.Delete));
        Assert.AreEqual(ChatCopy.ActionFailed, vm.ActionError);
        Assert.AreEqual("a", vm.SelectedConversationId);
        Assert.AreEqual("Could not update conversation (MessagingUnavailableException)", rig.Log.Messages.Skip(logged).Single());

        vm.DismissActionError();
        Assert.IsNull(vm.ActionError);
        Assert.IsFalse(vm.HasActionError);
    }

    /// <summary>
    /// A second run on a conversation still waiting on the phone sends nothing and shows no error; another conversation
    /// isn't held up, and once the first answers the conversation can run again.
    /// </summary>
    [TestMethod]
    public async Task Run_SameConversationTwice_OneRequest()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        var gate              = new TaskCompletionSource();
        rig.Client.UpdateGate = gate;

        Task<bool> first = vm.RunAsync("a", ConversationAction.Archive);
        Assert.IsFalse(await vm.RunAsync("a", ConversationAction.Archive));
        Assert.IsNull(vm.ActionError);
        Assert.HasCount(1, rig.Client.ConversationUpdates);

        Assert.IsTrue(await vm.RunAsync("b", ConversationAction.Mute));
        Assert.HasCount(2, rig.Client.ConversationUpdates);

        gate.SetResult();
        Assert.IsTrue(await first);

        Assert.IsTrue(await vm.RunAsync("a", ConversationAction.Unarchive));
        Assert.HasCount(3, rig.Client.ConversationUpdates);
    }

    /// <summary>
    /// Reaching Connected (and only the arrival, not staying there) tells the window to retry its photos.
    /// </summary>
    [TestMethod]
    public async Task Connected_RaisedOnArrivalOnly()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        int raised = 0;
        vm.Connected += (_, _) => raised++;

        vm.ApplyState(ConnectionState.Reconnecting);
        vm.ApplyState(ConnectionState.Connected);
        vm.ApplyState(ConnectionState.Connected);
        Assert.AreEqual(1, raised);

        vm.ApplyState(ConnectionState.Reconnecting);
        vm.ApplyState(ConnectionState.Connected);
        Assert.AreEqual(2, raised);
    }

    [TestMethod]
    public async Task Banner_FollowsStateAndPhoneEvents()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);

        vm.ApplyState(ConnectionState.Connecting);
        Assert.AreEqual(ChatCopy.Connecting, vm.StatusBanner);
        Assert.IsTrue(vm.HasStatusBanner);

        vm.ApplyState(ConnectionState.Connected);
        Assert.IsNull(vm.StatusBanner);
        Assert.IsFalse(vm.HasStatusBanner);

        vm.ApplyEvent(new PhoneNotResponding());
        Assert.AreEqual(ChatCopy.WaitingForPhone, vm.StatusBanner);

        vm.ApplyEvent(new PhoneRespondingAgain());
        Assert.IsNull(vm.StatusBanner);

        vm.ApplyState(ConnectionState.Reconnecting);
        Assert.AreEqual(ChatCopy.Connecting, vm.StatusBanner);

        // Stale Silence Doesn't Survive A Reconnect
        vm.ApplyState(ConnectionState.Connected);
        vm.ApplyEvent(new PhoneNotResponding());
        vm.ApplyState(ConnectionState.Reconnecting);
        vm.ApplyState(ConnectionState.Connected);
        Assert.IsNull(vm.StatusBanner);
    }

    /// <summary>
    /// A list load that failed while the phone slept fills once the phone answers again, and again when the connection
    /// comes back; both lists reload.
    /// </summary>
    [TestMethod]
    public async Task PhoneRespondingAgainOrConnected_ReloadsBothLists()
    {
        using var rig = new MainRig();
        rig.Client.ListThrows = new PhoneNotRespondingException();
        using var vm          = new MainViewModel(rig.Controller, rig.Store, TimeProvider.System, a => a());
        await vm.Inbox.LoadAsync();
        vm.OpenSpamBlocked();
        Assert.AreEqual(0, vm.Inbox.Rows.Count);

        // The Phone Answers Again
        rig.Client.ListThrows = null;
        rig.Page(MainRig.Conv("a", "Ann", 1), MainRig.Conv("s", "Spammer", 2, status: ConversationStatus.SpamFolder));
        int calls = rig.Client.ListCalls.Count;
        vm.ApplyEvent(new PhoneNotResponding());
        vm.ApplyEvent(new PhoneRespondingAgain());
        Assert.AreEqual("a", string.Join(",", vm.Inbox.Rows.Select(r => r.Id)));
        Assert.AreEqual("s", string.Join(",", vm.SpamBlocked!.Rows.Select(r => r.Id)));
        Assert.AreEqual(calls + 2, rig.Client.ListCalls.Count, "the inbox and spam lists, once each");

        // Back To Connected
        rig.Page(MainRig.Conv("a", "Ann", 1), MainRig.Conv("b", "Bo", 2));
        vm.ApplyState(ConnectionState.Reconnecting);
        vm.ApplyState(ConnectionState.Connected);
        Assert.AreEqual("a,b", string.Join(",", vm.Inbox.Rows.Select(r => r.Id)));
        Assert.AreEqual(calls + 4, rig.Client.ListCalls.Count);
    }

    /// <summary>
    /// A Start chat that finishes after the window closed (the view model is disposed) opens nothing: no chat is made,
    /// so nothing stays subscribed to the store or fetches messages.
    /// </summary>
    [TestMethod]
    public async Task Disposed_SelectAndOpenDoNothing()
    {
        using var rig = new MainRig();
        MainViewModel vm = await NewMainAsync(rig);
        vm.OpenStartChat();
        int list_calls = rig.Client.ListCalls.Count;
        vm.Dispose();

        vm.Select("new-1");
        vm.OpenStartChat();
        vm.OpenSpamBlocked();
        vm.OpenArchived();
        vm.ApplyState(ConnectionState.Reconnecting);
        vm.ApplyState(ConnectionState.Connected);
        vm.ApplyEvent(new PhoneRespondingAgain());
        Assert.IsFalse(await vm.RunAsync("a", ConversationAction.Archive));

        Assert.IsEmpty(rig.Client.ConversationUpdates, "no actions sent after closing");
        Assert.AreEqual(ConversationFolder.Inbox, vm.Folder);
        Assert.IsNull(vm.Chat);
        Assert.IsNull(vm.SelectedConversationId);
        Assert.IsNull(vm.SpamBlocked);
        Assert.AreEqual(0, rig.Client.FetchCount, "no messages fetched for a chat nobody sees");
        Assert.AreEqual(list_calls, rig.Client.ListCalls.Count, "no list reloads after closing");
    }

    [TestMethod]
    public async Task Select_CreatesChat_NewSelectionReplaces_ExitClears()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        Assert.IsNull(vm.Chat);

        Assert.IsFalse(vm.IsChatOpen);

        vm.Select("a");
        ChatViewModel first = vm.Chat!;
        Assert.AreEqual("a", first.ConversationId);
        Assert.IsTrue(vm.IsChatOpen);

        // Same Conversation Again Keeps The Chat
        vm.Select("a");
        Assert.AreSame(first, vm.Chat);

        vm.Select("b");
        Assert.AreEqual("b", vm.Chat!.ConversationId);
        Assert.AreNotSame(first, vm.Chat);

        vm.ExitConversation();
        Assert.IsNull(vm.Chat);
        Assert.IsFalse(vm.IsChatOpen);

        vm.Select("a");
        vm.OpenStartChat();
        Assert.IsNull(vm.Chat);
        Assert.IsFalse(vm.IsChatOpen);
    }

    [TestMethod]
    public async Task OpenStartChat_MakesPane_SelectAndExitDropIt()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        Assert.IsNull(vm.StartChat);

        vm.OpenStartChat();
        StartChatViewModel first = vm.StartChat!;

        // Opening Again Keeps The Pane (And What Was Typed)
        vm.OpenStartChat();
        Assert.AreSame(first, vm.StartChat);

        vm.Select("a");
        Assert.IsNull(vm.StartChat);

        vm.OpenStartChat();
        Assert.AreNotSame(first, vm.StartChat);
        vm.ExitConversation();
        Assert.IsNull(vm.StartChat);
    }

    [TestMethod]
    public async Task Chat_IsActiveOnlyWhenWindowFocused()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        vm.Select("a");
        Assert.IsFalse(vm.Chat!.IsActive);

        vm.IsWindowFocused = true;
        Assert.IsTrue(vm.Chat.IsActive);

        vm.Select("b");
        Assert.IsTrue(vm.Chat.IsActive);

        vm.IsWindowFocused = false;
        Assert.IsFalse(vm.Chat.IsActive);
    }

    [TestMethod]
    public async Task ToggleList_Flips()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);

        vm.ToggleList();
        Assert.IsTrue(vm.IsListCollapsed);

        vm.ToggleList();
        Assert.IsFalse(vm.IsListCollapsed);
    }

    /// <summary>
    /// Each chat keeps its own draft while the window is open: switching chats or closing one with Escape and opening
    /// it again brings back what was typed.
    /// </summary>
    [TestMethod]
    public async Task Drafts_KeptPerConversation()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);

        vm.Select("a");
        vm.Chat!.Draft = "for ann";
        vm.Select("b");
        Assert.AreEqual(string.Empty, vm.Chat!.Draft);
        vm.Chat.Draft = "for bo";

        vm.Select("a");
        Assert.AreEqual("for ann", vm.Chat!.Draft);

        vm.ExitConversation();
        vm.Select("b");
        Assert.AreEqual("for bo", vm.Chat!.Draft);

        // Sent (Cleared) Drafts Are Forgotten
        vm.Chat.Draft = string.Empty;
        vm.Select("a");
        vm.Select("b");
        Assert.AreEqual(string.Empty, vm.Chat!.Draft);
    }

    private static UserAlert Alert(AlertType type) => new(new UserAlertEvent { AlertType = type });

    /// <summary>A window opened after the alert shows the banner right away.</summary>
    [TestMethod]
    public async Task MainViewModel_NewWindow_ReadsBatteryState()
    {
        using var rig = new MainRig();
        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryLow));

        using MainViewModel vm = await NewMainAsync(rig);

        Assert.IsTrue(vm.IsPhoneBatteryLow);
    }

    /// <summary>Closing the banner hides it, and the next low alert shows it again even though the store was already low.</summary>
    [TestMethod]
    public async Task BatteryLow_ClosedThenNewLow_ShowsAgain()
    {
        using var rig = new MainRig();
        using MainViewModel vm = await NewMainAsync(rig);
        Assert.IsFalse(vm.IsPhoneBatteryLow);

        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryLow));
        Assert.IsTrue(vm.IsPhoneBatteryLow);

        vm.DismissPhoneBattery();
        Assert.IsFalse(vm.IsPhoneBatteryLow);

        // Restored Then Low Again
        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryRestored));
        await rig.Store.HandleEventAsync(Alert(AlertType.MobileBatteryLow));
        Assert.IsTrue(vm.IsPhoneBatteryLow);
    }
}

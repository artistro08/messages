using GMessages.Presentation.Connection;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;

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
}

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

        vm.ApplyState(ConnectionState.Connected);
        Assert.IsNull(vm.StatusBanner);

        vm.ApplyEvent(new PhoneNotResponding());
        Assert.AreEqual(ChatCopy.WaitingForPhone, vm.StatusBanner);

        vm.ApplyEvent(new PhoneRespondingAgain());
        Assert.IsNull(vm.StatusBanner);

        vm.ApplyState(ConnectionState.Reconnecting);
        Assert.AreEqual(ChatCopy.Connecting, vm.StatusBanner);
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

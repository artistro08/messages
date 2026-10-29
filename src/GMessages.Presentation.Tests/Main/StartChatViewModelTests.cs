using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Main;

[TestClass]
public sealed class StartChatViewModelTests
{
    private static Contact Person(string id, string name, string number) => new()
    {
        ContactID = id,
        Name      = name,
        Number    = new ContactNumber { MysteriousInt = 2, Number = number, FormattedNumber = number },
    };

    private static async Task<StartChatViewModel> NewPaneAsync(MainRig rig)
    {
        rig.Client.TopContactsAnswer.Contacts.Add(Person("c1", "Ann Lee", "+1 (555) 010-0001"));
        rig.Client.ContactsAnswer.Contacts.Add(Person("c1", "Ann Lee", "+1 (555) 010-0001"));
        rig.Client.ContactsAnswer.Contacts.Add(Person("c2", "Bob Stone", "+1 (555) 020-0002"));
        rig.Client.ContactsAnswer.Contacts.Add(Person("c3", "annabel", "+1 (555) 030-0003"));

        var vm = new StartChatViewModel(rig.Store, a => a());
        await vm.LoadAsync();
        return vm;
    }

    private static string Names(StartChatViewModel vm) => string.Join("|", vm.Results.Select(r => r.Name));

    [TestMethod]
    public async Task Query_FiltersByNameAndDigits()
    {
        using var rig = new MainRig();
        StartChatViewModel vm = await NewPaneAsync(rig);

        // Top Contact First, Rest After, No Duplicate
        Assert.AreEqual("Ann Lee|Bob Stone|annabel", Names(vm));

        vm.Query = "ANN";
        Assert.AreEqual("Ann Lee|annabel", Names(vm));

        // Digits Only, Punctuation In The Number Ignored
        vm.Query = "020-00";
        Assert.AreEqual("Send to 020-00|Bob Stone", Names(vm));
    }

    [TestMethod]
    public async Task PhoneLikeQuery_AddsSendToRow()
    {
        using var rig = new MainRig();
        StartChatViewModel vm = await NewPaneAsync(rig);

        vm.Query = "+15559998888";
        Assert.IsTrue(vm.Results[0].IsSendTo);
        Assert.AreEqual("Send to +15559998888", vm.Results[0].Name);

        vm.Query = "me@example.com";
        Assert.IsTrue(vm.Results[0].IsSendTo);

        vm.Query = "Bob";
        Assert.IsFalse(vm.Results.Any(r => r.IsSendTo));

        // Picking The Send-To Row Makes A Chip And Clears The Query
        vm.Query = "+15559998888";
        vm.Toggle(vm.Results[0]);
        Assert.AreEqual("+15559998888", vm.Selected.Single().Number);
        Assert.AreEqual(string.Empty, vm.Query);
        Assert.IsTrue(vm.CanStart);
    }

    [TestMethod]
    public async Task Start_OneContact_SingleNumber()
    {
        using var rig = new MainRig();
        StartChatViewModel vm = await NewPaneAsync(rig);
        rig.Client.CreateAnswers.Enqueue(new GetOrCreateConversationResponse
        {
            Status       = GetOrCreateConversationResponse.Types.Status.Success,
            Conversation = new Conversation { ConversationID = "new-1" },
        });
        vm.Toggle(vm.Results[1]);

        string id = await vm.StartAsync();

        Assert.AreEqual("new-1", id);
        GetOrCreateConversationRequest req = rig.Client.CreateCalls.Single();
        ContactNumber n                    = req.Numbers.Single();
        Assert.AreEqual(2, n.MysteriousInt);
        Assert.AreEqual("+1 (555) 020-0002", n.Number);
        Assert.AreEqual(n.Number, n.Number2);
        Assert.IsFalse(req.CreateRCSGroup);
    }

    [TestMethod]
    public async Task Start_CreateRcs_RetriesWithGroupFlag()
    {
        using var rig = new MainRig();
        StartChatViewModel vm = await NewPaneAsync(rig);
        rig.Client.CreateAnswers.Enqueue(new GetOrCreateConversationResponse { Status = GetOrCreateConversationResponse.Types.Status.CreateRcs });
        rig.Client.CreateAnswers.Enqueue(new GetOrCreateConversationResponse
        {
            Status       = GetOrCreateConversationResponse.Types.Status.Success,
            Conversation = new Conversation { ConversationID = "grp-1" },
        });
        vm.Toggle(vm.Results[0]);
        vm.Toggle(vm.Results[1]);

        string id = await vm.StartAsync();

        Assert.AreEqual("grp-1", id);
        Assert.AreEqual(2, rig.Client.CreateCalls.Count);
        Assert.AreEqual(2, rig.Client.CreateCalls[0].Numbers.Count);
        Assert.IsFalse(rig.Client.CreateCalls[0].CreateRCSGroup);
        Assert.IsTrue(rig.Client.CreateCalls[1].CreateRCSGroup);
        Assert.AreEqual(string.Empty, rig.Client.CreateCalls[1].RCSGroupName);
        Assert.AreEqual(2, rig.Client.CreateCalls[1].Numbers.Count);
    }

    [TestMethod]
    public async Task Toggle_Twice_RemovesChip()
    {
        using var rig = new MainRig();
        StartChatViewModel vm = await NewPaneAsync(rig);

        vm.Toggle(vm.Results[0]);
        Assert.IsTrue(vm.CanStart);

        vm.Toggle(vm.Results[0]);
        Assert.IsFalse(vm.CanStart);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(vm.StartAsync);
    }
}

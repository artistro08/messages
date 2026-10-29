using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;

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
        Assert.AreEqual(string.Empty, vm.Results[0].Initial, "A typed number has no name, so it gets the person icon.");

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

        string? id = await vm.StartAsync();

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

        string? id = await vm.StartAsync();

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

    [TestMethod]
    public async Task Start_NotSuccessAfterRetry_ShowsError()
    {
        using var rig = new MainRig();
        StartChatViewModel vm = await NewPaneAsync(rig);
        rig.Client.CreateAnswers.Enqueue(new GetOrCreateConversationResponse { Status = GetOrCreateConversationResponse.Types.Status.CreateRcs });
        rig.Client.CreateAnswers.Enqueue(new GetOrCreateConversationResponse { Status = GetOrCreateConversationResponse.Types.Status.CreateRcs });
        vm.Toggle(vm.Results[0]);

        Assert.IsNull(await vm.StartAsync());
        Assert.AreEqual(ChatCopy.StartFailed, vm.Error);
    }

    /// <summary>
    /// R11: a start the phone can't do says so in the pane, keeps everyone picked, and the next attempt clears it.
    /// </summary>
    /// <summary>
    /// Go never throws at the click handler: every failure (network, timeout, server, decode, phone) shows the error and
    /// keeps everyone picked.
    /// </summary>
    [TestMethod]
    public async Task Start_AnyFailure_ShowsError_NeverThrows()
    {
        foreach (Exception failure in MainRig.Failures())
        {
            using var rig = new MainRig();
            StartChatViewModel vm = await NewPaneAsync(rig);
            vm.Toggle(vm.Results[0]);
            rig.Client.CreateThrows = failure;

            string name = failure.GetType().Name;
            Assert.IsNull(await vm.StartAsync(), name);
            Assert.AreEqual(ChatCopy.StartFailed, vm.Error, name);
            Assert.IsTrue(vm.CanStart, name);
        }
    }

    [TestMethod]
    public async Task Start_PhoneProblem_ShowsError_NextAttemptClears()
    {
        using var rig = new MainRig();
        StartChatViewModel vm = await NewPaneAsync(rig);
        vm.Toggle(vm.Results[0]);
        Assert.IsNull(vm.Error);
        Assert.IsFalse(vm.HasError);

        rig.Client.CreateThrows = new PhoneNotRespondingException();
        Assert.IsNull(await vm.StartAsync());
        Assert.AreEqual("Couldn't start this conversation. Try again.", vm.Error);
        Assert.IsTrue(vm.HasError);
        Assert.IsTrue(vm.CanStart, "still picked");

        // The Next Attempt Clears It Before Asking (And It Stays Clear On Success)
        string? seen = "unset";
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StartChatViewModel.Error) && seen == "unset")
            {
                seen = vm.Error;
            }
        };
        rig.Client.CreateThrows = null;
        rig.Client.CreateAnswers.Enqueue(new GetOrCreateConversationResponse
        {
            Status       = GetOrCreateConversationResponse.Types.Status.Success,
            Conversation = new Conversation { ConversationID = "new-2" },
        });

        Assert.AreEqual("new-2", await vm.StartAsync());
        Assert.IsNull(seen);
        Assert.IsNull(vm.Error);
        Assert.IsFalse(vm.HasError);
    }

    /// <summary>
    /// A phone that fails the contact load (not answering, connection closed) is no crash; the next open loads again.
    /// </summary>
    [TestMethod]
    public async Task Load_PhoneProblem_NextLoadRetries()
    {
        using var rig = new MainRig();
        rig.Client.TopContactsAnswer.Contacts.Add(Person("c1", "Ann Lee", "+1 (555) 010-0001"));
        rig.Client.ContactsAnswer.Contacts.Add(Person("c2", "Bob Stone", "+1 (555) 020-0002"));
        rig.Client.ContactsThrows = new ConnectionClosedException();
        var vm = new StartChatViewModel(rig.Store, a => a());

        await vm.LoadAsync();
        Assert.AreEqual("Ann Lee", Names(vm));

        rig.Client.ContactsThrows = null;
        await vm.LoadAsync();
        Assert.AreEqual("Ann Lee|Bob Stone", Names(vm));
    }

    /// <summary>
    /// A refresh that matches the same rows (the full contact list arriving after the top contacts) leaves the list
    /// alone, so a row being clicked isn't swapped out from under the click.
    /// </summary>
    [TestMethod]
    public async Task Refresh_SameRows_LeavesListAlone()
    {
        using var rig = new MainRig();
        StartChatViewModel vm = await NewPaneAsync(rig);
        vm.Query                = "Ann";
        ContactRowViewModel ann = vm.Results[0];
        int changes             = 0;
        vm.Results.CollectionChanged += (_, _) => changes++;

        vm.Query = "Ann";

        Assert.AreEqual(0, changes);
        Assert.AreSame(ann, vm.Results[0]);
    }

    /// <summary>
    /// The loading ring shows while the contacts load and while Go looks up the conversation.
    /// </summary>
    [TestMethod]
    public async Task IsLoading_WhileContactsLoadAndWhileGoLooksUp()
    {
        using var rig = new MainRig();
        rig.Client.TopContactsAnswer.Contacts.Add(Person("c1", "Ann Lee", "+1 (555) 010-0001"));
        var vm                     = new StartChatViewModel(rig.Store, a => a());
        var contacts_gate          = new TaskCompletionSource();
        rig.Client.TopContactsGate  = contacts_gate;

        Task load = vm.LoadAsync();
        Assert.IsTrue(vm.IsLoading);
        contacts_gate.SetResult();
        await load;
        Assert.IsFalse(vm.IsLoading);

        vm.Toggle(vm.Results[0]);
        var create_gate       = new TaskCompletionSource();
        rig.Client.CreateGate = create_gate;
        rig.Client.CreateAnswers.Enqueue(new GetOrCreateConversationResponse
        {
            Status       = GetOrCreateConversationResponse.Types.Status.Success,
            Conversation = new Conversation { ConversationID = "new-1" },
        });

        Task<string?> start = vm.StartAsync();
        Assert.IsTrue(vm.IsLoading);
        create_gate.SetResult();
        Assert.AreEqual("new-1", await start);
        Assert.IsFalse(vm.IsLoading);
    }
}

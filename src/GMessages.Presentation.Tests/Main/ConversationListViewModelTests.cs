using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Tests.Main;

[TestClass]
public sealed class ConversationListViewModelTests
{
    private static ConversationListViewModel NewList(MainRig rig, ConversationFolder folder = ConversationFolder.Inbox)
        => new(rig.Store, folder, TimeProvider.System, a => a());

    private static string Ids(ConversationListViewModel list) => string.Join(",", list.Rows.Select(r => r.Id));

    [TestMethod]
    public async Task Load_SortsPinnedFirstThenNewest()
    {
        using var rig = new MainRig();
        rig.Page(
            MainRig.Conv("old", "Old", minutesAgo: 90),
            MainRig.Conv("new", "New", minutesAgo: 1),
            MainRig.Conv("pin", "Pin", minutesAgo: 600, pinned: true));
        using var list = NewList(rig);

        await list.LoadAsync();

        Assert.AreEqual("pin,new,old", Ids(list));
        Assert.IsFalse(list.IsLoading);
        Assert.AreEqual("P", list.Rows[0].Initial);
        Assert.IsTrue(list.Rows[0].IsPinned);
        Assert.AreEqual("p-pin", list.Rows[0].ParticipantIds.Single());
    }

    [TestMethod]
    public void Row_ToString_NamesAndFlagsUnread()
    {
        ConversationInfo read   = ConversationInfo.From(MainRig.Conv("a", "Ann"));
        ConversationInfo unread = read with { Unread = true };

        Assert.AreEqual("Ann", new ConversationRowViewModel(read, MainRig.Now).ToString());
        Assert.AreEqual("Ann, Unread", new ConversationRowViewModel(unread, MainRig.Now).ToString());
    }

    [TestMethod]
    public void Row_Avatar_OneOnOneHasOnePhoto_GroupStacksTwoPeople()
    {
        ParticipantInfo me    = new("me", "Me", "Me", "", true, null);
        ParticipantInfo bo    = new("p-bo", "Bo Ng", "Bo", "#1a73e8", false, null);
        ParticipantInfo cy    = new("p-cy", "", "", "", false, "+1 555");
        ConversationInfo c    = ConversationInfo.From(MainRig.Conv("a", "Ann")) with { AvatarHexColor = "#00ff00", Participants = [me, bo] };
        ConversationInfo gr   = c with { Name = "Team", IsGroup = true, Participants = [me, bo, cy] };
        ConversationInfo bare = gr with { Participants = [me] };

        var one   = new ConversationRowViewModel(c, MainRig.Now);
        var group = new ConversationRowViewModel(gr, MainRig.Now);
        var alone = new ConversationRowViewModel(bare, MainRig.Now);

        // One On One: The Conversation's Letter And Color, The Other Person's Photo
        Assert.AreEqual("A", one.Initial);
        Assert.AreEqual(0xFF00FF00u, one.AvatarColor);
        Assert.AreEqual("p-bo", one.PhotoId);
        Assert.AreEqual(string.Empty, one.SecondPhotoId);
        Assert.AreEqual(0u, one.SecondColor);

        // R12 Group: Front Circle Is The First Other Person, Back Circle The Second
        Assert.AreEqual("B", group.Initial);
        Assert.AreEqual(0xFF1A73E8u, group.AvatarColor);
        Assert.AreEqual("p-bo", group.PhotoId);
        Assert.AreEqual("+", group.SecondInitial);
        Assert.AreNotEqual(0u, group.SecondColor);
        Assert.AreEqual("p-cy", group.SecondPhotoId);

        // A Group With Nobody Else: The Conversation's Letter, One Circle
        Assert.AreEqual("T", alone.Initial);
        Assert.AreEqual(0xFF00FF00u, alone.AvatarColor);
        Assert.AreEqual(string.Empty, alone.PhotoId);
        Assert.AreEqual(0u, alone.SecondColor);
    }

    [TestMethod]
    public async Task LiveUpdate_MovesRowToTop_UpdatesSnippet()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann", minutesAgo: 5), MainRig.Conv("b", "Bo", minutesAgo: 10), MainRig.Conv("c", "Cy", minutesAgo: 20));
        using var list = NewList(rig);
        await list.LoadAsync();
        ConversationRowViewModel row_c = list.Rows[2];
        var changed                   = 0;
        row_c.PropertyChanged         += (_, _) => changed++;

        await rig.Live(MainRig.Conv("c", "Cy", minutesAgo: 0, snippet: "fresh"));

        Assert.AreEqual("c,a,b", Ids(list));
        Assert.AreSame(row_c, list.Rows[0]);
        Assert.AreEqual("fresh", row_c.Snippet);
        Assert.AreEqual(1, changed);

        // A Brand New Conversation Is Inserted In Order
        await rig.Live(MainRig.Conv("d", "Di", minutesAgo: 7));

        Assert.AreEqual("c,a,d,b", Ids(list));
    }

    [TestMethod]
    public async Task StatusChangedToSpam_RemovesFromInbox()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"), MainRig.Conv("b", "Bo", minutesAgo: 1));
        using var list = NewList(rig);
        await list.LoadAsync();

        await rig.Live(MainRig.Conv("a", "Ann", status: ConversationStatus.SpamFolder));

        Assert.AreEqual("b", Ids(list));
    }

    [TestMethod]
    public async Task SpamFolder_ShowsSpamAndBlockedOnly()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("s", "Spam", status: ConversationStatus.SpamFolder), MainRig.Conv("k", "Block", minutesAgo: 1, status: ConversationStatus.BlockedFolder));
        using var list = NewList(rig, ConversationFolder.SpamBlocked);
        await list.LoadAsync();

        await rig.Live(MainRig.Conv("a", "Active"));

        Assert.AreEqual("s,k", Ids(list));
    }

    /// <summary>
    /// A load or load-more that fails with any error (phone asleep, network, server, decode) keeps the rows and never
    /// throws; the next load fills the list.
    /// </summary>
    [TestMethod]
    public async Task Load_AnyFailure_KeepsRows_NextLoadFills()
    {
        foreach (Exception failure in MainRig.Failures())
        {
            using var rig = new MainRig();
            rig.Page(MainRig.Conv("a", "Ann"));
            rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "a" };
            using var list = NewList(rig);
            await list.LoadAsync();

            string name           = failure.GetType().Name;
            rig.Client.ListThrows = failure;
            await list.LoadAsync();
            await list.LoadMoreAsync();
            Assert.AreEqual("a", Ids(list), name);
            Assert.IsFalse(list.IsLoading, name);

            rig.Client.ListThrows = null;
            rig.Page(MainRig.Conv("a", "Ann"), MainRig.Conv("n", "Newcomer", minutesAgo: 1));
            await list.LoadAsync();
            Assert.AreEqual("a,n", Ids(list), name);
        }
    }

    [TestMethod]
    public async Task Resynced_Reloads()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        using var list = NewList(rig);
        await list.LoadAsync();

        rig.Page(MainRig.Conv("a", "Ann"), MainRig.Conv("n", "Newcomer", minutesAgo: 1));
        await rig.Store.HandleEventAsync(new ListenRecovered());

        Assert.AreEqual("a,n", Ids(list));
        Assert.AreEqual(2, rig.Client.ListCalls.Count);
    }

    [TestMethod]
    public async Task LoadMore_UsesNextCursor_AndStopsWhenNone()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "next" };
        using var list = NewList(rig);
        await list.LoadAsync();

        rig.Page(MainRig.Conv("b", "Bo", minutesAgo: 30));
        await list.LoadMoreAsync();
        await list.LoadMoreAsync();

        Assert.AreEqual("a,b", Ids(list));
        Assert.AreEqual(2, rig.Client.ListCalls.Count);
    }

    [TestMethod]
    public async Task Resynced_DuringLoad_RunsASecondLoad()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        var gate         = new TaskCompletionSource();
        rig.Client.ListGate = gate;
        using var list   = NewList(rig);

        Task first = list.LoadAsync();
        rig.Page(MainRig.Conv("a", "Ann"), MainRig.Conv("n", "Newcomer", minutesAgo: 1));
        await rig.Store.HandleEventAsync(new ListenRecovered());
        gate.SetResult();
        await first;

        Assert.AreEqual("a,n", Ids(list));
        Assert.AreEqual(2, rig.Client.ListCalls.Count);
    }

    [TestMethod]
    public async Task Resynced_DuringLoadMore_RunsAReload()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "next" };
        using var list = NewList(rig);
        await list.LoadAsync();

        rig.Page(MainRig.Conv("b", "Bo", minutesAgo: 30));
        var gate            = new TaskCompletionSource();
        rig.Client.ListGate = gate;
        Task more = list.LoadMoreAsync();

        rig.Page(MainRig.Conv("a", "Ann"), MainRig.Conv("n", "Newcomer", minutesAgo: 1));
        await rig.Store.HandleEventAsync(new ListenRecovered());
        gate.SetResult();
        await more;

        Assert.AreEqual("a,n", Ids(list));
        Assert.AreEqual(3, rig.Client.ListCalls.Count);
    }

    /// <summary>
    /// The loading ring shows while an empty list loads, not while a list that already has rows reloads or loads more.
    /// </summary>
    [TestMethod]
    public async Task IsLoadingFirstPage_OnlyWhileTheListIsEmpty()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "next" };
        using var list = NewList(rig);

        var gate            = new TaskCompletionSource();
        rig.Client.ListGate = gate;
        Task first          = list.LoadAsync();
        Assert.IsTrue(list.IsLoadingFirstPage);
        gate.SetResult();
        await first;
        Assert.IsFalse(list.IsLoadingFirstPage);

        // Reload And More Keep The Rows On Screen
        rig.Client.ListGate = gate = new TaskCompletionSource();
        Task reload         = list.LoadAsync();
        Assert.IsFalse(list.IsLoadingFirstPage);
        gate.SetResult();
        await reload;

        rig.Client.ListGate = gate = new TaskCompletionSource();
        Task more           = list.LoadMoreAsync();
        Assert.IsFalse(list.IsLoadingFirstPage);
        gate.SetResult();
        await more;
    }
}

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
        Assert.AreEqual("p-pin", list.Rows[0].PhotoId);
    }

    /// <summary>
    /// With PinnedApart on, pinned conversations sit in Pinned, newest first, and not in Rows. Pinning or unpinning on
    /// the phone moves the same row across. Turning PinnedApart off puts them back at the top of Rows.
    /// </summary>
    [TestMethod]
    public async Task PinnedApart_SplitsPinnedRows_AndFollowsPinChanges()
    {
        using var rig = new MainRig();
        rig.Page(
            MainRig.Conv("old", "Old", minutesAgo: 90),
            MainRig.Conv("new", "New", minutesAgo: 1),
            MainRig.Conv("pin", "Pin", minutesAgo: 600, pinned: true),
            MainRig.Conv("pin2", "Pin 2", minutesAgo: 300, pinned: true));
        using var list = NewList(rig);
        list.PinnedApart = true;

        await list.LoadAsync();

        Assert.AreEqual("pin2,pin", string.Join(",", list.Pinned.Select(r => r.Id)));
        Assert.AreEqual("new,old", Ids(list));

        ConversationRowViewModel oldRow = list.Rows[1];
        await rig.Live(MainRig.Conv("old", "Old", minutesAgo: 90, pinned: true));

        Assert.AreEqual("old,pin2,pin", string.Join(",", list.Pinned.Select(r => r.Id)));
        Assert.AreSame(oldRow, list.Pinned[0]);
        Assert.AreEqual("new", Ids(list));

        await rig.Live(MainRig.Conv("pin2", "Pin 2", minutesAgo: 300));

        Assert.AreEqual("old,pin", string.Join(",", list.Pinned.Select(r => r.Id)));
        Assert.AreEqual("new,pin2", Ids(list));

        list.PinnedApart = false;

        Assert.AreEqual(0, list.Pinned.Count);
        Assert.AreEqual("old,pin,new,pin2", Ids(list));
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
    public void Row_IsMuted_FollowsConversation()
    {
        ConversationInfo c = ConversationInfo.From(MainRig.Conv("a", "Ann"));
        var row            = new ConversationRowViewModel(c with { IsMuted = true }, MainRig.Now);
        bool createdMuted  = row.IsMuted;
        int raised         = 0;
        row.PropertyChanged += (_, _) => raised++;

        row.Update(c, MainRig.Now);

        Assert.IsTrue(createdMuted);
        Assert.IsFalse(row.IsMuted);
        Assert.AreEqual(1, raised);
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

        Assert.AreEqual("A", one.Initial);
        Assert.AreEqual(0xFF00FF00u, one.AvatarColor);
        Assert.AreEqual("p-bo", one.PhotoId);
        Assert.AreEqual(string.Empty, one.SecondPhotoId);
        Assert.AreEqual(0u, one.SecondColor);

        // In a group, the front circle is the first other person and the back circle is the second.
        Assert.AreEqual("B", group.Initial);
        Assert.AreEqual(0xFF1A73E8u, group.AvatarColor);
        Assert.AreEqual("p-bo", group.PhotoId);
        Assert.AreEqual(string.Empty, group.SecondInitial, "A number with no saved name gets the person icon, not its first digit.");
        Assert.AreNotEqual(0u, group.SecondColor);
        Assert.AreEqual("p-cy", group.SecondPhotoId);

        // A group with nobody else shows the letter of the conversation in one circle.
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
        ConversationRowViewModel rowC = list.Rows[2];
        var changed                   = 0;
        rowC.PropertyChanged         += (_, _) => changed++;

        await rig.Live(MainRig.Conv("c", "Cy", minutesAgo: 0, snippet: "fresh"));

        Assert.AreEqual("c,a,b", Ids(list));
        Assert.AreSame(rowC, list.Rows[0]);
        Assert.AreEqual("fresh", rowC.Snippet);
        Assert.AreEqual(1, changed);

        await rig.Live(MainRig.Conv("d", "Di", minutesAgo: 7));

        Assert.AreEqual("c,a,d,b", Ids(list));
    }

    /// <summary>
    /// A live message newer than the last message of its conversation, sent or received, moves the row to the top at
    /// once, without the conversation update of Google. This holds with the first page only, and on a row that a later
    /// page added. A status update to an older message, or a catch-up message, moves nothing.
    /// </summary>
    [TestMethod]
    public async Task LiveMessage_MovesRowToTop_WithAndWithoutPaging()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann", minutesAgo: 5), MainRig.Conv("b", "Bo", minutesAgo: 10), MainRig.Conv("c", "Cy", minutesAgo: 20));
        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "next" };
        using var list = NewList(rig);
        await list.LoadAsync();

        await rig.LiveMessage(LiveMsg("m1", "c", minutesAgo: 1));
        Assert.AreEqual("c,a,b", Ids(list));

        rig.Page(MainRig.Conv("d", "Di", minutesAgo: 60));
        await list.LoadMoreAsync();
        Assert.AreEqual("c,a,b,d", Ids(list));
        await rig.LiveMessage(LiveMsg("m2", "d", minutesAgo: 0, from: "me"));
        Assert.AreEqual("d,c,a,b", Ids(list));

        await rig.LiveMessage(LiveMsg("m3", "b", minutesAgo: 30));
        await rig.Store.HandleEventAsync(new MessageReceived(LiveMsg("m4", "a", minutesAgo: 0), true));
        Assert.AreEqual("d,c,a,b", Ids(list));
    }

    /// <summary>
    /// Only live changes animate. A live message lifts its row with motion. The first load, a page and a reload land
    /// in place.
    /// </summary>
    [TestMethod]
    public async Task OnlyLiveChangesAnimate()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann", minutesAgo: 5), MainRig.Conv("b", "Bo", minutesAgo: 10), MainRig.Conv("c", "Cy", minutesAgo: 20));
        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "next" };
        using var list      = NewList(rig);
        List<bool> changing = [];
        list.RowsChanging  += (_, live) => changing.Add(live);

        await list.LoadAsync();
        rig.Page(MainRig.Conv("d", "Di", minutesAgo: 60));
        await list.LoadMoreAsync();
        Assert.AreEqual("a,b,c,d", Ids(list));
        Assert.IsTrue(changing.Count > 0 && changing.All(live => !live), "Loads and pages never animate.");

        changing.Clear();
        await rig.LiveMessage(LiveMsg("m1", "c", minutesAgo: 1));
        Assert.AreEqual("c,a,b,d", Ids(list));
        Assert.AreEqual("True", string.Join(",", changing));

        changing.Clear();
        await rig.Live(MainRig.Conv("e", "Ed", minutesAgo: 7));
        Assert.AreEqual("True", string.Join(",", changing));

        changing.Clear();
        rig.Page(MainRig.Conv("b", "Bo", minutesAgo: -2), MainRig.Conv("f", "Fa", minutesAgo: -1));
        await list.LoadAsync();
        Assert.IsTrue(changing.All(live => !live));
    }

    /// <summary>
    /// New received messages count up the badge of a pinned tile. A repeat of one, or my own message, does not count.
    /// When the phone says the conversation is read, the badge clears. An unread conversation with nothing counted
    /// shows its short newest message.
    /// </summary>
    [TestMethod]
    public async Task LiveMessages_CountUnreadBadge_UntilRead()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann", minutesAgo: 5, pinned: true));
        using var list = NewList(rig);
        list.PinnedApart = true;
        await list.LoadAsync();

        // As the phone sends them: the unread conversation first, then the message.
        // The app judges new against the real clock, so these messages are sent now.
        DateTimeOffset now  = DateTimeOffset.UtcNow;
        Conversation unread = MainRig.Conv("a", "Ann", pinned: true, snippet: "ok");
        unread.Unread       = true;
        await rig.Live(unread);
        await rig.LiveMessage(LiveMsg("m1", "a", 0, at: now));
        Assert.AreEqual("ok", list.Pinned[0].UnreadBadge);

        await rig.Live(unread);
        await rig.LiveMessage(LiveMsg("m1", "a", 0, at: now));
        await rig.LiveMessage(LiveMsg("m2", "a", 0, at: now));
        await rig.LiveMessage(LiveMsg("m3", "a", 0, at: now, status: MessageStatusType.OutgoingComplete));
        Assert.AreEqual(2, rig.Store.LiveUnreadTexts("a").Count);
        Assert.AreEqual("2", list.Pinned[0].UnreadBadge);
        Assert.IsTrue(list.Pinned[0].HasUnreadList);
        Assert.AreEqual("Ann\nx\nx", list.Pinned[0].HoverText);

        await rig.Live(MainRig.Conv("a", "Ann", pinned: true, snippet: "ok"));
        Assert.AreEqual(0, rig.Store.LiveUnreadTexts("a").Count);
        Assert.AreEqual(string.Empty, list.Pinned[0].UnreadBadge);
        Assert.IsFalse(list.Pinned[0].HasUnreadList);
        Assert.AreEqual("Ann\nok", list.Pinned[0].HoverText);

        // A message while the conversation reads as read on the phone does not count toward the next unread spell.
        await rig.LiveMessage(LiveMsg("m4", "a", 0, at: now));
        Assert.AreEqual(0, rig.Store.LiveUnreadTexts("a").Count);

        // Unread again with nothing counted, because it came while the app was closed. The row shows the short message.
        await rig.Live(unread);
        Assert.AreEqual("ok", list.Pinned[0].UnreadBadge);
    }

    // A live message in a conversation, sent the given minutes before MainRig.Now or at a given time.
    private static Message LiveMsg(string id, string conversation, long minutesAgo, string from = "p-x", DateTimeOffset? at = null, MessageStatusType status = MessageStatusType.IncomingDelivered)
    {
        var m = new Message
        {
            MessageID      = id,
            ConversationID = conversation,
            ParticipantID  = from,
            Timestamp      = (at ?? MainRig.Now.AddMinutes(-minutesAgo)).ToUnixTimeMilliseconds() * 1000,
            MessageStatus  = new MessageStatus { Status = status },
        };
        m.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = "x" } });
        return m;
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

    [TestMethod]
    public async Task ArchivedList_ShowsArchivedAndKeepArchived()
    {
        using var rig = new MainRig();
        rig.Page(
            MainRig.Conv("a", "Archived", status: ConversationStatus.Archived),
            MainRig.Conv("k", "Kept", minutesAgo: 1, status: ConversationStatus.KeepArchived),
            MainRig.Conv("x", "Active", minutesAgo: 2),
            MainRig.Conv("s", "Spam", minutesAgo: 3, status: ConversationStatus.SpamFolder));
        using var list = NewList(rig, ConversationFolder.Archived);
        await list.LoadAsync();

        await rig.Live(MainRig.Conv("y", "Active Too"));

        Assert.AreEqual("a,k", Ids(list));
        Assert.AreEqual(ListConversationsRequest.Types.Folder.Archive, rig.Client.ListCalls.Single().Folder);
    }

    /// <summary>
    /// Unarchived on the phone: the live update moves the row from Archived into the inbox.
    /// </summary>
    [TestMethod]
    public async Task ArchivedChat_PhoneReactivates_MovesToInbox()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"), MainRig.Conv("z", "Zed", minutesAgo: 1, status: ConversationStatus.Archived));
        using var inbox    = NewList(rig);
        using var archived = NewList(rig, ConversationFolder.Archived);
        await inbox.LoadAsync();
        await archived.LoadAsync();
        Assert.AreEqual("a", Ids(inbox));
        Assert.AreEqual("z", Ids(archived));

        await rig.Live(MainRig.Conv("z", "Zed", minutesAgo: 1));

        Assert.AreEqual(string.Empty, Ids(archived));
        Assert.AreEqual("a,z", Ids(inbox));
    }

    /// <summary>
    /// A load or load-more that fails with any error keeps the rows and never throws. This covers phone asleep,
    /// network, server and decode errors. The next load fills the list.
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
    public async Task LoadMore_UsesTheCursorSentBack_StopsWhenNothingNew()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "next" };
        using var list = NewList(rig);
        await list.LoadAsync();

        rig.Page(MainRig.Conv("b", "Bo", minutesAgo: 30));
        await list.LoadMoreAsync();
        await list.LoadMoreAsync();
        await list.LoadMoreAsync();

        Assert.AreEqual("a,b", Ids(list));
        Assert.AreEqual(3, rig.Client.ListCalls.Count);
        Assert.AreEqual("next", rig.Client.ListCursors[1]?.LastItemID);
    }

    /// <summary>
    /// Google sends no cursor back, so the next page starts at the last conversation shown, by its time in
    /// milliseconds. An empty page ends the paging.
    /// </summary>
    [TestMethod]
    public async Task LoadMore_NoCursorBack_PagesFromLastConversation_StopsAtEmptyPage()
    {
        using var rig = new MainRig();
        Conversation ann = MainRig.Conv("a", "Ann");
        rig.Page(ann);
        using var list = NewList(rig);
        await list.LoadAsync();

        rig.Page(MainRig.Conv("b", "Bo", minutesAgo: 30));
        await list.LoadMoreAsync();
        rig.Page();
        await list.LoadMoreAsync();
        await list.LoadMoreAsync();

        Assert.AreEqual("a,b", Ids(list));
        Assert.AreEqual(3, rig.Client.ListCalls.Count);
        Assert.IsNull(rig.Client.ListCursors[0]);
        Assert.AreEqual("a", rig.Client.ListCursors[1]?.LastItemID);
        Assert.AreEqual(ann.LastMessageTimestamp / 1000, rig.Client.ListCursors[1]?.LastItemTimestamp);
        Assert.AreEqual("b", rig.Client.ListCursors[2]?.LastItemID);
    }

    /// <summary>
    /// A page with nothing new, because the phone ignored the cursor, ends the paging. The list never asks forever.
    /// </summary>
    [TestMethod]
    public async Task LoadMore_PageWithNothingNew_Stops()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        using var list = NewList(rig);
        await list.LoadAsync();

        await list.LoadMoreAsync();
        await list.LoadMoreAsync();

        Assert.AreEqual("a", Ids(list));
        Assert.AreEqual(2, rig.Client.ListCalls.Count);
    }

    /// <summary>
    /// The conversations of the next page can reach the list first as live updates, which the phone pushes. That page
    /// is still new to the paging, so the paging continues. The rows were already on screen, so the last row never
    /// changed to ask for the next page. The next page loads at once instead.
    /// </summary>
    [TestMethod]
    public async Task LoadMore_PageAlreadyShownByLiveUpdates_KeepsPaging()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "p2" };
        using var list = NewList(rig);
        await list.LoadAsync();

        Conversation bo = MainRig.Conv("b", "Bo", minutesAgo: 30);
        await rig.Live(bo);
        Assert.AreEqual("a,b", Ids(list));

        var page2 = new ListConversationsResponse { Cursor = new Cursor { LastItemID = "p3" } };
        page2.Conversations.Add(bo);
        var page3 = new ListConversationsResponse { Cursor = new Cursor { LastItemID = "p4" } };
        page3.Conversations.Add(MainRig.Conv("c", "Cy", minutesAgo: 60));
        rig.Client.NextPages.Enqueue(page2);
        rig.Client.NextPages.Enqueue(page3);
        rig.Page();

        await list.LoadMoreAsync();

        Assert.AreEqual("a,b,c", Ids(list));
        Assert.AreEqual(3, rig.Client.ListCalls.Count);
        Assert.AreEqual("p3", rig.Client.ListCursors[2]?.LastItemID);

        await list.LoadMoreAsync();
        await list.LoadMoreAsync();
        Assert.AreEqual(4, rig.Client.ListCalls.Count);
    }

    /// <summary>
    /// The bottom ring shows only while the next page loads, not while the list reloads.
    /// </summary>
    [TestMethod]
    public async Task IsLoadingMore_OnlyWhileTheNextPageLoads()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        using var list = NewList(rig);
        await list.LoadAsync();

        var reload          = new TaskCompletionSource();
        rig.Client.ListGate = reload;
        Task again          = list.LoadAsync();
        Assert.IsFalse(list.IsLoadingMore);
        reload.SetResult();
        await again;

        rig.Page(MainRig.Conv("b", "Bo", minutesAgo: 30));
        var gate            = new TaskCompletionSource();
        rig.Client.ListGate = gate;
        Task more           = list.LoadMoreAsync();
        Assert.IsTrue(list.IsLoadingMore);
        gate.SetResult();
        await more;
        Assert.IsFalse(list.IsLoadingMore);
    }

    /// <summary>
    /// The next page, asked for while the first page still loads, runs when that load ends. The last row showed during
    /// the load and does not show again until the user scrolls away and back. The page runs from the cursor that the
    /// load sent back, instead of being dropped. After a failed page, the next ask loads again.
    /// </summary>
    [TestMethod]
    public async Task LoadMore_AskedMidLoad_RunsAfter_AndAfterAFailure()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "next" };
        var gate            = new TaskCompletionSource();
        rig.Client.ListGate = gate;
        using var list      = NewList(rig);

        Task first = list.LoadAsync();
        await list.LoadMoreAsync();
        Assert.HasCount(1, rig.Client.ListCalls);
        gate.SetResult();
        await first;

        Assert.HasCount(2, rig.Client.ListCalls);
        Assert.AreEqual("next", rig.Client.ListCursors[1]?.LastItemID);
        Assert.IsFalse(list.IsLoading);
        Assert.IsFalse(list.IsLoadingMore);

        rig.Page(MainRig.Conv("a", "Ann"), MainRig.Conv("b", "Bo", minutesAgo: 30));
        rig.Client.ConversationsPage.Cursor = new Cursor { LastItemID = "next" };
        await list.LoadAsync();
        rig.Client.ListThrows = new TimeoutException();
        await list.LoadMoreAsync();
        Assert.IsFalse(list.IsLoading);
        Assert.IsFalse(list.IsLoadingMore);
        rig.Client.ListThrows = null;
        int calls = rig.Client.ListCalls.Count;
        await list.LoadMoreAsync();
        Assert.HasCount(calls + 1, rig.Client.ListCalls);
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

    /// <summary>
    /// A live row that lands during the first load hides the ring at once. The list is no longer empty.
    /// </summary>
    [TestMethod]
    public async Task IsLoadingFirstPage_LiveRowDuringFirstLoad_HidesRing()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann"));
        using var list      = NewList(rig);
        var gate            = new TaskCompletionSource();
        rig.Client.ListGate = gate;
        Task first          = list.LoadAsync();
        var raised          = new List<bool>();
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationListViewModel.IsLoadingFirstPage))
            {
                raised.Add(list.IsLoadingFirstPage);
            }
        };

        await rig.Live(MainRig.Conv("n", "Newcomer"));

        Assert.IsFalse(list.IsLoadingFirstPage);
        CollectionAssert.Contains(raised, false, "the view was told the ring went away");
        gate.SetResult();
        await first;
    }
    /// <summary>
    /// A list made after another list loaded, for example when the window reopened, shows the conversations of the
    /// store at once, with no ring. Then the page of the phone updates the same rows and drops the rows it no longer
    /// lists.
    /// </summary>
    [TestMethod]
    public async Task Load_ShowsCachedRowsAtOnce_ThenRefreshUpdatesThem()
    {
        using var rig = new MainRig();
        rig.Page(
            MainRig.Conv("a", "Ann", minutesAgo: 5),
            MainRig.Conv("b", "Bo", minutesAgo: 1),
            MainRig.Conv("s", "Spam", status: ConversationStatus.SpamFolder));
        using (ConversationListViewModel first = NewList(rig))
        {
            await first.LoadAsync();
        }

        rig.Page(MainRig.Conv("a", "Ann", minutesAgo: 5, snippet: "new"));
        using var list      = NewList(rig);
        var gate            = new TaskCompletionSource();
        rig.Client.ListGate = gate;
        Task load           = list.LoadAsync();

        Assert.AreEqual("b,a", Ids(list));
        Assert.IsFalse(list.IsLoadingFirstPage);
        ConversationRowViewModel ann = list.Rows[1];

        gate.SetResult();
        await load;

        Assert.AreEqual("a", Ids(list));
        Assert.AreSame(ann, list.Rows[0], "the row was updated in place, not replaced");
    }

    /// <summary>
    /// A conversation that arrives live while a reload waits on the phone keeps its row, although the page does not list
    /// it. The phone made the page before the conversation arrived.
    /// </summary>
    [TestMethod]
    public async Task Load_ConversationArrivesLiveMidLoad_KeepsItsRow()
    {
        using var rig = new MainRig();
        rig.Page(MainRig.Conv("a", "Ann", minutesAgo: 5));
        using var list = NewList(rig);
        await list.LoadAsync();

        var gate            = new TaskCompletionSource();
        rig.Client.ListGate = gate;
        Task reload         = list.LoadAsync();
        await rig.Live(MainRig.Conv("n", "Newcomer", minutesAgo: 1));
        gate.SetResult();
        await reload;

        Assert.AreEqual("n,a", Ids(list));
    }
}

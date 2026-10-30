using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;
using GMessages.Protocol.Proto;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Presentation.Tests.Notifications;

/// <summary>
/// Checks <see cref="UnreadBadge"/>: what counts, the 250 ms debounce, and clearing on sign-out.
/// </summary>
[TestClass]
public sealed class UnreadBadgeTests
{
    private static ConversationInfo Conv(string id, bool unread = true, ConversationStatus status = ConversationStatus.Active) =>
        new(id, "Ann", false, status, unread, false, DateTimeOffset.UnixEpoch, "", false, "", [], "me", "m1");

    /// <summary>
    /// Only unread, unmuted conversations in the main view count.
    /// </summary>
    [TestMethod]
    public void Count_UnreadInboxUnmutedOnly()
    {
        ConversationInfo[] all =
        [
            Conv("a"),
            Conv("b"),
            Conv("read", unread: false),
            Conv("archived", status: ConversationStatus.Archived),
            Conv("spam", status: ConversationStatus.SpamFolder),
            Conv("blocked", status: ConversationStatus.BlockedFolder),
            Conv("muted") with { IsMuted = true },
        ];

        Assert.AreEqual(2, UnreadBadge.Count(all));
    }

    /// <summary>
    /// A burst of updates hands the sink one count after 250 ms; with nothing unread that count is 0, which clears.
    /// </summary>
    [TestMethod]
    public void Count_Zero_Clears()
    {
        var time                        = new FakeTimeProvider();
        var shown                       = new List<int>();
        IEnumerable<ConversationInfo> c = [Conv("a")];
        using var badge                 = new UnreadBadge(() => c, shown.Add, time);

        badge.Update();
        badge.Update();
        time.Advance(UnreadBadge.Debounce);
        Assert.AreEqual("1", string.Join(",", shown));

        c = [Conv("a", unread: false)];
        badge.Update();
        time.Advance(UnreadBadge.Debounce);
        Assert.AreEqual("1,0", string.Join(",", shown));
    }

    /// <summary>
    /// Sign-out clears the badge at once, and a count still pending never shows.
    /// </summary>
    [TestMethod]
    public void SignOut_ClearsBadge()
    {
        var time        = new FakeTimeProvider();
        var shown       = new List<int>();
        using var badge = new UnreadBadge(() => [Conv("a")], shown.Add, time);

        badge.Update();
        badge.Clear();
        time.Advance(UnreadBadge.Debounce);

        Assert.AreEqual("0", string.Join(",", shown));
    }
}

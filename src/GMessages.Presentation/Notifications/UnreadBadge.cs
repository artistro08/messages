using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Notifications;

/// <summary>
/// Keeps the number on the taskbar badge: the count of unread main-view conversations that are not muted.
/// </summary>
/// <remarks>
/// The app calls <see cref="Update"/> on every conversation change and when the list loads. A burst of calls gives the
/// sink a single count, 250 ms after the first call. <see cref="Clear"/> gives the sink 0 at once and drops a pending
/// count. The sink shows the number, and for 0 the sink clears the badge. Any thread can call the members.
/// </remarks>
/// <param name="conversations">Returns the cached conversations.</param>
/// <param name="sink">Shows a count on the badge. A count of 0 clears the badge.</param>
/// <param name="time">The clock of the debounce.</param>
public sealed class UnreadBadge(Func<IEnumerable<ConversationInfo>> conversations, Action<int> sink, TimeProvider time) : IDisposable
{
    /// <summary>The time during which a burst of updates becomes a single count.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private ITimer? _pending;

    /// <summary>
    /// Counts the conversations that the badge shows.
    /// </summary>
    /// <remarks>
    /// The count includes each unread conversation in the main view that is not muted. The main view leaves out
    /// archived, spam, blocked, deleted and empty one-on-one conversations.
    /// </remarks>
    /// <param name="conversations">The conversations.</param>
    /// <returns>The count.</returns>
    public static int Count(IEnumerable<ConversationInfo> conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        return conversations.Count(c => c.Unread && c.IsMainView && !c.IsMuted);
    }

    /// <summary>
    /// Gives the sink the count in 250 ms, unless a count is already pending.
    /// </summary>
    public void Update()
    {
        lock (_lock)
        {
            _pending ??= time.CreateTimer(_ => Fire(), null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Drops a pending count and clears the badge at once. The app calls this method on a sign-out or an unpair.
    /// </summary>
    public void Clear()
    {
        Cancel();
        sink(0);
    }

    /// <summary>
    /// Drops a pending count and keeps the badge. On a quit, Windows keeps the badge, as it does for Mail.
    /// </summary>
    public void Dispose() => Cancel();

    // The count happens at the end of the debounce, so the count includes every change in the burst.
    private void Fire()
    {
        lock (_lock)
        {
            if (_pending is null)
            {
                return;
            }

            _pending.Dispose();
            _pending = null;
        }

        sink(Count(conversations()));
    }

    private void Cancel()
    {
        lock (_lock)
        {
            _pending?.Dispose();
            _pending = null;
        }
    }
}

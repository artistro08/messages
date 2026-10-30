using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Notifications;

/// <summary>
/// Keeps the taskbar badge's number: how many inbox conversations are unread and not muted.
/// </summary>
/// <remarks>
/// <see cref="Update"/> is called on every conversation change and when the list loads; a burst of them hands the sink
/// one count, 250 ms after the first. <see cref="Clear"/> (sign-out or unpair) hands it 0 at once and drops a pending
/// count. The sink shows the number, and clears the badge for 0. Safe to call from any thread.
/// </remarks>
/// <param name="conversations">Gets the cached conversations.</param>
/// <param name="sink">Shows a count on the badge; 0 clears it.</param>
/// <param name="time">The clock the debounce runs on.</param>
public sealed class UnreadBadge(Func<IEnumerable<ConversationInfo>> conversations, Action<int> sink, TimeProvider time) : IDisposable
{
    /// <summary>How long a burst of updates is gathered into one count.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private ITimer? _pending;

    /// <summary>
    /// Counts the conversations the badge shows.
    /// </summary>
    /// <remarks>Unread ones in the main view (not archived, spam, blocked or deleted) that aren't muted.</remarks>
    /// <param name="conversations">The conversations.</param>
    /// <returns>The count.</returns>
    public static int Count(IEnumerable<ConversationInfo> conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        return conversations.Count(c => c.Unread && c.IsMainView && !c.IsMuted);
    }

    /// <summary>
    /// Hands the sink the count 250 ms from now, unless a count is already on its way.
    /// </summary>
    public void Update()
    {
        lock (_lock)
        {
            _pending ??= time.CreateTimer(_ => Fire(), null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Drops a pending count and clears the badge now (sign-out or unpair).
    /// </summary>
    public void Clear()
    {
        Cancel();
        sink(0);
    }

    /// <summary>
    /// Drops a pending count, leaving the badge as it is (quit: Windows keeps it, as Mail does).
    /// </summary>
    public void Dispose() => Cancel();

    // The Debounce Ended: Count Now, So Every Change In The Burst Is In It
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

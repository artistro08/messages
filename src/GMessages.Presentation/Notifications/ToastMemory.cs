namespace GMessages.Presentation.Notifications;

/// <summary>
/// A toast as shown: its message (tag), its conversation (group) and its payload.
/// </summary>
/// <param name="Tag">The message ID.</param>
/// <param name="Group">The conversation ID.</param>
/// <param name="Xml">The toast payload.</param>
public sealed record ShownToast(string Tag, string Group, string Xml);

/// <summary>
/// The toasts Snooze can show again: the latest few shown, and the ones snoozed and waiting.
/// </summary>
/// <remarks>
/// Payloads live in memory only, never on disk. Only the latest <c>capacity</c> shown toasts can be snoozed (older ones
/// are dropped first), but a snooze already waiting is kept apart, so newer toasts never push it out. Opening the chat
/// (<see cref="RemoveConversation"/>) or clearing every toast (<see cref="Clear"/>) cancels its waiting snoozes.
/// </remarks>
/// <param name="capacity">How many recently shown toasts can still be snoozed.</param>
public sealed class ToastMemory(int capacity)
{
    private readonly List<ShownToast> _recent = [];
    private readonly HashSet<ShownToast> _snoozed = [];

    /// <summary>
    /// Keeps a shown toast so it can be snoozed; the oldest is dropped past the capacity.
    /// </summary>
    /// <param name="toast">The toast.</param>
    public void Remember(ShownToast toast)
    {
        ArgumentNullException.ThrowIfNull(toast);

        _recent.RemoveAll(t => t.Tag == toast.Tag && t.Group == toast.Group);
        _recent.Add(toast);
        if (_recent.Count > capacity)
        {
            _recent.RemoveAt(0);
        }
    }

    /// <summary>
    /// Starts a snooze: moves the toast from the recent ones to the waiting ones.
    /// </summary>
    /// <param name="tag">The toast's message ID.</param>
    /// <param name="group">The toast's conversation ID.</param>
    /// <returns>The toast, or null when it isn't one of the recent ones.</returns>
    public ShownToast? Snooze(string? tag, string group)
    {
        if (_recent.Find(t => t.Tag == tag && t.Group == group) is not { } toast)
        {
            return null;
        }

        _recent.Remove(toast);
        _snoozed.Add(toast);
        return toast;
    }

    /// <summary>
    /// Ends a snooze once its time is up. When it is still waiting, it becomes a recent toast again, so it can be
    /// snoozed once more after it shows.
    /// </summary>
    /// <param name="toast">The snoozed toast.</param>
    /// <returns>True to show it again; false when its chat was opened or the toasts were cleared meanwhile.</returns>
    public bool Wake(ShownToast toast)
    {
        if (!_snoozed.Remove(toast))
        {
            return false;
        }

        Remember(toast);
        return true;
    }

    /// <summary>
    /// Forgets a conversation's toasts, the waiting snoozes included (its chat was opened).
    /// </summary>
    /// <param name="group">The conversation ID.</param>
    public void RemoveConversation(string group)
    {
        _recent.RemoveAll(t => t.Group == group);
        _snoozed.RemoveWhere(t => t.Group == group);
    }

    /// <summary>
    /// Forgets every toast and cancels every waiting snooze.
    /// </summary>
    public void Clear()
    {
        _recent.Clear();
        _snoozed.Clear();
    }
}

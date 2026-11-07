namespace GMessages.Presentation.Notifications;

/// <summary>
/// Holds a shown toast: the message as the tag, the conversation as the group, and the payload.
/// </summary>
/// <param name="Tag">The message ID.</param>
/// <param name="Group">The conversation ID.</param>
/// <param name="Xml">The toast payload.</param>
public sealed record ShownToast(string Tag, string Group, string Xml);

/// <summary>
/// Keeps the toasts that Snooze can show again: the latest shown toasts and the snoozed toasts. It also keeps the
/// toasted message IDs, so each message gets a single toast.
/// </summary>
/// <remarks>
/// The payloads stay in memory and never go to disk. The real phone sends several updates for each incoming message,
/// such as status changes and reactions. <see cref="FirstShow"/> keeps the last 256 toasted message IDs, so no update
/// shows the banner again. An open of the chat or a clear of the toasts keeps these IDs. A snooze wake is not a first
/// show. The user can snooze only the latest <c>capacity</c> shown toasts. The oldest toast goes first. A waiting
/// snooze is kept apart, so newer toasts never push it out. <see cref="RemoveConversation"/> cancels the waiting
/// snoozes of a conversation. <see cref="Clear"/> cancels every waiting snooze.
/// </remarks>
/// <param name="capacity">The number of recent shown toasts that the user can snooze.</param>
public sealed class ToastMemory(int capacity)
{
    // ChatStore remembers the same number of message IDs.
    private const int ShownIdCapacity = 256;

    private readonly List<ShownToast> _recent = [];
    private readonly HashSet<ShownToast> _snoozed = [];

    private readonly Queue<string> _shownOrder = new();
    private readonly HashSet<string> _shownIds = [];

    /// <summary>
    /// Claims the single toast of a message. Call this method before the app shows a toast for a message that arrived.
    /// </summary>
    /// <param name="messageId">The message ID, which is the tag of the toast.</param>
    /// <returns>True for the first call. False for a repeated update of a message that already has a toast.</returns>
    public bool FirstShow(string messageId)
    {
        ArgumentNullException.ThrowIfNull(messageId);

        if (!_shownIds.Add(messageId))
        {
            return false;
        }

        _shownOrder.Enqueue(messageId);
        if (_shownOrder.Count > ShownIdCapacity)
        {
            _shownIds.Remove(_shownOrder.Dequeue());
        }

        return true;
    }

    /// <summary>
    /// Keeps a shown toast for Snooze. Past the capacity, the oldest toast goes.
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
    /// Starts a snooze and moves the toast from the recent toasts to the waiting toasts.
    /// </summary>
    /// <param name="tag">The message ID of the toast.</param>
    /// <param name="group">The conversation ID of the toast.</param>
    /// <returns>The toast, or null when the toast is not a recent toast.</returns>
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
    /// Ends a snooze when its time is over. A toast that still waits becomes a recent toast again, so the user can
    /// snooze it again.
    /// </summary>
    /// <param name="toast">The snoozed toast.</param>
    /// <returns>
    /// True to show the toast again. False when <see cref="RemoveConversation"/> or <see cref="Clear"/> canceled the
    /// snooze.
    /// </returns>
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
    /// Forgets the toasts and the waiting snoozes of a conversation.
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

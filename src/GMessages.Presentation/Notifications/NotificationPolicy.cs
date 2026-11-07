using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Notifications;

/// <summary>
/// Decides whether a message raises a toast, and when the toasts of a conversation go.
/// </summary>
public static class NotificationPolicy
{
    /// <summary>
    /// Says whether to show a toast for a message.
    /// </summary>
    /// <remarks>
    /// Only a live, new, incoming text or attachment notifies. The conversation of the message must be in the main view
    /// and not muted. The chat on screen stays quiet while the window has focus, unless
    /// <paramref name="notifyForViewedChat"/> is on.
    /// </remarks>
    /// <param name="e">The message that arrived.</param>
    /// <param name="notificationsEnabled">The notifications setting.</param>
    /// <param name="viewedConversationId">The conversation open in the window, or null.</param>
    /// <param name="windowFocused">True when the window has focus.</param>
    /// <param name="notifyForViewedChat">The setting that notifies also for the chat on screen.</param>
    /// <returns>True to show a toast.</returns>
    public static bool ShouldNotify(MessageArrivedEventArgs e, bool notificationsEnabled, string? viewedConversationId, bool windowFocused, bool notifyForViewedChat)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!notificationsEnabled || e.IsOld || !e.IsNew || e.Message.FromMe || e.Message.Kind == MessageKind.Tombstone)
        {
            return false;
        }

        if (e.Conversation is null || !e.Conversation.IsMainView || e.Conversation.IsMuted)
        {
            return false;
        }

        return notifyForViewedChat || !windowFocused || e.Conversation.Id != viewedConversationId;
    }

    /// <summary>
    /// Says whether the toasts of a changed conversation go because the user read it.
    /// </summary>
    /// <remarks>
    /// The toasts of a read conversation go, wherever the user read it: here, in a toast reply or on the phone. With
    /// <paramref name="notifyForViewedChat"/> on, the chat on screen in the focused window keeps its toasts. The app
    /// reads that chat as messages arrive, so a dismiss at that time undoes the setting. Those toasts go when the user
    /// views the chat again: the window gets focus on the chat again, or the user opens the chat again.
    /// </remarks>
    /// <param name="conversation">The changed conversation.</param>
    /// <param name="viewedConversationId">The conversation open in the window, or null.</param>
    /// <param name="windowFocused">True when the window has focus.</param>
    /// <param name="notifyForViewedChat">The setting that notifies also for the chat on screen.</param>
    /// <returns>True to remove the toasts of the conversation.</returns>
    public static bool ShouldDismissOnRead(ConversationInfo conversation, string? viewedConversationId, bool windowFocused, bool notifyForViewedChat)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (conversation.Unread)
        {
            return false;
        }

        return !notifyForViewedChat || !windowFocused || conversation.Id != viewedConversationId;
    }

    /// <summary>
    /// Says whether the toasts of a conversation go because the conversation left the main view or is muted.
    /// </summary>
    /// <remarks>
    /// The rule is the same rule that <see cref="ShouldNotify"/> uses. An archived, blocked, spam, deleted or muted
    /// conversation keeps no toast, wherever the user made the change: here or on the phone.
    /// </remarks>
    /// <param name="conversation">The changed conversation.</param>
    /// <returns>True to remove the toasts of the conversation.</returns>
    public static bool ShouldDismissOnLeave(ConversationInfo conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        return !conversation.IsMainView || conversation.IsMuted;
    }
}

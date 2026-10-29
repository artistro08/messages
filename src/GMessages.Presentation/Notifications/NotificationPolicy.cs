using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Notifications;

/// <summary>
/// Decides whether an arriving message should raise a toast.
/// </summary>
public static class NotificationPolicy
{
    /// <summary>
    /// Says whether to show a toast for a message.
    /// </summary>
    /// <remarks>
    /// Only live, new, incoming text or attachments in a main-list conversation notify. The chat on screen
    /// stays quiet while the window has focus, unless <paramref name="notifyForViewedChat"/> is on.
    /// </remarks>
    /// <param name="e">The arrived message.</param>
    /// <param name="notificationsEnabled">The notifications setting.</param>
    /// <param name="viewedConversationId">The conversation open in the window, or null.</param>
    /// <param name="windowFocused">True when the window has focus.</param>
    /// <param name="notifyForViewedChat">The setting that notifies even for the chat on screen.</param>
    /// <returns>True to show a toast.</returns>
    public static bool ShouldNotify(MessageArrivedEventArgs e, bool notificationsEnabled, string? viewedConversationId, bool windowFocused, bool notifyForViewedChat)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!notificationsEnabled || e.IsOld || !e.IsNew || e.Message.FromMe || e.Message.Kind == MessageKind.Tombstone)
        {
            return false;
        }

        // TODO: skip muted conversations. LibGM's Conversation has no mute field yet, so this is not guessed.
        if (e.Conversation is null || !e.Conversation.IsMainView)
        {
            return false;
        }

        // The Chat On Screen Stays Quiet While Focused
        return notifyForViewedChat || !windowFocused || e.Conversation.Id != viewedConversationId;
    }
}

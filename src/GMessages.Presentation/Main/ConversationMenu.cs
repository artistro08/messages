using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Names an item in the ⋮ menu of a conversation.
/// </summary>
public enum ConversationMenuItem
{
    /// <summary>Opens the Details dialog. Only the menu of the chat header has it.</summary>
    Details,

    /// <summary>Moves the conversation to Archived.</summary>
    Archive,

    /// <summary>Moves an archived conversation back to the inbox.</summary>
    Unarchive,

    /// <summary>Stops the notifications of the conversation.</summary>
    Mute,

    /// <summary>Starts the notifications of the conversation again.</summary>
    Unmute,

    /// <summary>
    /// Asks to block the conversation. The block also reports spam unless the user clears the check box.
    /// </summary>
    BlockAndReport,

    /// <summary>Unblocks the conversation.</summary>
    Unblock,

    /// <summary>Asks to delete the conversation with <see cref="ConversationAction.Delete"/>.</summary>
    MoveToTrash,
}

/// <summary>
/// Decides which items the ⋮ menu of a conversation offers.
/// </summary>
public static class ConversationMenu
{
    /// <summary>
    /// Lists the menu items for a conversation, in menu order.
    /// </summary>
    /// <remarks>
    /// Details shows only in the chat header. Archive shows for an active conversation. Unarchive shows for an archived
    /// or kept-archived conversation. <see cref="ConversationInfo.IsMuted"/> selects Mute or Unmute. Block &amp; report
    /// spam shows only for a one-on-one chat. The spam and blocked folders show no Mute and no Block. A conversation in
    /// the blocked folder offers Unblock. Move to trash always shows.
    /// </remarks>
    /// <param name="c">The conversation.</param>
    /// <param name="inHeader">True for the open chat's header menu, false for a list row's.</param>
    /// <returns>The items, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="c"/> is null.</exception>
    public static IReadOnlyList<ConversationMenuItem> For(ConversationInfo c, bool inHeader)
    {
        ArgumentNullException.ThrowIfNull(c);

        bool spamOrBlocked               = c.Status is ConversationStatus.SpamFolder or ConversationStatus.BlockedFolder;
        List<ConversationMenuItem> items = [];

        if (inHeader)
        {
            items.Add(ConversationMenuItem.Details);
        }

        if (c.Status == ConversationStatus.Active)
        {
            items.Add(ConversationMenuItem.Archive);
        }
        else if (c.Status is ConversationStatus.Archived or ConversationStatus.KeepArchived)
        {
            items.Add(ConversationMenuItem.Unarchive);
        }

        if (!spamOrBlocked)
        {
            items.Add(c.IsMuted ? ConversationMenuItem.Unmute : ConversationMenuItem.Mute);
        }

        if (!c.IsGroup && !spamOrBlocked)
        {
            items.Add(ConversationMenuItem.BlockAndReport);
        }
        else if (c.Status == ConversationStatus.BlockedFolder)
        {
            items.Add(ConversationMenuItem.Unblock);
        }

        items.Add(ConversationMenuItem.MoveToTrash);

        return items;
    }
}

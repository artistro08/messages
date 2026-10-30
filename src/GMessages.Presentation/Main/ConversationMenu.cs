using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// An item in a conversation's ⋮ menu.
/// </summary>
public enum ConversationMenuItem
{
    /// <summary>Opens the Details dialog (the chat header's menu only).</summary>
    Details,

    /// <summary>Moves the conversation to Archived.</summary>
    Archive,

    /// <summary>Moves an archived conversation back to the inbox.</summary>
    Unarchive,

    /// <summary>Stops the conversation's notifications.</summary>
    Mute,

    /// <summary>Turns the conversation's notifications back on.</summary>
    Unmute,

    /// <summary>Asks to block the conversation, reporting spam unless unchecked.</summary>
    BlockAndReport,

    /// <summary>Unblocks the conversation.</summary>
    Unblock,

    /// <summary>Asks to delete the conversation (<see cref="ConversationAction.Delete"/>).</summary>
    MoveToTrash,
}

/// <summary>
/// Decides which items a conversation's ⋮ menu offers.
/// </summary>
public static class ConversationMenu
{
    /// <summary>
    /// Lists the menu items for a conversation, in menu order.
    /// </summary>
    /// <remarks>
    /// Details shows in the chat header only. Archive shows for an active conversation, Unarchive for an archived or
    /// kept-archived one. Mute or Unmute (by <see cref="ConversationInfo.IsMuted"/>) and Block &amp; report spam (one-on-one
    /// only) are left out in the spam and blocked folders, where a blocked conversation offers Unblock. Move to
    /// trash always shows.
    /// </remarks>
    /// <param name="c">The conversation.</param>
    /// <param name="inHeader">True for the open chat's header menu, false for a list row's.</param>
    /// <returns>The items, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="c"/> is null.</exception>
    public static IReadOnlyList<ConversationMenuItem> For(ConversationInfo c, bool inHeader)
    {
        ArgumentNullException.ThrowIfNull(c);

        bool spam_or_blocked             = c.Status is ConversationStatus.SpamFolder or ConversationStatus.BlockedFolder;
        List<ConversationMenuItem> items = [];

        // Details
        if (inHeader)
        {
            items.Add(ConversationMenuItem.Details);
        }

        // Archive
        if (c.Status == ConversationStatus.Active)
        {
            items.Add(ConversationMenuItem.Archive);
        }
        else if (c.Status is ConversationStatus.Archived or ConversationStatus.KeepArchived)
        {
            items.Add(ConversationMenuItem.Unarchive);
        }

        // Mute
        if (!spam_or_blocked)
        {
            items.Add(c.IsMuted ? ConversationMenuItem.Unmute : ConversationMenuItem.Mute);
        }

        // Block
        if (!c.IsGroup && !spam_or_blocked)
        {
            items.Add(ConversationMenuItem.BlockAndReport);
        }
        else if (c.Status == ConversationStatus.BlockedFolder)
        {
            items.Add(ConversationMenuItem.Unblock);
        }

        // Trash
        items.Add(ConversationMenuItem.MoveToTrash);

        return items;
    }
}

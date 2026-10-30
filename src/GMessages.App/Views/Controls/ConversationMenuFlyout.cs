using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A conversation's ⋮ menu: builds the flyout from <see cref="ConversationMenu.For"/>'s items and runs the item picked,
/// asking first for Block &amp; report spam and Move to trash.
/// </summary>
/// <remarks>
/// Built in code with plain <c>new</c> (Native AOT safe). The dialogs take the window's theme from the
/// <see cref="XamlRoot"/>'s content, since a dialog's popup doesn't inherit it. One dialog shows at a time: a second
/// ask while one is open does nothing.
/// </remarks>
internal static class ConversationMenuFlyout
{
    // One Confirm Dialog At A Time (WinUI Throws On A Second ContentDialog In The Same Window)
    private static bool _dialogOpen;

    /// <summary>
    /// Builds the menu flyout for a conversation's items.
    /// </summary>
    /// <remarks>
    /// One item per entry, with its text, a Segoe Fluent glyph and the AutomationId <c>Conversation.{item}</c>. A
    /// separator follows Details and comes before Move to trash.
    /// </remarks>
    /// <param name="items">The items, in menu order (from <see cref="ConversationMenu.For"/>).</param>
    /// <param name="picked">Called with the item the user clicks.</param>
    /// <returns>The flyout, ready to show.</returns>
    public static MenuFlyout Build(IReadOnlyList<ConversationMenuItem> items, Action<ConversationMenuItem> picked)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(picked);

        var menu = new MenuFlyout();
        foreach (ConversationMenuItem item in items)
        {
            // Separator Before Move To Trash
            if (item == ConversationMenuItem.MoveToTrash && menu.Items.Count > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            var entry = new MenuFlyoutItem
            {
                Text = TextOf(item),
                Icon = new FontIcon { Glyph = GlyphOf(item) },
            };
            AutomationProperties.SetAutomationId(entry, "Conversation." + item);
            entry.Click += (_, _) => picked(item);
            menu.Items.Add(entry);

            // Separator After Details
            if (item == ConversationMenuItem.Details)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }
        }

        return menu;
    }

    /// <summary>
    /// Runs a picked menu item on a conversation.
    /// </summary>
    /// <remarks>
    /// Block &amp; report spam asks first (Report spam checked by default) and runs
    /// <see cref="ConversationAction.BlockAndReport"/>, or <see cref="ConversationAction.Block"/> when unchecked. Move
    /// to trash asks first and runs <see cref="ConversationAction.Delete"/>. Details calls
    /// <paramref name="showDetails"/>. The rest run at once. A failure shows the view model's banner
    /// (<see cref="MainViewModel.ActionError"/>); nothing is thrown.
    /// </remarks>
    /// <param name="vm">Runs the action.</param>
    /// <param name="root">The window's XAML root, for the dialogs.</param>
    /// <param name="c">The conversation.</param>
    /// <param name="item">The picked item.</param>
    /// <param name="showDetails">Opens the Details dialog, or null where there is none.</param>
    /// <returns>A task that completes once the item ran (or the user canceled).</returns>
    public static async Task RunAsync(MainViewModel vm, XamlRoot root, ConversationInfo c, ConversationMenuItem item, Func<Task>? showDetails)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(c);

        switch (item)
        {
            case ConversationMenuItem.Details:
                if (showDetails is not null)
                {
                    await showDetails();
                }

                return;

            case ConversationMenuItem.BlockAndReport:
                if (await AskBlockAsync(root) is { } report)
                {
                    await vm.RunAsync(c.Id, report ? ConversationAction.BlockAndReport : ConversationAction.Block);
                }

                return;

            case ConversationMenuItem.MoveToTrash:
                if (await AskTrashAsync(root))
                {
                    await vm.RunAsync(c.Id, ConversationAction.Delete);
                }

                return;

            default:
                await vm.RunAsync(c.Id, ActionOf(item));
                return;
        }
    }

    // =========================================================================
    // DIALOGS
    // =========================================================================

    // Block & Report Spam: True To Report As Well, False To Only Block, Null When Canceled (Or Another Dialog Is Open)
    private static async Task<bool?> AskBlockAsync(XamlRoot root)
    {
        var report = new CheckBox
        {
            Content   = ChatCopy.ReportSpam,
            IsChecked = true,
        };
        AutomationProperties.SetAutomationId(report, "Dialog.ReportSpam");

        var text = new TextBlock
        {
            Text         = ChatCopy.BlockText,
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(text);
        content.Children.Add(report);

        ContentDialog dialog = NewDialog(root, "Dialog.Block", ChatCopy.BlockAndReportSpam, content, ChatCopy.Block);
        return await ShowAsync(dialog) ? report.IsChecked == true : null;
    }

    // Move To Trash: True When Confirmed
    private static async Task<bool> AskTrashAsync(XamlRoot root)
    {
        var text = new TextBlock
        {
            Text         = ChatCopy.MoveToTrashText,
            TextWrapping = TextWrapping.Wrap,
        };

        return await ShowAsync(NewDialog(root, "Dialog.Trash", ChatCopy.MoveToTrashTitle, text, ChatCopy.MoveToTrash));
    }

    // A Confirm Dialog: Enter Picks Cancel (DefaultButton Close), So Nothing Happens By Accident
    private static ContentDialog NewDialog(XamlRoot root, string automationId, string title, UIElement content, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot          = root,
            RequestedTheme    = root.Content.As<FrameworkElement>().ActualTheme,
            Title             = title,
            Content           = content,
            PrimaryButtonText = primary,
            CloseButtonText   = ChatCopy.Cancel,
            DefaultButton     = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        return dialog;
    }

    // Shows The Dialog Unless Another Is Open; True When Its Primary Button Was Picked
    private static async Task<bool> ShowAsync(ContentDialog dialog)
    {
        if (_dialogOpen)
        {
            return false;
        }

        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static string TextOf(ConversationMenuItem item) => item switch
    {
        ConversationMenuItem.Details        => ChatCopy.Details,
        ConversationMenuItem.Archive        => ChatCopy.Archive,
        ConversationMenuItem.Unarchive      => ChatCopy.Unarchive,
        ConversationMenuItem.Mute           => ChatCopy.Mute,
        ConversationMenuItem.Unmute         => ChatCopy.Unmute,
        ConversationMenuItem.BlockAndReport => ChatCopy.BlockAndReportSpam,
        ConversationMenuItem.Unblock        => ChatCopy.Unblock,
        ConversationMenuItem.MoveToTrash    => ChatCopy.MoveToTrash,
        _                                   => throw new ArgumentOutOfRangeException(nameof(item), item, null),
    };

    private static string GlyphOf(ConversationMenuItem item) => item switch
    {
        ConversationMenuItem.Details                                          => "\uE946",
        ConversationMenuItem.Archive or ConversationMenuItem.Unarchive        => "\uE7B8",
        ConversationMenuItem.Mute                                             => "\uE7ED",
        ConversationMenuItem.Unmute                                           => "\uEA8F",
        ConversationMenuItem.BlockAndReport or ConversationMenuItem.Unblock   => "\uE8F8",
        ConversationMenuItem.MoveToTrash                                      => "\uE74D",
        _                                                                     => throw new ArgumentOutOfRangeException(nameof(item), item, null),
    };

    // The Items That Run Without Asking
    private static ConversationAction ActionOf(ConversationMenuItem item) => item switch
    {
        ConversationMenuItem.Archive   => ConversationAction.Archive,
        ConversationMenuItem.Unarchive => ConversationAction.Unarchive,
        ConversationMenuItem.Mute      => ConversationAction.Mute,
        ConversationMenuItem.Unmute    => ConversationAction.Unmute,
        ConversationMenuItem.Unblock   => ConversationAction.Unblock,
        _                              => throw new ArgumentOutOfRangeException(nameof(item), item, null),
    };
}

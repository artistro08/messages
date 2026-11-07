using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Builds the ⋮ menu of a conversation from the items of <see cref="ConversationMenu.For"/> and runs the picked item.
/// Block &amp; report spam and Move to trash ask first.
/// </summary>
/// <remarks>
/// The code builds the menu with plain <c>new</c>, which is safe under Native AOT. A dialog popup does not inherit the
/// window theme. Thus the dialogs take the theme from the content of the <see cref="XamlRoot"/>. Only 1 dialog shows at
/// a time. A second ask while a dialog is open does nothing.
/// </remarks>
internal static class ConversationMenuFlyout
{
    // Only 1 confirm dialog shows at a time. WinUI throws on a second ContentDialog in the same window.
    private static bool _dialogOpen;

    /// <summary>
    /// Builds the menu flyout for the items of a conversation.
    /// </summary>
    /// <remarks>
    /// Each entry gets 1 item with its text, a Segoe Fluent glyph and the AutomationId <c>Conversation.{item}</c>. A
    /// separator follows Details, and another separator comes before Move to trash.
    /// </remarks>
    /// <param name="items">The items in menu order, from <see cref="ConversationMenu.For"/>.</param>
    /// <param name="picked">Gets the item that the user clicks.</param>
    /// <returns>The flyout, ready to show.</returns>
    public static MenuFlyout Build(IReadOnlyList<ConversationMenuItem> items, Action<ConversationMenuItem> picked)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(picked);

        var menu = new MenuFlyout();
        foreach (ConversationMenuItem item in items)
        {
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
    /// Block &amp; report spam asks first, with Report spam checked by default. It runs
    /// <see cref="ConversationAction.BlockAndReport"/>, or <see cref="ConversationAction.Block"/> when the user clears
    /// Report spam. Move to trash asks first and runs <see cref="ConversationAction.Delete"/>. Details calls
    /// <paramref name="showDetails"/>. The other items run at once. A failure shows the banner of the view model,
    /// <see cref="MainViewModel.ActionError"/>. The method throws nothing.
    /// </remarks>
    /// <param name="vm">Runs the action.</param>
    /// <param name="root">The XAML root of the window for the dialogs.</param>
    /// <param name="c">The conversation.</param>
    /// <param name="item">The picked item.</param>
    /// <param name="showDetails">Opens the Details dialog, or null where there is none.</param>
    /// <returns>A task that completes after the item runs or after the user cancels.</returns>
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

    // Asks about Block & report spam. The result is true to block and report, false to block only, and null on a
    // cancel. It is also null while another dialog is open.
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

    // Asks about Move to trash. The result is true when the user confirms.
    private static async Task<bool> AskTrashAsync(XamlRoot root)
    {
        var text = new TextBlock
        {
            Text         = ChatCopy.MoveToTrashText,
            TextWrapping = TextWrapping.Wrap,
        };

        return await ShowAsync(NewDialog(root, "Dialog.Trash", ChatCopy.MoveToTrashTitle, text, ChatCopy.MoveToTrash));
    }

    // Creates a confirm dialog. Enter picks Cancel through DefaultButton Close, so an accidental Enter changes nothing.
    internal static ContentDialog NewDialog(XamlRoot root, string automationId, string title, UIElement content, string primary)
    {
        ContentDialog dialog     = Dialogs.Create(root);
        dialog.Title             = title;
        dialog.Content           = content;
        dialog.PrimaryButtonText = primary;
        dialog.CloseButtonText   = ChatCopy.Cancel;
        dialog.DefaultButton     = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, automationId);
        return dialog;
    }

    // Shows the dialog unless another dialog is open. Details also shows through this method. The result is true when
    // the user picks the primary button.
    internal static async Task<bool> ShowAsync(ContentDialog dialog)
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

    // Maps the items that run without a question.
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

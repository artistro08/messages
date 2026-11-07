using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Gives a text box its right-click menu: spelling fixes for a misspelled word, then Cut, Copy, Paste and Select all.
/// </summary>
/// <remarks>
/// The menu is a plain menu flyout, as the other menus of the app are. The WinUI 2.8 text menu does not open here,
/// because the box cannot use it as a command bar flyout. The menu of Windows opens as a command bar below the box,
/// with the fixes in a submenu. The fixes come from Windows through <see cref="TextBox.ProofingMenuFlyout"/>. Windows
/// fills it for the word under the pointer. Their automation peers run the fixes, so Add to dictionary and Ignore also
/// work. The names and icons come from the standard commands of Windows. The menu only shows their shortcuts, because
/// the box handles the keys itself.
/// </remarks>
internal static class TextMenu
{
    /// <summary>
    /// Gives the box the menu.
    /// </summary>
    /// <param name="box">The text box.</param>
    /// <param name="pasteMedia">Gives the paste action for a picture or file that the box cannot take as text. It gives
    /// null when the clipboard has no such content.</param>
    public static void Attach(TextBox box, Func<Action?>? pasteMedia = null)
    {
        ArgumentNullException.ThrowIfNull(box);

        var menu          = new MenuFlyout();
        menu.Opening     += (_, _) => Fill(menu, box, pasteMedia?.Invoke());
        box.ContextFlyout = menu;
    }

    // Fills the menu each time it opens, for the pointer position and the selection.
    private static void Fill(MenuFlyout menu, TextBox box, Action? pasteMedia)
    {
        menu.Items.Clear();

        // The spelling fixes come first, then a separator. Windows lists the fixes before its first separator. The
        // right-click puts the caret in the word.
        if (box.ProofingMenuFlyout is MenuFlyout { Items.Count: > 0 } proofing)
        {
            int at           = box.SelectionStart;
            bool suggestions = true;
            foreach (MenuFlyoutItemBase item in proofing.Items)
            {
                if (item is not MenuFlyoutItem original)
                {
                    suggestions = false;
                    menu.Items.Add(new MenuFlyoutSeparator());
                    continue;
                }

                var copy = new MenuFlyoutItem { Text = original.Text };
                if (suggestions)
                {
                    copy.Click += (_, _) => ReplaceWord(box, at, original.Text);
                }
                else
                {
                    copy.Click += (_, _) => new MenuFlyoutItemAutomationPeer(original).Invoke();
                }

                menu.Items.Add(copy);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        bool selected = box.SelectionLength > 0;
        Add(menu, StandardUICommandKind.Cut, "Ctrl+X", selected, box.CutSelectionToClipboard);
        Add(menu, StandardUICommandKind.Copy, "Ctrl+C", selected, box.CopySelectionToClipboard);
        Add(menu, StandardUICommandKind.Paste, "Ctrl+V", box.CanPasteClipboardContent || pasteMedia is not null, pasteMedia ?? box.PasteFromClipboard);
        Add(menu, StandardUICommandKind.SelectAll, "Ctrl+A", box.Text.Length > 0, box.SelectAll);
    }

    // Replaces the word around the caret with the fix. The fix replaces the selection as typed text, so Undo can
    // reverse it.
    private static void ReplaceWord(TextBox box, int at, string fix)
    {
        string text = box.Text;
        int start   = Math.Min(at, text.Length);
        int end     = start;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        while (end < text.Length && IsWordChar(text[end]))
        {
            end++;
        }

        box.Select(start, end - start);
        box.SelectedText = fix;
        box.Select(start + fix.Length, 0);
    }

    // A word holds letters, digits and apostrophes.
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’';

    // Adds 1 editing item with the name and icon of the standard Windows command. The item only shows its shortcut.
    private static void Add(MenuFlyout menu, StandardUICommandKind kind, string keys, bool enabled, Action run)
    {
        var command = new StandardUICommand(kind);
        var item    = new MenuFlyoutItem
        {
            Text                            = command.Label,
            Icon                            = new IconSourceElement { IconSource = command.IconSource },
            KeyboardAcceleratorTextOverride = keys,
            IsEnabled                       = enabled,
        };
        item.Click += (_, _) => run();
        menu.Items.Add(item);
    }
}

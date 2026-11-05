using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A text box's right-click menu: the spelling fixes for a misspelled word, then Cut, Copy, Paste and Select all.
/// </summary>
/// <remarks>
/// A plain menu flyout like the app's other menus. WinUI 2.8's text menu never opened here (the box couldn't use it as a
/// command bar flyout), and Windows' own opened as a command bar, below the box, with the fixes in a submenu. The fixes
/// are Windows' own (<see cref="TextBox.ProofingMenuFlyout"/>, filled for the word under the pointer), run through their
/// automation peers, so Add to dictionary and Ignore work too. Names and icons come from Windows' standard commands; their
/// shortcuts are only shown, as the box handles the keys itself.
/// </remarks>
internal static class TextMenu
{
    /// <summary>
    /// Gives the box the menu.
    /// </summary>
    /// <param name="box">The text box.</param>
    /// <param name="pasteMedia">Pastes a picture or file the box can't take as text, or null when nothing of that is on the clipboard.</param>
    public static void Attach(TextBox box, Func<Action?>? pasteMedia = null)
    {
        var menu          = new MenuFlyout();
        menu.Opening     += (_, _) => Fill(menu, box, pasteMedia?.Invoke());
        box.ContextFlyout = menu;
    }

    // Fills The Menu Each Time It Opens, For Where The Pointer Is And What Is Selected
    private static void Fill(MenuFlyout menu, TextBox box, Action? pasteMedia)
    {
        menu.Items.Clear();

        // Spelling Fixes (Windows Lists Them Before Its First Line), Then A Line. The Right-Click Put The Caret In The Word
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

        // Editing
        bool selected = box.SelectionLength > 0;
        Add(menu, StandardUICommandKind.Cut, "Ctrl+X", selected, box.CutSelectionToClipboard);
        Add(menu, StandardUICommandKind.Copy, "Ctrl+C", selected, box.CopySelectionToClipboard);
        Add(menu, StandardUICommandKind.Paste, "Ctrl+V", box.CanPasteClipboardContent || pasteMedia is not null, pasteMedia ?? box.PasteFromClipboard);
        Add(menu, StandardUICommandKind.SelectAll, "Ctrl+A", box.Text.Length > 0, box.SelectAll);
    }

    // The Word Around The Caret Becomes The Fix, Typed Over Its Selection So Undo Takes It Back
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

    // Letters, Digits And Apostrophes Make A Word
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’';

    // One Editing Item, Named And Drawn As Windows' Standard Command; Its Shortcut Only Shown
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

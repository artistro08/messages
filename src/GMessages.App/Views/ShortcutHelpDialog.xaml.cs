using GMessages.Presentation.Shortcuts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views;

/// <summary>
/// The Ctrl + ? dialog: a two-column list of the wired shortcuts from <see cref="ShortcutMap.HelpRows"/>.
/// </summary>
/// <remarks>
/// <para>
/// The rows are built once in code from the fixed list, so there is no binding to keep AOT-safe. Each row's keys
/// are <c>Shortcuts.Keys.{n}</c> and its action <c>Shortcuts.Action.{n}</c>, numbered top to bottom.
/// </para>
/// <para>
/// Place it in the window's tree (that gives it the window's <c>XamlRoot</c>) and set its <c>RequestedTheme</c> to the
/// window's before showing it.
/// </para>
/// </remarks>
public sealed partial class ShortcutHelpDialog : ContentDialog
{
    /// <summary>
    /// Creates the dialog with its rows.
    /// </summary>
    public ShortcutHelpDialog()
    {
        InitializeComponent();
        Controls.Dialogs.UseFluentStyle(this);

        // One Grid Row Per Shortcut: Keys Left, Action Right
        for (int i = 0; i < ShortcutMap.HelpRows.Count; i++)
        {
            (string keys, string action) = ShortcutMap.HelpRows[i];
            RowsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(keys, "Shortcuts.Keys." + i, i, 0);
            AddCell(action, "Shortcuts.Action." + i, i, 1);
        }
    }

    private void AddCell(string text, string automationId, int row, int column)
    {
        var cell = new TextBlock { Text = text };
        AutomationProperties.SetAutomationId(cell, automationId);
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        RowsGrid.Children.Add(cell);
    }
}

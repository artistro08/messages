using GMessages.Presentation.Shortcuts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GMessages.App.Views;

/// <summary>
/// The Ctrl + ? dialog: the wired shortcuts from <see cref="ShortcutMap.HelpRows"/>, each action with its keys as key
/// caps joined by "+", as Layers' shortcut sheet shows them.
/// </summary>
/// <remarks>
/// <para>
/// The rows are built once in code from the fixed list, so there is no binding to keep AOT-safe. Each row's action is
/// <c>Shortcuts.Action.{n}</c> and its key caps <c>Shortcuts.Key.{n}.{k}</c>, numbered top to bottom and left to
/// right. Shift and Enter show as their Segoe Fluent Icons glyphs, named for Narrator.
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

        for (int i = 0; i < ShortcutMap.HelpRows.Count; i++)
        {
            (string keys, string action) = ShortcutMap.HelpRows[i];
            RowsPanel.Children.Add(Row(i, keys, action));
        }
    }

    // One Row: The Action Left, Its Key Caps Right; A Divider Above Every Row But The First
    private Grid Row(int index, string keys, string action)
    {
        var row = new Grid { Style = (Style)Resources["ShortcutRowStyle"] };
        if (index == 0)
        {
            row.BorderThickness = new Thickness(0);
        }

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Action
        var label = new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(label, "Shortcuts.Action." + index);
        row.Children.Add(label);

        // Keys: "Shift + +" Splits To Shift And +, So The Plus Key Keeps Its Cap
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        string[] parts = keys.Split(" + ");
        for (int k = 0; k < parts.Length; k++)
        {
            if (k > 0)
            {
                caps.Children.Add(new TextBlock { Text = "+", Style = (Style)Resources["KeyJoinerStyle"] });
            }

            caps.Children.Add(Cap(parts[k], $"Shortcuts.Key.{index}.{k}"));
        }

        Grid.SetColumn(caps, 1);
        row.Children.Add(caps);
        return row;
    }

    // A Key Cap: Shift And Enter As Their Glyphs, A Single Letter In Capitals, Escape Shortened; Anything Else As Written
    private Border Cap(string key, string automationId)
    {
        string? glyph = key switch
        {
            "Shift" => "",
            "Enter" => "",
            _       => null,
        };

        var text = new TextBlock
        {
            Text                = glyph ?? (key.Length == 1 ? key.ToUpperInvariant() : key == "Escape" ? "Esc" : key),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        if (glyph is not null)
        {
            text.FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];
            AutomationProperties.SetName(text, key);
        }

        AutomationProperties.SetAutomationId(text, automationId);
        return new Border { Style = (Style)Resources["KeyCapStyle"], Child = text };
    }
}

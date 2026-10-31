using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GMessages.App.Views;

/// <summary>
/// One row of the emoji picker: a category's name, or up to a row's worth of that category's emoji.
/// </summary>
/// <param name="Header">The category's name for a name row, else null.</param>
/// <param name="Emoji">The row's emoji with their search words (the first word is the tooltip); empty for a name row.</param>
internal sealed partial record EmojiRow(string? Header, IReadOnlyList<(string Emoji, string Words)> Emoji);

/// <summary>
/// Makes the emoji picker's rows for its <see cref="ItemsRepeater"/> as they scroll into view, and reuses the ones that
/// scroll out.
/// </summary>
/// <remarks>
/// <para>
/// Each row is one grid: a name line across it, and a button per column, in equal columns that fill the picker's width.
/// A name row shows only the name, an emoji row only its buttons (the spare ones in a category's last row hide). Each
/// button carries the AutomationId <c>EmojiPicker.{Code Points In Hex}</c>, the emoji's first search word as its tooltip,
/// and draws in Segoe UI Emoji, so emoji that default to plain text (☝️, ✍️) still show in color.
/// </para>
/// <para>
/// Made rows are kept, so a reused row is found again by reference (it was made here, so it comes back as the same
/// object; no type test on the element is needed).
/// </para>
/// </remarks>
internal sealed partial class EmojiRowFactory : IElementFactory
{
    // Every Row Made, With Its Parts And The Emoji Its Buttons Show Now, And The Rows Scrolled Out For Reuse
    private readonly Dictionary<UIElement, RowParts> _made = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<UIElement> _free = new();

    private readonly int _columns;
    private readonly double _cell;
    private readonly Style _buttonStyle;
    private readonly Style _noteStyle;
    private readonly Action<string> _pick;

    /// <summary>
    /// Creates the factory.
    /// </summary>
    /// <param name="columns">How many emoji a row holds.</param>
    /// <param name="cell">An emoji button's height, in effective pixels.</param>
    /// <param name="buttonStyle">The reaction picker's button style, which the emoji buttons start from.</param>
    /// <param name="noteStyle">The category name's text style.</param>
    /// <param name="pick">Called with the emoji when its button is clicked.</param>
    public EmojiRowFactory(int columns, double cell, Style buttonStyle, Style noteStyle, Action<string> pick)
    {
        _columns     = columns;
        _cell        = cell;
        _buttonStyle = buttonStyle;
        _noteStyle   = noteStyle;
        _pick        = pick;
    }

    /// <summary>
    /// Gets how many rows have been made (not reused), for the timing log.
    /// </summary>
    public int Made => _made.Count;

    /// <summary>
    /// Gives the repeater a row for an <see cref="EmojiRow"/>: a scrolled-out row filled again, or a new one.
    /// </summary>
    /// <param name="args">The row's data (an <see cref="EmojiRow"/>).</param>
    /// <returns>The row's grid.</returns>
    public UIElement GetElement(ElementFactoryGetArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var row           = (EmojiRow)args.Data;
        UIElement element = _free.Count > 0 ? _free.Pop() : MakeRow();
        RowParts parts    = _made[element];

        // Name Line
        parts.Header.Text       = row.Header ?? string.Empty;
        parts.Header.Visibility = row.Header is null ? Visibility.Collapsed : Visibility.Visible;

        // Buttons
        for (int i = 0; i < _columns; i++)
        {
            Button button = parts.Buttons[i];
            if (i >= row.Emoji.Count)
            {
                parts.Emoji[i]    = string.Empty;
                button.Visibility = Visibility.Collapsed;
                continue;
            }

            (string emoji, string words) = row.Emoji[i];
            parts.Emoji[i]               = emoji;
            button.Content               = emoji;
            button.Visibility            = Visibility.Visible;
            AutomationProperties.SetAutomationId(button, "EmojiPicker." + Key(emoji));
            ToolTipService.SetToolTip(button, words.Split(' ')[0]);
        }

        return element;
    }

    /// <summary>
    /// Keeps a row that scrolled out, for the next one that scrolls in.
    /// </summary>
    /// <param name="args">The scrolled-out row.</param>
    public void RecycleElement(ElementFactoryRecycleArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (_made.ContainsKey(args.Element))
        {
            _free.Push(args.Element);
        }
    }

    /// <summary>
    /// Gets an emoji's AutomationId part: its code points in hex, joined by dashes (🔥 is <c>1F525</c>).
    /// </summary>
    /// <param name="emoji">The emoji.</param>
    /// <returns>The code points.</returns>
    public static string Key(string emoji) =>
        string.Join("-", emoji.EnumerateRunes().Select(r => r.Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture)));

    // A New Row: The Name Line Across Equal Columns, Then A Button In Each Column That Picks What It Shows Now
    private Grid MakeRow()
    {
        var grid   = new Grid();
        var header = new TextBlock { Margin = new Thickness(4, 8, 0, 4), Style = _noteStyle };
        AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
        Grid.SetColumnSpan(header, _columns);
        grid.Children.Add(header);

        var parts = new RowParts(header, new Button[_columns], new string[_columns]);
        for (int i = 0; i < _columns; i++)
        {
            int column = i;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var button = new Button
            {
                Style               = _buttonStyle,
                MinWidth            = 0,
                Height              = _cell,
                Padding             = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontFamily          = new FontFamily("Segoe UI Emoji"),
            };
            button.Click += (_, _) =>
            {
                if (parts.Emoji[column].Length > 0)
                {
                    _pick(parts.Emoji[column]);
                }
            };
            Grid.SetColumn(button, column);
            grid.Children.Add(button);
            parts.Buttons[column] = button;
        }

        _made[grid] = parts;
        return grid;
    }

    // A Row's Name Line, Its Buttons, And The Emoji Each Button Shows Now (Empty When Hidden)
    private sealed record RowParts(TextBlock Header, Button[] Buttons, string[] Emoji);
}

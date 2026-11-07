using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace GMessages.App.Views;

/// <summary>
/// Holds 1 row of the emoji picker: a category name, or up to a row of emoji of that category.
/// </summary>
/// <param name="Header">The name of the category for a name row, else null.</param>
/// <param name="Emoji">
/// The emoji of the row with their search words, where the first word is the tooltip. Empty for a name row.
/// </param>
internal sealed partial record EmojiRow(string? Header, IReadOnlyList<(string Emoji, string Words)> Emoji);

/// <summary>
/// Makes the rows of the emoji picker for its <see cref="Microsoft.UI.Xaml.Controls.ItemsRepeater"/> as they scroll
/// into view. It reuses the rows that scroll out.
/// </summary>
/// <remarks>
/// <para>
/// Each row is 1 grid: a name line across it and a button per column. The equal columns fill the width of the picker.
/// A name row shows only the name, and an emoji row shows only its buttons. The spare buttons in the last row of a
/// category hide. Each button carries the AutomationId <c>EmojiPicker.{code points in hex}</c>. Its tooltip is the
/// first search word of the emoji. It draws only in the Fluent emoji font of the app, see App.xaml. Thus emoji that
/// default to plain text, such as ☝️ and ✍️, still show in color.
/// </para>
/// <para>
/// An emoji that this run did not draw yet appears a few frames later. Each frame draws a few new emoji. Thus a first
/// open never stops the app. An emoji drawn before shows at once. Its button is there from the start, named and
/// clickable.
/// </para>
/// <para>
/// The factory keeps the rows that it made, so it finds a reused row again by reference. The row comes back as the
/// same object, so no type test on the element is necessary.
/// </para>
/// <para>
/// The repeater of WinUI 2.8 takes only its own <see cref="Microsoft.UI.Xaml.Controls.ElementFactory"/>, not the
/// IElementFactory of Windows. A set of that factory throws.
/// </para>
/// </remarks>
internal sealed partial class EmojiRowFactory : Microsoft.UI.Xaml.Controls.ElementFactory
{
    // Every row made, with its parts and the emoji that its buttons show now. The free stack holds the rows that
    // scrolled out, for reuse.
    private readonly Dictionary<UIElement, RowParts> _made = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<UIElement> _free = new();

    private readonly int _columns;
    private readonly double _cell;
    private readonly Style _buttonStyle;
    private readonly Style _noteStyle;
    private readonly Action<string> _pick;
    private readonly FontFamily _emojiFont = new("ms-appx:///Assets/Fonts/FluentEmojiColor.ttf#Fluent Emoji Color");

    // The number of new emoji that 1 frame draws, about 50 ms of work.
    private const int DrawnPerTurn = 4;

    // The emoji that wait for their draw, and true while frames draw them.
    private readonly Queue<(RowParts Parts, int Column, string Emoji)> _pending = new();
    private bool _drawing;

    /// <summary>
    /// Creates the factory.
    /// </summary>
    /// <param name="columns">The number of emoji in a row.</param>
    /// <param name="cell">The height of an emoji button, in effective pixels.</param>
    /// <param name="buttonStyle">The button style of the reaction picker, which the emoji buttons start from.</param>
    /// <param name="noteStyle">The text style of the category name.</param>
    /// <param name="pick">Gets the emoji on a click of its button.</param>
    public EmojiRowFactory(int columns, double cell, Style buttonStyle, Style noteStyle, Action<string> pick)
    {
        _columns     = columns;
        _cell        = cell;
        _buttonStyle = buttonStyle;
        _noteStyle   = noteStyle;
        _pick        = pick;
    }

    /// <summary>
    /// Gets the emoji that the picker drew in this run. Windows keeps their glyphs, so the picker shows them at once.
    /// </summary>
    internal static HashSet<string> Drawn { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the number of rows made, not reused, for the timing log.
    /// </summary>
    public int Made => _made.Count;

    /// <summary>
    /// Gives the repeater a row for an <see cref="EmojiRow"/>: a scrolled-out row filled again, or a new row.
    /// </summary>
    /// <param name="args">The data of the row, an <see cref="EmojiRow"/>.</param>
    /// <returns>The grid of the row.</returns>
    protected override UIElement GetElementCore(Microsoft.UI.Xaml.Controls.ElementFactoryGetArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var row           = (EmojiRow)args.Data;
        UIElement element = _free.Count > 0 ? _free.Pop() : MakeRow();
        RowParts parts    = _made[element];

        parts.Header.Text       = row.Header ?? string.Empty;
        parts.Header.Visibility = row.Header is null ? Visibility.Collapsed : Visibility.Visible;

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
            button.Visibility            = Visibility.Visible;
            AutomationProperties.SetAutomationId(button, "EmojiPicker." + Key(emoji));
            AutomationProperties.SetName(button, emoji);
            ToolTipService.SetToolTip(button, words.Split(' ')[0]);

            // An emoji already drawn in this run shows at once. A new emoji waits for its turn. See DrawPending.
            if (Drawn.Contains(emoji))
            {
                parts.Glyphs[i].Text = emoji;
                continue;
            }

            parts.Glyphs[i].Text = string.Empty;
            _pending.Enqueue((parts, i, emoji));
        }

        if (_pending.Count > 0 && !_drawing)
        {
            _drawing = true;
            CompositionTarget.Rendering += DrawPending;
        }

        return element;
    }

    // Draws the waiting emoji, a few per frame, until none wait. The first draw of an emoji by Windows costs about
    // 12 ms, as measured in Plan 7. The 80 emoji of a first open in 1 frame stopped the app for about 1 second. The 550
    // emoji of the old picker froze it. This way the picker opens at once, and its emoji appear over about 1 second.
    // The app stays responsive between frames. The method runs just before each frame, so each frame draws only its
    // few. It skips an emoji whose button had a reuse after the queue took it.
    private void DrawPending(object? sender, object e)
    {
        int drawn = 0;
        while (drawn < DrawnPerTurn && _pending.TryDequeue(out (RowParts Parts, int Column, string Emoji) next))
        {
            if (next.Parts.Emoji[next.Column] != next.Emoji)
            {
                continue;
            }

            next.Parts.Glyphs[next.Column].Text = next.Emoji;
            Drawn.Add(next.Emoji);
            drawn++;
        }

        if (_pending.Count == 0)
        {
            CompositionTarget.Rendering -= DrawPending;
            _drawing = false;
        }
    }

    /// <summary>
    /// Keeps a row that scrolled out, for the next row that scrolls in.
    /// </summary>
    /// <param name="args">The scrolled-out row.</param>
    protected override void RecycleElementCore(Microsoft.UI.Xaml.Controls.ElementFactoryRecycleArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (_made.ContainsKey(args.Element))
        {
            _free.Push(args.Element);
        }
    }

    /// <summary>
    /// Gets the AutomationId part of an emoji: its code points in hex, joined by dashes. For example, 🔥 is
    /// <c>1F525</c>.
    /// </summary>
    /// <param name="emoji">The emoji.</param>
    /// <returns>The code points.</returns>
    public static string Key(string emoji) =>
        string.Join("-", emoji.EnumerateRunes().Select(r => r.Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture)));

    // Makes a new row: the name line across equal columns, then a button in each column. A button picks the emoji that
    // it shows at the time of the click.
    private Grid MakeRow()
    {
        var grid   = new Grid();
        var header = new TextBlock { Margin = new Thickness(4, 8, 0, 4), Style = _noteStyle };
        AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
        Grid.SetColumnSpan(header, _columns);
        grid.Children.Add(header);

        var parts = new RowParts(header, new Button[_columns], new TextBlock[_columns], new string[_columns]);
        for (int i = 0; i < _columns; i++)
        {
            int column = i;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var glyph = new TextBlock { FontFamily = _emojiFont };
            AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
            var button = new Button
            {
                Style               = _buttonStyle,
                MinWidth            = 0,
                Height              = _cell,
                Padding             = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content             = glyph,
            };
            parts.Glyphs[column] = glyph;
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

    // The name line of a row, its buttons, and the emoji that each button shows now. The emoji is empty while hidden.
    private sealed record RowParts(TextBlock Header, Button[] Buttons, TextBlock[] Glyphs, string[] Emoji);
}

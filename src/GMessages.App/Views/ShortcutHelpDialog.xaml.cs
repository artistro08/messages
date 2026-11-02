using GMessages.Presentation.Shortcuts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace GMessages.App.Views;

/// <summary>
/// The Ctrl + ? dialog: the wired shortcuts from <see cref="ShortcutMap.HelpRows"/>, each action with its keys as key
/// caps joined by "+", as Layers' shortcut sheet shows them. Closed by the X beside the title, Escape, or a click on
/// the dimmed window around it (not on the dialog itself); it keeps a gap from the window's top and bottom.
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
    // The Dialog Stays This Far From The Window's Edges, However Small The Window
    private const double WindowGap = 32;

    // The Empty Space Above The 20 px Heading's Letters Within Its Line, Taken Off The Title's Top
    private const double TitleRaise = 7;

    // The Dim Over The Window (A Click On It Closes The Dialog) And The Dialog's Card (Kept Clear Of The Window's Edges)
    private UIElement? _smoke;
    private FrameworkElement? _card;

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

    // =========================================================================
    // OPEN AND CLOSE
    // =========================================================================

    // Once Open: The Dim Takes Clicks (The Card Sits Above It, So Clicks On The Card Never Reach It), And The Card's Size
    // Follows The Window
    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // Shown, The Dialog Draws In Two Popups Of Its Own: One Holds Its Template (The Card), The Other Just The Dim, A
        // Lone Rectangle Over The Window (Told Apart By Having No Children; Not A Type Test, See UiHelpers.FindPart)
        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup.Child is not { } child)
            {
                continue;
            }

            if (UiHelpers.FindPart<FrameworkElement>(child, "BackgroundElement") is { } card)
            {
                _card = card;

                // The Template Holds The Title To Its Width At The Left; Stretched, The X Reaches The Right Corner. Raised
                // By The Space Above The Heading's Letters In Its Line, So The Gap Over Them Matches The Gap Beside Them
                if (UiHelpers.FindPart<Control>(card, "Title") is { } title)
                {
                    title.HorizontalAlignment        = HorizontalAlignment.Stretch;
                    title.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    title.Margin                     = new Thickness(title.Margin.Left, title.Margin.Top - TitleRaise, title.Margin.Right, title.Margin.Bottom);
                }
            }
            else if (VisualTreeHelper.GetChildrenCount(child) == 0)
            {
                _smoke = child;
            }
        }

        if (_smoke is not null)
        {
            _smoke.Tapped += OnSmokeTapped;
        }

        if (XamlRoot is { } root)
        {
            root.Changed += OnRootChanged;
            FitCard(root);
        }
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (_smoke is not null)
        {
            _smoke.Tapped -= OnSmokeTapped;
        }

        if (XamlRoot is { } root)
        {
            root.Changed -= OnRootChanged;
        }

        _smoke = null;
        _card  = null;
    }

    private void OnSmokeTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => Hide();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => FitCard(sender);

    // The Card Never Bigger Than The Window Less A Gap On Every Side; Its Rows Scroll Inside
    private void FitCard(XamlRoot root)
    {
        if (_card is not null)
        {
            _card.MaxHeight = Math.Max(0, root.Size.Height - (WindowGap * 2));
            _card.MaxWidth  = Math.Max(0, root.Size.Width - (WindowGap * 2));
        }
    }

    // =========================================================================
    // ROWS
    // =========================================================================

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

using GMessages.Presentation.Shortcuts;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;

namespace GMessages.App.Views;

/// <summary>
/// Shows the Ctrl + ? dialog: the wired shortcuts from <see cref="ShortcutMap.HelpRows"/>. Each action shows its keys
/// as key caps joined by "+", as the shortcut sheet of Layers shows them.
/// </summary>
/// <remarks>
/// <para>
/// The X beside the title, Escape or a click on the dimmed window around the dialog closes it. A click on the dialog
/// itself does not. The dialog keeps a gap from the top and bottom of the window.
/// </para>
/// <para>
/// The code builds the rows once from the fixed list, so there is no binding to keep AOT-safe. The action of each row
/// is <c>Shortcuts.Action.{n}</c> and its key caps are <c>Shortcuts.Key.{n}.{k}</c>. The numbers go from top to
/// bottom and from left to right. Shift and Enter show as their Segoe Fluent Icons glyphs, with names for Narrator.
/// </para>
/// <para>
/// Put the dialog in the tree of the window, which gives it the <c>XamlRoot</c> of the window. Set its
/// <c>RequestedTheme</c> to that of the window before it shows.
/// </para>
/// </remarks>
public sealed partial class ShortcutHelpDialog : ContentDialog
{
    // The dialog stays this far from the edges of the window, however small the window is.
    private const double WindowGap = 32;

    // The dim over the window, where a click closes the dialog. The card of the dialog stays clear of the window edges.
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

    // After the open, the dim takes clicks and the size of the card follows the window. The card sits above the dim, so
    // clicks on the card never reach the dim.
    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // The shown dialog draws in 2 popups of its own. One popup holds its template, the card. The other popup holds
        // only the dim, a lone rectangle over the window. The dim has no children, which tells it apart without a type
        // test. See UiHelpers.FindPart.
        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup.Child is not { } child)
            {
                continue;
            }

            if (UiHelpers.FindPart<FrameworkElement>(child, "BackgroundElement") is { } card)
            {
                _card = card;

                // The first open builds the title part after this point. The dialog tries again after each layout until
                // the stretch works.
                if (!StretchTitle())
                {
                    card.LayoutUpdated += OnCardLayoutUpdated;
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

    // The template holds the title to its width at the left. A stretched title puts the X in the right corner. False
    // while the title part does not exist yet.
    private bool StretchTitle()
    {
        if (_card is null || UiHelpers.FindPart<Control>(_card, "Title") is not { } title)
        {
            return false;
        }

        title.HorizontalAlignment        = HorizontalAlignment.Stretch;
        title.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return true;
    }

    private void OnCardLayoutUpdated(object? sender, object e)
    {
        if (StretchTitle() && _card is not null)
        {
            _card.LayoutUpdated -= OnCardLayoutUpdated;
        }
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (_smoke is not null)
        {
            _smoke.Tapped -= OnSmokeTapped;
        }

        if (_card is not null)
        {
            _card.LayoutUpdated -= OnCardLayoutUpdated;
        }

        if (XamlRoot is { } root)
        {
            root.Changed -= OnRootChanged;
        }

        _smoke = null;
        _card  = null;
    }

    private void OnSmokeTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) => Hide();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => FitCard(sender);

    // The card is never bigger than the window less a gap on every side. Its rows scroll inside.
    private void FitCard(XamlRoot root)
    {
        if (_card is not null)
        {
            _card.MaxHeight = Math.Max(0, root.Size.Height - (WindowGap * 2));
            _card.MaxWidth  = Math.Max(0, root.Size.Width - (WindowGap * 2));
        }
    }

    // Builds 1 row with the action at the left and its key caps at the right. A divider sits above every row except
    // the first row.
    private Grid Row(int index, string keys, string action)
    {
        var row = new Grid { Style = (Style)Resources["ShortcutRowStyle"] };
        if (index == 0)
        {
            row.BorderThickness = new Thickness(0);
        }

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(label, "Shortcuts.Action." + index);
        row.Children.Add(label);

        // "Shift + +" splits into Shift and +, so the plus key keeps its cap.
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

    // Builds a key cap. Shift and Enter show as their glyphs, a single letter shows in capitals, and Escape shows as
    // Esc. Any other key shows as written.
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

using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;

namespace GMessages.App.Views.Controls;

/// <summary>
/// One conversation list row: avatar, name, snippet, time, the pin and muted icons, and the ⋮ shown on hover.
/// </summary>
/// <remarks>
/// Unread rows draw the name, snippet and time bold. The row view model raises one change for every update, so every
/// <c>x:Bind</c> here re-reads it. The ⋮ (<c>Row.More.{id}</c>) fades in while the pointer is over the row, through the
/// <c>Hover</c> visual states on the root element, and raises <see cref="MoreRequested"/> when clicked.
/// </remarks>
public sealed partial class ConversationRow : UserControl
{
    /// <summary>Identifies the <see cref="Row"/> property.</summary>
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(ConversationRowViewModel), typeof(ConversationRow), new PropertyMetadata(null));

    // Reads The Windows Animation Setting Live (Settings > Accessibility > Visual Effects)
    private static readonly UISettings Ui = new();

    /// <summary>
    /// Creates the row.
    /// </summary>
    public ConversationRow()
    {
        InitializeComponent();

        // Copy
        AutomationProperties.SetName(MoreButton, ChatCopy.MoreOptions);
        ToolTipService.SetToolTip(MoreButton, ChatCopy.MoreOptions);
        AutomationProperties.SetName(MutedIcon, ChatCopy.Muted);
    }

    /// <summary>
    /// Raised when the ⋮ is clicked; the list shows the conversation's menu at <see cref="MoreAnchor"/>.
    /// </summary>
    public event EventHandler? MoreRequested;

    /// <summary>Gets or sets the conversation the row shows.</summary>
    public ConversationRowViewModel? Row
    {
        get => (ConversationRowViewModel?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>Gets the ⋮ button, where its menu opens.</summary>
    public FrameworkElement MoreAnchor => MoreButton;

    /// <summary>
    /// Gives a row part's AutomationId, such as <c>Row.More.c-ann</c>.
    /// </summary>
    /// <param name="prefix">The part's prefix, such as <c>Row.More.</c>.</param>
    /// <param name="id">The conversation ID.</param>
    /// <returns>The AutomationId.</returns>
    public static string PartId(string prefix, string id) => prefix + id;

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Shown", Ui.AnimationsEnabled);

    private void OnPointerLeft(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Hidden", Ui.AnimationsEnabled);

    private void OnMoreClick(object sender, RoutedEventArgs e) => MoreRequested?.Invoke(this, EventArgs.Empty);
}

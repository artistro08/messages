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
        DependencyProperty.Register(nameof(Row), typeof(ConversationRowViewModel), typeof(ConversationRow), new PropertyMetadata(null, OnRowChanged));

    // Reads The Windows Animation Setting Live (Settings > Accessibility > Visual Effects)
    private static readonly UISettings Ui = new();

    // The Pointer Is Over The Row; The ⋮'s Menu Is Open, So The ⋮ Stays While The Pointer Is In The Menu
    private bool _pointerOver;
    private bool _keepShown;

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

    /// <summary>
    /// Keeps the ⋮ shown while its menu is open (the pointer leaves the row for the menu).
    /// </summary>
    /// <remarks>Turned off, the ⋮ hides unless the pointer is back over the row.</remarks>
    /// <param name="keep">True while the menu is open.</param>
    internal void KeepShown(bool keep)
    {
        _keepShown = keep;
        if (!keep && !_pointerOver)
        {
            VisualStateManager.GoToState(this, "Hidden", Ui.AnimationsEnabled);
        }
    }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // A Recycled Row Shows Another Conversation: No ⋮ Left Over From The Last One
    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view          = (ConversationRow)d;
        view._pointerOver = false;
        view._keepShown   = false;
        VisualStateManager.GoToState(view, "Hidden", false);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        VisualStateManager.GoToState(this, "Shown", Ui.AnimationsEnabled);
    }

    // The Pointer Left The Row. Moving Between The Name, Avatar And Icons Raises No Exit Here (Checked In The UI Tests),
    // And No Bounds Check Is Made: Leaving The Window Straight From A Row Reports Its Last Point Inside The Row. While
    // The Menu Is Open (Its Overlay Takes The Pointer) The ⋮ Waits For The Menu To Close
    private void OnPointerLeft(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        if (!_keepShown)
        {
            VisualStateManager.GoToState(this, "Hidden", Ui.AnimationsEnabled);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e) => MoreRequested?.Invoke(this, EventArgs.Empty);
}

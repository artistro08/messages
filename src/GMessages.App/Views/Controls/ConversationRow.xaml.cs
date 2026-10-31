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
/// <c>x:Bind</c> here re-reads it. The ⋮ (<c>Row.More.{id}</c>) is collapsed until the pointer is over the row; then it
/// takes 32 px at the right edge and fades in while this row's time and icons slide left (the <c>Hover</c> visual states
/// on the root element, snapped when Windows animations are off). It raises <see cref="MoreRequested"/> when clicked.
/// <see cref="Flash"/> lights the row briefly for a new message.
/// </remarks>
public sealed partial class ConversationRow : UserControl
{
    /// <summary>Identifies the <see cref="Row"/> property.</summary>
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(ConversationRowViewModel), typeof(ConversationRow), new PropertyMetadata(null, OnRowChanged));

    // Reads The Windows Animation Setting Live (Settings > Accessibility > Visual Effects)
    private static readonly UISettings Ui = new();

    // How Far The Time Slides Left For The ⋮ (Its Width)
    private const double MoreWidth = 32;

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
            HideMore(Ui.AnimationsEnabled);
        }
    }

    /// <summary>
    /// Flashes the row: the accent fill rises for a moment, then fades out over about 800 ms. A new message calls it.
    /// </summary>
    /// <remarks>Does nothing while Windows' animation effects are off. A second flash restarts the first.</remarks>
    internal void Flash()
    {
        if (!Ui.AnimationsEnabled)
        {
            return;
        }

        FlashStoryboard.Begin();
    }

    // =========================================================================
    // HOVER STATES
    // =========================================================================

    // Shows The ⋮. From Rest It Takes Its Space At Once, So The Time Starts Where It Was (32 px Right Of Its New Spot) And
    // Slides Left; Mid Slide-Back The ⋮ Still Has Its Space, So The Slide Just Turns Around From Where The Time Is
    private void ShowMore()
    {
        if (MoreButton.Visibility == Visibility.Collapsed)
        {
            TimeShift.X = MoreWidth;
        }

        VisualStateManager.GoToState(this, "Shown", Ui.AnimationsEnabled);
    }

    // Hides The ⋮: The Time Slides Back From Where It Is, And Rests At 0 Once The ⋮ Collapses
    private void HideMore(bool useTransitions)
    {
        TimeShift.X = 0;
        VisualStateManager.GoToState(this, "Hidden", useTransitions);
    }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // A Recycled Row Shows Another Conversation: No ⋮ Or Flash Left Over From The Last One
    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view          = (ConversationRow)d;
        view._pointerOver = false;
        view._keepShown   = false;
        view.HideMore(false);
        view.FlashStoryboard.Stop();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        ShowMore();
    }

    // The Pointer Left The Row. Moving Between The Name, Avatar And Icons Raises No Exit Here (Checked In The UI Tests),
    // And No Bounds Check Is Made: Leaving The Window Straight From A Row Reports Its Last Point Inside The Row. While
    // The Menu Is Open (Its Overlay Takes The Pointer) The ⋮ Waits For The Menu To Close
    private void OnPointerLeft(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        if (!_keepShown)
        {
            HideMore(Ui.AnimationsEnabled);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e) => MoreRequested?.Invoke(this, EventArgs.Empty);
}

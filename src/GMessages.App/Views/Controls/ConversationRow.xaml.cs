using System.ComponentModel;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows 1 row of the conversation list: avatar, name, snippet, time, the pin and muted icons, and the ⋮ on hover.
/// </summary>
/// <remarks>
/// <para>
/// An unread row draws the name, snippet and time in bold. The <c>ReadState</c> visual states also draw its snippet and
/// time in primary text. The row view model raises 1 change for each update, so each <c>x:Bind</c> here reads it
/// again.
/// </para>
/// <para>
/// The ⋮ <c>Row.More.{id}</c> stays collapsed until the pointer is over the row. Then it takes 32 px at the right edge
/// and fades in. At the same time, the time and icons of the row slide left. The <c>Hover</c> visual states on the root
/// element run this, and they snap when the Windows animations are off. A click on the ⋮ raises
/// <see cref="MoreRequested"/>.
/// </para>
/// <para>
/// A hover on the row shows <see cref="ConversationRowViewModel.HoverText"/> as a tooltip, as Windows does for trimmed
/// text. The tooltip shows only when the name or snippet is trimmed, or when more than 1 unread message came in.
/// </para>
/// </remarks>
public sealed partial class ConversationRow : UserControl
{
    /// <summary>Identifies the <see cref="Row"/> property.</summary>
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(ConversationRowViewModel), typeof(ConversationRow), new PropertyMetadata(null, OnRowChanged));

    // Reads the live Windows animation setting under Settings > Accessibility > Visual effects.
    private static readonly UISettings Ui = new();

    // The time slides left by the width of the ⋮.
    private const double MoreWidth = 32;

    // _pointerOver is true while the pointer is over the row. _keepShown is true while the menu of the ⋮ is open. The ⋮
    // then stays while the pointer is in the menu.
    private bool _pointerOver;
    private bool _keepShown;

    /// <summary>
    /// Creates the row.
    /// </summary>
    public ConversationRow()
    {
        InitializeComponent();

        AutomationProperties.SetName(MoreButton, ChatCopy.MoreOptions);
        ToolTipService.SetToolTip(MoreButton, ChatCopy.MoreOptions);
        AutomationProperties.SetName(MutedIcon, ChatCopy.Muted);
    }

    /// <summary>
    /// Raised when the user clicks the ⋮. The list then shows the menu of the conversation at <see cref="MoreAnchor"/>.
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
    /// Gives the AutomationId of a row part, such as <c>Row.More.c-ann</c>.
    /// </summary>
    /// <param name="prefix">The prefix of the part, such as <c>Row.More.</c>.</param>
    /// <param name="id">The conversation ID.</param>
    /// <returns>The AutomationId.</returns>
    public static string PartId(string prefix, string id) => prefix + id;

    /// <summary>
    /// Keeps the ⋮ on screen while its menu is open. The pointer leaves the row for the menu.
    /// </summary>
    /// <remarks>When the value is false, the ⋮ hides unless the pointer is over the row again.</remarks>
    /// <param name="keep">True while the menu is open.</param>
    internal void KeepShown(bool keep)
    {
        _keepShown = keep;
        if (!keep && !_pointerOver)
        {
            HideMore(Ui.AnimationsEnabled);
        }
    }

    // Shows the ⋮. From rest, the ⋮ takes its space at once. Thus the time starts at its old place and slides left. The
    // old place is 32 px right of the new place. During a slide back, the ⋮ still has its space. The slide then changes
    // direction from the current place of the time.
    private void ShowMore()
    {
        if (MoreButton.Visibility == Visibility.Collapsed)
        {
            TimeShift.X = MoreWidth;
        }

        VisualStateManager.GoToState(this, "Shown", Ui.AnimationsEnabled);
    }

    // Hides the ⋮. The time slides back from its current place. It rests at 0 after the ⋮ collapses.
    private void HideMore(bool useTransitions)
    {
        TimeShift.X = 0;
        VisualStateManager.GoToState(this, "Hidden", useTransitions);
    }

    // A recycled row shows another conversation. The ⋮ of the last conversation goes. The row shows the read state of
    // the new conversation and follows each update.
    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view          = (ConversationRow)d;
        view._pointerOver = false;
        view._keepShown   = false;
        view.HideMore(false);

        if (e.OldValue is ConversationRowViewModel old)
        {
            old.PropertyChanged -= view.OnRowUpdated;
        }

        if (e.NewValue is ConversationRowViewModel row)
        {
            row.PropertyChanged += view.OnRowUpdated;
        }

        view.ShowReadState();
        view.ShowToolTip();
    }

    // Each update can change the read state and the text of the tooltip.
    private void OnRowUpdated(object? sender, PropertyChangedEventArgs e)
    {
        ShowReadState();
        ShowToolTip();
    }

    // A resize or a new text trims the name or snippet, or makes it fit again.
    private void OnTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs e) => ShowToolTip();

    // The tooltip shows the full name and snippet when either is trimmed. It shows the unread messages when more than 1
    // came in. Otherwise the row has no tooltip.
    private void ShowToolTip() =>
        ToolTipService.SetToolTip(Root, Row is { } row && (row.HasUnreadList || NameText.IsTextTrimmed || SnippetText.IsTextTrimmed) ? row.HoverText : null);

    // The ReadState group draws the snippet and time of an unread row in primary text.
    private void ShowReadState() => VisualStateManager.GoToState(this, Row?.IsUnread == true ? "Unread" : "Read", false);

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        ShowMore();
    }

    // The pointer left the row. A move between the name, the avatar and the icons raises no exit here. The UI tests
    // check this. The method makes no bounds check. A direct exit out of the window reports a last point inside the
    // row, so a check would fail. While the menu is open, its overlay takes the pointer. The ⋮ then waits for the
    // menu to close.
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

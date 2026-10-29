using GMessages.Presentation.Main;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// One conversation list row: avatar, name, snippet, time and the pin.
/// </summary>
/// <remarks>
/// Unread rows draw the name, snippet and time bold. The row view model raises one change for every update, so every
/// <c>x:Bind</c> here re-reads it.
/// </remarks>
public sealed partial class ConversationRow : UserControl
{
    /// <summary>Identifies the <see cref="Row"/> property.</summary>
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(ConversationRowViewModel), typeof(ConversationRow), new PropertyMetadata(null));

    /// <summary>
    /// Creates the row.
    /// </summary>
    public ConversationRow() => InitializeComponent();

    /// <summary>Gets or sets the conversation the row shows.</summary>
    public ConversationRowViewModel? Row
    {
        get => (ConversationRowViewModel?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }
}

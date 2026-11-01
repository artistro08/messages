using GMessages.App.Views.Controls;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views;

/// <summary>
/// Start chat, shown in place of the chat pane like Google's "New conversation" page: search contacts by name or
/// number, pick one or several, then Go opens the conversation.
/// </summary>
/// <remarks>
/// Contact rows are <c>Contact.{n}</c> and picked chips <c>StartChat.Chip.{n}</c>, numbered top to bottom. Clicking a
/// row picks it, which moves it from the list to its chip; clicking the chip drops it back into the list. Both lists are wrapped with <see cref="BindableList"/> (see
/// there for why).
/// </remarks>
public sealed partial class StartChatPane : UserControl
{
    /// <summary>Identifies the <see cref="StartChat"/> property.</summary>
    public static readonly DependencyProperty StartChatProperty =
        DependencyProperty.Register(nameof(StartChat), typeof(StartChatViewModel), typeof(StartChatPane), new PropertyMetadata(null, OnStartChatChanged));

    /// <summary>
    /// Creates the pane.
    /// </summary>
    public StartChatPane()
    {
        InitializeComponent();
        QueryBox.PlaceholderText = ChatCopy.TypeNameOrNumber;
    }

    /// <summary>
    /// Raised with the conversation ID once Go has opened (or found) the conversation.
    /// </summary>
    public event EventHandler<string>? Started;

    /// <summary>Gets Go's text (for bindings).</summary>
    public static string GoText => ChatCopy.StartChatGo;

    /// <summary>Gets or sets the pane's state, or null while closed.</summary>
    public StartChatViewModel? StartChat
    {
        get => (StartChatViewModel?)GetValue(StartChatProperty);
        set => SetValue(StartChatProperty, value);
    }

    /// <summary>
    /// Puts the cursor in the search box (the Ctrl + k shortcut, when Start chat was already open).
    /// </summary>
    /// <returns>True when the box took focus.</returns>
    public bool FocusQuery() => QueryBox.Focus(FocusState.Keyboard);

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // Opened Again: Show The New Lists And Put The Cursor In The Search Box
    private static void OnStartChatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pane  = (StartChatPane)d;
        var start = (StartChatViewModel?)e.NewValue;

        BindableList.Show(pane.ContactList, start?.Results);
        BindableList.Show(pane.PickedList, start?.Selected);

        if (start is not null)
        {
            pane.QueryBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnContactClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ContactRowViewModel contact)
        {
            StartChat?.Toggle(contact);
        }
    }

    // Numbers Each Realized Row Or Chip For UI Automation, Top To Bottom
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        string prefix = sender == ContactList ? "Contact." : "StartChat.Chip.";
        AutomationProperties.SetAutomationId(args.ItemContainer, prefix + args.ItemIndex);
    }

    // Opens The Conversation; When The Phone Can't, The Pane Says So (R11) And Stays Open With Everyone Still Picked
    private async void OnGoClick(object sender, RoutedEventArgs e)
    {
        if (StartChat is not { CanStart: true } start)
        {
            return;
        }

        // Start Chat Closed Or Reopened While The Phone Answered: Open Nothing
        if (await start.StartAsync() is { } conversation_id && StartChat == start)
        {
            Started?.Invoke(this, conversation_id);
        }
    }
}

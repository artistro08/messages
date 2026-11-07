using GMessages.App.Views.Controls;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views;

/// <summary>
/// Shows Start chat in place of the chat pane, as the "New conversation" page of Google does. The user searches
/// contacts by name or number and picks 1 or more. Go then opens the conversation.
/// </summary>
/// <remarks>
/// The contact rows are <c>Contact.{n}</c> and the picked chips are <c>StartChat.Chip.{n}</c>, numbered from top to
/// bottom. A click on a row picks it and moves it from the list to its chip. A click on the chip puts it back into
/// the list. <see cref="BindableList"/> wraps both lists, and that class tells why.
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
        TextMenu.Attach(QueryBox);
    }

    /// <summary>
    /// Occurs with the conversation ID after Go opened or found the conversation.
    /// </summary>
    public event EventHandler<string>? Started;

    /// <summary>Gets the text of Go for bindings.</summary>
    public static string GoText => ChatCopy.StartChatGo;

    /// <summary>Gets or sets the state of the pane, or null while the pane is closed.</summary>
    public StartChatViewModel? StartChat
    {
        get => (StartChatViewModel?)GetValue(StartChatProperty);
        set => SetValue(StartChatProperty, value);
    }

    /// <summary>
    /// Puts the cursor in the search box for the Ctrl + k shortcut, when Start chat is already open.
    /// </summary>
    /// <returns>True when the box took focus.</returns>
    public bool FocusQuery() => QueryBox.Focus(FocusState.Keyboard);

    // An open of the pane shows the new lists and puts the cursor in the search box.
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

    // Numbers each realized row or chip for UI Automation, from top to bottom.
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        string prefix = sender == ContactList ? "Contact." : "StartChat.Chip.";
        AutomationProperties.SetAutomationId(args.ItemContainer, prefix + args.ItemIndex);
    }

    // Opens the conversation. When the phone cannot open it, the pane shows the failure that requirement R11 asks for.
    // The pane then stays open with all people still picked.
    private async void OnGoClick(object sender, RoutedEventArgs e)
    {
        if (StartChat is not { CanStart: true } start)
        {
            return;
        }

        // When Start chat closed or opened again while the phone answered, the pane opens nothing.
        if (await start.StartAsync() is { } conversationId && StartChat == start)
        {
            Started?.Invoke(this, conversationId);
        }
    }
}

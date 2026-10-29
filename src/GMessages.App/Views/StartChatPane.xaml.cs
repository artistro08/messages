using GMessages.App.Views.Controls;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Rpc;
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
/// row picks it, clicking it again or its chip drops it. Both lists are wrapped with <see cref="BindableList"/> (see
/// there for why).
/// </remarks>
public sealed partial class StartChatPane : UserControl
{
    /// <summary>Identifies the <see cref="StartChat"/> property.</summary>
    public static readonly DependencyProperty StartChatProperty =
        DependencyProperty.Register(nameof(StartChat), typeof(StartChatViewModel), typeof(StartChatPane), new PropertyMetadata(null, OnStartChatChanged));

    /// <summary>Identifies the <see cref="ShowBack"/> property.</summary>
    public static readonly DependencyProperty ShowBackProperty =
        DependencyProperty.Register(nameof(ShowBack), typeof(bool), typeof(StartChatPane), new PropertyMetadata(false));

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

    /// <summary>
    /// Asks the window to close Start chat and show the list (the back button of a narrow window).
    /// </summary>
    public event EventHandler? BackRequested;

    /// <summary>Gets or sets the pane's state, or null while closed.</summary>
    public StartChatViewModel? StartChat
    {
        get => (StartChatViewModel?)GetValue(StartChatProperty);
        set => SetValue(StartChatProperty, value);
    }

    /// <summary>Gets or sets whether the back button shows (narrow windows).</summary>
    public bool ShowBack
    {
        get => (bool)GetValue(ShowBackProperty);
        set => SetValue(ShowBackProperty, value);
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

    // Opens The Conversation; When The Phone Can't, The Pane Stays Open With Everyone Still Picked
    private async void OnGoClick(object sender, RoutedEventArgs e)
    {
        if (StartChat is not { CanStart: true } start)
        {
            return;
        }

        try
        {
            string conversation_id = await start.StartAsync();
            Started?.Invoke(this, conversation_id);
        }
        catch (Exception ex) when (ex is MessageSendException or MessagingUnavailableException or PhoneNotRespondingException or ConnectionClosedException)
        {
            // TODO: No copy yet for a failed start; Go can be pressed again
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}

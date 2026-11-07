using System.Collections.ObjectModel;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows the Details of a conversation: its people, the Mute toggle and Block &amp; report spam.
/// </summary>
/// <remarks>
/// <para>
/// The dialog reads <see cref="ConversationDetailsViewModel"/> when it opens. The Mute toggle does not show in spam or
/// blocked. Block &amp; report spam shows only for a one-on-one chat.
/// </para>
/// <para>
/// The toggle shows whether the chat is muted. A flip runs Mute or Unmute at once, as a Windows toggle does. The view
/// model refuses a run on a failure or while a run is busy. A failure also shows the banner of the window. A refused
/// run flips the toggle back and sends nothing.
/// </para>
/// <para>
/// Block &amp; report spam closes the dialog and sets <see cref="IsBlockPicked"/>. The caller then asks as the menu
/// does. Set the <c>XamlRoot</c> and <c>RequestedTheme</c> of the dialog before it shows.
/// </para>
/// </remarks>
public sealed partial class ConversationDetailsDialog : ContentDialog
{
    private readonly MainViewModel _viewModel;
    private readonly string _conversationId;

    // The code flips the toggle back, so its Toggled event sends nothing.
    private bool _reverting;

    /// <summary>
    /// Creates the dialog for a conversation.
    /// </summary>
    /// <param name="viewModel">Runs Mute and Unmute.</param>
    /// <param name="c">The conversation, as the store holds it now.</param>
    public ConversationDetailsDialog(MainViewModel viewModel, ConversationInfo c)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(c);

        _viewModel      = viewModel;
        _conversationId = c.Id;
        InitializeComponent();
        Dialogs.UseFluentStyle(this);

        var details = new ConversationDetailsViewModel(c);

        Title               = ChatCopy.Details;
        CloseButtonText     = ChatCopy.Close;
        PeopleHeader.Text   = ChatCopy.People;
        MuteSwitch.Header   = ChatCopy.Mute;
        BlockButton.Content = ChatCopy.BlockAndReportSpam;
        AutomationProperties.SetName(MuteSwitch, ChatCopy.Mute);

        // The dialog watches the toggle only after the toggle shows the current mute state.
        BindableList.Show(PeopleList, new ObservableCollection<PersonRow>(details.People));
        MuteSwitch.IsOn        = details.IsMuted;
        MuteSwitch.Toggled    += OnMuteToggled;
        MuteSwitch.Visibility  = details.CanMute ? Visibility.Visible : Visibility.Collapsed;
        BlockButton.Visibility = details.CanBlock ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Gets whether Block &amp; report spam closed the dialog.</summary>
    public bool IsBlockPicked { get; private set; }

    // The method is async void as a click handler is. Thus a failure reaches the unhandled error log of the app.
    private async void OnMuteToggled(object sender, RoutedEventArgs e)
    {
        if (_reverting)
        {
            return;
        }

        bool mute = MuteSwitch.IsOn;
        if (await _viewModel.RunAsync(_conversationId, mute ? ConversationAction.Mute : ConversationAction.Unmute))
        {
            return;
        }

        // The view model refused the run, so the toggle returns to its last state.
        _reverting      = true;
        MuteSwitch.IsOn = !mute;
        _reverting      = false;
    }

    private void OnBlockClick(object sender, RoutedEventArgs e)
    {
        IsBlockPicked = true;
        Hide();
    }
}

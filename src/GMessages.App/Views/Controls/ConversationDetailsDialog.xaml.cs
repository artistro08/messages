using System.Collections.ObjectModel;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A conversation's Details: its people, the Mute toggle (not in spam or blocked) and, for a one-on-one chat, Block
/// &amp; report spam.
/// </summary>
/// <remarks>
/// <para>
/// Built from <see cref="ConversationDetailsViewModel"/> when it opens. The toggle shows whether the chat is muted and
/// runs Mute or Unmute as soon as it's flipped, Windows-toggle style; a refused run (a failure, which shows the
/// window's banner, or one still running) flips it back without sending anything.
/// </para>
/// <para>
/// Block &amp; report spam closes the dialog and sets <see cref="IsBlockPicked"/>; the caller then asks as the menu does.
/// Set its <c>XamlRoot</c> and <c>RequestedTheme</c> before showing it.
/// </para>
/// </remarks>
public sealed partial class ConversationDetailsDialog : ContentDialog
{
    private readonly MainViewModel _viewModel;
    private readonly string _conversationId;

    // The Toggle Is Being Flipped Back, So Its Toggled Sends Nothing
    private bool _reverting;

    /// <summary>
    /// Creates the dialog for a conversation.
    /// </summary>
    /// <param name="viewModel">Runs Mute and Unmute.</param>
    /// <param name="c">The conversation, read fresh from the store.</param>
    public ConversationDetailsDialog(MainViewModel viewModel, ConversationInfo c)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(c);

        _viewModel      = viewModel;
        _conversationId = c.Id;
        InitializeComponent();

        var details = new ConversationDetailsViewModel(c);

        // Copy
        Title               = ChatCopy.Details;
        CloseButtonText     = ChatCopy.Close;
        PeopleHeader.Text   = ChatCopy.People;
        MuteSwitch.Header   = ChatCopy.Mute;
        BlockButton.Content = ChatCopy.BlockAndReportSpam;
        AutomationProperties.SetName(MuteSwitch, ChatCopy.Mute);

        // State: The Toggle Is Watched Only Once It Shows The Current Mute
        BindableList.Show(PeopleList, new ObservableCollection<PersonRow>(details.People));
        MuteSwitch.IsOn        = details.IsMuted;
        MuteSwitch.Toggled    += OnMuteToggled;
        MuteSwitch.Visibility  = details.CanMute ? Visibility.Visible : Visibility.Collapsed;
        BlockButton.Visibility = details.CanBlock ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Gets whether Block &amp; report spam closed the dialog.</summary>
    public bool IsBlockPicked { get; private set; }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    // Async Void Like A Click Handler, So A Failure Reaches The App's Unhandled Error Log
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

        // Refused: Back To What It Was
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

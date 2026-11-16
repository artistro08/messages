using GMessages.Presentation.Messaging;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows a turning ring while a photo, video, voice message or file of mine sends from here.
/// </summary>
public sealed partial class UploadSpinner : UserControl
{
    /// <summary>
    /// Creates the spinner. Its screen reader name is "Sending".
    /// </summary>
    public UploadSpinner()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, ChatCopy.Sending);
    }

    /// <summary>
    /// Gives the spinner an automation peer, so its AutomationId and name reach UI Automation. A UserControl has no
    /// peer.
    /// </summary>
    /// <returns>The peer.</returns>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}

using Windows.UI.Xaml.Controls;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows a turning arc with smooth edges at all screen scales. It turns from when it loads.
/// </summary>
public sealed partial class Spinner : UserControl
{
    /// <summary>
    /// Creates the spinner.
    /// </summary>
    public Spinner() => InitializeComponent();
}

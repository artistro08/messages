using GMessages.App.Media;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Settings;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// Shows the settings over the main window below the title bar, each on a Windows Settings card. The cards include
/// the theme, Start with Windows, notifications, the microphone, unpair, the logs and About.
/// </summary>
/// <remarks>
/// The page adapts the <c>SettingsWindow</c> of Magic Buds, its cards and its About page. The window makes the page on
/// open and drops it on Back or with the window. The main window owns the view model, so the page never disposes it.
/// Unpair asks the window, which owns the confirm dialog. After the unpair the App switches the window to pairing.
/// </remarks>
public sealed partial class SettingsPage : UserControl
{
    // This is the card width of the Windows Community Toolkit SettingsCard at which the control goes below the header.
    private const double CardWrapWidth = 476;

    // Microphone names are long. Beside the header, the box stops at this width.
    private const double MicrophoneMaxWidth = 320;

    private static readonly string[] ThemeChoices = ["Use system setting", "Light", "Dark"];

    private bool _closed;
    private bool? _wrapped;

    /// <summary>
    /// Creates the page and reads the Start with Windows state.
    /// </summary>
    /// <param name="viewModel">The settings state, which the main window owns.</param>
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;

        // The page forgets the microphones of the last page. This box holds only "Windows default" until the search
        // finds them again. A saved index past it throws ArgumentException out of the first binding pass. That left
        // every dropdown blank.
        ViewModel.SetMicrophones([]);
        InitializeComponent();

        PinnedGridHeader.Text      = ChatCopy.ShowPinnedGrid;
        PinnedGridDescription.Text = ChatCopy.ShowPinnedGridDescription;
        AutomationProperties.SetName(PinnedGridSwitch, ChatCopy.ShowPinnedGrid);
        PinnedCardsHeader.Text      = ChatCopy.ShowPinnedCards;
        PinnedCardsDescription.Text = ChatCopy.ShowPinnedCardsDescription;
        AutomationProperties.SetName(PinnedCardsSwitch, ChatCopy.ShowPinnedCards);
        SnoozeHeader.Text      = ChatCopy.SnoozeLength;
        SnoozeDescription.Text = ChatCopy.SnoozeLengthDescription;
        AutomationProperties.SetName(SnoozeBox, ChatCopy.SnoozeLength);
        ReceivedPreviewsHeader.Text = ChatCopy.ShowReceivedLinkPreviews;
        AutomationProperties.SetName(ReceivedPreviewsSwitch, ChatCopy.ShowReceivedLinkPreviews);
        MicrophoneHeader.Text      = ChatCopy.Microphone;
        MicrophoneDescription.Text = ChatCopy.MicrophoneDescription;
        AutomationProperties.SetName(MicrophoneBox, ChatCopy.Microphone);

        AddChoices(ThemeBox, ThemeChoices);
        AddChoices(SnoozeBox, ViewModel.SnoozeChoices);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPagePressed), true);

        // The box holds "Windows default" now, and the active microphones of Windows after the search.
        MicrophoneBox.Items.Add(ChatCopy.WindowsDefault);
        _ = LoadMicrophonesAsync();

        _ = ViewModel.LoadAsync();
    }

    /// <summary>
    /// Asks the window to confirm and unpair, from Unpair this PC.
    /// </summary>
    public event EventHandler? UnpairRequested;

    /// <summary>
    /// Gets the settings state that the view binds to.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>Gets the name of the app for About.</summary>
    public static string AppName => AppInfo.DisplayName;

    /// <summary>Gets "Version 1.2.3" for About.</summary>
    public static string VersionText => $"Version {AppInfo.Version}";

    /// <summary>Gets the text of the repository card for About.</summary>
    public static string RepositoryText => $"{AppInfo.DisplayName} source code, releases and issues.";

    /// <summary>
    /// Flips a flag for a binding. The Unpair button is enabled while no unpair runs.
    /// </summary>
    /// <param name="value">The flag.</param>
    /// <returns>The opposite.</returns>
    public static bool Not(bool value) => !value;

    /// <summary>
    /// Stops the bindings of the page after the window drops it, so the long-lived view model stops its updates.
    /// </summary>
    public void Close()
    {
        _closed = true;
        Bindings.StopTracking();
    }

    // Finds the active microphones of Windows each time the page opens. Their items go in before the view model gets
    // them. Thus the saved microphone is in the box when the binding selects it. A failed search leaves only "Windows
    // default".
    private async Task LoadMicrophonesAsync()
    {
        List<(string Id, string Name)> microphones;
        try
        {
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
            microphones                         = [.. devices.Where(device => device.IsEnabled).Select(device => (device.Id, device.Name))];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MicrophoneConsent.Log("devices", ex.GetType().Name);
            return;
        }

        // A search that ends after the page closed changes nothing. A new page can have reset the list of the shared
        // view model. A stale list then throws out of its first binding pass.
        if (_closed)
        {
            return;
        }

        foreach ((_, string name) in microphones)
        {
            MicrophoneBox.Items.Add(name);
        }

        // "Windows default" names the microphone that it means today, so the user sees which microphone records.
        string defaultId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        if (microphones.FindIndex(mic => string.Equals(mic.Id, defaultId, StringComparison.OrdinalIgnoreCase)) is var index and >= 0)
        {
            MicrophoneBox.Items[0] = $"{ChatCopy.WindowsDefault} ({microphones[index].Name})";
        }

        ViewModel.SetMicrophones(microphones);
    }

    // The page adds the choices before the bindings select one. Under AOT a .NET list is not a XAML ItemsSource.
    // A box takes the width of its selected choice, but its list takes the width of its widest choice. A list wider
    // than its box opens past the edge of the window, and then a click outside does not close it. Thus the box gets the
    // width of its widest choice.
    private static void AddChoices(ComboBox box, IReadOnlyList<string> choices)
    {
        foreach (string choice in choices)
        {
            box.Items.Add(choice);
        }

        void FitToWidestChoice(object sender, RoutedEventArgs e)
        {
            box.Loaded -= FitToWidestChoice;
            box.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            string selected = box.SelectedIndex >= 0 ? choices[box.SelectedIndex] : string.Empty;
            box.MinWidth    = box.DesiredSize.Width - TextWidth(selected) + choices.Max(TextWidth);
        }

        box.Loaded += FitToWidestChoice;
    }

    // In the island, a press outside an open dropdown list does not close the list, so the page closes it. The parents
    // of an item in a list do not include the page, or they include the box. A press on the box itself stays with the
    // box.
    private void OnPagePressed(object sender, PointerRoutedEventArgs e)
    {
        var path = new List<DependencyObject>();
        for (DependencyObject? at = e.OriginalSource?.As<DependencyObject>(); at is not null; at = VisualTreeHelper.GetParent(at))
        {
            path.Add(at);
        }

        if (!path.Contains(this))
        {
            return;
        }

        foreach (ComboBox box in new[] { ThemeBox, SnoozeBox, MicrophoneBox })
        {
            if (box.IsDropDownOpen && !path.Contains(box))
            {
                box.IsDropDownOpen = false;
            }
        }
    }

    private static double TextWidth(string text)
    {
        var block = new TextBlock { Text = text };
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    // On a narrow page, the wide dropdowns and buttons go below the icon and the header and fill the card, as in Windows
    // Settings. On the right they make the card too tall. The width is the width of the cards, not of the window.
    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wrap = e.NewSize.Width < CardWrapWidth;
        if (wrap == _wrapped)
        {
            return;
        }

        _wrapped = wrap;
        foreach (FrameworkElement control in new FrameworkElement[] { SnoozeBox, MicrophoneBox, UpdateActions, RepositoryLink })
        {
            Grid.SetRow(control, wrap ? 1 : 0);
            Grid.SetColumn(control, wrap ? 0 : 2);
            Grid.SetColumnSpan(control, wrap ? 3 : 1);
            control.Margin              = wrap ? new Thickness(0, 12, 0, 0) : default;
            control.HorizontalAlignment = wrap ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        }

        MicrophoneBox.MaxWidth = wrap ? double.PositiveInfinity : MicrophoneMaxWidth;
    }

    private void OnUnpairClick(object sender, RoutedEventArgs e) => UnpairRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => ViewModel.OpenLogsFolder();

    private void OnCheckForUpdatesClick(object sender, RoutedEventArgs e) => ViewModel.CheckForUpdates();

    private void OnInstallUpdateClick(object sender, RoutedEventArgs e) => ViewModel.InstallUpdate();
}

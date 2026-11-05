using GMessages.App.Windowing;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using static GMessages.App.Windowing.WindowInterop;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The main window's title bar: Back, ☰, the icon and title, the app's own content, and the caption buttons.
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3's TitleBar rebuilt for WinUI 2.8 (which has none), with its look and layout. The window can't draw a system
/// caption over an island, so the caption buttons are drawn here, and the window's title bar input (see
/// <see cref="TitleBarInput"/>) gives the row the caption's behavior: after every layout change this tells the window
/// where the row, the caption buttons and the click-through holes are. Back and ☰ are holes, and so is every element
/// passed to <see cref="AddPassthrough"/> (the profile picture), or only those passed to
/// <see cref="SetPassthroughOverride"/> while it's set (the photo viewer's toolbar).
/// </para>
/// <para>
/// The caption buttons take no pointer input of their own: the input window reports hover and press, and runs the
/// click as the system command. Their Click handlers are for UI Automation (Narrator, the UI tests), which invokes
/// them directly.
/// </para>
/// </remarks>
public sealed partial class TitleBar : UserControl
{
    private readonly List<FrameworkElement> _passthrough = [];
    private IReadOnlyList<FrameworkElement>? _passthroughOverride;
    private XamlWindow? _window;
    private TitleBarLayout? _lastLayout;
    private bool _isActive = true;

    /// <summary>
    /// Creates the title bar; <see cref="Attach"/> connects it to its window.
    /// </summary>
    public TitleBar()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => RecomputeDragRegions();
    }

    /// <summary>
    /// The title bar's Back was clicked.
    /// </summary>
    public event EventHandler? BackRequested;

    /// <summary>
    /// The title bar's ☰ was clicked.
    /// </summary>
    public event EventHandler? PaneToggleRequested;

    /// <summary>
    /// Gets or sets the title beside the icon; UI Automation reads the title bar by it, as WinUI 3's TitleBar.
    /// </summary>
    public string Title
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value;
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(this, value);
        }
    }

    /// <summary>
    /// Sets the icon left of the title.
    /// </summary>
    public ImageSource? Icon
    {
        get => IconImage.Source;
        set => IconImage.Source = value;
    }

    /// <summary>
    /// Gets or sets the app's own content, filling the row from the title to the caption buttons.
    /// </summary>
    public UIElement? MainContent
    {
        get => ContentHost.Content as UIElement;
        set => ContentHost.Content = value;
    }

    /// <summary>
    /// Gets the title bar's Back button (shown with <see cref="IsBackButtonVisible"/>).
    /// </summary>
    public Button Back => BackButton;

    /// <summary>
    /// Gets the title bar's ☰ button (shown with <see cref="IsPaneToggleButtonVisible"/>).
    /// </summary>
    public Button PaneToggle => PaneToggleButton;

    /// <summary>
    /// Gets or sets whether Back shows.
    /// </summary>
    public bool IsBackButtonVisible
    {
        get => BackButton.Visibility == Visibility.Visible;
        set
        {
            BackButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ShowHeaderSpacing();
        }
    }

    /// <summary>
    /// Gets or sets whether ☰ shows.
    /// </summary>
    public bool IsPaneToggleButtonVisible
    {
        get => PaneToggleButton.Visibility == Visibility.Visible;
        set
        {
            PaneToggleButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ShowHeaderSpacing();
        }
    }

    // After Back Or ☰ The Icon Sits 2 px On (WinUI 3's NegativeInsetSpacing), Else 14 px From The Edge
    private void ShowHeaderSpacing() =>
        LeftHeaderPaddingColumn.Width = new GridLength(IsBackButtonVisible || IsPaneToggleButtonVisible ? 2 : 14);

    /// <summary>
    /// Sets the caption buttons' light or dark look apart from the rest (dark over the photo viewer's scrim).
    /// </summary>
    public ElementTheme CaptionTheme
    {
        set => CaptionButtons.RequestedTheme = value;
    }

    /// <summary>
    /// Makes this the window's title bar: its row drags the window and its caption buttons work.
    /// </summary>
    /// <param name="window">The main window.</param>
    internal void Attach(XamlWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window                        = window;
        window.CaptionButtonsChanged  += (_, state) => ShowCaptionState(state.Hovered, state.Pressed);
        window.BoundsChanged          += (_, _) => ShowMaximized();
        window.Activated              += (_, active) =>
        {
            _isActive = active;
            _ = VisualStateManager.GoToState(this, active ? "Active" : "Deactivated", false);
            ShowCaptionState(0, 0);
        };
    }

    /// <summary>
    /// Lets clicks through to an element in the row (it isn't a drag area), for as long as it shows.
    /// </summary>
    /// <param name="element">An interactive element in the title bar's content.</param>
    public void AddPassthrough(FrameworkElement element) => _passthrough.Add(element);

    /// <summary>
    /// Lets clicks through only where these elements are, in place of Back, ☰ and the added elements; null puts those
    /// back. For the photo viewer's toolbar, whose scrim covers the row while it's open.
    /// </summary>
    /// <param name="elements">The elements taking clicks, or null.</param>
    public void SetPassthroughOverride(IReadOnlyList<FrameworkElement>? elements)
    {
        _passthroughOverride = elements;
        RecomputeDragRegions();
    }

    /// <summary>
    /// Tells the window where the row, the caption buttons and the click-through holes are now. Runs after every layout
    /// pass, and does nothing unless something moved.
    /// </summary>
    public void RecomputeDragRegions()
    {
        if (_window is null || XamlRoot is not { } root || ActualWidth <= 0)
        {
            return;
        }

        double scale = root.RasterizationScale;
        var holes    = new List<RECT>();
        foreach (FrameworkElement element in _passthroughOverride ?? [BackButton, PaneToggleButton, .. _passthrough])
        {
            if (element.Visibility == Visibility.Visible && element.ActualWidth > 0 && ToClient(element, scale) is { } hole)
            {
                holes.Add(hole);
            }
        }

        if (ToClient(this, scale) is not { } bar || ToClient(MinimizeButton, scale) is not { } minimize
            || ToClient(MaximizeButton, scale) is not { } maximize || ToClient(CloseButton, scale) is not { } close)
        {
            return;
        }

        var layout = new TitleBarLayout(bar, minimize, maximize, close, holes);
        if (_lastLayout is { } last && last.Bar.Equals(bar) && last.Minimize.Equals(minimize) && last.Maximize.Equals(maximize) && last.Close.Equals(close)
            && last.Passthrough.SequenceEqual(holes))
        {
            return;
        }

        _lastLayout = layout;
        _window.SetTitleBar(layout);
    }

    // An Element's Bounds In The Window's Client Pixels (The Island Fills The Client Area From Its Top Left)
    private static RECT? ToClient(FrameworkElement element, double scale)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return null;
        }

        Rect bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new RECT
        {
            Left   = (int)Math.Round(bounds.Left * scale),
            Top    = (int)Math.Round(bounds.Top * scale),
            Right  = (int)Math.Round(bounds.Right * scale),
            Bottom = (int)Math.Round(bounds.Bottom * scale),
        };
    }

    // =========================================================================
    // CAPTION BUTTONS
    // =========================================================================

    // Hover And Press, As The Input Window Reports Them; Dimmed While The Window Is Inactive
    private void ShowCaptionState(int hovered, int pressed)
    {
        ShowCaptionState(MinimizeButton, HTMINBUTTON, hovered, pressed);
        ShowCaptionState(MaximizeButton, HTMAXBUTTON, hovered, pressed);
        ShowCaptionState(CloseButton, HTCLOSE, hovered, pressed);
    }

    private void ShowCaptionState(Button button, int hit, int hovered, int pressed)
    {
        string state = pressed == hit ? "Pressed" : hovered == hit ? "PointerOver" : _isActive ? "Normal" : "Inactive";
        _ = VisualStateManager.GoToState(button, state, false);
    }

    // Maximize Becomes Restore While Maximized
    private void ShowMaximized()
    {
        bool maximized              = _window?.IsMaximized == true;
        MaximizeButton.Content      = maximized ? "" : "";
        string name                 = maximized ? "Restore" : "Maximize";
        Windows.UI.Xaml.Automation.AutomationProperties.SetName(MaximizeButton, name);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => RunCommand(SC_MINIMIZE);

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => RunCommand(_window?.IsMaximized == true ? SC_RESTORE : SC_MAXIMIZE);

    private void OnCloseClick(object sender, RoutedEventArgs e) => RunCommand(SC_CLOSE);

    private void RunCommand(int command)
    {
        if (_window is { } window)
        {
            _ = PostMessageW(window.Hwnd, WM_SYSCOMMAND, command, 0);
        }
    }

    /// <summary>
    /// Gives the title bar its own UI Automation element (a UserControl has none), named by its title.
    /// </summary>
    /// <returns>The title bar's automation peer.</returns>
    protected override Windows.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new Windows.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer(this);

    // =========================================================================
    // BACK AND ☰
    // =========================================================================

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void OnPaneToggleClick(object sender, RoutedEventArgs e) => PaneToggleRequested?.Invoke(this, EventArgs.Empty);
}

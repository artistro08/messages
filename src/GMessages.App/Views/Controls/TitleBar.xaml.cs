using GMessages.App.Windowing;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using static GMessages.App.Windowing.WindowInterop;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows the title bar of the main window: Back, ☰, the icon and title, the app content and the caption buttons.
/// </summary>
/// <remarks>
/// <para>
/// This control rebuilds the TitleBar of WinUI 3 with its look and layout, because WinUI 2.8 has no TitleBar. The
/// window cannot draw a system caption over an island, so this control draws the caption buttons. The title bar input
/// of the window, <see cref="TitleBarInput"/>, gives the row the behavior of a caption. After each layout change, the
/// control sends the window the places of the row, caption buttons and click-through holes.
/// </para>
/// <para>
/// Back and ☰ are holes, and so is each element from <see cref="AddPassthrough"/>, such as the profile picture. While
/// <see cref="SetPassthroughOverride"/> has a value, only its elements are holes, such as the toolbar of the photo
/// viewer.
/// </para>
/// <para>
/// The caption buttons take no pointer input. The input window reports hover and press, and it runs the click as the
/// system command. The Click handlers are for UI Automation, such as Narrator and the UI tests, which invokes them
/// directly.
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
    /// Creates the title bar. <see cref="Attach"/> connects it to its window.
    /// </summary>
    public TitleBar()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => RecomputeDragRegions();
    }

    /// <summary>
    /// Raised when the user clicks Back in the title bar.
    /// </summary>
    public event EventHandler? BackRequested;

    /// <summary>
    /// Raised when the user clicks ☰ in the title bar.
    /// </summary>
    public event EventHandler? PaneToggleRequested;

    /// <summary>
    /// Gets or sets the title beside the icon. UI Automation names the title bar by it, as for the TitleBar of WinUI 3.
    /// </summary>
    public string Title
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value;
            AutomationProperties.SetName(this, value);
        }
    }

    /// <summary>
    /// Gets or sets the icon left of the title.
    /// </summary>
    public ImageSource? Icon
    {
        get => IconImage.Source;
        set => IconImage.Source = value;
    }

    /// <summary>
    /// Gets or sets the content of the app, which fills the row from the title to the caption buttons.
    /// </summary>
    public UIElement? MainContent
    {
        get => ContentHost.Content as UIElement;
        set => ContentHost.Content = value;
    }

    /// <summary>
    /// Gets the Back button of the title bar. <see cref="IsBackButtonVisible"/> shows it.
    /// </summary>
    public Button Back => BackButton;

    /// <summary>
    /// Gets the ☰ button of the title bar. <see cref="IsPaneToggleButtonVisible"/> shows it.
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

    // After Back or ☰, the icon sits 2 px from the button, as NegativeInsetSpacing of WinUI 3 does. Otherwise it sits
    // 14 px from the edge.
    private void ShowHeaderSpacing() =>
        LeftHeaderPaddingColumn.Width = new GridLength(IsBackButtonVisible || IsPaneToggleButtonVisible ? 2 : 14);

    /// <summary>
    /// Sets the light or dark look of the caption buttons alone, such as dark over the photo viewer scrim.
    /// </summary>
    public ElementTheme CaptionTheme
    {
        set => CaptionButtons.RequestedTheme = value;
    }

    /// <summary>
    /// Makes this control the title bar of the window. Its row drags the window, and its caption buttons work.
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
    /// Lets clicks through to an element in the row while the element shows. The element is not a drag area.
    /// </summary>
    /// <param name="element">An interactive element in the content of the title bar.</param>
    public void AddPassthrough(FrameworkElement element) => _passthrough.Add(element);

    /// <summary>
    /// Lets clicks through only at these elements, in place of Back, ☰ and the added elements. A null value restores
    /// those holes.
    /// </summary>
    /// <remarks>The photo viewer uses it for its toolbar, because its scrim covers the row while it is open.</remarks>
    /// <param name="elements">The elements that take clicks, or null.</param>
    public void SetPassthroughOverride(IReadOnlyList<FrameworkElement>? elements)
    {
        _passthroughOverride = elements;
        RecomputeDragRegions();
    }

    /// <summary>
    /// Sends the window the current places of the row, the caption buttons and the click-through holes. The method runs
    /// after each layout pass and does nothing unless something moved.
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

    // Gives the bounds of an element in client pixels of the window. The island fills the client area from its top
    // left.
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

    // Shows hover and press as the input window reports them. The glyphs dim while the window is inactive.
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

    // Maximize becomes Restore while the window is maximized.
    private void ShowMaximized()
    {
        bool maximized              = _window?.IsMaximized == true;
        MaximizeButton.Content      = maximized ? "" : "";
        string name                 = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, name);
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
    /// Gives the title bar a UI Automation element, named by its title. A UserControl has none.
    /// </summary>
    /// <returns>The automation peer of the title bar.</returns>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void OnPaneToggleClick(object sender, RoutedEventArgs e) => PaneToggleRequested?.Invoke(this, EventArgs.Empty);
}

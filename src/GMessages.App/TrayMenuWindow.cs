using GMessages.App.Windowing;
using GMessages.Presentation.Placement;
using GMessages.Presentation.Settings;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace GMessages.App;

/// <summary>
/// Hosts the right-click menu of the tray icon as a XAML <see cref="MenuFlyout"/>.
/// </summary>
/// <remarks>
/// Ported from the <c>TrayMenuWindow.cs</c> of Magic Buds, with an added Open item and AutomationIds on the items. A
/// MenuFlyout needs a XAML root. Thus a frameless, see-through popup window of 1 × 1 pixels sits at the click point
/// (<see cref="XamlWindowKind.Popup"/>). The menu opens outside its bounds, away from the taskbar, as a windowed popup
/// (<c>ShouldConstrainToRootBounds = false</c>). The window hides again when the menu closes or the focus moves away.
/// </remarks>
internal sealed partial class TrayMenuWindow : XamlWindow
{
    // A fixed copy of MenuFlyoutItemThemePaddingNarrow of WinUI. An unbox of the theme resource throws under Native
    // AOT.
    private static readonly Thickness NarrowPadding = new(11, 4, 11, 7);

    private readonly Grid _root       = new();
    private readonly MenuFlyout _menu = new() { ShouldConstrainToRootBounds = false };
    private FlyoutShowOptions? _pendingShow;
    private bool _closed;

    /// <summary>
    /// Creates the hidden host window and its menu.
    /// </summary>
    public TrayMenuWindow()
        : base(XamlWindowKind.Popup)
    {
        Content = _root;

        BuildItems();
        _menu.Closed += (_, _) => OnMenuClosed();
        Closed       += (_, _) => _closed = true;

        _root.Loaded += (_, _) => ShowPendingMenu();
        Activated    += (_, active) =>
        {
            if (!active)
            {
                HideMenu();
            }
        };
    }

    /// <summary>
    /// Raised when the person clicks Open.
    /// </summary>
    public event EventHandler? OpenRequested;

    /// <summary>
    /// Raised when the person clicks Settings.
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Raised when the person clicks Quit.
    /// </summary>
    public event EventHandler? QuitRequested;

    /// <summary>
    /// Raised when the host window hides after the menu closes, so the app can trim memory.
    /// </summary>
    public event EventHandler? Hidden;

    /// <summary>
    /// Opens the menu at a screen point in physical pixels. The menu grows away from the taskbar.
    /// </summary>
    /// <param name="x">Anchor X in screen pixels.</param>
    /// <param name="y">Anchor Y in screen pixels.</param>
    /// <param name="edge">The taskbar edge the menu grows away from.</param>
    public void ShowAt(int x, int y, TaskbarEdge edge)
    {
        MoveAndResize(x, y, 1, 1);
        Activate();

        _pendingShow = new FlyoutShowOptions
        {
            Position  = new Point(0, 0),
            Placement = edge switch
            {
                TaskbarEdge.Top   => FlyoutPlacementMode.BottomEdgeAlignedRight,
                TaskbarEdge.Left  => FlyoutPlacementMode.RightEdgeAlignedBottom,
                TaskbarEdge.Right => FlyoutPlacementMode.LeftEdgeAlignedBottom,
                _                 => FlyoutPlacementMode.TopEdgeAlignedRight,
            },
        };
        if (_root.XamlRoot is not null)
        {
            ShowPendingMenu();
        }
    }

    /// <summary>
    /// Sets the light or dark look of the menu from the app theme.
    /// </summary>
    /// <param name="theme">The app theme setting.</param>
    public void ApplyTheme(AppTheme theme) => _root.RequestedTheme = theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark  => ElementTheme.Dark,
        _              => ElementTheme.Default,
    };

    private void BuildItems()
    {
        _menu.Items.Add(CreateItem("Open", "\uE8A7", "TrayMenu.Open", () => OpenRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(CreateItem("Settings", "\uE713", "TrayMenu.Settings", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(CreateItem("Quit", "\uE7E8", "TrayMenu.Quit", () => QuitRequested?.Invoke(this, EventArgs.Empty)));
    }

    // Closes the menu and hides its host window at once. It does not wait for the Closed event of the menu. An item
    // can activate a window that is already open, such as Settings or Open while the main window shows. That item
    // deactivates the host during the click, and the host can then stay on screen.
    private void HideMenu()
    {
        if (_closed)
        {
            return;
        }

        _menu.Hide();
        Hide();
    }

    // The host hides however the menu closed.
    private void OnMenuClosed()
    {
        if (_closed)
        {
            return;
        }

        Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    // Each item always gets the mouse padding. WinUI picks the taller touch padding when it cannot tell how the menu
    // opened. That happens often, because the host window never gets input. The host hides before the action of the
    // item runs, so the action can activate another window.
    private MenuFlyoutItem CreateItem(string text, string glyph, string automationId, Action onClick)
    {
        var item = new MenuFlyoutItem
        {
            Text    = text,
            Icon    = new FontIcon { Glyph = glyph },
            Padding = NarrowPadding,
        };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) =>
        {
            HideMenu();
            onClick();
        };
        return item;
    }

    private void ShowPendingMenu()
    {
        if (_closed || _pendingShow is not { } options)
        {
            return;
        }

        _pendingShow = null;
        _menu.ShowAt(_root, options);
    }
}

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
/// Hosts the tray icon's right-click menu as a XAML <see cref="MenuFlyout"/>.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>TrayMenuWindow.cs</c>, with Open added and the items given AutomationIds. A
/// MenuFlyout needs a XAML root, so a 1 × 1 frameless, see-through popup window (<see cref="XamlWindowKind.Popup"/>)
/// sits at the click point and the menu opens outside its bounds (<c>ShouldConstrainToRootBounds = false</c>, a
/// windowed popup), away from the taskbar. The window hides again when the menu closes or focus moves elsewhere.
/// </remarks>
internal sealed partial class TrayMenuWindow : XamlWindow
{
    // Hard-Coded Copy Of WinUI's MenuFlyoutItemThemePaddingNarrow: Unboxing The Theme Resource Throws Under Native AOT
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

        // Behavior
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
    /// Open was clicked.
    /// </summary>
    public event EventHandler? OpenRequested;

    /// <summary>
    /// Settings was clicked.
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Quit was clicked.
    /// </summary>
    public event EventHandler? QuitRequested;

    /// <summary>
    /// The host window hid (the menu closed), so the App can trim memory.
    /// </summary>
    public event EventHandler? Hidden;

    /// <summary>
    /// Opens the menu at a screen point (physical pixels), growing away from the taskbar.
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
    /// Sets the menu's light or dark look from the app theme.
    /// </summary>
    /// <param name="theme">The app theme setting.</param>
    public void ApplyTheme(AppTheme theme) => _root.RequestedTheme = theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark  => ElementTheme.Dark,
        _              => ElementTheme.Default,
    };

    // Open, Settings, Separator, Quit
    private void BuildItems()
    {
        _menu.Items.Add(CreateItem("Open", "\uE8A7", "TrayMenu.Open", () => OpenRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(CreateItem("Settings", "\uE713", "TrayMenu.Settings", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(CreateItem("Quit", "\uE7E8", "TrayMenu.Quit", () => QuitRequested?.Invoke(this, EventArgs.Empty)));
    }

    // Closes the menu and hides its host window directly, rather than waiting for the menu's Closed event. An item
    // that activates an already open window (Settings or Open with the main window up) deactivates the host while the
    // click is still running, and the host could stay shown
    private void HideMenu()
    {
        if (_closed)
        {
            return;
        }

        _menu.Hide();
        Hide();
    }

    // The Menu Is Gone, However It Went: The Host Hides
    private void OnMenuClosed()
    {
        if (_closed)
        {
            return;
        }

        Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    // Always the mouse-sized padding. WinUI picks touch padding (taller rows) when it can't
    // tell how the menu was opened, which is often, since the host window never gets input.
    // The host hides before the item's action runs, so the action can activate another window
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

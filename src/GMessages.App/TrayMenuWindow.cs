using GMessages.Presentation.Placement;
using GMessages.Presentation.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace GMessages.App;

/// <summary>
/// Hosts the tray icon's right-click menu as a WinUI <see cref="MenuFlyout"/>.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>TrayMenuWindow.cs</c>, with Open added and the items given AutomationIds. A
/// MenuFlyout needs a XAML root, so a 1 × 1 frameless, transparent window sits at the click point and the
/// menu opens outside its bounds (<c>ShouldConstrainToRootBounds = false</c>), away from the taskbar. The
/// window hides again when the menu closes or focus moves elsewhere.
/// </remarks>
internal sealed partial class TrayMenuWindow : Window
{
    private readonly Grid _root       = new();
    private readonly MenuFlyout _menu = new() { ShouldConstrainToRootBounds = false };
    private readonly IntPtr _hwnd;
    private FlyoutShowOptions? _pendingShow;

    /// <summary>
    /// Creates the hidden host window and its menu.
    /// </summary>
    public TrayMenuWindow()
    {
        Content = _root;
        _hwnd   = WindowNative.GetWindowHandle(this);

        BuildItems();
        _menu.Closed += (_, _) => AppWindow?.Hide();

        // Window Chrome
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable    = false;
        presenter.IsMaximizable  = false;
        presenter.IsMinimizable  = false;
        presenter.IsAlwaysOnTop  = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        // Invisible Host: frameless and see-through, so only the menu shows
        PopupWindowStyle.Apply(_hwnd);
        SystemBackdrop = new TransparentBackdrop();

        // Behavior
        _root.Loaded += (_, _) => ShowPendingMenu();
        Activated    += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _menu.Hide();
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
    /// Opens the menu at a screen point (physical pixels), growing away from the taskbar.
    /// </summary>
    /// <param name="x">Anchor X in screen pixels.</param>
    /// <param name="y">Anchor Y in screen pixels.</param>
    /// <param name="edge">The taskbar edge the menu grows away from.</param>
    public void ShowAt(int x, int y, TaskbarEdge edge)
    {
        AppWindow.MoveAndResize(new RectInt32(x, y, 1, 1));
        AppWindow.Show(true);
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);

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
        if (_root.IsLoaded)
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
        _menu.Items.Add(CreateItem("Open", "", "TrayMenu.Open", () => OpenRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(CreateItem("Settings", "", "TrayMenu.Settings", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(CreateItem("Quit", "", "TrayMenu.Quit", () => QuitRequested?.Invoke(this, EventArgs.Empty)));
    }

    // Always the mouse-sized padding. WinUI picks touch padding (taller rows) when it can't
    // tell how the menu was opened, which is often, since the host window never gets input.
    // Hard-coded copy of WinUI's MenuFlyoutItemThemePaddingNarrow: unboxing the theme resource
    // throws InvalidCastException under Native AOT.
    private static readonly Thickness NarrowPadding = new(11, 4, 11, 7);

    private static MenuFlyoutItem CreateItem(string text, string glyph, string automationId, Action onClick)
    {
        var item = new MenuFlyoutItem
        {
            Text    = text,
            Icon    = new FontIcon { Glyph = glyph },
            Padding = NarrowPadding,
        };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => onClick();
        return item;
    }

    private void ShowPendingMenu()
    {
        if (_pendingShow is not { } options)
        {
            return;
        }

        _pendingShow = null;
        _menu.ShowAt(_root, options);
    }
}

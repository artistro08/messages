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
    // Hard-Coded Copy Of WinUI's MenuFlyoutItemThemePaddingNarrow: Unboxing The Theme Resource Throws Under Native AOT
    private static readonly Thickness NarrowPadding = new(11, 4, 11, 7);

    private readonly Grid _root       = new();
    private readonly MenuFlyout _menu = new() { ShouldConstrainToRootBounds = false };
    private readonly IntPtr _hwnd;
    private FlyoutShowOptions? _pendingShow;

    // The Items' Text Color, Kept Apart From The Theme's Shared Text Brush: The Main Window Dims That One While Inactive
    // (WindowTextDimmer), Which Is Just When This Menu Shows
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _text = new();

    /// <summary>
    /// Creates the hidden host window and its menu.
    /// </summary>
    public TrayMenuWindow()
    {
        Content = _root;
        _hwnd   = WindowNative.GetWindowHandle(this);

        BuildItems();
        ColorText();
        _root.ActualThemeChanged += (_, _) => ColorText();
        _menu.Closed             += (_, _) => AppWindow?.Hide();

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
    public void ApplyTheme(AppTheme theme)
    {
        _root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };
        ColorText();
    }

    // The Items' Text In The Menu's Theme's Text Color, Full Strength
    private void ColorText()
    {
        if (Views.WindowTextDimmer.FindBrush(_root.ActualTheme, "TextFillColorPrimaryBrush") is { } shared)
        {
            _text.Color = shared.Color;
        }
    }

    // Open, Settings, Separator, Quit
    private void BuildItems()
    {
        _menu.Items.Add(CreateItem("Open", "", "TrayMenu.Open", () => OpenRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(CreateItem("Settings", "", "TrayMenu.Settings", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(CreateItem("Quit", "", "TrayMenu.Quit", () => QuitRequested?.Invoke(this, EventArgs.Empty)));
    }

    // Closes the menu and hides its host window directly, rather than waiting for the menu's Closed event. An item
    // that activates an already open window (Settings or Open with the main window up) deactivates the host while the
    // click is still running, and the host could stay shown. The App only rebuilds a hidden menu for a new theme, so a
    // host left shown kept the old theme's backdrop (the dark menu after choosing Light)
    private void HideMenu()
    {
        _menu.Hide();
        AppWindow?.Hide();
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

        // Its Text Draws With The Menu's Own Brush (See ColorText), Not The Shared One The Main Window Dims
        foreach (string key in (ReadOnlySpan<string>)["MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed"])
        {
            item.Resources[key] = _text;
        }

        item.Click += (_, _) =>
        {
            HideMenu();
            onClick();
        };
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

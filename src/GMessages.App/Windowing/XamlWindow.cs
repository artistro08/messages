using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using WinRT;
using static GMessages.App.Windowing.WindowInterop;

namespace GMessages.App.Windowing;

/// <summary>
/// How a <see cref="XamlWindow"/> looks: the app's main window, or an invisible frameless popup (the tray menu's host).
/// </summary>
internal enum XamlWindowKind
{
    /// <summary>A resizable window with Mica, its content drawn into the title bar (see <see cref="XamlWindow.SetTitleBar"/>).</summary>
    Main,

    /// <summary>A frameless, see-through, always-on-top window that's in no switcher; only what its content draws shows.</summary>
    Popup,
}

/// <summary>
/// A Win32 top-level window hosting system XAML (WinUI 2.8) in a XAML Island: what WinUI 3's Window and AppWindow did.
/// </summary>
/// <remarks>
/// <para>
/// Every window lives on the UI thread and is pumped by <see cref="RunMessageLoop"/>, which hands each message to the
/// islands first (Tab, arrow keys and accelerators reach XAML). Sizes and positions are physical pixels of the whole
/// window, as AppWindow's were.
/// </para>
/// <para>
/// Resize Sync (the reason for WinUI 2.8): system XAML renders through the OS compositor, and four steps make DWM hold
/// each resized frame until XAML has laid out at the new size, as UWP's CoreWindow does, so the content never trails
/// the frame: user32's private EnableResizeLayoutSynchronization on the window; the XAML Application's private
/// SetSynchronizationWindow naming it; each WM_SIZE sizes the island, then the thread's hidden CoreWindow to match (its
/// size is also the most a popup may fill: left at 1 × 1, the emoji picker's repeater made one row); and once XAML has
/// laid out and drawn a frame at the new size, CoreWindowResizeManager.NotifyLayoutCompleted. An island never sends
/// that last one itself, so without it Windows waits after the first step of every drag and the window freezes until
/// the mouse is let go (measured: one WM_SIZE per drag without it, every step with it). Only a shown window's real
/// size change waits for the frame; any other WM_SIZE releases XAML at once. Only the main window synchronizes.
/// </para>
/// <para>
/// The main window draws its own title bar: the system caption is taken off (WM_NCCALCSIZE) and Mica from DWM fills the
/// whole window. A <see cref="TitleBarInput"/> child over the title bar row gives back what the caption did: dragging,
/// the top resize edge, the caption buttons (with Snap Layouts on Maximize) and the system menu.
/// </para>
/// </remarks>
/// @link https://github.com/microsoft/microsoft-ui-xaml/issues/5148#issuecomment-6139475689
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "The island is disposed when the window is destroyed (WM_DESTROY); Close is the window's dispose.")]
internal unsafe class XamlWindow
{
    // Native Interface IDs
    private static readonly Guid IidDesktopWindowXamlSourceNative2 = new("e3dcd8c7-3057-4692-99c3-7b7720afda31");
    private static readonly Guid IidCoreWindowInterop              = new("45d64a29-a63e-4cb6-b498-5781d298cb4f");

    // IXamlSourceTransparency (Windows.UI.Xaml, Not Projected): IInspectable, Then get_ And put_IsBackgroundTransparent
    // https://learn.microsoft.com/windows/apps/api-reference/interface-members/ixamlsourcetransparency-isbackgroundtransparent
    private static readonly Guid IidXamlSourceTransparency = new("06636c29-5a17-458d-8ea2-2422d997a922");
    private const int SlotPutIsBackgroundTransparent       = 7;

    // Private (TranslucentFlyouts' DiagnosticsHandler.cpp): IInspectable, then StartOnCurrentThread, CreateIsland,
    // CreateIslandWithAppWindow, CreateIslandWithContentBridge, RemoveIsland, SetSynchronizationWindow
    private static readonly Guid IidFrameworkApplicationPrivate = new("b3ab45d8-6a4e-4e76-a00d-32d4643a9f1a");
    private const int SlotSetSynchronizationWindow = 11;

    private const string ClassName  = "GMessages.XamlWindow";
    private const int WaInactive    = 0;
    private const int SizeMinimized = 1;

    // Every Open Window, By Handle (The Window Procedure Is Static) And In Order (The Message Loop)
    private static readonly Dictionary<nint, XamlWindow> ByHandle = [];
    private static readonly List<XamlWindow> Open = [];
    private static bool s_registered;
    private static nint s_bigIcon;
    private static nint s_smallIcon;

    private readonly XamlWindowKind _kind;
    private readonly DesktopWindowXamlSource _source;
    private readonly nint _sourceNative;
    private readonly nint _island;
    private readonly nint _coreWindow;
    private readonly CoreWindowResizeManager? _resizeManager;
    private TitleBarInput? _titleBar;
    private UIElement? _content;
    private bool _layoutPending;
    private nint _lastSize;
    private bool _maximizeOnShow;
    private bool _destroyed;
    private int _minWidth;
    private int _minHeight;

    /// <summary>
    /// Creates the window, hidden, with an empty island.
    /// </summary>
    /// <param name="kind">The main window or a popup.</param>
    public XamlWindow(XamlWindowKind kind)
    {
        _kind = kind;
        RegisterClass();
        MakeIslandsTransparent();

        // Create The Window
        uint style   = kind == XamlWindowKind.Main ? WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN : WS_POPUP | WS_CLIPCHILDREN;
        uint exStyle = WS_EX_NOREDIRECTIONBITMAP | (kind == XamlWindowKind.Popup ? WS_EX_TOOLWINDOW | WS_EX_TOPMOST : 0);
        Hwnd         = CreateWindowExW(exStyle, ClassName, string.Empty, style, CW_USEDEFAULT, CW_USEDEFAULT, 1100, 760, 0, 0, GetModuleHandleW(null), 0);
        if (Hwnd == 0)
        {
            throw new InvalidOperationException($"CreateWindowExW failed ({Marshal.GetLastPInvokeError()}).");
        }

        ByHandle[Hwnd] = this;
        Open.Add(this);

        if (kind == XamlWindowKind.Main)
        {
            ApplyMica();
        }
        else
        {
            ApplySeeThrough();
        }

        // Attach The XAML Island
        _source       = new DesktopWindowXamlSource();
        _sourceNative = QueryInterface(_source, IidDesktopWindowXamlSourceNative2);
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)VTable(_sourceNative, 3))(_sourceNative, Hwnd));

        nint island;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTable(_sourceNative, 4))(_sourceNative, &island));
        _island = island;

        // Resize Sync: the user32 switch, the XAML core's synchronization window, and the CoreWindow WM_SIZE goes on to
        if (kind == XamlWindowKind.Main)
        {
            _ = EnableResizeLayoutSynchronization(Hwnd, true);
            _coreWindow    = GetCoreWindowHandle();
            _resizeManager = CoreWindowResizeManager.GetForCurrentView();
            SetSynchronizationWindow(Hwnd);
        }

        // The Frame Change Applies The Custom Frame (WM_NCCALCSIZE)
        _ = SetWindowPos(Hwnd, 0, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        ResizeIsland();
    }

    /// <summary>
    /// Raised when the window becomes active (true) or inactive (false).
    /// </summary>
    public event EventHandler<bool>? Activated;

    /// <summary>
    /// Raised once the window is closed (by <see cref="Close"/> or by the person), while its content still exists.
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Raised after the window moved, resized, or was maximized, minimized or restored.
    /// </summary>
    public event EventHandler? BoundsChanged;

    /// <summary>
    /// Raised when the pointer moves onto, off, or presses a caption button: the hit test code hovered and pressed
    /// (<see cref="WindowInterop.HTMINBUTTON"/>, <see cref="WindowInterop.HTMAXBUTTON"/>, <see cref="WindowInterop.HTCLOSE"/>, or 0).
    /// </summary>
    public event EventHandler<(int Hovered, int Pressed)>? CaptionButtonsChanged;

    /// <summary>
    /// Gets the window's handle.
    /// </summary>
    public nint Hwnd { get; }

    /// <summary>
    /// Gets or sets the island's root element.
    /// </summary>
    public UIElement? Content
    {
        get => _content;
        set
        {
            if (_content is FrameworkElement old)
            {
                old.LayoutUpdated -= OnContentLayoutUpdated;
            }

            _content        = value;
            _source.Content = value;
            if (value is FrameworkElement root && _resizeManager is not null)
            {
                root.LayoutUpdated += OnContentLayoutUpdated;
            }
        }
    }

    /// <summary>
    /// Gets the content's XAML root, or null before the content is loaded.
    /// </summary>
    public XamlRoot? XamlRoot => _content?.XamlRoot;

    /// <summary>
    /// Sets the window's title (the taskbar, Alt + Tab and Narrator read it).
    /// </summary>
    public string Title
    {
        set => SetWindowTextW(Hwnd, value);
    }

    /// <summary>
    /// Gets whether the window is shown.
    /// </summary>
    public bool IsVisible => !_destroyed && IsWindowVisible(Hwnd);

    /// <summary>
    /// Gets whether the window is maximized.
    /// </summary>
    public bool IsMaximized => !_destroyed && IsZoomed(Hwnd);

    /// <summary>
    /// Gets whether the window is minimized.
    /// </summary>
    public bool IsMinimized => !_destroyed && IsIconic(Hwnd);

    /// <summary>
    /// Gets the display scale (1 at 100%).
    /// </summary>
    public double Scale => GetDpiForWindow(Hwnd) / 96.0;

    /// <summary>
    /// Gets the window's bounds on screen, physical pixels, frame included.
    /// </summary>
    public RECT Bounds
    {
        get
        {
            RECT rect;
            _ = GetWindowRect(Hwnd, &rect);
            return rect;
        }
    }

    /// <summary>
    /// Gets the client area's size, physical pixels.
    /// </summary>
    public (int Width, int Height) ClientSize
    {
        get
        {
            RECT rect;
            _ = GetClientRect(Hwnd, &rect);
            return (rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    /// <summary>
    /// Gets the handle of the window whose island holds this XAML root (for a file picker's owner, say), or 0.
    /// WinUI 3's XamlRoot.ContentIslandEnvironment.AppWindowId, which system XAML doesn't have.
    /// </summary>
    /// <param name="root">An element's XAML root.</param>
    /// <returns>The window's handle, or 0 when no open window shows it.</returns>
    public static nint HwndFor(XamlRoot? root)
    {
        foreach (XamlWindow window in Open)
        {
            if (root is not null && ReferenceEquals(window.XamlRoot, root))
            {
                return window.Hwnd;
            }
        }

        return 0;
    }

    // =========================================================================
    // MESSAGE LOOP
    // =========================================================================

    /// <summary>
    /// Pumps messages for every window until <see cref="ExitMessageLoop"/>. Each message goes to the islands first.
    /// </summary>
    public static void RunMessageLoop()
    {
        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            if (PreTranslate(&msg))
            {
                continue;
            }

            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    /// <summary>
    /// Ends <see cref="RunMessageLoop"/> once the messages already queued have run.
    /// </summary>
    public static void ExitMessageLoop() => PostQuitMessage(0);

    // The Islands' Own Keyboard Handling, Window By Window, Until One Takes The Message
    private static bool PreTranslate(MSG* msg)
    {
        for (int i = 0; i < Open.Count; i++)
        {
            nint native = Open[i]._sourceNative;
            int handled = 0;
            _ = ((delegate* unmanaged[Stdcall]<nint, MSG*, int*, int>)VTable(native, 5))(native, msg, &handled);
            if (handled != 0)
            {
                return true;
            }
        }

        return false;
    }

    // =========================================================================
    // SHOWING AND PLACING
    // =========================================================================

    /// <summary>
    /// Shows the window (maximized, when <see cref="Maximize"/> was asked for while hidden) and brings it forward.
    /// </summary>
    public void Activate()
    {
        if (_destroyed)
        {
            return;
        }

        if (!IsWindowVisible(Hwnd))
        {
            _ = ShowWindow(Hwnd, _maximizeOnShow ? SW_SHOWMAXIMIZED : SW_SHOW);
            _maximizeOnShow = false;
        }

        _ = SetForegroundWindow(Hwnd);
    }

    /// <summary>
    /// Shows the window without changing its state.
    /// </summary>
    /// <param name="activate">True to make it the active window too.</param>
    public void Show(bool activate) => _ = ShowWindow(Hwnd, activate ? SW_SHOW : SW_SHOWNOACTIVATE);

    /// <summary>
    /// Hides the window.
    /// </summary>
    public void Hide() => _ = ShowWindow(Hwnd, SW_HIDE);

    /// <summary>
    /// Closes the window: <see cref="Closed"/> is raised, then the island and the window go.
    /// </summary>
    public void Close()
    {
        if (!_destroyed)
        {
            _ = DestroyWindow(Hwnd);
        }
    }

    /// <summary>
    /// Maximizes the window; while it's hidden, the next <see cref="Activate"/> shows it maximized.
    /// </summary>
    public void Maximize()
    {
        if (IsWindowVisible(Hwnd))
        {
            _ = ShowWindow(Hwnd, SW_SHOWMAXIMIZED);
            return;
        }

        _maximizeOnShow = true;
    }

    /// <summary>
    /// Moves and sizes the window, physical pixels, frame included.
    /// </summary>
    public void MoveAndResize(int x, int y, int width, int height) =>
        _ = SetWindowPos(Hwnd, 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>
    /// Sizes the window, physical pixels, frame included.
    /// </summary>
    public void Resize(int width, int height) =>
        _ = SetWindowPos(Hwnd, 0, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>
    /// Sets the least size the person can resize the window to, physical pixels, frame included.
    /// </summary>
    public void SetMinimumSize(int width, int height)
    {
        _minWidth  = width;
        _minHeight = height;
    }

    /// <summary>
    /// Sets the taskbar and Alt + Tab icon from an .ico file.
    /// </summary>
    /// <param name="path">The icon file.</param>
    public void SetIcon(string path)
    {
        // Loaded Once For The Process: WM_SETICON Doesn't Take The Icons, And The Main Window Is Made On Every Open
        // ponytail: one icon file for every window; key the cache by path if a second ever comes
        if (s_bigIcon == 0)
        {
            s_bigIcon   = LoadImageW(0, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            s_smallIcon = LoadImageW(0, path, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        }

        _ = SendMessageW(Hwnd, WM_SETICON, (nint)ICON_BIG, s_bigIcon);
        _ = SendMessageW(Hwnd, WM_SETICON, (nint)ICON_SMALL, s_smallIcon);
    }

    /// <summary>
    /// Tells DWM whether the window is dark, so its border and Mica take the dark tint.
    /// </summary>
    public void SetDarkFrame(bool dark)
    {
        int value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(Hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &value, sizeof(int));
    }

    // =========================================================================
    // TITLE BAR
    // =========================================================================

    /// <summary>
    /// Places the title bar's input over its row: where it drags the window, where the caption buttons are, and the
    /// holes where clicks reach the content instead (client pixels). Called after every layout change of the title bar.
    /// </summary>
    /// <param name="layout">The title bar's areas.</param>
    public void SetTitleBar(TitleBarLayout layout)
    {
        _titleBar ??= new TitleBarInput(Hwnd, (hovered, pressed) => CaptionButtonsChanged?.Invoke(this, (hovered, pressed)));
        _titleBar.Update(layout);
    }

    // =========================================================================
    // WINDOW PROCEDURE
    // =========================================================================

    private static void RegisterClass()
    {
        if (s_registered)
        {
            return;
        }

        fixed (char* className = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize        = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc   = &WndProc,
                hInstance     = GetModuleHandleW(null),
                hCursor       = LoadCursorW(0, IDC_ARROW),
                lpszClassName = className,
            };
            _ = RegisterClassExW(&windowClass);
        }

        s_registered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // Messages Before The Constructor Stored The Handle (WM_CREATE And Friends)
        if (!ByHandle.TryGetValue(hwnd, out XamlWindow? window))
        {
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        // An Exception Can't Cross Into Windows: The App's Handler Logs It (As WinUI 3 Sent Event Handlers' Exceptions To
        // Application.UnhandledException); One It Doesn't Handle Ends The App Here
        try
        {
            return window.OnMessage(msg, wParam, lParam);
        }
        catch (Exception ex) when (XamlHost.UnhandledException?.Invoke(ex) == true)
        {
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private nint OnMessage(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            // The Island And Mica Cover The Client Area: Never Paint A GDI Background Under Them
            case WM_ERASEBKGND:
                return 1;

            // Main: No System Caption; The Side And Bottom Frame Stay (Resize Edges), And A Maximized Window Keeps Its Top
            // Clear Of The Screen Edge
            case WM_NCCALCSIZE when _kind == XamlWindowKind.Main && wParam != 0:
                var calc = (NCCALCSIZE_PARAMS*)lParam;
                int top  = calc->rgrc0.Top;
                _ = DefWindowProcW(Hwnd, msg, wParam, lParam);
                calc->rgrc0.Top = top + (IsZoomed(Hwnd) ? FrameThickness() : 0);
                return 0;

            // Popup: Not A Single Pixel Of Frame
            case WM_NCCALCSIZE when _kind == XamlWindowKind.Popup && wParam != 0:
                return 0;

            // Island First, Then The Hidden CoreWindow (Left Alone While Minimized: Popups Can't Be Bigger), Then Wait For
            // XAML's Frame At The New Size (See The Remarks). Only A Shown Window's Real Size Change Waits: One That Changes
            // No Layout Draws No Frame, And XAML Would Wait For The Release Forever
            case WM_SIZE:
                // Minimized Keeps The Island's Size: At 0 × 0 The Chats Went Narrow, And The List Slid Back In On Restore
                bool minimized = wParam == SizeMinimized;
                if (!minimized)
                {
                    ResizeIsland();
                }

                if (!minimized && _coreWindow != 0)
                {
                    _ = SetWindowPos(_coreWindow, 0, 0, 0, LowWord(lParam), HighWord(lParam), SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                }

                bool changed = lParam != _lastSize;
                _lastSize    = lParam;
                if (changed && !minimized && IsWindowVisible(Hwnd))
                {
                    _layoutPending = _resizeManager is not null;
                }
                else
                {
                    _resizeManager?.NotifyLayoutCompleted();
                }

                return 0;

            case WM_ACTIVATE:
                Activated?.Invoke(this, LowWord(wParam) != WaInactive);
                break;

            // No Menu-Bar Mode On F10 Or A Lone Alt: There's No Menu Bar, And The Mode Swallowed Every Key After It (Shift +
            // F10 Then Typing Went Nowhere). Alt + Space (lParam ' ') Still Opens The Window Menu
            case WM_SYSCOMMAND when ((int)wParam & 0xFFF0) == SC_KEYMENU && lParam == 0:
                return 0;

            case WM_SETFOCUS:
                if (_island != 0)
                {
                    _ = SetFocus(_island);
                }

                return 0;

            case WM_GETMINMAXINFO:
                var info                  = (MINMAXINFO*)lParam;
                info->ptMinTrackSize.X    = Math.Max(info->ptMinTrackSize.X, _minWidth);
                info->ptMinTrackSize.Y    = Math.Max(info->ptMinTrackSize.Y, _minHeight);
                return 0;

            // Moved To A Monitor With Another Scale: Take The Size Windows Suggests
            case WM_DPICHANGED:
                var suggested = (RECT*)lParam;
                _ = SetWindowPos(Hwnd, 0, suggested->Left, suggested->Top, suggested->Right - suggested->Left, suggested->Bottom - suggested->Top, SWP_NOZORDER | SWP_NOACTIVATE);
                return 0;

            // DefWindowProc Sends WM_SIZE And WM_MOVE From Here, So It Runs First
            case WM_WINDOWPOSCHANGED:
                nint result = DefWindowProcW(Hwnd, msg, wParam, lParam);
                BoundsChanged?.Invoke(this, EventArgs.Empty);
                return result;

            case WM_DESTROY:
                OnDestroyed();
                return 0;
        }

        return DefWindowProcW(Hwnd, msg, wParam, lParam);
    }

    // Closed While The Content Still Exists, Then The Island Goes
    private void OnDestroyed()
    {
        _destroyed = true;
        CompositionTarget.Rendered -= OnResizedFrameRendered;
        Closed?.Invoke(this, EventArgs.Empty);

        Content = null;
        _source.Dispose();
        _titleBar?.Dispose();
        Marshal.Release(_sourceNative);
        ByHandle.Remove(Hwnd);
        Open.Remove(this);
    }

    // Layout Done At The New Size: Wait For That Frame To Be Drawn (Releasing Now Lets Windows Show The Old Frame Stretched)
    // ponytail: one pass per WM_SIZE; a size change that somehow lays nothing out waits for Windows' own timeout
    private void OnContentLayoutUpdated(object? sender, object e)
    {
        if (!_layoutPending)
        {
            return;
        }

        _layoutPending = false;
        CompositionTarget.Rendered += OnResizedFrameRendered;
    }

    // Frame At The New Size Drawn: Release The Frame Windows Is Holding
    private void OnResizedFrameRendered(object? sender, RenderedEventArgs e)
    {
        CompositionTarget.Rendered -= OnResizedFrameRendered;
        _resizeManager?.NotifyLayoutCompleted();
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Fill The Client Area With The Island
    private void ResizeIsland()
    {
        if (_island == 0)
        {
            return;
        }

        (int width, int height) = ClientSize;
        _ = SetWindowPos(_island, 0, 0, 0, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // The Resize Edge's Thickness, Also How Far A Maximized Window Hangs Past The Screen
    private int FrameThickness()
    {
        uint dpi = GetDpiForWindow(Hwnd);
        return GetSystemMetricsForDpi(SM_CYFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
    }

    // Mica From DWM Over The Whole Window; The Content Draws On It
    private void ApplyMica()
    {
        int backdrop = DWMSBT_MAINWINDOW;
        var margins  = new MARGINS();

        _ = DwmSetWindowAttribute(Hwnd, DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(int));
        _ = DwmExtendFrameIntoClientArea(Hwnd, &margins);
    }

    // Nothing But The Content: Blur-Behind With A Region That Covers Nothing Turns On Alpha Without Any Blur, No 1 px
    // Outline, Square Corners (WinUIEx's TransparentTintBackdrop Way)
    private void ApplySeeThrough()
    {
        var margins    = new MARGINS();
        nint nowhere   = CreateRectRgn(-2, -2, -1, -1);
        var blurBehind = new DWM_BLURBEHIND { dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION, fEnable = 1, hRgnBlur = nowhere };
        int noBorder   = DWMWA_COLOR_NONE;
        int square     = DWMWCP_DONOTROUND;

        _ = DwmExtendFrameIntoClientArea(Hwnd, &margins);
        _ = DwmEnableBlurBehindWindow(Hwnd, &blurBehind);
        _ = DeleteObject(nowhere);
        _ = DwmSetWindowAttribute(Hwnd, DWMWA_BORDER_COLOR, &noBorder, sizeof(int));
        _ = DwmSetWindowAttribute(Hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, &square, sizeof(int));
    }

    // Islands Paint An Opaque (Black) Background Of Their Own Unless Told Not To, Which Hid Mica And Made The Popup's
    // Corner Black. One Setting For Every Island On The Thread, From The Thread's XAML Window
    private static bool s_transparent;

    private static void MakeIslandsTransparent()
    {
        if (s_transparent || Window.Current is not { } xamlWindow)
        {
            return;
        }

        nint transparency = QueryInterface(xamlWindow, IidXamlSourceTransparency);
        if (transparency == 0)
        {
            return;
        }

        _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)VTable(transparency, SlotPutIsBackgroundTransparent))(transparency, 1);
        Marshal.Release(transparency);
        s_transparent = true;
    }

    // The Hidden CoreWindow XAML Islands Makes On This Thread
    private static nint GetCoreWindowHandle()
    {
        if (CoreWindow.GetForCurrentThread() is not { } coreWindow)
        {
            return 0;
        }

        nint interop = QueryInterface(coreWindow, IidCoreWindowInterop);
        if (interop == 0)
        {
            return 0;
        }

        nint coreHwnd = 0;
        _ = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTable(interop, 3))(interop, &coreHwnd);
        Marshal.Release(interop);
        return coreHwnd;
    }

    // Names The Window As The XAML Core's Resize Synchronization Window. A Missing Interface (Another Windows Build) Just
    // Leaves Resizing Unsynced
    private static void SetSynchronizationWindow(nint hwnd)
    {
        if (Application.Current is null)
        {
            return;
        }

        nint appPrivate = QueryInterface(Application.Current, IidFrameworkApplicationPrivate);
        if (appPrivate == 0)
        {
            return;
        }

        _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)VTable(appPrivate, SlotSetSynchronizationWindow))(appPrivate, hwnd);
        Marshal.Release(appPrivate);
    }

    // QI The Native Object Behind A Projected WinRT Object; 0 When It Doesn't Implement The Interface
    private static nint QueryInterface(object projected, Guid iid)
    {
        nint native = ((IWinRTObject)projected).NativeObject.ThisPtr;
        return Marshal.QueryInterface(native, in iid, out nint result) == 0 ? result : 0;
    }

    // Function Pointer In A COM Object's Vtable
    private static nint VTable(nint comObject, int slot) => (*(nint**)comObject)[slot];
}

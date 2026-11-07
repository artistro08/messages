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
/// Tells how a <see cref="XamlWindow"/> looks: the main window of the app, or an invisible frameless popup that hosts
/// the tray menu.
/// </summary>
internal enum XamlWindowKind
{
    /// <summary>
    /// A resizable window with Mica, whose content draws into the title bar (see <see cref="XamlWindow.SetTitleBar"/>).
    /// </summary>
    Main,

    /// <summary>
    /// A frameless, see-through, always-on-top window in no switcher. Only what its content draws shows.
    /// </summary>
    Popup,
}

/// <summary>
/// Hosts system XAML (WinUI 2.8) in a XAML Island in a Win32 top-level window. It does what Window and AppWindow of
/// WinUI 3 did.
/// </summary>
/// <remarks>
/// <para>
/// Every window lives on the UI thread, and <see cref="RunMessageLoop"/> pumps its messages. The loop gives each
/// message to the islands first, so Tab, the arrow keys and the accelerators reach XAML. Sizes and positions are
/// physical pixels of the whole window, as in AppWindow.
/// </para>
/// <para>
/// Resize sync is the reason for WinUI 2.8. System XAML renders through the compositor of the OS. DWM holds each
/// resized frame until XAML has laid out at the new size, as the CoreWindow of UWP does. Thus the content never trails
/// the frame. That takes 4 steps:
/// </para>
/// <list type="number">
/// <item>The private EnableResizeLayoutSynchronization of user32 turns on for the window.</item>
/// <item>The private SetSynchronizationWindow of the XAML Application names the window.</item>
/// <item>
/// Each WM_SIZE sizes the island, then sizes the hidden CoreWindow of the thread to match. Its size is also the most
/// that a popup can fill. At 1 × 1, the repeater of the emoji picker made a single row.
/// </item>
/// <item>
/// When XAML has laid out and drawn a frame at the new size, the window calls
/// CoreWindowResizeManager.NotifyLayoutCompleted.
/// </item>
/// </list>
/// <para>
/// An island never sends that last call itself. Without it, Windows waits after the first step of each drag. The
/// window then freezes until the person releases the mouse. A measure showed 1 WM_SIZE for each drag without the
/// call, and every step with it. Only a real size change of a shown window waits for the frame. Each other WM_SIZE
/// releases XAML at once. Only the main window synchronizes.
/// </para>
/// <para>
/// The main window draws its own title bar. WM_NCCALCSIZE removes the system caption, and Mica from DWM fills the
/// whole window. A <see cref="TitleBarInput"/> child over the title bar row gives back what the caption did. That is
/// the drag, the top resize edge, the caption buttons and the system menu. Maximize also keeps its Snap Layouts.
/// </para>
/// </remarks>
/// <seealso href="https://github.com/microsoft/microsoft-ui-xaml/issues/5148#issuecomment-6139475689"/>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "The window disposes the island when Windows destroys the window (WM_DESTROY). Close is the dispose of the window.")]
internal unsafe class XamlWindow
{
    private static readonly Guid IidDesktopWindowXamlSourceNative2 = new("e3dcd8c7-3057-4692-99c3-7b7720afda31");
    private static readonly Guid IidCoreWindowInterop              = new("45d64a29-a63e-4cb6-b498-5781d298cb4f");

    // IXamlSourceTransparency of Windows.UI.Xaml has no projection. Its vtable holds IInspectable, then
    // get_IsBackgroundTransparent and put_IsBackgroundTransparent.
    // https://learn.microsoft.com/windows/apps/api-reference/interface-members/ixamlsourcetransparency-isbackgroundtransparent
    private static readonly Guid IidXamlSourceTransparency = new("06636c29-5a17-458d-8ea2-2422d997a922");
    private const int SlotPutIsBackgroundTransparent       = 7;

    // A private interface, from DiagnosticsHandler.cpp of TranslucentFlyouts. Its vtable holds IInspectable, then
    // StartOnCurrentThread, CreateIsland, CreateIslandWithAppWindow, CreateIslandWithContentBridge, RemoveIsland and
    // SetSynchronizationWindow.
    private static readonly Guid IidFrameworkApplicationPrivate = new("b3ab45d8-6a4e-4e76-a00d-32d4643a9f1a");
    private const int SlotSetSynchronizationWindow = 11;

    private const string ClassName  = "GMessages.XamlWindow";
    private const int WaInactive    = 0;
    private const int SizeMinimized = 1;

    // Every open window, by handle for the static window procedure, and in order for the message loop.
    private static readonly Dictionary<nint, XamlWindow> ByHandle = [];
    private static readonly List<XamlWindow> Open = [];
    private static bool s_registered;
    private static bool s_transparent;
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
    private bool _shownOnce;
    private Windows.System.DispatcherQueueTimer? _uncloakAnyway;
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

        _source       = new DesktopWindowXamlSource();
        _sourceNative = QueryInterface(_source, IidDesktopWindowXamlSourceNative2);
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)VTable(_sourceNative, 3))(_sourceNative, Hwnd));

        nint island;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)VTable(_sourceNative, 4))(_sourceNative, &island));
        _island = island;

        // Resize sync needs the user32 switch, the synchronization window of the XAML core, and the CoreWindow that
        // WM_SIZE also sizes.
        if (kind == XamlWindowKind.Main)
        {
            _ = EnableResizeLayoutSynchronization(Hwnd, true);
            _coreWindow    = GetCoreWindowHandle();
            _resizeManager = CoreWindowResizeManager.GetForCurrentView();
            SetSynchronizationWindow(Hwnd);
        }

        // The frame change applies the custom frame through WM_NCCALCSIZE.
        _ = SetWindowPos(Hwnd, 0, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        ResizeIsland();
    }

    /// <summary>
    /// Raised when the window becomes active (true) or inactive (false).
    /// </summary>
    public event EventHandler<bool>? Activated;

    /// <summary>
    /// Raised after <see cref="Close"/> or the person closes the window, while its content still exists.
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Raised when the person closes the window. A cancel keeps the window. <see cref="Close"/> does not raise it.
    /// </summary>
    public event EventHandler<System.ComponentModel.CancelEventArgs>? Closing;

    /// <summary>
    /// Raised after the window moves or resizes, or after a maximize, a minimize or a restore.
    /// </summary>
    public event EventHandler? BoundsChanged;

    /// <summary>
    /// Raised when the pointer moves onto or off a caption button, or presses it. The argument holds the hit test codes
    /// of the hovered and the pressed button: <see cref="WindowInterop.HTMINBUTTON"/>,
    /// <see cref="WindowInterop.HTMAXBUTTON"/>, <see cref="WindowInterop.HTCLOSE"/> or 0.
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
    /// Sets the title of the window, which the taskbar, Alt + Tab and Narrator read.
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
    /// Gets the bounds of the window on screen in physical pixels, with the frame.
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
    /// Gets the size of the client area in physical pixels.
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
    /// Gets the handle of the window whose island holds this XAML root, or 0. A file picker uses it as its owner.
    /// </summary>
    /// <remarks>
    /// It replaces XamlRoot.ContentIslandEnvironment.AppWindowId of WinUI 3, which system XAML does not have.
    /// </remarks>
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
    /// Pumps the messages of every window until <see cref="ExitMessageLoop"/>. Each message goes to the islands first.
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

    // Gives the message to the keyboard handling of each island in turn, until an island takes it.
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
    /// Shows the window and brings it to the front. After a <see cref="Maximize"/> call while hidden, the window shows
    /// maximized.
    /// </summary>
    public void Activate()
    {
        if (_destroyed)
        {
            return;
        }

        if (!IsWindowVisible(Hwnd))
        {
            // The first show stays cloaked until XAML draws the first frame, as WinUI 3 windows do. Under memory
            // pressure, the build of a new window takes 1 second, which showed as a blank window. XAML draws only after
            // a change. Thus the window also listens to Rendering, which asks for a frame on each tick until XAML draws
            // a frame. XAML can also hold its frames for a window that it takes as hidden. A UI test once waited 20 s.
            // Thus the window shows after half a second in any case.
            if (!_shownOnce)
            {
                _shownOnce = true;
                SetCloak(true);
                CompositionTarget.Rendering += OnFirstFrameRendering;
                CompositionTarget.Rendered  += OnFirstFrameRendered;
                _uncloakAnyway               = Windows.System.DispatcherQueue.GetForCurrentThread().CreateTimer();
                _uncloakAnyway.Interval      = TimeSpan.FromMilliseconds(500);
                _uncloakAnyway.IsRepeating   = false;
                _uncloakAnyway.Tick         += (_, _) => Uncloak();
                _uncloakAnyway.Start();
            }

            _ = ShowWindow(Hwnd, _maximizeOnShow ? SW_SHOWMAXIMIZED : SW_SHOW);
            _maximizeOnShow = false;
        }

        _ = SetForegroundWindow(Hwnd);
    }

    // This handler is empty on purpose. While a handler listens to Rendering, XAML draws a frame on each tick, so the
    // first frame comes.
    private static void OnFirstFrameRendering(object? sender, object e)
    {
    }

    // XAML drew the first frame, so the window shows.
    private void OnFirstFrameRendered(object? sender, RenderedEventArgs e) => Uncloak();

    // Shows the cloaked window and stops the requests for frames. The first frame, the fallback after half a second or
    // the close calls it.
    private void Uncloak()
    {
        CompositionTarget.Rendering -= OnFirstFrameRendering;
        CompositionTarget.Rendered  -= OnFirstFrameRendered;
        _uncloakAnyway?.Stop();
        _uncloakAnyway = null;
        if (!_destroyed)
        {
            SetCloak(false);
        }
    }

    // Hides or shows the window through DWM, which still lets the window draw while hidden.
    private void SetCloak(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        _ = DwmSetWindowAttribute(Hwnd, DWMWA_CLOAK, &value, sizeof(int));
    }

    /// <summary>
    /// Shows the window without changing its state.
    /// </summary>
    /// <param name="activate">True to also make the window active.</param>
    public void Show(bool activate) => _ = ShowWindow(Hwnd, activate ? SW_SHOW : SW_SHOWNOACTIVATE);

    /// <summary>
    /// Hides the window.
    /// </summary>
    public void Hide() => _ = ShowWindow(Hwnd, SW_HIDE);

    /// <summary>
    /// Closes the window. The window raises <see cref="Closed"/>, then destroys the island and itself.
    /// </summary>
    public void Close()
    {
        if (!_destroyed)
        {
            _ = DestroyWindow(Hwnd);
        }
    }

    /// <summary>
    /// Maximizes the window. While the window is hidden, the next <see cref="Activate"/> shows it maximized.
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
    /// Moves and sizes the window in physical pixels, with the frame.
    /// </summary>
    public void MoveAndResize(int x, int y, int width, int height) =>
        _ = SetWindowPos(Hwnd, 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>
    /// Sizes the window in physical pixels, with the frame.
    /// </summary>
    public void Resize(int width, int height) =>
        _ = SetWindowPos(Hwnd, 0, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>
    /// Sets the smallest size that the person can give the window, in physical pixels with the frame.
    /// </summary>
    public void SetMinimumSize(int width, int height)
    {
        _minWidth  = width;
        _minHeight = height;
    }

    /// <summary>
    /// Sets the icon of the taskbar and Alt + Tab from an .ico file.
    /// </summary>
    /// <param name="path">The icon file.</param>
    public void SetIcon(string path)
    {
        // The process loads the icons once. WM_SETICON does not take ownership of the icons, and the app makes the main
        // window on each open.
        // ponytail: a single icon file for every window. Key the cache by path if a second file ever comes.
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
    /// Places the input of the title bar over its row, in client pixels. The title bar calls it after each change of
    /// its layout.
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
        // These messages, such as WM_CREATE, come before the constructor stores the handle.
        if (!ByHandle.TryGetValue(hwnd, out XamlWindow? window))
        {
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        // An exception cannot cross into Windows. The handler of the app logs it, as WinUI 3 sent the exceptions of
        // event handlers to Application.UnhandledException. An exception that the handler does not handle ends the app
        // here.
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
            // The island and Mica cover the client area, so the window never paints a GDI background under them.
            case WM_ERASEBKGND:
                return 1;

            // The main window has no system caption. The side and bottom frames stay as resize edges. A maximized
            // window keeps its top clear of the screen edge.
            case WM_NCCALCSIZE when _kind == XamlWindowKind.Main && wParam != 0:
                var calc = (NCCALCSIZE_PARAMS*)lParam;
                int top  = calc->rgrc0.Top;
                _ = DefWindowProcW(Hwnd, msg, wParam, lParam);
                calc->rgrc0.Top = top + (IsZoomed(Hwnd) ? FrameThickness() : 0);
                return 0;

            // The popup has no frame at all.
            case WM_NCCALCSIZE when _kind == XamlWindowKind.Popup && wParam != 0:
                return 0;

            // The island resizes first, then the hidden CoreWindow. Then the window waits for the XAML frame at the new
            // size (see the remarks). A minimized window leaves the CoreWindow alone, because popups cannot be bigger
            // than it. Only a real size change of a shown window waits. A change with no layout draws no frame, and
            // XAML then waits for the release forever.
            case WM_SIZE:
                // A minimized window keeps the size of the island. At 0 × 0 the chats went narrow, and the list slid
                // back in on restore.
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

            // F10 or a lone Alt starts no menu bar mode. The window has no menu bar. The mode took every key after it,
            // so Shift + F10 and then typed text went nowhere. Alt + Space (lParam ' ') still opens the window menu.
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

            // After a move to a monitor with another scale, the window takes the size that Windows suggests.
            case WM_DPICHANGED:
                var suggested = (RECT*)lParam;
                _ = SetWindowPos(Hwnd, 0, suggested->Left, suggested->Top, suggested->Right - suggested->Left, suggested->Bottom - suggested->Top, SWP_NOZORDER | SWP_NOACTIVATE);
                return 0;

            // DefWindowProc sends WM_SIZE and WM_MOVE from here, so it runs first.
            case WM_WINDOWPOSCHANGED:
                nint result = DefWindowProcW(Hwnd, msg, wParam, lParam);
                BoundsChanged?.Invoke(this, EventArgs.Empty);
                return result;

            // The person closed the window with the Close button, Alt+F4 or the taskbar. The owner can hide it instead.
            case WM_CLOSE:
                var closing = new System.ComponentModel.CancelEventArgs();
                Closing?.Invoke(this, closing);
                if (closing.Cancel)
                {
                    return 0;
                }

                break;

            case WM_DESTROY:
                OnDestroyed();
                return 0;
        }

        return DefWindowProcW(Hwnd, msg, wParam, lParam);
    }

    // Raises Closed while the content still exists, then destroys the island.
    private void OnDestroyed()
    {
        _destroyed = true;
        CompositionTarget.Rendered -= OnResizedFrameRendered;
        Uncloak();
        Closed?.Invoke(this, EventArgs.Empty);

        Content = null;
        _source.Dispose();
        _titleBar?.Dispose();
        Marshal.Release(_sourceNative);
        ByHandle.Remove(Hwnd);
        Open.Remove(this);
    }

    // The layout at the new size is done, so the window waits until XAML draws that frame. A release now lets Windows
    // show the old frame stretched.
    // ponytail: a single pass for each WM_SIZE. A size change with no layout waits for the timeout of Windows.
    private void OnContentLayoutUpdated(object? sender, object e)
    {
        if (!_layoutPending)
        {
            return;
        }

        _layoutPending = false;
        CompositionTarget.Rendered += OnResizedFrameRendered;
    }

    // XAML drew the frame at the new size, so the window releases the frame that Windows holds.
    private void OnResizedFrameRendered(object? sender, RenderedEventArgs e)
    {
        CompositionTarget.Rendered -= OnResizedFrameRendered;
        _resizeManager?.NotifyLayoutCompleted();
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Fills the client area with the island.
    private void ResizeIsland()
    {
        if (_island == 0)
        {
            return;
        }

        (int width, int height) = ClientSize;
        _ = SetWindowPos(_island, 0, 0, 0, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // Gets the thickness of the resize edge. A maximized window also hangs past the screen by this distance.
    private int FrameThickness()
    {
        uint dpi = GetDpiForWindow(Hwnd);
        return GetSystemMetricsForDpi(SM_CYFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
    }

    // Puts Mica from DWM over the whole window. The content draws on the Mica.
    private void ApplyMica()
    {
        int backdrop = DWMSBT_MAINWINDOW;
        var margins  = new MARGINS();

        _ = DwmSetWindowAttribute(Hwnd, DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(int));
        _ = DwmExtendFrameIntoClientArea(Hwnd, &margins);
    }

    // Shows nothing but the content, as TransparentTintBackdrop of WinUIEx does. A blur-behind with a region that
    // covers nothing turns on alpha without blur. The window also gets no 1 px outline and square corners.
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

    // Without this setting, islands paint their own opaque black background. That background hid Mica and made the
    // corner of the popup black. The XAML window of the thread holds a single setting for every island on the thread.
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

    // Gets the hidden CoreWindow that XAML Islands makes on this thread.
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

    // Names the window as the resize synchronization window of the XAML core. When another Windows build does not have
    // the interface, the resize stays without sync.
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

    // Queries the native object behind a projected WinRT object for an interface. Returns 0 when the object does not
    // implement the interface.
    private static nint QueryInterface(object projected, Guid iid)
    {
        nint native = ((IWinRTObject)projected).NativeObject.ThisPtr;
        return Marshal.QueryInterface(native, in iid, out nint result) == 0 ? result : 0;
    }

    // Gets a function pointer from the vtable of a COM object.
    private static nint VTable(nint comObject, int slot) => (*(nint**)comObject)[slot];
}

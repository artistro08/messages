using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static GMessages.App.Windowing.WindowInterop;

namespace GMessages.App.Windowing;

/// <summary>
/// Holds the areas of the title bar of the main window in client pixels. They are the drag area, the caption buttons
/// and the holes for clicks to the content.
/// </summary>
/// <param name="Bar">The whole title bar row.</param>
/// <param name="Minimize">The Minimize button.</param>
/// <param name="Maximize">The Maximize (or Restore) button.</param>
/// <param name="Close">The Close button.</param>
/// <param name="Passthrough">
/// The interactive elements in the row, which take their own clicks: Back, ☰ and the profile picture.
/// </param>
internal readonly record struct TitleBarLayout(RECT Bar, RECT Minimize, RECT Maximize, RECT Close, IReadOnlyList<RECT> Passthrough);

/// <summary>
/// Covers the title bar row with an invisible child window, which answers hit tests as a system caption does.
/// </summary>
/// <remarks>
/// <para>
/// The drag bar of Windows Terminal works the same way. XAML cannot tell Windows that a part of an island is a
/// caption. Thus this layered window, which draws nothing, sits over the row. A window region cuts holes where the
/// buttons of the row are. Over the rest, the window answers HTCAPTION, so a drag moves and a double-click maximizes.
/// Along the top edge it answers HTTOP, which resizes when the window is not maximized. Over the caption buttons it
/// answers HTMINBUTTON, HTMAXBUTTON or HTCLOSE, which also gives Maximize its Snap Layouts flyout.
/// </para>
/// <para>
/// XAML draws the caption buttons under this window, so they never see the pointer. This window reports the hover
/// and the press back (see <see cref="XamlWindow.CaptionButtonsChanged"/>), and runs the click as the system command.
/// The default handling of the top-level window does all other input. That input is a drag, a double-click or the
/// top edge.
/// </para>
/// </remarks>
/// <seealso href="https://github.com/microsoft/terminal/blob/main/src/cascadia/WindowsTerminal/NonClientIslandWindow.cpp"/>
internal sealed unsafe class TitleBarInput : IDisposable
{
    private const string ClassName = "GMessages.TitleBarInput";

    private static readonly Dictionary<nint, TitleBarInput> ByHandle = [];
    private static bool s_registered;

    private readonly nint _parent;
    private readonly nint _hwnd;
    private readonly Action<int, int> _captionButtonsChanged;
    private TitleBarLayout _layout;
    private int _hovered;
    private int _pressed;
    private int _reportedPressed;
    private bool _tracking;

    /// <summary>
    /// Creates the input window over the title bar row of the parent. The window is empty until <see cref="Update"/>.
    /// </summary>
    /// <param name="parent">The top-level window.</param>
    /// <param name="captionButtonsChanged">Gets the hovered and the pressed caption button as hit test codes, or 0.
    /// </param>
    public TitleBarInput(nint parent, Action<int, int> captionButtonsChanged)
    {
        _parent                = parent;
        _captionButtonsChanged = captionButtonsChanged;
        RegisterClass();

        _hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_NOREDIRECTIONBITMAP, ClassName, string.Empty, WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS, 0, 0, 0, 0, parent, 0, GetModuleHandleW(null), 0);
        if (_hwnd == 0)
        {
            throw new InvalidOperationException($"CreateWindowExW failed ({Marshal.GetLastPInvokeError()}).");
        }

        // The window is fully opaque but draws nothing, because it has no redirection bitmap. Thus it takes hit tests
        // and shows the island under it.
        _ = SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        ByHandle[_hwnd] = this;
    }

    /// <summary>
    /// Moves the window over the title bar row and cuts the holes for the row's own buttons.
    /// </summary>
    /// <param name="layout">The areas of the title bar, in client pixels of the parent.</param>
    public void Update(TitleBarLayout layout)
    {
        _layout = layout;
        RECT bar = layout.Bar;
        _ = SetWindowPos(_hwnd, 0, bar.Left, bar.Top, bar.Right - bar.Left, bar.Bottom - bar.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // The region uses the pixels of this window. After SetWindowRgn, the window owns the region.
        nint region = CreateRectRgn(0, 0, bar.Right - bar.Left, bar.Bottom - bar.Top);
        foreach (RECT hole in layout.Passthrough)
        {
            nint cut = CreateRectRgn(hole.Left - bar.Left, hole.Top - bar.Top, hole.Right - bar.Left, hole.Bottom - bar.Top);
            _ = CombineRgn(region, region, cut, RGN_DIFF);
            _ = DeleteObject(cut);
        }

        _ = SetWindowRgn(_hwnd, region, true);
    }

    /// <summary>
    /// Destroys the input window.
    /// </summary>
    public void Dispose()
    {
        ByHandle.Remove(_hwnd);
        _ = DestroyWindow(_hwnd);
    }

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
        if (!ByHandle.TryGetValue(hwnd, out TitleBarInput? input))
        {
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        return input.OnMessage(msg, wParam, lParam);
    }

    private nint OnMessage(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return HitTest(LowWord(lParam), HighWord(lParam));

            // The window tracks the hover until the pointer leaves, so the hover fill of the button also goes.
            case WM_NCMOUSEMOVE:
                if (!_tracking)
                {
                    var track = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = _hwnd };
                    _tracking = TrackMouseEvent(&track);
                }

                int hit = IsCaptionButton((int)wParam) ? (int)wParam : 0;
                SetCaptionState(hit, hit != 0 && hit == _pressed ? hit : 0);
                return hit != 0 ? 0 : SendMessageW(_parent, msg, wParam, lParam);

            // The pointer left, also during a press that ends somewhere else. No button stays hovered or pressed.
            case WM_NCMOUSELEAVE:
                _tracking = false;
                _pressed  = 0;
                SetCaptionState(0, 0);
                return 0;

            // This window handles a press on a caption button. The top-level window handles all other presses, such as
            // a drag or the top edge.
            case WM_NCLBUTTONDOWN:
                if (IsCaptionButton((int)wParam))
                {
                    _pressed = (int)wParam;
                    SetCaptionState((int)wParam, _pressed);
                    return 0;
                }

                return SendMessageW(_parent, msg, wParam, lParam);

            // A release over the pressed button runs its command.
            case WM_NCLBUTTONUP:
                if (_pressed != 0)
                {
                    int pressed = _pressed;
                    _pressed    = 0;
                    SetCaptionState(IsCaptionButton((int)wParam) ? (int)wParam : 0, 0);
                    if (pressed == (int)wParam)
                    {
                        RunCaptionButton(pressed);
                    }

                    return 0;
                }

                return SendMessageW(_parent, msg, wParam, lParam);

            // A right-click on the caption shows the system menu, as the real caption does.
            case WM_NCRBUTTONUP when (int)wParam == HTCAPTION:
                ShowSystemMenu(LowWord(lParam), HighWord(lParam));
                return 0;

            case WM_NCLBUTTONDBLCLK:
            case WM_NCRBUTTONDOWN:
            case WM_NCMBUTTONDOWN:
            case WM_NCMBUTTONUP:
                return SendMessageW(_parent, msg, wParam, lParam);
        }

        return DefWindowProcW(_hwnd, msg, wParam, lParam);
    }

    // Maps a screen point to a caption button, the top resize edge or the caption.
    private nint HitTest(int screenX, int screenY)
    {
        var point = new POINT { X = screenX, Y = screenY };
        _ = ScreenToClient(_parent, &point);

        if (Contains(_layout.Close, point))
        {
            return HTCLOSE;
        }

        if (Contains(_layout.Maximize, point))
        {
            return HTMAXBUTTON;
        }

        if (Contains(_layout.Minimize, point))
        {
            return HTMINBUTTON;
        }

        // The top edge resizes, except when the window is maximized.
        if (!IsZoomed(_parent))
        {
            uint dpi    = GetDpiForWindow(_parent);
            int  border = GetSystemMetricsForDpi(SM_CYFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
            if (point.Y < border)
            {
                RECT client;
                _ = GetClientRect(_parent, &client);
                return point.X < border ? HTTOPLEFT : point.X >= client.Right - border ? HTTOPRIGHT : HTTOP;
            }
        }

        return HTCAPTION;
    }

    private static bool Contains(RECT rect, POINT point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    private static bool IsCaptionButton(int hit) => hit is HTMINBUTTON or HTMAXBUTTON or HTCLOSE;

    // Tells the title bar only when the hovered or the pressed button changes.
    private void SetCaptionState(int hovered, int pressed)
    {
        if (hovered == _hovered && pressed == _reportedPressed)
        {
            return;
        }

        _hovered         = hovered;
        _reportedPressed = pressed;
        _captionButtonsChanged(hovered, pressed);
    }

    // Posts the system command, so Windows animates it as for a real caption button.
    private void RunCaptionButton(int hit)
    {
        int command = hit switch
        {
            HTMINBUTTON => SC_MINIMIZE,
            HTMAXBUTTON => IsZoomed(_parent) ? SC_RESTORE : SC_MAXIMIZE,
            _           => SC_CLOSE,
        };
        _ = PostMessageW(_parent, WM_SYSCOMMAND, command, 0);
    }

    private void ShowSystemMenu(int screenX, int screenY)
    {
        nint menu    = GetSystemMenu(_parent, false);
        int  command = TrackPopupMenu(menu, TPM_RETURNCMD, screenX, screenY, 0, _parent, 0);
        if (command != 0)
        {
            _ = PostMessageW(_parent, WM_SYSCOMMAND, command, 0);
        }
    }
}

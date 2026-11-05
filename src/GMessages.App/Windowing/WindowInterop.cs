using System.Runtime.InteropServices;

namespace GMessages.App.Windowing;

/// <summary>
/// Win32, DWM and CoreMessaging calls for the XAML Island windows (<see cref="XamlWindow"/>).
/// </summary>
/// <remarks>
/// LibraryImport with function pointers, so window procedures need no delegates kept alive. The resize sync
/// export has no name: user32 exports it by ordinal only, so it's looked up with GetProcAddress.
/// </remarks>
internal static unsafe partial class WindowInterop
{
    // =========================================================================
    // MESSAGES
    // =========================================================================

    public const uint WM_DESTROY          = 0x0002;
    public const uint WM_SIZE             = 0x0005;
    public const uint WM_ACTIVATE         = 0x0006;
    public const uint WM_SETFOCUS         = 0x0007;
    public const uint WM_CLOSE            = 0x0010;
    public const uint WM_ERASEBKGND       = 0x0014;
    public const uint WM_SETCURSOR        = 0x0020;
    public const uint WM_GETMINMAXINFO    = 0x0024;
    public const uint WM_WINDOWPOSCHANGED = 0x0047;
    public const uint WM_SETICON          = 0x0080;
    public const uint WM_NCCALCSIZE       = 0x0083;
    public const uint WM_NCHITTEST        = 0x0084;
    public const uint WM_NCMOUSEMOVE      = 0x00A0;
    public const uint WM_NCLBUTTONDOWN    = 0x00A1;
    public const uint WM_NCLBUTTONUP      = 0x00A2;
    public const uint WM_NCLBUTTONDBLCLK  = 0x00A3;
    public const uint WM_NCRBUTTONDOWN    = 0x00A4;
    public const uint WM_NCRBUTTONUP      = 0x00A5;
    public const uint WM_NCMBUTTONDOWN    = 0x00A7;
    public const uint WM_NCMBUTTONUP      = 0x00A8;
    public const uint WM_SYSCOMMAND       = 0x0112;
    public const uint WM_NCMOUSELEAVE     = 0x02A2;
    public const uint WM_DPICHANGED       = 0x02E0;

    // Hit Test Codes
    public const int HTCAPTION     = 2;
    public const int HTMINBUTTON   = 8;
    public const int HTMAXBUTTON   = 9;
    public const int HTTOP         = 12;
    public const int HTTOPLEFT     = 13;
    public const int HTTOPRIGHT    = 14;
    public const int HTCLOSE       = 20;

    // System Commands
    public const int SC_MINIMIZE = 0xF020;
    public const int SC_MAXIMIZE = 0xF030;
    public const int SC_CLOSE    = 0xF060;
    public const int SC_RESTORE  = 0xF120;
    public const int SC_KEYMENU  = 0xF100;

    // =========================================================================
    // STYLES AND FLAGS
    // =========================================================================

    public const uint WS_OVERLAPPEDWINDOW      = 0x00CF0000;
    public const uint WS_POPUP                 = 0x80000000;
    public const uint WS_CHILD                 = 0x40000000;
    public const uint WS_VISIBLE               = 0x10000000;
    public const uint WS_CLIPCHILDREN          = 0x02000000;
    public const uint WS_CLIPSIBLINGS          = 0x04000000;
    public const uint WS_EX_TOPMOST            = 0x00000008;
    public const uint WS_EX_TOOLWINDOW         = 0x00000080;
    public const uint WS_EX_LAYERED            = 0x00080000;
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int  CW_USEDEFAULT            = unchecked((int)0x80000000);

    public const int SW_HIDE           = 0;
    public const int SW_SHOWMAXIMIZED  = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW           = 5;
    public const int SW_RESTORE        = 9;

    public const uint SWP_NOSIZE       = 0x0001;
    public const uint SWP_NOMOVE       = 0x0002;
    public const uint SWP_NOZORDER     = 0x0004;
    public const uint SWP_NOACTIVATE   = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW   = 0x0040;

    public const int SM_CYFRAME        = 33;
    public const int SM_CXPADDEDBORDER = 92;

    public const uint ICON_SMALL = 0;
    public const uint ICON_BIG   = 1;
    public const uint IMAGE_ICON      = 1;
    public const uint LR_LOADFROMFILE = 0x0010;
    public const int  IDC_ARROW       = 32512;

    public const uint LWA_ALPHA   = 0x2;
    public const int  RGN_DIFF    = 4;
    public const uint TME_LEAVE     = 0x00000002;
    public const uint TME_NONCLIENT = 0x00000010;
    public const uint TPM_RETURNCMD = 0x0100;

    // DWM
    public const int DWMWA_CLOAK                    = 13;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR             = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE      = 38;
    public const int DWMWCP_DONOTROUND              = 1;
    public const int DWMWA_COLOR_NONE               = unchecked((int)0xFFFFFFFE);
    public const int DWMSBT_MAINWINDOW              = 2;
    public const uint DWM_BB_ENABLE                 = 0x1;
    public const uint DWM_BB_BLURREGION             = 0x2;

    // Private user32 Ordinal (apkipa's gist: https://gist.github.com/apkipa/20cae438aef2a8633f99e10e0b90b11e)
    private const int OrdinalEnableResizeLayoutSynchronization = 2615;

    // =========================================================================
    // STRUCTS
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint  hwnd;
        public uint  message;
        public nint  wParam;
        public nint  lParam;
        public uint  time;
        public POINT pt;
        public uint  lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS
    {
        public RECT  rgrc0;
        public RECT  rgrc1;
        public RECT  rgrc2;
        public nint  lppos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public int  fEnable;
        public nint hRgnBlur;
        public int  fTransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint  cbSize;
        public uint  style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int   cbClsExtra;
        public int   cbWndExtra;
        public nint  hInstance;
        public nint  hIcon;
        public nint  hCursor;
        public nint  hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint  hIconSm;
    }

    // DispatcherQueueOptions: a dispatcher queue on the calling thread, which runs a single-threaded apartment
    [StructLayout(LayoutKind.Sequential)]
    public struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    // =========================================================================
    // USER32
    // =========================================================================

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* windowClass);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    public static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hwnd);


    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, RECT* rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, RECT* rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, POINT* point);

    [LibraryImport("user32.dll")]
    public static partial nint SetFocus(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowTextW(nint hwnd, string text);

    [LibraryImport("user32.dll")]
    public static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(TRACKMOUSEEVENT* track);

    [LibraryImport("user32.dll")]
    public static partial nint GetSystemMenu(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool revert);

    // MONITOR_DEFAULTTONULL: 0 When The Rectangle Is On No Display
    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromRect(RECT* rect, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    // =========================================================================
    // GDI, KERNEL32, DWM, COREMESSAGING
    // =========================================================================

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    public static partial int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetProcAddress(nint module, nint ordinal);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, int* value, int size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmExtendFrameIntoClientArea(nint hwnd, MARGINS* margins);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmEnableBlurBehindWindow(nint hwnd, DWM_BLURBEHIND* blurBehind);

    [LibraryImport("CoreMessaging.dll")]
    public static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, nint* controller);

    /// <summary>
    /// Turns DComp resize synchronization on or off for a top-level window.
    /// </summary>
    /// <remarks>
    /// Undocumented user32 export (ordinal 2615) UWP uses so the system compositor holds each resized frame until
    /// XAML has laid out at the new size. Returns false when this Windows build doesn't export it.
    /// </remarks>
    /// <param name="hwnd">The top-level window.</param>
    /// <param name="enable">True to synchronize.</param>
    /// <returns>True when the export was found and called.</returns>
    public static bool EnableResizeLayoutSynchronization(nint hwnd, bool enable)
    {
        nint user32 = GetModuleHandleW("user32.dll");
        nint export = user32 == 0 ? 0 : GetProcAddress(user32, OrdinalEnableResizeLayoutSynchronization);

        if (export == 0)
        {
            return false;
        }

        ((delegate* unmanaged[Stdcall]<nint, int, void>)export)(hwnd, enable ? 1 : 0);
        return true;
    }

    // Low And High Words Of A Message Parameter, Signed (Screen Coordinates Can Be Negative)
    public static int LowWord(nint value) => (short)(value & 0xFFFF);

    public static int HighWord(nint value) => (short)((value >> 16) & 0xFFFF);
}

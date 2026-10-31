using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace GMessages.App;

/// <summary>
/// Win32 calls for the tray icon, the tray menu's host window, single instance, memory trim and the emoji panel shortcut.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>NativeMethods.cs</c>, keeping only the members this app uses. Plain
/// <c>DllImport</c> with delegates for the window procedures, as in Magic Buds; Native AOT generates the
/// marshalling stubs at compile time, so it publishes without warnings.
/// </remarks>
internal static class NativeMethods
{
    // =========================================================================
    // MESSAGES AND FLAGS
    // =========================================================================

    public const uint WM_CONTEXTMENU   = 0x007B;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_STYLECHANGING = 0x007C;
    public const uint WM_ERASEBKGND    = 0x0014;
    public const uint WM_APP           = 0x8000;

    public const uint NIM_ADD        = 0x00;
    public const uint NIM_MODIFY     = 0x01;
    public const uint NIM_DELETE     = 0x02;
    public const uint NIM_SETVERSION = 0x04;

    public const uint NIF_MESSAGE  = 0x01;
    public const uint NIF_ICON     = 0x02;
    public const uint NIF_TIP      = 0x04;
    public const uint NIF_GUID     = 0x20;
    public const uint NIF_SHOWTIP  = 0x80;

    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIN_SELECT           = 0x0400;
    public const uint NIN_KEYSELECT        = 0x0401;

    public const uint IMAGE_ICON      = 1;
    public const uint LR_LOADFROMFILE = 0x0010;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI         = 0;
    public const int SM_CXSMICON               = 49;

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_DONOTROUND              = 1;
    public const int DWMWA_BORDER_COLOR             = 34;
    public const int DWMWA_COLOR_NONE               = unchecked((int)0xFFFFFFFE);
    public const uint DWM_BB_ENABLE                 = 0x1;
    public const uint DWM_BB_BLURREGION             = 0x2;

    public const int GWL_STYLE          = -16;
    public const uint WS_POPUP          = 0x80000000;
    public const uint WS_CLIPSIBLINGS   = 0x04000000;
    public const uint WS_SYSMENU        = 0x00080000;

    // WS_BORDER | WS_DLGFRAME | WS_THICKFRAME
    public const uint WS_FRAME_BITS     = 0x00800000 | 0x00400000 | 0x00040000;
    public const uint SWP_NOSIZE        = 0x0001;
    public const uint SWP_NOMOVE        = 0x0002;
    public const uint SWP_NOZORDER      = 0x0004;
    public const uint SWP_NOACTIVATE    = 0x0010;
    public const uint SWP_FRAMECHANGED  = 0x0020;
    public const int BLACK_BRUSH        = 4;

    // Any process may take the foreground next
    public const uint ASFW_ANY = unchecked((uint)-1);

    // ShowWindow: restore a minimized window to its last size and place
    public const int SW_RESTORE = 9;

    // GetCurrentPackageFullName's answer when the process has no package identity
    public const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    // SendInput: keyboard input, key up; Windows key and period for the emoji panel, Ctrl and Shift to let go first
    public const uint INPUT_KEYBOARD  = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_LWIN       = 0x5B;
    public const ushort VK_OEM_PERIOD = 0xBE;
    public const ushort VK_SHIFT      = 0x10;
    public const ushort VK_CONTROL    = 0x11;

    // =========================================================================
    // STRUCTS AND CALLBACKS
    // =========================================================================

    public delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    public delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // INPUT With Its KEYBDINPUT Member: 40 Bytes On 64-Bit Windows (The Union Is Sized By MOUSEINPUT). The App
    // Ships x64 And ARM64 Only, Both 64-Bit
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct KEYBOARD_INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STYLESTRUCT
    {
        public uint styleOld;
        public uint styleNew;
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
    public struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fTransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // =========================================================================
    // SHLWAPI
    // =========================================================================

    public const int SFBS_FLAGS_ROUND_TO_NEAREST_DISPLAYED_DIGIT = 1;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrFormatByteSizeEx(ulong size, int flags, [Out] char[] buffer, uint bufferSize);

    // =========================================================================
    // USER32
    // =========================================================================

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint attachThread, uint attachToThread, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    // =========================================================================
    // SHELL, SHCORE, COMCTL32, GDI32, DWM, KERNEL32
    // =========================================================================

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    public static extern IntPtr GetStockObject(int index);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    public static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blurBehind);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, KEYBOARD_INPUT[] inputs, int size);

    // -1, -1 empties the working set
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}

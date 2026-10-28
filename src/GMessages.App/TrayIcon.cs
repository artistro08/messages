using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GMessages.App;

/// <summary>
/// The notification-area icon.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>TrayIcon.cs</c>; the hover tooltip window, global shortcuts and icon
/// rectangle lookup were dropped. Owns a hidden top-level window that receives the icon's callback
/// messages, the "TaskbarCreated" broadcast (Explorer restarted, so the icon is added again) and theme
/// changes (the icon switches between its light- and dark-taskbar versions). Uses NOTIFYICON_VERSION_4,
/// so a click or Enter arrives as NIN_SELECT/NIN_KEYSELECT and a right-click as WM_CONTEXTMENU with the
/// anchor point in wParam. The menu itself is WinUI (<see cref="TrayMenuWindow"/>); the hover tooltip is
/// the standard one (NIF_SHOWTIP).
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const string WindowClassName = "GMessagesTrayWindow";
    private const uint IconId            = 1;
    private const uint CallbackMessage   = NativeMethods.WM_APP + 1;

    private readonly string _tooltip;
    private readonly Guid _iconGuid;
    private readonly NativeMethods.WndProc _windowProc;
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _icon;
    private bool _visible;
    private bool _disposed;

    /// <summary>
    /// Creates the icon's hidden window. The icon itself shows on <see cref="Show"/>.
    /// </summary>
    /// <param name="tooltip">Hover text, also read by screen readers.</param>
    /// <param name="iconGuid">
    /// Fixed identity, so Windows keeps the icon's taskbar placement across updates. Each install location
    /// needs its own GUID.
    /// </param>
    /// <exception cref="Win32Exception">The window class or window could not be created.</exception>
    public TrayIcon(string tooltip, Guid iconGuid)
    {
        _tooltip    = tooltip;
        _iconGuid   = iconGuid;
        _windowProc = WindowProc;

        // Hidden Window
        var windowClass = new NativeMethods.WNDCLASSEXW
        {
            cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_windowProc),
            hInstance     = NativeMethods.GetModuleHandleW(null),
            lpszClassName = WindowClassName,
        };
        if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _hwnd = NativeMethods.CreateWindowExW(0, WindowClassName, tooltip, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    }

    /// <summary>
    /// Left click or Enter.
    /// </summary>
    public event EventHandler? Invoked;

    /// <summary>
    /// Right click or the menu key, with the anchor point in screen pixels.
    /// </summary>
    public event EventHandler<(int X, int Y)>? ContextMenuRequested;

    /// <summary>
    /// The icon's hidden window.
    /// </summary>
    public IntPtr Handle => _hwnd;

    /// <summary>
    /// Puts the icon on the tray. It's added again whenever Explorer restarts.
    /// </summary>
    /// <returns>True when the shell took the icon now; false when it was already showing or Explorer isn't up yet.</returns>
    public bool Show()
    {
        if (_visible || _disposed)
        {
            return false;
        }

        _visible = true;
        return Add();
    }

    /// <summary>
    /// Takes the icon off the tray. Its window stays.
    /// </summary>
    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        var data = CreateData(0);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
    }

    /// <summary>
    /// Removes the icon and destroys its window and icon handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;

        NativeMethods.DestroyWindow(_hwnd);
        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
        }
    }

    // Fails when Explorer isn't up yet at sign-in; TaskbarCreated adds the icon later
    private bool Add()
    {
        LoadIcon();

        var data = CreateData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data))
        {
            return false;
        }

        data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
        return true;
    }

    private static bool TaskbarUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    private NativeMethods.NOTIFYICONDATAW CreateData(uint flags) => new()
    {
        cbSize           = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd             = _hwnd,
        uID              = IconId,
        uFlags           = flags | NativeMethods.NIF_GUID,
        guidItem         = _iconGuid,
        uCallbackMessage = CallbackMessage,
        hIcon            = _icon,
        szTip            = _tooltip,
        szInfo           = "",
        szInfoTitle      = "",
    };

    // Light taskbar gets the dark glyph and the other way around. The .ico holds a separately
    // drawn image per size, so Windows never has to shrink a large one.
    private void LoadIcon()
    {
        var fileName = TaskbarUsesLightTheme() ? "TrayLight.ico" : "TrayDark.ico";
        var path     = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        var size     = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, NativeMethods.GetDpiForSystem());

        var icon = NativeMethods.LoadImageW(IntPtr.Zero, path, NativeMethods.IMAGE_ICON, size, size, NativeMethods.LR_LOADFROMFILE);
        if (icon == IntPtr.Zero)
        {
            return;
        }
        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
        }
        _icon = icon;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // Icon Callback
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case NativeMethods.NIN_SELECT:
                case NativeMethods.NIN_KEYSELECT:
                    Invoked?.Invoke(this, EventArgs.Empty);
                    break;
                case NativeMethods.WM_CONTEXTMENU:
                    var x = (short)(wParam.ToInt64() & 0xFFFF);
                    var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    ContextMenuRequested?.Invoke(this, (x, y));
                    break;
            }
            return IntPtr.Zero;
        }

        // Explorer Restarted
        if (message == _taskbarCreatedMessage)
        {
            if (_visible)
            {
                Add();
            }
            return IntPtr.Zero;
        }

        // Theme Changed: swap to the glyph that suits the taskbar
        if (message == NativeMethods.WM_SETTINGCHANGE && _visible)
        {
            LoadIcon();
            var data = CreateData(NativeMethods.NIF_ICON);
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }
}

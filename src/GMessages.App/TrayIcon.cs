using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GMessages.App;

/// <summary>
/// Shows the icon of the app in the notification area.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the <c>TrayIcon.cs</c> of Magic Buds, without the hover tooltip window, global shortcuts and icon
/// rectangle lookup. The class owns a hidden top-level window that receives the callback messages of the
/// icon. That window also receives the "TaskbarCreated" broadcast after an Explorer restart, and the icon goes on the
/// tray again. A theme change switches the icon between its versions for a light and a dark taskbar.
/// </para>
/// <para>
/// The icon uses NOTIFYICON_VERSION_4. Thus a click or Enter arrives as NIN_SELECT or NIN_KEYSELECT. A right-click
/// arrives as WM_CONTEXTMENU, with the anchor point in wParam. The menu is WinUI (<see cref="TrayMenuWindow"/>). The
/// hover tooltip is the standard tooltip (NIF_SHOWTIP).
/// </para>
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
    /// Creates the hidden window of the icon. The icon shows on <see cref="Show"/>.
    /// </summary>
    /// <param name="tooltip">The hover text, which screen readers also read.</param>
    /// <param name="iconGuid">
    /// The fixed identity, so Windows keeps the place of the icon on the taskbar across updates. Each install location
    /// needs its own GUID.
    /// </param>
    /// <exception cref="Win32Exception">The window class or window could not be created.</exception>
    public TrayIcon(string tooltip, Guid iconGuid)
    {
        _tooltip    = tooltip;
        _iconGuid   = iconGuid;
        _windowProc = WindowProc;

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
    /// Raised on a left-click or Enter.
    /// </summary>
    public event EventHandler? Invoked;

    /// <summary>
    /// Raised on a right-click or the menu key. The argument is the anchor point in screen pixels.
    /// </summary>
    public event EventHandler<(int X, int Y)>? ContextMenuRequested;

    /// <summary>
    /// Raised when Windows switches between light and dark (<c>WM_SETTINGCHANGE</c> with <c>ImmersiveColorSet</c>).
    /// </summary>
    public event EventHandler? ThemeChanged;

    /// <summary>
    /// Puts the icon on the tray. The icon goes on the tray again after each Explorer restart.
    /// </summary>
    /// <returns>
    /// True when the shell took the icon now. False when the icon already shows, or when Explorer is not ready yet.
    /// </returns>
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
    /// Removes the icon from the tray. Its window stays.
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

    // Fails when Explorer is not ready yet at sign-in. TaskbarCreated adds the icon later.
    private bool Add()
    {
        LoadIcon();

        // A copy that crashed or was killed leaves its icon with the same GUID in the shell. NIM_ADD then fails until
        // the user moves the pointer over the old icon. A delete by GUID first clears it, and does nothing otherwise.
        var stale = CreateData(0);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref stale);

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

    // A light taskbar gets the dark glyph, and a dark taskbar gets the light glyph. The .ico file holds an image drawn
    // for each size, so Windows never makes a large image smaller.
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

        // Explorer restarted.
        if (message == _taskbarCreatedMessage)
        {
            if (_visible)
            {
                Add();
            }
            return IntPtr.Zero;
        }

        // A setting changed. The icon takes the glyph that suits the taskbar.
        if (message == NativeMethods.WM_SETTINGCHANGE && _visible)
        {
            LoadIcon();
            var data = CreateData(NativeMethods.NIF_ICON);
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
        }

        // Windows switched between light and dark. The popups of the app follow.
        if (message == NativeMethods.WM_SETTINGCHANGE && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }
}

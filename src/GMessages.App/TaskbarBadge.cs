using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using Windows.System;
using Windows.UI.ViewManagement;
using static GMessages.App.Windowing.WindowInterop;

namespace GMessages.App;

/// <summary>
/// Shows the number of unread chats on the taskbar button of the main window (see <c>UnreadBadge</c>).
/// </summary>
/// <remarks>
/// <para>
/// The badge is the overlay icon of <c>ITaskbarList3</c>. The badge API of <c>BadgeUpdateManager</c> needs package
/// identity, and the MSI install has none. That API failed with 0x80070490 on each run. The overlay works for each
/// window that has a taskbar button.
/// </para>
/// <para>
/// The taskbar button exists only while the main window shows. Windows sends <c>TaskbarButtonCreated</c> each time that
/// it makes the button, and <see cref="Attach"/> then sets the last count again. The icon is a circle in the accent
/// color with the number, or "99+" above 99. It is drawn again when the accent color changes. All calls run on the UI
/// thread, because <c>ITaskbarList3</c> is an STA object. The log of a badge holds only the count.
/// </para>
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist3-setoverlayicon"/>
internal sealed partial class TaskbarBadge
{
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const int  FW_BOLD              = 700;
    private const uint ANTIALIASED_QUALITY  = 4;
    private const int  TRANSPARENT          = 1;
    private const uint DT_CENTER            = 0x0001;
    private const uint DT_VCENTER           = 0x0004;
    private const uint DT_SINGLELINE        = 0x0020;
    private const uint DT_NOPREFIX          = 0x0800;

    private static readonly Guid TaskbarListClass = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid TaskbarList3Id   = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger _logger;
    private readonly UISettings _ui = new();

    private ITaskbarList3? _taskbar;
    private nint _hwnd;
    private int _count;
    private int _loggedCount = -1;
    private int _lastError;

    /// <summary>
    /// Creates the badge with no window. The badge draws itself again when the accent color changes.
    /// </summary>
    /// <param name="dispatcher">The UI thread.</param>
    /// <param name="logger">The app log.</param>
    public TaskbarBadge(DispatcherQueue dispatcher, ILogger logger)
    {
        _dispatcher             = dispatcher;
        _logger                 = logger;
        _ui.ColorValuesChanged += (_, _) => _ = _dispatcher.TryEnqueue(Apply);
    }

    /// <summary>
    /// Shows a count on the badge, or clears it for 0. Any thread can call this method.
    /// </summary>
    /// <param name="count">The number of unread chats.</param>
    public void Show(int count) => _ = _dispatcher.TryEnqueue(() =>
    {
        _count = count;
        Apply();
    });

    /// <summary>
    /// Puts the badge on the taskbar button of a window, or takes it off for 0. The app calls this method when the main
    /// window gets a new taskbar button.
    /// </summary>
    /// <param name="hwnd">The handle of the main window, or 0 when the main window is gone.</param>
    public void Attach(nint hwnd)
    {
        _hwnd = hwnd;
        Apply();
    }

    private void Apply()
    {
        if (_hwnd == 0)
        {
            return;
        }

        nint icon = 0;
        try
        {
            _taskbar ??= CreateTaskbarList();
            icon       = _count > 0 ? DrawIcon(_count, _ui.GetColorValue(UIColorType.Accent)) : 0;
            _taskbar.SetOverlayIcon(_hwnd, icon, _count > 0 ? $"{_count} unread" : null);
            _lastError = 0;
            if (_count != _loggedCount)
            {
                _loggedCount = _count;
                AppLog.BadgeSet(_logger, _count);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A restart of Explorer can make the taskbar object stale. The next call then makes a new one. A missing
            // badge is better than a failed event handler, and the log holds each new failure only once.
            _taskbar = null;
            if (ex.HResult != _lastError)
            {
                _lastError = ex.HResult;
                AppLog.BadgeFailed(_logger, ex.GetType().Name, ex.HResult);
            }
        }
        finally
        {
            // The taskbar keeps its own copy of the icon.
            if (icon != 0)
            {
                _ = NativeMethods.DestroyIcon(icon);
            }
        }
    }

    private static ITaskbarList3 CreateTaskbarList()
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(in TaskbarListClass, 0, CLSCTX_INPROC_SERVER, in TaskbarList3Id, out ITaskbarList3 taskbar));
        taskbar.HrInit();
        return taskbar;
    }

    // GDI draws the number in white on black with grayscale smoothing. The gray level of each pixel is then the alpha of
    // the text over the circle. GDI text has no alpha of its own. The size is the small icon size at the DPI of the
    // taskbar, because the taskbar shows the overlay.
    private static unsafe nint DrawIcon(int count, Windows.UI.Color back)
    {
        nint   tray = FindWindowW("Shell_TrayWnd", null);
        int    size = GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, tray != 0 ? GetDpiForWindow(tray) : NativeMethods.GetDpiForSystem());
        string text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        double font = text.Length switch { 1 => 0.8, 2 => 0.66, _ => 0.5 };

        bool darkText = (0.299 * back.R) + (0.587 * back.G) + (0.114 * back.B) > 150;
        byte fore     = darkText ? (byte)0 : (byte)255;

        var header = new BITMAPINFOHEADER
        {
            Size     = (uint)sizeof(BITMAPINFOHEADER),
            Width    = size,
            Height   = -size,
            Planes   = 1,
            BitCount = 32,
        };

        // The mask is all zeros. The alpha of the color bitmap gives the shape. Its rows are WORD aligned.
        nint dc    = CreateCompatibleDC(0);
        nint color = CreateDIBSection(dc, &header, 0, out nint bits, 0, 0);
        nint mask  = CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);
        nint hfont = CreateFontW(-(int)Math.Round(size * font), 0, 0, 0, FW_BOLD, 0, 0, 0, 0, 0, 0, ANTIALIASED_QUALITY, 0, "Segoe UI");
        try
        {
            if (dc == 0 || color == 0 || bits == 0 || mask == 0 || hfont == 0)
            {
                throw new InvalidOperationException("GDI could not make the badge bitmaps.");
            }

            nint oldBitmap = SelectObject(dc, color);
            nint oldFont   = SelectObject(dc, hfont);
            _              = SetTextColor(dc, 0x00FFFFFF);
            _              = SetBkMode(dc, TRANSPARENT);
            var area       = new RECT { Right = size, Bottom = size };
            _              = DrawTextW(dc, text, text.Length, &area, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            _              = GdiFlush();

            // CreateIconIndirect needs bitmaps that no DC holds.
            _ = SelectObject(dc, oldFont);
            _ = SelectObject(dc, oldBitmap);

            var    pixels = new Span<uint>((void*)bits, size * size);
            double radius = size / 2.0;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // The part of the pixel inside the circle, from the distance of its center. The edge is then smooth.
                    double dx    = x + 0.5 - radius;
                    double dy    = y + 0.5 - radius;
                    double cover = Math.Clamp(radius - Math.Sqrt((dx * dx) + (dy * dy)) + 0.5, 0, 1);
                    double ink   = (pixels[(y * size) + x] & 0xFF) / 255.0;
                    byte   b     = Blend(back.B, fore, ink, cover);
                    byte   g     = Blend(back.G, fore, ink, cover);
                    byte   r     = Blend(back.R, fore, ink, cover);
                    byte   a     = (byte)Math.Round(cover * 255);
                    pixels[(y * size) + x] = (uint)(b | (g << 8) | (r << 16) | (a << 24));
                }
            }

            var  info = new ICONINFO { IsIcon = 1, Mask = mask, Color = color };
            nint icon = CreateIconIndirect(&info);
            return icon != 0 ? icon : throw new InvalidOperationException("CreateIconIndirect failed.");
        }
        finally
        {
            _ = DeleteObject(hfont);
            _ = DeleteObject(mask);
            _ = DeleteObject(color);
            _ = DeleteDC(dc);
        }
    }

    // Mixes the text over the circle, then premultiplies by the circle alpha, as a 32-bit icon requires.
    private static byte Blend(byte back, byte fore, double ink, double cover) =>
        (byte)Math.Round(((back * (1 - ink)) + (fore * ink)) * cover);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint   Size;
        public int    Width;
        public int    Height;
        public ushort Planes;
        public ushort BitCount;
        public uint   Compression;
        public uint   SizeImage;
        public int    XPelsPerMeter;
        public int    YPelsPerMeter;
        public uint   ClrUsed;
        public uint   ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int  IsIcon;
        public uint XHotspot;
        public uint YHotspot;
        public nint Mask;
        public nint Color;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out ITaskbarList3 instance);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial nint CreateDIBSection(nint dc, BITMAPINFOHEADER* info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GdiFlush();

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int DrawTextW(nint dc, string text, int length, RECT* area, uint format);

    [LibraryImport("user32.dll")]
    private static unsafe partial nint CreateIconIndirect(ICONINFO* info);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    /// <summary>
    /// Declares the taskbar button calls up to <c>SetOverlayIcon</c>, in the order of <c>shobjidl_core.h</c>. The
    /// interface holds the methods of <c>ITaskbarList</c> and <c>ITaskbarList2</c> first, as its vtable does.
    /// </summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    internal partial interface ITaskbarList3
    {
        void HrInit();

        void AddTab(nint hwnd);

        void DeleteTab(nint hwnd);

        void ActivateTab(nint hwnd);

        void SetActiveAlt(nint hwnd);

        void MarkFullscreenWindow(nint hwnd, int fullscreen);

        void SetProgressValue(nint hwnd, ulong completed, ulong total);

        void SetProgressState(nint hwnd, int flags);

        void RegisterTab(nint tab, nint mdi);

        void UnregisterTab(nint tab);

        void SetTabOrder(nint tab, nint insertBefore);

        void SetTabActive(nint tab, nint mdi, uint reserved);

        void ThumbBarAddButtons(nint hwnd, uint count, nint buttons);

        void ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);

        void ThumbBarSetImageList(nint hwnd, nint imageList);

        void SetOverlayIcon(nint hwnd, nint icon, string? description);
    }
}

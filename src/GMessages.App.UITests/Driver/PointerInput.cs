using System.Runtime.InteropServices;
using Mouse = FlaUI.Core.Input.Mouse;

namespace GMessages.App.UITests.Driver;

/// <summary>
/// Moves the pointer as real input, for hover tests.
/// </summary>
/// <remarks>
/// FlaUI moves the cursor with <c>SetCursorPos</c>. WinUI does not see this as pointer input and raises no
/// <c>PointerEntered</c>. Every move here ends with a real move through <c>SendInput</c>.
/// </remarks>
public static partial class PointerInput
{
    private const uint InputMouse     = 0;
    private const uint MouseEventMove = 0x0001;

    /// <summary>
    /// Moves the pointer over the middle of an element.
    /// </summary>
    /// <param name="over">The bounds of the element, in screen pixels.</param>
    public static void Hover(System.Drawing.Rectangle over)
    {
        Mouse.MoveTo(over.X + (over.Width / 2) - 1, over.Y + (over.Height / 2));
        MoveBy(1);
    }

    /// <summary>
    /// Moves the pointer sideways in one real move.
    /// </summary>
    /// <param name="dx">The distance in screen pixels. A negative value moves left.</param>
    public static void MoveBy(int dx)
    {
        Input[] move = [new() { Type = InputMouse, Mouse = new MouseInput { Dx = dx, Flags = MouseEventMove } }];
        Assert.AreEqual(1u, SendInput(1, move, Marshal.SizeOf<Input>()), "Windows refused the mouse move.");
    }

    /// <summary>
    /// Moves the pointer out through the left edge of the window in one real move.
    /// The test is inconclusive when the pointer cannot leave there, for example at the left edge of the screen.
    /// </summary>
    /// <param name="window">The bounds of the window, in screen pixels.</param>
    public static void LeaveLeft(System.Drawing.Rectangle window)
    {
        MoveBy(window.Left - Mouse.Position.X - 80);
        if (Mouse.Position.X >= window.Left)
        {
            Assert.Inconclusive($"The pointer ({Mouse.Position}) couldn't leave the window ({window}) on the left.");
        }
    }

    /// <summary>
    /// Tells whether the pointer shows the Windows hand cursor, as over a link.
    /// </summary>
    /// <returns>True when the cursor is the hand cursor.</returns>
    public static bool IsHandCursor()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && info.Cursor == LoadCursorW(IntPtr.Zero, IdcHand);
    }

    /// <summary>
    /// Tells how long ago Windows last saw keyboard or mouse input in this session.
    /// </summary>
    /// <remarks>
    /// UI Automation calls and <c>SetCursorPos</c> are not input. While a test drives the app only with these,
    /// the result shows how long the person at the machine is away.
    /// </remarks>
    /// <returns>The time since the last input.</returns>
    public static TimeSpan SinceLastInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        Assert.IsTrue(GetLastInputInfo(ref info), "Windows didn't say when the last input was.");
        return TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.Time);
    }

    // Resource ID of the system hand cursor (IDC_HAND).
    private const int IdcHand = 32649;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport("user32.dll")]
    private static partial IntPtr LoadCursorW(IntPtr instance, int cursorName);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    // The Time field is the tick count of the last input.
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    // The Win32 INPUT union holds only MOUSEINPUT here, because its other members are smaller.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}

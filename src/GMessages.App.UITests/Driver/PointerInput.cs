using System.Runtime.InteropServices;
using Mouse = FlaUI.Core.Input.Mouse;

namespace GMessages.App.UITests.Driver;

/// <summary>
/// Moves the pointer as real input, for hover tests.
/// </summary>
/// <remarks>
/// FlaUI moves the cursor with <c>SetCursorPos</c>, which WinUI doesn't see as pointer input (no <c>PointerEntered</c>),
/// so every move here ends with a real one through <c>SendInput</c>.
/// </remarks>
public static partial class PointerInput
{
    private const uint InputMouse     = 0;
    private const uint MouseEventMove = 0x0001;

    /// <summary>
    /// Moves the pointer over the middle of an element, as a person would.
    /// </summary>
    /// <param name="over">The element's bounds, in screen pixels.</param>
    public static void Hover(System.Drawing.Rectangle over)
    {
        Mouse.MoveTo(over.X + (over.Width / 2) - 1, over.Y + (over.Height / 2));
        MoveBy(1);
    }

    /// <summary>
    /// Moves the pointer sideways in one real move, such as straight out of the window.
    /// </summary>
    /// <param name="dx">How far, in screen pixels; negative moves left.</param>
    public static void MoveBy(int dx)
    {
        Input[] move = [new() { Type = InputMouse, Mouse = new MouseInput { Dx = dx, Flags = MouseEventMove } }];
        Assert.AreEqual(1u, SendInput(1, move, Marshal.SizeOf<Input>()), "Windows refused the mouse move.");
    }

    /// <summary>
    /// Moves the pointer straight out through the window's left edge in one real move. The test is inconclusive when
    /// the pointer can't leave there (the window sits at the screen's left edge).
    /// </summary>
    /// <param name="window">The window's bounds, in screen pixels.</param>
    public static void LeaveLeft(System.Drawing.Rectangle window)
    {
        MoveBy(window.Left - Mouse.Position.X - 80);
        if (Mouse.Position.X >= window.Left)
        {
            Assert.Inconclusive($"The pointer ({Mouse.Position}) couldn't leave the window ({window}) on the left.");
        }
    }

    /// <summary>
    /// Tells whether the pointer shows Windows' hand cursor, as over a link.
    /// </summary>
    /// <returns>True for the hand cursor.</returns>
    public static bool IsHandCursor()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && info.Cursor == LoadCursorW(IntPtr.Zero, IdcHand);
    }

    // =========================================================================
    // NATIVE MOUSE INPUT
    // =========================================================================

    // The System Hand Cursor's Resource ID
    private const int IdcHand = 32649;

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport("user32.dll")]
    private static partial IntPtr LoadCursorW(IntPtr instance, int cursorName);

    // CURSORINFO
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    // INPUT With Its MOUSEINPUT (The Union's Other Members Are Smaller)
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

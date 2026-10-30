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

    // =========================================================================
    // NATIVE MOUSE INPUT
    // =========================================================================

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

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

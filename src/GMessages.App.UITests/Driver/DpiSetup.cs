using System.Runtime.InteropServices;

namespace GMessages.App.UITests.Driver;

/// <summary>
/// Makes the test process per-monitor DPI aware before any UI test runs.
/// </summary>
/// <remarks>
/// UI Automation reports physical pixels. A DPI-unaware test host gets scaled screen coordinates on a scaled display,
/// so screen captures and mouse clicks would land beside the element.
/// </remarks>
[TestClass]
public static partial class DpiSetup
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    /// <summary>
    /// Switches the test host to per-monitor DPI awareness (v2).
    /// </summary>
    /// <param name="context">The MSTest context (unused).</param>
    [AssemblyInitialize]
    /// <exception cref="AssertFailedException">Windows refused, so captures and clicks could miss.</exception>
    public static void MakeDpiAware(TestContext context)
    {
        if (!SetProcessDpiAwarenessContext(PerMonitorAwareV2))
        {
            Assert.Fail($"Could not make the test host per-monitor DPI aware (Win32 error {Marshal.GetLastPInvokeError()}); screen captures and clicks would miss their elements.");
        }
    }

    /// <summary>
    /// The display scale of a window: 1.0 at 100%, 1.25 at 125%.
    /// </summary>
    /// <remarks>UI Automation sizes are physical pixels; divide by this to get effective pixels.</remarks>
    /// <param name="window">The window.</param>
    /// <returns>The window's scale.</returns>
    public static double ScaleOf(FlaUI.Core.AutomationElements.AutomationElement window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return GetDpiForWindow(window.Properties.NativeWindowHandle.Value) / 96.0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr window);
}

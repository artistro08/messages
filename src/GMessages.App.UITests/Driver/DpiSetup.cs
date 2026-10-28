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
    public static void MakeDpiAware(TestContext context) => SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(IntPtr value);
}

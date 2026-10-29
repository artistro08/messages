using System.Diagnostics;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using GMessages.App.UITests.Driver;

namespace GMessages.App.UITests;

/// <summary>
/// Diagnostic probes, not checks: they measure and write what they find to the test output. Removed once their
/// findings are recorded (Task 8).
/// </summary>
[TestClass]
[TestCategory("Probe")]
[DoNotParallelize]
public sealed class ProbeTests : UiTest
{
    // Owner Wording, Verbatim (As In PairingTests)
    private const string WrongEmojiText = "That wasn't the matching emoji. Try again.";

    /// <summary>
    /// After a wrong emoji and Try again, writes the sign-in's UI Automation tree and the WebView2's native windows
    /// every 2 s for 20 s, to see why the sign-in page doesn't come back for UI Automation.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task PairingTryAgain_DumpsSignInTree()
    {
        Window window = WaitForPairing();
        await SignInAndWaitForEmojiAsync();

        // Wrong Tap, Then Try Again
        Server.Phone.Tap(false);
        App.WaitForText("Pairing.Message", WrongEmojiText);
        App.Find("Pairing.TryAgain").Patterns.Invoke.Pattern.Invoke();

        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            TestContext.WriteLine($"--- t={watch.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s");
            TestContext.WriteLine(App.TryFind("Pairing.SignIn") is { } sign_in ? AppDriver.DumpTree(sign_in, 6) : "(no Pairing.SignIn)");
            TestContext.WriteLine(NativeWindows(window));
            Thread.Sleep(2000);
        }
    }

    // Every Element Under The Window With Its Own Native Window (The WebView2's Child Window): Class, Handle, Bounds
    private static string NativeWindows(Window window)
    {
        var lines = new List<string>();
        AppDriver.Search(window, e =>
        {
            nint handle = e.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle != 0 && handle != window.Properties.NativeWindowHandle.ValueOrDefault)
            {
                System.Drawing.Rectangle bounds = e.BoundingRectangle;
                lines.Add($"hwnd {handle:X} {e.Properties.ClassName.ValueOrDefault} [{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}]");
            }

            return false;
        });
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "(no child windows)";
    }
}

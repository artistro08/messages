using System.Diagnostics;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using GMessages.App.UITests.Driver;
using GMessages.Presentation.Notifications;
using Microsoft.Win32;

namespace GMessages.App.UITests;

/// <summary>
/// Diagnostic probes, not checks: they measure and write what they find to the test output. Removed once their
/// findings are recorded (Tasks 5 and 8).
/// </summary>
[TestClass]
[TestCategory("Probe")]
[DoNotParallelize]
public sealed class ProbeTests : UiTest
{
    // Owner Wording, Verbatim (As In PairingTests)
    private const string WrongEmojiText = "That wasn't the matching emoji. Try again.";

    /// <summary>
    /// How long a message toast stays on screen with each set of buttons, and the Windows message duration it is
    /// compared with.
    /// </summary>
    /// <remarks>
    /// Each variant writes its <see cref="ToastParts"/> to <c>toast-parts.txt</c> (read each time a toast is built),
    /// pushes a message into Ann's chat, then times the toast window from appearing to leaving the desktop root,
    /// polling every 250 ms, capped at 60 s. The accessibility setting is only read, never written (R14).
    /// </remarks>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ToastBanner_SecondsOnScreen_PerParts()
    {
        await PairAsync();
        var toasts = new ToastDriver(App.Automation);

        // Windows' "Dismiss Notifications After" Setting, In Seconds
        object? duration = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Accessibility", "MessageDuration", null);
        TestContext.WriteLine($"MessageDuration={duration ?? "(not set)"}");

        ToastParts[] variants = [ToastParts.All, ToastParts.Reply, ToastParts.Snooze, ToastParts.Dismiss, ToastParts.Reply | ToastParts.Snooze, ToastParts.None];
        for (int n = 0; n < variants.Length; n++)
        {
            ToastParts parts = variants[n];
            File.WriteAllText(Path.Combine(App.DataDir, "toast-parts.txt"), parts.ToString());

            string text = $"probe {parts} {n}";
            Server.Phone.PushIncoming("c-ann", text);
            toasts.WaitFor(AppDriver.DefaultTimeout, "Ann Lee", text);

            // Shown Until It Leaves The Desktop Root, Or The Cap
            var shown = Stopwatch.StartNew();
            while (toasts.TryFind(text) is not null && shown.Elapsed < TimeSpan.FromSeconds(60))
            {
                Thread.Sleep(250);
            }

            string seconds = shown.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            TestContext.WriteLine($"parts={parts} seconds={seconds}{(shown.Elapsed >= TimeSpan.FromSeconds(60) ? " (cap)" : "")}");
        }
    }

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

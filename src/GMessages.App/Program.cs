using GMessages.App.Windowing;
using GMessages.Presentation;
using Microsoft.Windows.AppLifecycle;

namespace GMessages.App;

/// <summary>
/// Holds the entry point. It replaces the Main that XAML generates, so only a single copy of the tray app runs.
/// </summary>
/// <remarks>
/// Ported from the <c>Program.cs</c> of Magic Buds. <c>Messages.exe --quit</c> asks the running copy to quit, as Quit
/// in the tray menu does. A second launch tells the running copy to open its main window, then exits. The names come
/// from <see cref="AppInfo.DisplayName"/>. Test runs add a <c>.Test</c> suffix, so UI test runs never collide with a
/// real copy.
/// </remarks>
public static class Program
{
    /// <summary>
    /// Gets whether the command line holds <c>--test-data-dir</c>. Only Debug builds with test hooks read it.
    /// </summary>
    public static bool IsTestRun { get; } = TestHooks.DataDir is not null;

    // This field comes after IsTestRun, because static fields initialize in text order.
    private static readonly string NameSuffix = IsTestRun ? ".Test" : "";

    /// <summary>
    /// Gets the single-instance key.
    /// </summary>
    public static string InstanceKey => AppInfo.DisplayName + NameSuffix;

    /// <summary>
    /// Gets the name of the event that <c>--quit</c> signals. The running copy waits on it.
    /// </summary>
    public static string QuitEventName => $"{AppInfo.DisplayName}.Quit{NameSuffix}";

    /// <summary>
    /// Gets the name of the event that a second launch signals. The running copy then opens its main window.
    /// </summary>
    public static string ShowEventName => $"{AppInfo.DisplayName}.Show{NameSuffix}";

    /// <summary>
    /// Gets the end of <see cref="ShowEventName"/> that the running copy holds. It is null when another process owns
    /// the name.
    /// </summary>
    /// <remarks>
    /// Each launch makes it before the single-instance check. Thus a second launch during the start-up of this copy is
    /// not lost.
    /// </remarks>
    public static EventWaitHandle? ShowRequest { get; private set; }

    /// <summary>
    /// Gets the end of <see cref="QuitEventName"/> that the running copy holds. It is null when another process owns
    /// the name.
    /// </summary>
    /// <remarks>
    /// Each launch makes it before the single-instance check. Thus a <c>--quit</c> during the start-up of this copy is
    /// not lost.
    /// </remarks>
    public static EventWaitHandle? QuitRequest { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

#if EnableTestHooks
        // When the test hook flags are not usable, the app refuses to start, with no UI and no network. It writes the
        // reason where a UI test can read it. A valid start deletes a stale reason of an earlier run.
        var errorFile = Path.Combine(Path.GetTempPath(), $"{AppInfo.DisplayName}-testhooks-error.txt");
        try
        {
            if (TestHooks.Error is { } testHookError)
            {
                File.WriteAllText(errorFile, testHookError);
                Environment.Exit(2);
                return;
            }

            File.Delete(errorFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file cannot be written or deleted. A refusal still exits here.
            if (TestHooks.Error is not null)
            {
                Environment.Exit(2);
                return;
            }
        }
#endif

        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
            {
                quit.Set();
                quit.Dispose();
            }
            return;
        }

        // Each launch makes or opens the show and quit events before the check for a running copy. Thus the events
        // always exist when a second launch or a --quit looks for them.
        var showRequest = OpenEvent(ShowEventName);
        var quitRequest = OpenEvent(QuitEventName);

        // A second launch tells the running copy to show its window, lets it take the foreground, then exits.
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            if (showRequest is not null)
            {
                NativeMethods.AllowSetForegroundWindow((uint)instance.ProcessId);
                showRequest.Set();
            }
            showRequest?.Dispose();
            quitRequest?.Dispose();
            return;
        }

        // The first launch readies WinUI 2.8 and the dispatcher of the UI thread. Then it makes the app, which starts
        // XAML, and runs the message loop until the app quits.
        ShowRequest = showRequest;
        QuitRequest = quitRequest;
        XamlHost.Start();
        var app = new App();
        app.Launch();
        XamlWindow.RunMessageLoop();
        ShowRequest?.Dispose();
        QuitRequest?.Dispose();
    }

    // Returns null when another process owns the name, such as an elevated copy. The app then runs without the event.
    private static EventWaitHandle? OpenEvent(string name)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            return null;
        }
    }
}

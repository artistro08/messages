using GMessages.Presentation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace GMessages.App;

/// <summary>
/// Entry point. Replaces the XAML-generated Main so only one copy of the tray app runs.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>Program.cs</c>. <c>Messages.exe --quit</c> asks the running copy to quit the
/// same way the tray menu's Quit does. A second launch tells the running copy to open its main window, then
/// exits. <c>Messages.exe --sign-in &lt;pipe&gt;</c> is the sign-in helper (<see cref="SignInRequest"/>), which skips all
/// of that. Names come from <see cref="AppInfo.DisplayName"/>, with a <c>.Test</c> suffix under test so UI test
/// runs never collide with a real copy.
/// </remarks>
public static class Program
{
    /// <summary>
    /// True when <c>--test-data-dir</c> was passed. Only Debug builds (with test hooks) honor it.
    /// </summary>
    public static bool IsTestRun { get; } = TestHooks.DataDir is not null;

    // Declared after IsTestRun: static fields initialize in text order
    private static readonly string NameSuffix = IsTestRun ? ".Test" : "";

    /// <summary>
    /// Single-instance key.
    /// </summary>
    public static string InstanceKey => AppInfo.DisplayName + NameSuffix;

    /// <summary>
    /// Signaled by <c>--quit</c>; the running copy waits on it.
    /// </summary>
    public static string QuitEventName => $"{AppInfo.DisplayName}.Quit{NameSuffix}";

    /// <summary>
    /// Signaled by a second launch; the running copy opens its main window.
    /// </summary>
    public static string ShowEventName => $"{AppInfo.DisplayName}.Show{NameSuffix}";

    /// <summary>
    /// The sign-in helper's pipe, log folder and theme when started with <c>--sign-in</c>, else null.
    /// </summary>
    public static (string PipeName, string? LogsFolder, Presentation.Settings.AppTheme Theme)? SignInRequest { get; } =
        Presentation.Pairing.SignInHelperArgs.Parse(Environment.GetCommandLineArgs());

    /// <summary>
    /// The running copy's end of <see cref="ShowEventName"/>, made before the single-instance check so a
    /// second launch while this copy is still starting isn't lost. Null when another process owns the name.
    /// </summary>
    public static EventWaitHandle? ShowRequest { get; private set; }

    /// <summary>
    /// The running copy's end of <see cref="QuitEventName"/>, made before the single-instance check so a
    /// <c>--quit</c> sent while this copy is still starting isn't lost. Null when another process owns the name.
    /// </summary>
    public static EventWaitHandle? QuitRequest { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

#if EnableTestHooks
        // Test Hook Flags: unusable ones refuse to start (no UI, no network) and leave the reason where a UI test can
        // read it; a valid start clears any stale reason from an earlier run
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
            // Nothing more to do; a refusal still exits below
            if (TestHooks.Error is not null)
            {
                Environment.Exit(2);
                return;
            }
        }
#endif

        // Quit Request
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
            {
                quit.Set();
                quit.Dispose();
            }
            return;
        }

        // Sign-In Helper: no single instance, quit or show events; the app that started it owns the lifetime
        if (SignInRequest is not null)
        {
            StartApplication();
            return;
        }

        // Every launch makes or opens the show and quit events before checking for a running copy, so they
        // always exist by the time a second launch or a --quit looks for them
        var showRequest = OpenEvent(ShowEventName);
        var quitRequest = OpenEvent(QuitEventName);

        // Second Launch: tell the running copy and let it take the foreground, then exit
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            if (showRequest is not null)
            {
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                showRequest.Set();
            }
            showRequest?.Dispose();
            quitRequest?.Dispose();
            return;
        }

        // First Launch
        ShowRequest = showRequest;
        QuitRequest = quitRequest;
        StartApplication();
        ShowRequest?.Dispose();
        QuitRequest?.Dispose();
    }

    // Runs The XAML App Until It Exits
    private static void StartApplication() =>
        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

    // Null when another process owns the name (an elevated copy, say); the app runs without it
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

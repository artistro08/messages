using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;

namespace GMessages.App.UITests.Driver;

/// <summary>
/// Runs the real, built Debug <c>Messages.exe</c> against a <see cref="FakeGoogleServer"/> and an isolated temp data
/// folder, and finds its windows and controls from outside through UI Automation.
/// </summary>
/// <remarks>
/// The app is never hosted in the test process. It starts with <c>--test-endpoints</c> and <c>--test-data-dir</c>,
/// so it gets the <c>.Test</c> single-instance names and the test tray GUID and never touches a real install's data.
/// Every lookup is limited to windows of the app's own process. Dispose always cleans up, also after a failed test:
/// <c>--quit</c>, up to 10 s for the exit, then a kill, then the temp folder is deleted.
/// </remarks>
public sealed class AppDriver : IAsyncDisposable
{
    /// <summary>The default time a UI wait polls before failing.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private const string ExeRelativePath = @"GMessages.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Messages.exe";

    private static readonly string? FoundExe      = FindExe();
    private const int MaxSearchNodes = 5000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan QuitTimeout  = TimeSpan.FromSeconds(10);

    private readonly FakeGoogleServer _server;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private Process _process = null!;

    private AppDriver(FakeGoogleServer server, string dataDir)
    {
        _server = server;
        DataDir = dataDir;
    }

    /// <summary>
    /// Gets the full path of the built Debug exe the tests drive.
    /// </summary>
    /// <exception cref="AssertFailedException">The exe isn't built.</exception>
    public static string ExePath => FoundExe ?? throw new AssertFailedException(
        $"Messages.exe is not built. Run 'dotnet build src/GMessages.App -c Debug -p:Platform=x64' first. Looked for src\\{ExeRelativePath} in every folder above {AppContext.BaseDirectory}.");

    /// <summary>
    /// Gets the UI Automation client.
    /// </summary>
    public UIA3Automation Automation { get; } = new();

    /// <summary>
    /// Gets the app's temp data folder (<c>session.bin</c>, <c>settings.json</c>, <c>Logs</c>).
    /// </summary>
    public string DataDir { get; }

    /// <summary>
    /// Gets the running app process.
    /// </summary>
    public Process Process => _process;

    /// <summary>
    /// Gets the arguments every launch (and <c>--quit</c>) passes.
    /// </summary>
    public string[] TestArguments => ["--test-endpoints", _server.BaseUrl.AbsoluteUri, "--test-data-dir", DataDir];

    /// <summary>
    /// Starts the app with a fresh temp data folder.
    /// </summary>
    /// <param name="server">The fake server the app talks to.</param>
    /// <returns>The driver; dispose it to quit the app and delete the folder.</returns>
    /// <exception cref="AssertFailedException">The exe isn't built, or it exited right away.</exception>
    public static AppDriver Start(FakeGoogleServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        string data_dir = Path.Combine(Path.GetTempPath(), "Messages-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data_dir);

        // A Refused Start Leaves Nothing Behind: No UIA Client, No Temp Folder
        var driver = new AppDriver(server, data_dir);
        try
        {
            driver._process = driver.Launch();
            return driver;
        }
        catch (Exception)
        {
            driver.Automation.Dispose();
            Directory.Delete(data_dir, recursive: true);
            throw;
        }
    }

    // =========================================================================
    // WINDOWS AND ELEMENTS
    // =========================================================================

    /// <summary>
    /// Waits for an app window whose title starts with <paramref name="titleStartsWith"/>.
    /// </summary>
    /// <param name="titleStartsWith">The start of the title, such as <c>"Messages Settings"</c>.</param>
    /// <param name="timeout">How long to poll.</param>
    /// <returns>The window.</returns>
    /// <exception cref="AssertFailedException">No such window appeared in time.</exception>
    public Window WaitForWindow(string titleStartsWith, TimeSpan timeout)
    {
        Window? window = null;
        Wait.Until(() => (window = FindWindow(titleStartsWith)) is not null, timeout,
            () => $"No window titled '{titleStartsWith}…' appeared. Open app windows: [{string.Join(", ", WindowTitles())}].");
        return window!;
    }

    /// <summary>
    /// Finds an app window whose title starts with <paramref name="titleStartsWith"/>, without waiting.
    /// </summary>
    /// <param name="titleStartsWith">The start of the title.</param>
    /// <returns>The window, or null.</returns>
    public Window? FindWindow(string titleStartsWith) =>
        TopLevel().FirstOrDefault(e => SafeName(e).StartsWith(titleStartsWith, StringComparison.Ordinal))?.AsWindow();

    /// <summary>
    /// Waits until no app window's title starts with <paramref name="titleStartsWith"/>.
    /// </summary>
    /// <param name="titleStartsWith">The start of the title.</param>
    /// <param name="timeout">How long to poll.</param>
    /// <exception cref="AssertFailedException">The window was still open after the timeout.</exception>
    public void WaitForNoWindow(string titleStartsWith, TimeSpan timeout) =>
        Wait.Until(() => FindWindow(titleStartsWith) is null, timeout, () => $"The window '{titleStartsWith}…' did not close.");

    /// <summary>
    /// Waits for a control with the given AutomationId in any of the app's windows (and its popups).
    /// </summary>
    /// <param name="automationId">The AutomationId, such as <c>Pairing.Emoji</c>.</param>
    /// <returns>The control.</returns>
    /// <exception cref="AssertFailedException">It didn't appear within <see cref="DefaultTimeout"/>.</exception>
    public AutomationElement Find(string automationId) => Find(automationId, DefaultTimeout);

    /// <summary>
    /// Waits for a control with the given AutomationId in any of the app's windows (and its popups).
    /// </summary>
    /// <param name="automationId">The AutomationId.</param>
    /// <param name="timeout">How long to poll.</param>
    /// <returns>The control.</returns>
    /// <exception cref="AssertFailedException">It didn't appear in time.</exception>
    public AutomationElement Find(string automationId, TimeSpan timeout)
    {
        AutomationElement? element = null;
        Wait.Until(() => (element = TryFind(automationId)) is not null, timeout,
            () => $"No control '{automationId}' appeared in the app's windows: [{string.Join(", ", WindowTitles())}].");
        return element!;
    }

    /// <summary>
    /// Finds a control with the given AutomationId in any of the app's windows, without waiting.
    /// </summary>
    /// <param name="automationId">The AutomationId.</param>
    /// <returns>The control, or null.</returns>
    public AutomationElement? TryFind(string automationId)
    {
        foreach (AutomationElement window in TopLevel())
        {
            if (Search(window, e => e.Properties.AutomationId.ValueOrDefault == automationId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the UIA tree under <paramref name="root"/> breadth-first and returns the first element that matches.
    /// </summary>
    /// <remarks>
    /// Not UIA's own descendant search: under a WinUI WebView2 the Chromium tree leads back to the WebView2 element,
    /// a cycle that UIA's search walks until it times out. This walk skips elements it has already seen (by runtime
    /// ID), and stops after a few thousand.
    /// </remarks>
    /// <param name="root">Where to start; it is checked too.</param>
    /// <param name="match">The test for the wanted element.</param>
    /// <returns>The element, or null.</returns>
    public static AutomationElement? Search(AutomationElement root, Func<AutomationElement, bool> match)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(match);

        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<AutomationElement>([root]);
        while (queue.Count > 0 && seen.Count < MaxSearchNodes)
        {
            AutomationElement element = queue.Dequeue();
            try
            {
                if (!seen.Add(string.Join('.', element.Properties.RuntimeId.Value)))
                {
                    continue;
                }

                if (match(element))
                {
                    return element;
                }

                foreach (AutomationElement child in element.FindAllChildren())
                {
                    queue.Enqueue(child);
                }
            }
            catch (Exception ex) when (IsGone(ex))
            {
                // Gone While Walking; Its Subtree Goes With It
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the UIA tree under <paramref name="root"/>, one element per line: control type, name, AutomationId and
    /// bounds, indented by level.
    /// </summary>
    /// <remarks>
    /// For probes and failure messages. Like <see cref="Search"/>, it skips elements it has already seen (the WebView2
    /// cycle) and stops after a few thousand; an element that goes away mid-walk is noted and its subtree skipped.
    /// </remarks>
    /// <param name="root">Where to start; it is written too.</param>
    /// <param name="depth">How many levels below <paramref name="root"/> to write.</param>
    /// <returns>The tree.</returns>
    public static string DumpTree(AutomationElement root, int depth)
    {
        ArgumentNullException.ThrowIfNull(root);

        var lines = new System.Text.StringBuilder();
        var seen  = new HashSet<string>(StringComparer.Ordinal);
        Write(root, 0);
        return lines.ToString();

        void Write(AutomationElement element, int level)
        {
            if (seen.Count >= MaxSearchNodes)
            {
                return;
            }

            string indent = new(' ', level * 2);
            try
            {
                if (!seen.Add(string.Join('.', element.Properties.RuntimeId.Value)))
                {
                    return;
                }

                System.Drawing.Rectangle bounds = element.Properties.BoundingRectangle.ValueOrDefault;
                lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}{element.Properties.ControlType.ValueOrDefault} '{SafeName(element)}' #{element.Properties.AutomationId.ValueOrDefault} [{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}]");
                if (level >= depth)
                {
                    return;
                }

                foreach (AutomationElement child in element.FindAllChildren())
                {
                    Write(child, level + 1);
                }
            }
            catch (Exception ex) when (IsGone(ex))
            {
                lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}(gone: {ex.GetType().Name})");
            }
        }
    }

    /// <summary>
    /// Waits until a control's Name (a TextBlock's text) equals <paramref name="expected"/>.
    /// </summary>
    /// <param name="automationId">The control's AutomationId.</param>
    /// <param name="expected">The wanted text.</param>
    /// <exception cref="AssertFailedException">The text never matched.</exception>
    public void WaitForText(string automationId, string expected)
    {
        string? last = null;
        Wait.Until(() => (last = SafeName(TryFind(automationId))) == expected, DefaultTimeout,
            () => $"'{automationId}' never read '{expected}'; last text: '{last}'.");
    }

    // =========================================================================
    // PROCESS
    // =========================================================================

    /// <summary>
    /// Asks the app to quit with <c>Messages.exe --quit</c>, then waits for it to exit.
    /// </summary>
    /// <returns>A task that completes once the app has exited.</returns>
    /// <exception cref="AssertFailedException">The app was still running after 10 s.</exception>
    public async Task QuitAsync()
    {
        if (_process.HasExited)
        {
            return;
        }

        // The --quit Helper Must Never Outlive The Call; A Refused Flag Exits 2 With Its Reason In The Error File
        using (Process quit = StartExe(["--quit", .. TestArguments]))
        {
            try
            {
                await quit.WaitForExitAsync().WaitAsync(QuitTimeout);
                if (quit.ExitCode == 2)
                {
                    Assert.Fail($"Messages.exe --quit refused its flags: {TestHookError()}");
                }
            }
            finally
            {
                if (!quit.HasExited)
                {
                    quit.Kill(entireProcessTree: true);
                }
            }
        }

        using var timeout = new CancellationTokenSource(QuitTimeout);
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"The app (pid {_process.Id}) was still running {QuitTimeout.TotalSeconds:0} s after --quit.");
        }
    }

    /// <summary>
    /// Waits for the app process to exit on its own (for example after the tray menu's Quit).
    /// </summary>
    /// <returns>A task that completes once the app has exited.</returns>
    /// <exception cref="AssertFailedException">The app was still running after 10 s.</exception>
    public async Task WaitForExitAsync()
    {
        using var timeout = new CancellationTokenSource(QuitTimeout);
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"The app (pid {_process.Id}) did not exit within {QuitTimeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// Quits the app and starts it again with the same data folder and fake server.
    /// </summary>
    /// <returns>A task that completes once the new process has started.</returns>
    public async Task RestartAsync()
    {
        await QuitAsync();
        _process.Dispose();
        _process = Launch();
    }

    /// <summary>
    /// Starts another <c>Messages.exe</c> with the given arguments, as a person or a shortcut would.
    /// </summary>
    /// <param name="arguments">The command-line arguments.</param>
    /// <returns>The started process.</returns>
    public static Process StartExe(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {ExePath}.");
    }

    /// <summary>
    /// Gets every running process started from <see cref="ExePath"/>, so a real install of the app is never counted.
    /// </summary>
    /// <returns>The process IDs.</returns>
    public static IReadOnlyList<int> RunningTestExeIds()
    {
        string name = Path.GetFileNameWithoutExtension(ExePath);
        var ids     = new List<int>();
        foreach (Process process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        ids.Add(process.Id);
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Exited Or Not Ours To Read
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// Quits the app (a kill if it won't), releases UI Automation and deletes the temp data folder.
    /// </summary>
    /// <returns>A task that completes when everything is cleaned up.</returns>
    public async ValueTask DisposeAsync()
    {
        // Quit, Then Kill If It Hangs
        try
        {
            await QuitAsync();
        }
#pragma warning disable CA1031 // Teardown Must Go On: The Kill Below Covers Any Failure
        catch (Exception)
#pragma warning restore CA1031
        {
            // Killed Below
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(QuitTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _process.Dispose();
        Automation.Dispose();
        await DeleteLeftoverSignInProfilesAsync();

        // Temp Data Folder: The Log File May Still Be Closing, So Retry Briefly
        for (int attempt = 0; attempt < 20 && Directory.Exists(DataDir); attempt++)
        {
            try
            {
                Directory.Delete(DataDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(PollInterval);
            }
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Starts The App And Checks It Didn't Refuse Its Flags
    private Process Launch()
    {
        Process process = StartExe(TestArguments);

        // Refused Flags Exit Before Any UI, Well Within 500 ms; Catching That Here Fails With The Reason Instead Of A
        // 20 s "No Window" Timeout. A Healthy Start Keeps Running, So This Only Adds Half A Second Per Launch
        if (process.WaitForExit(TimeSpan.FromMilliseconds(500)))
        {
            int code = process.ExitCode;
            process.Dispose();
            Assert.Fail($"Messages.exe exited at once with code {code}: {TestHookError()}");
        }

        return process;
    }

    // The Reason The App Wrote When It Refused Its Test Flags
    private static string TestHookError()
    {
        string error_file = Path.Combine(Path.GetTempPath(), $"{AppInfo.DisplayName}-testhooks-error.txt");
        return File.Exists(error_file) ? File.ReadAllText(error_file) : "(no error file)";
    }

    // Sign-In Profiles Made During This Test That The App Couldn't Delete (A Quit Mid-Sign-In, A Kill); Older Ones
    // Aren't Ours. The WebView2 Browser Processes Outlive The App Briefly, So Locked Folders Are Retried For Up To 5 s
    private async Task DeleteLeftoverSignInProfilesAsync()
    {
        string pattern = $"{AppInfo.DisplayName}.Test-signin-*";
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            string[] ours = [.. Directory.EnumerateDirectories(Path.GetTempPath(), pattern).Where(f => Directory.GetCreationTimeUtc(f) >= _startedUtc)];
            foreach (string folder in ours)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still Locked; Retried Below
                }
            }

            if (ours.All(f => !Directory.Exists(f)) || DateTime.UtcNow >= until)
            {
                return;
            }

            await Task.Delay(PollInterval);
        }
    }

    // Top-Level Windows Of The App Process, Including Its Popup Windows (Menus)
    private AutomationElement[] TopLevel()
    {
        if (_process.HasExited)
        {
            return [];
        }

        try
        {
            return Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(_process.Id));
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return [];
        }
    }

    private IEnumerable<string> WindowTitles() => TopLevel().Select(SafeName).Where(n => n.Length > 0);

    /// <summary>
    /// Reads an element's Name, or an empty string when it is null or already gone.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The Name.</returns>
    public static string SafeName(AutomationElement? element)
    {
        try
        {
            return element?.Name ?? "";
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return "";
        }
    }

    // UIA Throws These When An Element Disappears Mid-Call
    private static bool IsGone(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException or FlaUI.Core.Exceptions.PropertyNotSupportedException or TimeoutException;

    // The Exe Sits Under src/GMessages.App/bin; Walk Up From The Test Output To Find It
    private static string? FindExe()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ExeRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

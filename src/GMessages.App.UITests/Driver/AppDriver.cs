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
/// The test process never hosts the app. The app starts with <c>--test-endpoints</c> and <c>--test-data-dir</c>.
/// It then uses the <c>.Test</c> single-instance names and the test tray GUID.
/// It never touches the data of a real install.
/// Every lookup is limited to the windows of the process of the app.
/// Dispose always cleans up, also after a failed test: it sends <c>--quit</c> and waits up to 10 s for the exit.
/// It then kills the process and deletes the temp folder.
/// </remarks>
public sealed class AppDriver : IAsyncDisposable
{
    /// <summary>The default time a UI wait polls before failing.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    // GMESSAGES_UITEST_AOT=1 drives the optimized Native AOT publish instead. Only the AOT build returns WinUI objects
    // as base types, so only it catches a type test that fails in the shipped app.
    private static readonly bool Aot = Environment.GetEnvironmentVariable("GMESSAGES_UITEST_AOT") == "1";

    // The exe name comes from AppDisplayName in src/Directory.Build.props. Change both together.
    private const string ExeName = "Messages.exe";

    private static readonly string ExeRelativePath = Aot
        ? @"GMessages.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\publish\" + ExeName
        : @"GMessages.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\" + ExeName;

    // Use the MSBuild of Visual Studio. The system XAML compiler of the app does not run under the dotnet CLI.
    private static readonly string BuildCommand = Aot
        ? "msbuild src/GMessages.App -restore -t:Publish -p:Configuration=Debug -p:Platform=x64 -p:Optimize=true"
        : "msbuild src/GMessages.App -restore -p:Configuration=Debug -p:Platform=x64";

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
    /// Gets the full path of the built Debug exe that the tests drive. When <c>GMESSAGES_UITEST_AOT</c> is <c>1</c>,
    /// the path is the optimized Native AOT publish.
    /// </summary>
    /// <exception cref="AssertFailedException">The exe is not built.</exception>
    public static string ExePath => FoundExe ?? throw new AssertFailedException(
        $"{ExeName} is not built. Run '{BuildCommand}' first. Looked for src\\{ExeRelativePath} in every folder above {AppContext.BaseDirectory}.");

    /// <summary>
    /// Gets the UI Automation client.
    /// </summary>
    public UIA3Automation Automation { get; } = new();

    /// <summary>
    /// Gets the temp data folder of the app (<c>session.bin</c>, <c>settings.json</c>, <c>Logs</c>).
    /// </summary>
    public string DataDir { get; }

    /// <summary>
    /// Gets the running app process.
    /// </summary>
    public Process Process => _process;

    /// <summary>
    /// Gets the arguments that every launch and every <c>--quit</c> passes.
    /// </summary>
    public string[] TestArguments => ["--test-endpoints", _server.BaseUrl.AbsoluteUri, "--test-data-dir", DataDir];

    /// <summary>
    /// Starts the app with a fresh temp data folder.
    /// </summary>
    /// <param name="server">The fake server the app talks to.</param>
    /// <returns>The driver. Dispose it to quit the app and delete the folder.</returns>
    /// <exception cref="AssertFailedException">The exe is not built, or it exits at once.</exception>
    public static AppDriver Start(FakeGoogleServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        string dataDir = Path.Combine(Path.GetTempPath(), "Messages-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        // A refused start leaves nothing behind: no UIA client and no temp folder.
        var driver = new AppDriver(server, dataDir);
        try
        {
            driver._process = driver.Launch();
            return driver;
        }
        catch (Exception)
        {
            driver.Automation.Dispose();
            Directory.Delete(dataDir, recursive: true);
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
    /// <exception cref="AssertFailedException">No such window appears in time.</exception>
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
    /// <returns>The window, or null when none matches.</returns>
    public Window? FindWindow(string titleStartsWith) =>
        TopLevel().FirstOrDefault(e => SafeName(e).StartsWith(titleStartsWith, StringComparison.Ordinal))?.AsWindow();

    /// <summary>
    /// Waits until no app window's title starts with <paramref name="titleStartsWith"/>.
    /// </summary>
    /// <param name="titleStartsWith">The start of the title.</param>
    /// <param name="timeout">How long to poll.</param>
    /// <exception cref="AssertFailedException">The window is still open after the timeout.</exception>
    public void WaitForNoWindow(string titleStartsWith, TimeSpan timeout) =>
        Wait.Until(() => FindWindow(titleStartsWith) is null, timeout, () => $"The window '{titleStartsWith}…' did not close.");

    /// <summary>
    /// Waits for a control with the given AutomationId in any window of the app, including its popups.
    /// </summary>
    /// <param name="automationId">The AutomationId, such as <c>Pairing.Emoji</c>.</param>
    /// <returns>The control.</returns>
    /// <exception cref="AssertFailedException">
    /// The control does not appear within <see cref="DefaultTimeout"/>.
    /// </exception>
    public AutomationElement Find(string automationId) => Find(automationId, DefaultTimeout);

    /// <summary>
    /// Waits for a control with the given AutomationId in any window of the app, including its popups.
    /// </summary>
    /// <param name="automationId">The AutomationId.</param>
    /// <param name="timeout">How long to poll.</param>
    /// <returns>The control.</returns>
    /// <exception cref="AssertFailedException">The control does not appear in time.</exception>
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
    /// <returns>The control, or null when none matches.</returns>
    public AutomationElement? TryFind(string automationId) => TryFindWhere(e => e.Properties.AutomationId.ValueOrDefault == automationId);

    /// <summary>
    /// Finds the first control that matches, in any window of the app including its popups, without waiting.
    /// </summary>
    /// <param name="match">The test that each control must pass.</param>
    /// <returns>The control, or null when none matches.</returns>
    public AutomationElement? TryFindWhere(Func<AutomationElement, bool> match)
    {
        foreach (AutomationElement window in TopLevel())
        {
            if (Search(window, match) is { } found)
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
    /// This walk replaces the descendant search of UIA. Under a WinUI WebView2 the Chromium tree leads back to the
    /// WebView2 element. This cycle makes the search of UIA run until it times out.
    /// The walk skips elements that it has already seen, by runtime ID, and stops after 5000 elements.
    /// </remarks>
    /// <param name="root">Where to start. The method checks this element too.</param>
    /// <param name="match">The test for the wanted element.</param>
    /// <returns>The element, or null when none matches.</returns>
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
                // The element went away during the walk. The walk skips its subtree.
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the UIA tree under <paramref name="root"/>, one element per line, indented by level. A line holds the
    /// control type, name, AutomationId and bounds.
    /// </summary>
    /// <remarks>
    /// The output serves probes and failure messages. Like <see cref="Search"/>, the walk skips elements that it has
    /// already seen, which breaks the WebView2 cycle, and stops after 5000 elements.
    /// The walk notes an element that goes away during the walk and skips its subtree.
    /// </remarks>
    /// <param name="root">Where to start. The method writes this element too.</param>
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
    /// Waits until the Name of a control equals <paramref name="expected"/>. The Name of a TextBlock is its text.
    /// </summary>
    /// <param name="automationId">The AutomationId of the control.</param>
    /// <param name="expected">The expected text.</param>
    /// <exception cref="AssertFailedException">The text never matches.</exception>
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
    /// Asks the app to quit with <c>Messages.exe --quit</c>, then waits for the app to exit.
    /// </summary>
    /// <returns>A task that completes when the app has exited.</returns>
    /// <exception cref="AssertFailedException">The app is still running after 10 s.</exception>
    public async Task QuitAsync()
    {
        if (_process.HasExited)
        {
            return;
        }

        // The --quit helper must not outlive the call.
        // A refused flag exits with code 2 and writes the reason to the error file.
        using (Process quit = StartExe(["--quit", .. TestArguments]))
        {
            try
            {
                await quit.WaitForExitAsync().WaitAsync(QuitTimeout);
                if (quit.ExitCode == 2)
                {
                    Assert.Fail($"{ExeName} --quit refused its flags: {TestHookError()}");
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
    /// Waits for the app process to exit by itself, for example after Quit in the tray menu.
    /// </summary>
    /// <returns>A task that completes when the app has exited.</returns>
    /// <exception cref="AssertFailedException">The app is still running after 10 s.</exception>
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
    /// <returns>A task that completes when the new process has started.</returns>
    public async Task RestartAsync()
    {
        await QuitAsync();
        _process.Dispose();
        _process = Launch();
    }

    /// <summary>
    /// Starts another <c>Messages.exe</c> with the given arguments.
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
    /// Gets every running process started from <see cref="ExePath"/>.
    /// The result never counts a real install of the app.
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
                    // The process exited, or the test has no right to read it.
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// Quits the app, or kills it when the quit fails. Then releases UI Automation and deletes the temp data folder.
    /// </summary>
    /// <returns>A task that completes when the cleanup is done.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await QuitAsync();
        }
#pragma warning disable CA1031 // Teardown must go on. The kill below covers any failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The kill below ends the process.
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(QuitTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _process.Dispose();
        Automation.Dispose();
        await DeleteLeftoverSignInProfilesAsync();

        // The log file can still be closing, so the delete retries briefly.
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

    private Process Launch()
    {
        Process process = StartExe(TestArguments);

        // Refused flags exit before any UI, well within 500 ms. This check fails with the reason instead of a 20 s
        // "No Window" timeout. A healthy start keeps running, so the check adds only 0.5 s per launch.
        if (process.WaitForExit(TimeSpan.FromMilliseconds(500)))
        {
            int code = process.ExitCode;
            process.Dispose();
            Assert.Fail($"{ExeName} exited at once with code {code}: {TestHookError()}");
        }

        return process;
    }

    // The app writes the reason to this file when it refuses its test flags.
    private static string TestHookError()
    {
        string errorFile = Path.Combine(Path.GetTempPath(), $"{AppInfo.DisplayName}-testhooks-error.txt");
        return File.Exists(errorFile) ? File.ReadAllText(errorFile) : "(no error file)";
    }

    // Deletes the sign-in profiles that this test created and the app could not delete.
    // This happens, for example, after a quit during sign-in or after a kill.
    // Older profiles belong to other runs.
    // The WebView2 browser processes outlive the app briefly.
    // The method therefore retries locked folders for up to 5 s.
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
                    // The folder is still locked. The loop retries.
                }
            }

            if (ours.All(f => !Directory.Exists(f)) || DateTime.UtcNow >= until)
            {
                return;
            }

            await Task.Delay(PollInterval);
        }
    }

    // The top-level windows of the app process, including popup windows such as menus.
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
    /// Reads the Name of an element, or an empty string when the element is null or gone.
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

    // UIA throws these exceptions when an element disappears during a call.
    private static bool IsGone(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException or FlaUI.Core.Exceptions.PropertyNotSupportedException or TimeoutException;

    // The exe sits under src/GMessages.App/bin. The search walks up from the test output folder.
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

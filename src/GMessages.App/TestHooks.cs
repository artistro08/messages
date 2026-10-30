using GMessages.Protocol.Http;

namespace GMessages.App;

#if EnableTestHooks
/// <summary>
/// UI test hooks: point the app at a local fake Google server and an isolated data folder.
/// </summary>
/// <remarks>
/// Compiled only when <c>EnableTestHooks</c> is defined, which is Debug builds only. Release builds get the stub
/// below that always answers null, so the flags are ignored. Flags: <c>--test-endpoints &lt;base url&gt;</c> and
/// <c>--test-data-dir &lt;path&gt;</c>. If either flag is present both must be valid, or the app refuses to start.
/// </remarks>
internal static class TestHooks
{
    private static readonly (Uri? EndpointBase, string? DataDir, string? Error) Parsed =
        GMessages.Presentation.TestHookArgs.Validate(Environment.GetCommandLineArgs());

    /// <summary>
    /// The fake server base address from <c>--test-endpoints</c>, else null.
    /// </summary>
    public static Uri? EndpointBase { get; } = Parsed.EndpointBase;

    /// <summary>
    /// The data folder from <c>--test-data-dir</c>, else null.
    /// </summary>
    public static string? DataDir { get; } = Parsed.DataDir;

    /// <summary>
    /// A one-line reason when the test flags are unusable, else null. The app must then refuse to start.
    /// </summary>
    public static string? Error { get; } = Parsed.Error;

    /// <summary>
    /// Every server address pointed at <see cref="EndpointBase"/>, else null (use the real servers).
    /// </summary>
    /// <remarks>Authentication resolves to <c>{base}/web/authentication</c>.</remarks>
    public static GmEndpoints? Endpoints { get; } = Parsed.EndpointBase is { } b ? new GmEndpoints(b, b, b) : null;

    /// <summary>
    /// True when pointed at the fake server, so link previews may fetch its <c>http://127.0.0.1</c> pages.
    /// </summary>
    public static bool AllowLoopbackPreviews => EndpointBase is not null;

    /// <summary>
    /// True when pointed at the fake server: a clicked link is only logged, never opened in a browser.
    /// </summary>
    public static bool BlockLaunches => EndpointBase is not null;

    /// <summary>
    /// When pointed at the fake server, notes a file the app would open in its default app, or a Windows page it would
    /// open (such as <c>ms-settings:privacy-microphone</c>), in <c>{data folder}\launched.txt</c> (one full path or
    /// address per line) instead of opening it.
    /// </summary>
    /// <param name="path">The file's full path, or the page's address.</param>
    /// <returns>True when noted (the caller must not launch it); false on a normal run.</returns>
    public static bool RecordLaunch(string path)
    {
        if (!BlockLaunches || DataDir is null)
        {
            return false;
        }

        File.AppendAllText(Path.Combine(DataDir, "launched.txt"), path + Environment.NewLine);
        return true;
    }

    /// <summary>
    /// When pointed at the fake server, puts an element's internal state (such as a photo's "Blurred" or "Full") in its
    /// UI Automation item status, for the tests to read. Does nothing on a normal run.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="state">The state's name.</param>
    public static void ShowState(Microsoft.UI.Xaml.DependencyObject element, string state)
    {
        if (BlockLaunches)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(element, state);
        }
    }

    /// <summary>
    /// When pointed at the fake server, writes a part's internal state to <c>{data folder}\{name}.txt</c> (replacing it),
    /// for the tests to read, such as the voice player's real playback state and position. Does nothing on a normal run.
    /// </summary>
    /// <param name="name">The file's name, without <c>.txt</c>.</param>
    /// <param name="state">The state, one line.</param>
    public static void NoteState(string name, string state)
    {
        if (!BlockLaunches || DataDir is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(DataDir, name + ".txt"), state);
        }
        catch (IOException)
        {
            // The Test Is Reading It: The Next Note Lands
        }
    }

    /// <summary>
    /// When pointed at the fake server, stands in for the file picker: notes which picker opened ("files" or "photos")
    /// in <c>{data folder}\picker.txt</c>, and answers with the full paths in <c>{data folder}\pick.txt</c> (one per
    /// line), which it then deletes. No real picker ever opens in a test.
    /// </summary>
    /// <param name="photos">True for the photo picker.</param>
    /// <returns>The picked paths (empty when nothing waits), or null on a normal run (open the real picker).</returns>
    public static IReadOnlyList<string>? PickFiles(bool photos)
    {
        if (!BlockLaunches || DataDir is null)
        {
            return null;
        }

        File.AppendAllText(Path.Combine(DataDir, "picker.txt"), (photos ? "photos" : "files") + Environment.NewLine);
        string pick = Path.Combine(DataDir, "pick.txt");
        if (!File.Exists(pick))
        {
            return [];
        }

        string[] paths = File.ReadAllLines(pick).Where(p => p.Length > 0).ToArray();
        File.Delete(pick);
        return paths;
    }

    /// <summary>
    /// When pointed at the fake server, stands in for the microphone, so no test ever records from a real one:
    /// <c>{data folder}\microphone.txt</c> reads <c>denied</c> (access off), or holds the path of an audio file the fake
    /// recording copies. Without the file there is no microphone.
    /// </summary>
    /// <returns><c>denied</c>, <c>none</c> or a file path; null on a normal run (use the real microphone).</returns>
    public static string? FakeMicrophone()
    {
        if (!BlockLaunches || DataDir is null)
        {
            return null;
        }

        string microphone = Path.Combine(DataDir, "microphone.txt");
        return File.Exists(microphone) ? File.ReadAllText(microphone).Trim() : "none";
    }

    /// <summary>
    /// When pointed at the fake server, shortens a wait for a test: <c>{data folder}\{name}.txt</c> holds the seconds
    /// (such as <c>download-timeout</c> or <c>full-size-wait</c>). Read each time the wait starts.
    /// </summary>
    /// <param name="name">The wait's file name, without <c>.txt</c>.</param>
    /// <returns>The shortened wait, or null when the file is missing or on a normal run (use the real wait).</returns>
    public static TimeSpan? ShortWait(string name)
    {
        if (!BlockLaunches || DataDir is null)
        {
            return null;
        }

        string wait = Path.Combine(DataDir, name + ".txt");
        return File.Exists(wait) && double.TryParse(File.ReadAllText(wait).Trim(), System.Globalization.CultureInfo.InvariantCulture, out double seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
#else
/// <summary>
/// Release stub: the test hooks do not exist, so every flag is ignored.
/// </summary>
internal static class TestHooks
{
    /// <summary>Always null in Release.</summary>
    public static Uri? EndpointBase => null;

    /// <summary>Always null in Release.</summary>
    public static string? DataDir => null;

    /// <summary>Always null in Release.</summary>
    public static string? Error => null;

    /// <summary>Always null in Release: the app uses the real servers.</summary>
    public static GmEndpoints? Endpoints => null;

    /// <summary>Always false in Release: link previews never fetch loopback addresses.</summary>
    public static bool AllowLoopbackPreviews => false;

    /// <summary>Always false in Release: a clicked link opens in the default browser.</summary>
    public static bool BlockLaunches => false;

    /// <summary>Always false in Release: the file opens in its default app.</summary>
    /// <param name="path">The file's full path.</param>
    /// <returns>False.</returns>
    public static bool RecordLaunch(string path) => false;

    /// <summary>Does nothing in Release.</summary>
    /// <param name="element">The element.</param>
    /// <param name="state">The state's name.</param>
    public static void ShowState(Microsoft.UI.Xaml.DependencyObject element, string state)
    {
    }

    /// <summary>Does nothing in Release.</summary>
    /// <param name="name">The file's name.</param>
    /// <param name="state">The state.</param>
    public static void NoteState(string name, string state)
    {
    }

    /// <summary>Always null in Release: the real file picker opens.</summary>
    /// <param name="photos">True for the photo picker.</param>
    /// <returns>Null.</returns>
    public static IReadOnlyList<string>? PickFiles(bool photos) => null;

    /// <summary>Always null in Release: the real microphone records.</summary>
    /// <returns>Null.</returns>
    public static string? FakeMicrophone() => null;

    /// <summary>Always null in Release: every wait is its real length.</summary>
    /// <param name="name">The wait's name.</param>
    /// <returns>Null.</returns>
    public static TimeSpan? ShortWait(string name) => null;
}
#endif

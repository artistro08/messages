using GMessages.Protocol.Http;

namespace GMessages.App;

#if EnableTestHooks
/// <summary>
/// Holds the UI test hooks, which point the app at a local fake Google server and an isolated data folder.
/// </summary>
/// <remarks>
/// Only Debug builds define <c>EnableTestHooks</c> and compile this class. Release builds get the stub below, which
/// always answers null, so Release ignores the flags. The flags are <c>--test-endpoints &lt;base url&gt;</c> and
/// <c>--test-data-dir &lt;path&gt;</c>. When either flag is present, both must be valid, or the app refuses to start.
/// </remarks>
internal static class TestHooks
{
    private static readonly (Uri? EndpointBase, string? DataDir, string? Error) Parsed =
        GMessages.Presentation.TestHookArgs.Validate(Environment.GetCommandLineArgs());

    /// <summary>
    /// Gets the base address of the fake server from <c>--test-endpoints</c>, or null.
    /// </summary>
    public static Uri? EndpointBase { get; } = Parsed.EndpointBase;

    /// <summary>
    /// Gets the data folder from <c>--test-data-dir</c>, or null.
    /// </summary>
    public static string? DataDir { get; } = Parsed.DataDir;

    /// <summary>
    /// Gets a one-line reason when the test flags are not usable, or null. The app must then refuse to start.
    /// </summary>
    public static string? Error { get; } = Parsed.Error;

    /// <summary>
    /// Gets the server addresses, all pointed at <see cref="EndpointBase"/>. It is null when the app uses the real
    /// servers.
    /// </summary>
    /// <remarks>Authentication resolves to <c>{base}/web/authentication</c>.</remarks>
    public static GmEndpoints? Endpoints { get; } = Parsed.EndpointBase is { } b ? new GmEndpoints(b, b, b) : null;

    /// <summary>
    /// Gets whether the app uses the fake server. Then link previews can fetch its <c>http://127.0.0.1</c> pages.
    /// </summary>
    public static bool AllowLoopbackPreviews => EndpointBase is not null;

    /// <summary>
    /// Gets whether the app uses the fake server. Then the app only logs a clicked link and never opens a browser.
    /// </summary>
    public static bool BlockLaunches => EndpointBase is not null;

    /// <summary>
    /// With the fake server, writes a launch to <c>{data folder}\launched.txt</c> and does not open it. A launch is a
    /// file for its default app, or a Windows page such as <c>ms-settings:privacy-microphone</c>.
    /// </summary>
    /// <remarks>
    /// Each line holds a full path or an address. A file for the app picker of Windows (Open with) gets <c> picker</c>
    /// after its path, and no picker shows.
    /// </remarks>
    /// <param name="path">The full path of the file, or the address of the page.</param>
    /// <param name="picker">True when the file opens with the app picker of Windows.</param>
    /// <returns>True when the hook wrote the launch, so the caller must not launch it. False on a normal run.</returns>
    public static bool RecordLaunch(string path, bool picker = false)
    {
        if (!BlockLaunches || DataDir is null)
        {
            return false;
        }

        File.AppendAllText(Path.Combine(DataDir, "launched.txt"), path + (picker ? " picker" : string.Empty) + Environment.NewLine);
        return true;
    }

    /// <summary>
    /// With the fake server, puts the internal state of an element in its UI Automation item status for the tests. The
    /// hook does nothing on a normal run.
    /// </summary>
    /// <remarks>An example is "Blurred" or "Full" for a photo.</remarks>
    /// <param name="element">The element.</param>
    /// <param name="state">The name of the state.</param>
    public static void ShowState(Windows.UI.Xaml.DependencyObject element, string state)
    {
        if (BlockLaunches)
        {
            Windows.UI.Xaml.Automation.AutomationProperties.SetItemStatus(element, state);
        }
    }

    /// <summary>
    /// With the fake server, writes the internal state of a part to <c>{data folder}\{name}.txt</c> for the tests. The
    /// hook does nothing on a normal run.
    /// </summary>
    /// <remarks>
    /// An example is the real playback state and position of the voice player. Each note replaces the earlier state in
    /// the file.
    /// </remarks>
    /// <param name="name">The name of the file, without <c>.txt</c>.</param>
    /// <param name="state">The state, on a single line.</param>
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
            // The test reads the file at this time. The next note writes it.
        }
    }

    /// <summary>
    /// With the fake server, takes the place of the file picker, so no real picker opens in a test.
    /// </summary>
    /// <remarks>
    /// The hook writes the picker ("files" or "photos") to <c>{data folder}\picker.txt</c>. It answers with the full
    /// paths in <c>{data folder}\pick.txt</c>, a path on each line, then deletes that file.
    /// </remarks>
    /// <param name="photos">True for the photo picker.</param>
    /// <returns>
    /// The picked paths, empty when no file waits. Null on a normal run, where the app opens the real picker.
    /// </returns>
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

        string[] paths = [.. File.ReadAllLines(pick).Where(p => p.Length > 0)];
        File.Delete(pick);
        return paths;
    }

    /// <summary>
    /// With the fake server, takes the place of the clipboard for a paste into the message box. The hook builds what
    /// <c>{data folder}\paste.txt</c> describes, then deletes that file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each line of the file is a format and a value:
    /// <list type="bullet">
    /// <item><c>text &lt;text&gt;</c>.</item>
    /// <item><c>html &lt;fragment&gt;</c>, as the emoji panel gives a GIF: an <c>img</c> of the GIF.</item>
    /// <item><c>file &lt;path&gt;</c>, the files as Explorer copies them.</item>
    /// <item><c>bitmap &lt;path&gt;</c>, a picture as Windows copies a screenshot.</item>
    /// <item><c>format &lt;name&gt; &lt;path&gt;</c>, the bytes of a file under a named format, such as <c>GIF</c>.
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// Without the file, the paste reads the real clipboard. The copy tests use that: they paste what the Copy of the
    /// app put there. In a test run the paste always takes itself at once (see <see cref="BlockLaunches"/>). Thus the
    /// caller decides before the await. Only test runs use this hook.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The content of the clipboard. Null on a normal run or without the file, so the paste reads the real clipboard.
    /// </returns>
    public static async Task<Windows.ApplicationModel.DataTransfer.DataPackageView?> PasteContentAsync()
    {
        if (!BlockLaunches || DataDir is null)
        {
            return null;
        }

        string paste = Path.Combine(DataDir, "paste.txt");
        if (!File.Exists(paste))
        {
            return null;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();

        var files = new List<Windows.Storage.IStorageItem>();
        foreach (string line in File.ReadAllLines(paste).Where(l => l.Length > 0))
        {
            string[] parts = line.Split(' ', 3);
            switch (parts[0])
            {
                case "text":
                    package.SetText(line[5..]);
                    break;
                case "file":
                    files.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(line[5..]));
                    break;
                case "html":
                    package.SetHtmlFormat(Windows.ApplicationModel.DataTransfer.HtmlFormatHelper.CreateHtmlFormat(line[5..]));
                    break;
                case "bitmap":
                    package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(
                        await Windows.Storage.StorageFile.GetFileFromPathAsync(line[7..])));
                    break;
                case "format":
                    var stream    = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    Stream writer = stream.AsStreamForWrite();
                    writer.Write(File.ReadAllBytes(parts[2]));
                    writer.Flush();
                    stream.Seek(0);
                    package.SetData(parts[1], stream);
                    break;
            }
        }

        if (files.Count > 0)
        {
            package.SetStorageItems(files);
        }

        File.Delete(paste);
        return package.GetView();
    }

    /// <summary>
    /// With the fake server, takes the place of the pick in the Save as dialog. The app still makes and sets up the
    /// dialog, so that code runs.
    /// </summary>
    /// <remarks>
    /// The hook writes "save" to <c>{data folder}\picker.txt</c>. It answers with the full path in
    /// <c>{data folder}\save-pick.txt</c>, then deletes that file.
    /// </remarks>
    /// <returns>
    /// The picked path, empty as for a cancel when no file waits. Null on a normal run, where the real dialog shows.
    /// </returns>
    public static string? PickSaveFile()
    {
        if (!BlockLaunches || DataDir is null)
        {
            return null;
        }

        File.AppendAllText(Path.Combine(DataDir, "picker.txt"), "save" + Environment.NewLine);
        string pick = Path.Combine(DataDir, "save-pick.txt");
        if (!File.Exists(pick))
        {
            return string.Empty;
        }

        string path = File.ReadAllText(pick).Trim();
        File.Delete(pick);
        return path;
    }

    /// <summary>
    /// With the fake server, returns <c>{data folder}\Downloads</c> for Download, so no test writes to the real
    /// Downloads folder. The hook makes the folder when it is missing.
    /// </summary>
    /// <returns>The folder, or null on a normal run, where Download uses the Downloads folder of the user.</returns>
    public static string? DownloadsFolder()
    {
        if (!BlockLaunches || DataDir is null)
        {
            return null;
        }

        return Directory.CreateDirectory(Path.Combine(DataDir, "Downloads")).FullName;
    }

    /// <summary>
    /// With the fake server, takes the place of the microphone, so no test records from a real microphone.
    /// </summary>
    /// <remarks>
    /// <c>{data folder}\microphone.txt</c> holds <c>denied</c> when access is off, or <c>unfinished</c> for a recording
    /// whose file cannot finish. Otherwise it holds the path of an audio file that the fake recording copies. Without
    /// the file, there is no microphone.
    /// </remarks>
    /// <returns>
    /// <c>denied</c>, <c>none</c>, <c>unfinished</c> or a file path. Null on a normal run, where the real microphone
    /// records.
    /// </returns>
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
    /// With the fake server, makes a wait shorter for a test. <c>{data folder}\{name}.txt</c> holds the seconds, for
    /// names such as <c>download-timeout</c> or <c>full-size-wait</c>.
    /// </summary>
    /// <remarks>The hook reads the file each time the wait starts.</remarks>
    /// <param name="name">The file name of the wait, without <c>.txt</c>.</param>
    /// <returns>The shorter wait, or null on a normal run or without the file. Null means the real wait.</returns>
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
// The stub keeps the Debug signatures, so it does not use its parameters.
#pragma warning disable IDE0060

/// <summary>
/// Takes the place of the test hooks in Release. The hooks do not exist there, so Release ignores every flag.
/// </summary>
internal static class TestHooks
{
    /// <summary>Gets null in Release.</summary>
    public static Uri? EndpointBase => null;

    /// <summary>Gets null in Release.</summary>
    public static string? DataDir => null;

    /// <summary>Gets null in Release.</summary>
    public static string? Error => null;

    /// <summary>Gets null in Release, so the app uses the real servers.</summary>
    public static GmEndpoints? Endpoints => null;

    /// <summary>Gets false in Release, so link previews never fetch loopback addresses.</summary>
    public static bool AllowLoopbackPreviews => false;

    /// <summary>Gets false in Release, so a clicked link opens in the default browser.</summary>
    public static bool BlockLaunches => false;

    /// <summary>Returns false in Release, so the file opens in its default app.</summary>
    /// <param name="path">The full path of the file.</param>
    /// <param name="picker">True when the file opens with the app picker of Windows.</param>
    /// <returns>False.</returns>
    public static bool RecordLaunch(string path, bool picker = false) => false;

    /// <summary>Does nothing in Release.</summary>
    /// <param name="element">The element.</param>
    /// <param name="state">The name of the state.</param>
    public static void ShowState(Windows.UI.Xaml.DependencyObject element, string state)
    {
    }

    /// <summary>Does nothing in Release.</summary>
    /// <param name="name">The name of the file.</param>
    /// <param name="state">The state.</param>
    public static void NoteState(string name, string state)
    {
    }

    /// <summary>Returns null in Release, so the real file picker opens.</summary>
    /// <param name="photos">True for the photo picker.</param>
    /// <returns>Null.</returns>
    public static IReadOnlyList<string>? PickFiles(bool photos) => null;

    /// <summary>Returns null in Release, so a paste reads the real clipboard.</summary>
    /// <returns>Null.</returns>
    public static Task<Windows.ApplicationModel.DataTransfer.DataPackageView?> PasteContentAsync() =>
        Task.FromResult<Windows.ApplicationModel.DataTransfer.DataPackageView?>(null);

    /// <summary>Returns null in Release, so the real Save as dialog opens.</summary>
    /// <returns>Null.</returns>
    public static string? PickSaveFile() => null;

    /// <summary>Returns null in Release, so Download saves to the Downloads folder of the user.</summary>
    /// <returns>Null.</returns>
    public static string? DownloadsFolder() => null;

    /// <summary>Returns null in Release, so the real microphone records.</summary>
    /// <returns>Null.</returns>
    public static string? FakeMicrophone() => null;

    /// <summary>Returns null in Release, so each wait has its real length.</summary>
    /// <param name="name">The name of the wait.</param>
    /// <returns>Null.</returns>
    public static TimeSpan? ShortWait(string name) => null;
}

#pragma warning restore IDE0060
#endif

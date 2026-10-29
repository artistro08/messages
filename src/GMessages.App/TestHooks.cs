using GMessages.Presentation.Notifications;
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
    /// The toast buttons a probe asked for in <c>{DataDir}\toast-parts.txt</c>, read each time a toast is built, else null.
    /// </summary>
    /// <remarks>
    /// The file holds a <see cref="ToastParts"/> value such as <c>Reply, Snooze</c> or <c>None</c>. A missing,
    /// unreadable or unparsable file means null, so the toast keeps every button.
    /// </remarks>
    public static ToastParts? ToastPartsOverride
    {
        get
        {
            if (DataDir is null)
            {
                return null;
            }

            try
            {
                string text = File.ReadAllText(Path.Combine(DataDir, "toast-parts.txt")).Trim();
                return Enum.TryParse(text, ignoreCase: true, out ToastParts parts) ? parts : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
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

    /// <summary>Always null in Release: toasts keep every button.</summary>
    public static ToastParts? ToastPartsOverride => null;
}
#endif

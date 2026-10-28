using GMessages.Protocol.Http;

namespace GMessages.App;

#if EnableTestHooks
/// <summary>
/// UI test hooks: point the app at a local fake Google server and an isolated data folder.
/// </summary>
/// <remarks>
/// Compiled only when <c>EnableTestHooks</c> is defined, which is Debug builds only. Release builds get the stub
/// below that always answers null, so the flags are ignored. Flags: <c>--test-endpoints &lt;base url&gt;</c> and
/// <c>--test-data-dir &lt;path&gt;</c>. A flag with no value, or whose value starts with <c>--</c>, is rejected.
/// </remarks>
internal static class TestHooks
{
    private static readonly string[] CommandLine = Environment.GetCommandLineArgs();

    private static readonly (Uri? EndpointBase, string? DataDir) Parsed = GMessages.Presentation.TestHookArgs.Parse(CommandLine);

    /// <summary>
    /// The fake server base address from <c>--test-endpoints</c>, else null.
    /// </summary>
    public static Uri? EndpointBase { get; } = Parsed.EndpointBase;

    /// <summary>
    /// The data folder from <c>--test-data-dir</c>, else null.
    /// </summary>
    public static string? DataDir { get; } = Parsed.DataDir;

    /// <summary>
    /// True when a test flag was given with a missing or flag-like value. The app then runs as a normal (non-test) run.
    /// </summary>
    public static bool HasBadFlag { get; } =
        GMessages.Presentation.TestHookArgs.IsMalformed(CommandLine, "--test-data-dir")
        || GMessages.Presentation.TestHookArgs.IsMalformed(CommandLine, "--test-endpoints");

    /// <summary>
    /// Every server address pointed at <see cref="EndpointBase"/>, else null (use the real servers).
    /// </summary>
    /// <remarks>Authentication resolves to <c>{base}/web/authentication</c>.</remarks>
    public static GmEndpoints? Endpoints { get; } = Parsed.EndpointBase is { } b ? new GmEndpoints(b, b, b) : null;
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

    /// <summary>Always false in Release.</summary>
    public static bool HasBadFlag => false;

    /// <summary>Always null in Release: the app uses the real servers.</summary>
    public static GmEndpoints? Endpoints => null;
}
#endif

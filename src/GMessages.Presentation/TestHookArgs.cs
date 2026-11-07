namespace GMessages.Presentation;

/// <summary>
/// Reads and checks the UI-test command line flags <c>--test-endpoints</c> and <c>--test-data-dir</c>.
/// </summary>
/// <remarks>
/// The class only parses, so unit tests can cover it. The App calls it only in Debug builds with test hooks. Release
/// builds never read these flags. The flags come together or not at all. When either flag is present, both flags must
/// be valid, or the app refuses to start. Thus a half-set test run can never reach the production Google servers or
/// the real data folder.
/// </remarks>
public static class TestHookArgs
{
    private const string EndpointFlag = "--test-endpoints";
    private const string DataDirFlag  = "--test-data-dir";

    /// <summary>
    /// Checks the flags.
    /// </summary>
    /// <param name="args">The full command line. The first item can be the exe path.</param>
    /// <returns>
    /// For a normal run with no flag, all values are null. With both flags valid, the values are the fake server base
    /// address and the full data folder path. The address is an absolute http or https address on a loopback host. In
    /// any other case, <c>Error</c> holds a single-line reason, and the app must not start.
    /// </returns>
    public static (Uri? EndpointBase, string? DataDir, string? Error) Validate(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int endpointCount = Count(args, EndpointFlag);
        int dataDirCount  = Count(args, DataDirFlag);

        if (endpointCount == 0 && dataDirCount == 0)
        {
            return (null, null, null);
        }

        if (endpointCount != 1 || dataDirCount != 1)
        {
            return (null, null, $"{EndpointFlag} and {DataDirFlag} must each be given exactly once.");
        }

        string? endpoint = ValueOf(args, EndpointFlag);
        if (endpoint is null
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.IsLoopback)
        {
            return (null, null, $"{EndpointFlag} needs an absolute http or https address on this computer (loopback).");
        }

        string? dataDir = ValueOf(args, DataDirFlag);
        if (dataDir is null)
        {
            return (null, null, $"{DataDirFlag} needs a folder path.");
        }

        // A bad path, such as a path with invalid characters, gives a refusal, not a crash. GetFullPath alone accepts
        // some bad paths.
        if (dataDir.IndexOfAny([.. Path.GetInvalidPathChars(), '*', '?']) >= 0)
        {
            return (null, null, $"{DataDirFlag} is not a usable folder path.");
        }

        // A path that is too long also gives a refusal.
        try
        {
            return (uri, Path.GetFullPath(dataDir), null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, null, $"{DataDirFlag} is not a usable folder path.");
        }
    }

    private static int Count(string[] args, string flag) =>
        args.Count(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ValueOf(string[] args, string flag)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        string value = args[index + 1];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) ? null : value;
    }
}

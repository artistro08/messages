namespace GMessages.Presentation;

/// <summary>
/// Reads and checks the UI-test command line flags <c>--test-endpoints</c> and <c>--test-data-dir</c>.
/// </summary>
/// <remarks>
/// Pure parsing, so it can be unit tested. The App only calls it in Debug builds with test hooks; Release builds
/// never look at these flags. The two flags are all or nothing: if either is present, both must be valid, or the app
/// refuses to start. That way a half-set test run can never reach production Google or the real data folder.
/// </remarks>
public static class TestHookArgs
{
    private const string EndpointFlag = "--test-endpoints";
    private const string DataDirFlag  = "--test-data-dir";

    /// <summary>
    /// Checks the flags.
    /// </summary>
    /// <param name="args">The full command line (the first item may be the exe path).</param>
    /// <returns>
    /// Neither flag: all null (a normal run). Both valid: the fake server base address (absolute http or https) and the
    /// full data folder path. Anything else: <c>Error</c> holds a one-line reason and the app must not start.
    /// </returns>
    public static (Uri? EndpointBase, string? DataDir, string? Error) Validate(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var endpointCount = Count(args, EndpointFlag);
        var dataDirCount  = Count(args, DataDirFlag);

        // Normal Run
        if (endpointCount == 0 && dataDirCount == 0)
        {
            return (null, null, null);
        }

        // All Or Nothing, Each Exactly Once
        if (endpointCount != 1 || dataDirCount != 1)
        {
            return (null, null, $"{EndpointFlag} and {DataDirFlag} must each be given exactly once.");
        }

        // Endpoint
        var endpoint = ValueOf(args, EndpointFlag);
        if (endpoint is null
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return (null, null, $"{EndpointFlag} needs an absolute http or https address.");
        }

        // Data Folder
        var dataDir = ValueOf(args, DataDirFlag);
        if (dataDir is null)
        {
            return (null, null, $"{DataDirFlag} needs a folder path.");
        }

        // A bad path (invalid characters, too long) is a refusal, not a crash. GetFullPath alone lets some through
        if (dataDir.IndexOfAny([.. Path.GetInvalidPathChars(), '*', '?']) >= 0)
        {
            return (null, null, $"{DataDirFlag} is not a usable folder path.");
        }

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
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        var value = args[index + 1];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) ? null : value;
    }
}

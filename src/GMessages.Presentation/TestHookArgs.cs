namespace GMessages.Presentation;

/// <summary>
/// Reads the UI-test command line flags <c>--test-endpoints</c> and <c>--test-data-dir</c>.
/// </summary>
/// <remarks>
/// Pure parsing, so it can be unit tested. The App only calls it in Debug builds with test hooks; Release builds
/// never look at these flags. A flag with no value, or whose value starts with <c>--</c> (another flag), counts as not given.
/// </remarks>
public static class TestHookArgs
{
    /// <summary>
    /// Parses the flags.
    /// </summary>
    /// <param name="args">The full command line (the first item may be the exe path).</param>
    /// <returns>The fake server base address (http or https only) and the data folder; each is null when missing or bad.</returns>
    public static (Uri? EndpointBase, string? DataDir) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var endpoint = ValueOf(args, "--test-endpoints");
        Uri? endpointBase = null;
        if (endpoint is not null
            && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            endpointBase = uri;
        }

        return (endpointBase, ValueOf(args, "--test-data-dir"));
    }

    /// <summary>
    /// True when a flag is present but its value is missing or looks like another flag.
    /// </summary>
    /// <param name="args">The full command line.</param>
    /// <param name="flag">The flag name.</param>
    /// <returns>True for a malformed flag.</returns>
    public static bool IsMalformed(string[] args, string flag)
    {
        ArgumentNullException.ThrowIfNull(args);

        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && ValueOf(args, flag) is null;
    }

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

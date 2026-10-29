using System.Text;
using GMessages.Presentation.Settings;

namespace GMessages.Presentation.Pairing;

/// <summary>
/// The sign-in helper's command line: <c>--sign-in &lt;pipe&gt; --log-dir &lt;folder&gt; --theme &lt;theme&gt;</c>.
/// </summary>
/// <remarks>
/// The app starts its own exe in this mode for the Google sign-in (see the spec's "Sign-in helper"). The helper runs
/// outside the MSIX package, so it can't read the package's settings or data folder; the app passes the log folder and
/// theme instead. Debug builds add the test flags (<see cref="TestHookArgs"/>) after these.
/// </remarks>
public static class SignInHelperArgs
{
    /// <summary>The flag that starts the helper; its value is the pipe name.</summary>
    public const string SignInFlag = "--sign-in";

    private const string LogDirFlag = "--log-dir";
    private const string ThemeFlag  = "--theme";

    /// <summary>
    /// Builds the helper's arguments.
    /// </summary>
    /// <param name="pipeName">The pipe the helper writes to.</param>
    /// <param name="logsFolder">Where the helper writes its log.</param>
    /// <param name="theme">The app theme for the helper window.</param>
    /// <returns>The arguments, in order.</returns>
    public static string[] Build(string pipeName, string logsFolder, AppTheme theme) =>
        [SignInFlag, pipeName, LogDirFlag, logsFolder, ThemeFlag, theme.ToString()];

    /// <summary>
    /// Reads the helper flags.
    /// </summary>
    /// <param name="args">The full command line (the first item may be the exe path).</param>
    /// <returns>
    /// Null when <c>--sign-in</c> is absent (a normal launch). Otherwise the values; a missing log folder is null and an
    /// unknown theme is <see cref="AppTheme.System"/>, so a half-set command line still signs in.
    /// </returns>
    public static (string PipeName, string? LogsFolder, AppTheme Theme)? Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (ValueOf(args, SignInFlag) is not { } pipe)
        {
            return null;
        }

        var theme = Enum.TryParse(ValueOf(args, ThemeFlag), ignoreCase: true, out AppTheme parsed) ? parsed : AppTheme.System;
        return (pipe, ValueOf(args, LogDirFlag), theme);
    }

    /// <summary>
    /// Joins arguments into one Windows command line, quoted so the C runtime splits them back the same way.
    /// </summary>
    /// <remarks>
    /// The break-away launch goes through <c>CreateProcessW</c>, which takes a single string, so .NET's own quoting
    /// (<c>ProcessStartInfo.ArgumentList</c>) isn't available there.
    /// https://learn.microsoft.com/cpp/c-language/parsing-c-command-line-arguments
    /// </remarks>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The command line.</returns>
    public static string Join(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(Quote));
    }

    // Quotes When Needed; Backslashes Are Doubled Only Before A Quote
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var quoted      = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

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

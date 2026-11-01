namespace GMessages.Presentation.Logging;

/// <summary>
/// Turns an error into text that is safe to log.
/// </summary>
public static class ExceptionText
{
    /// <summary>
    /// An error's stack as <c>Type.Method(Parameter Types)</c> frames joined by <c> &lt; </c>, innermost first.
    /// </summary>
    /// <remarks>
    /// Method names only, never the message, source paths or line numbers. The message can hold content, so it is
    /// never logged.
    /// </remarks>
    /// <param name="error">The error, or null.</param>
    /// <returns>The frames, or <c>None</c> when the error has no managed stack.</returns>
    public static string StackFrames(Exception? error)
    {
        if (string.IsNullOrWhiteSpace(error?.StackTrace))
        {
            return "None";
        }

        IEnumerable<string> frames = error.StackTrace
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith("at ", StringComparison.Ordinal) ? line[3..] : line)
            .Select(line => line.IndexOf(" in ", StringComparison.Ordinal) is int at and >= 0 ? line[..at] : line);
        return string.Join(" < ", frames);
    }

    /// <summary>
    /// An error as its type, HRESULT and stack frames.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns>Text like <c>InvalidOperationException (HResult 0x80131509) at A.B() &lt; C.D()</c>.</returns>
    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return $"{error.GetType().Name} (HResult 0x{error.HResult:X8}) at {StackFrames(error)}";
    }
}

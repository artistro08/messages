namespace GMessages.Presentation.Logging;

/// <summary>
/// Changes an error to text that is safe to log.
/// </summary>
public static class ExceptionText
{
    /// <summary>
    /// Gives the stack of an error as <c>Type.Method(Parameter Types)</c> frames, innermost first. The text
    /// <c> &lt; </c> joins the frames.
    /// </summary>
    /// <remarks>
    /// The text holds only method names, never the message, the source paths or the line numbers. The message of an
    /// exception can hold message text, names or numbers, so the log never holds that message.
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
    /// Describes an error by its type, its HRESULT and its stack frames.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns>Text like <c>InvalidOperationException (HResult 0x80131509) at A.B() &lt; C.D()</c>.</returns>
    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return $"{error.GetType().Name} (HResult 0x{error.HResult:X8}) at {StackFrames(error)}";
    }
}

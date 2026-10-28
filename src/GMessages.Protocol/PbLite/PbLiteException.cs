namespace GMessages.Protocol.PbLite;

/// <summary>
/// Thrown when PBLite text cannot be read as, or written from, the requested protobuf message.
/// </summary>
/// <remarks>
/// Go's pblite returns plain errors here; this type carries the same information (the slot index and the full
/// protobuf field name) so callers can log which field of a server response was malformed, and it derives from
/// <see cref="FormatException"/> so generic format handling still catches it.
/// </remarks>
public sealed class PbLiteException : FormatException
{
    /// <summary>
    /// Creates the exception with a message that names the offending field.
    /// </summary>
    /// <param name="message">What went wrong, including the field name where there is one.</param>
    public PbLiteException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with a message and the lower-level error that caused it.
    /// </summary>
    /// <param name="message">What went wrong, including the field name where there is one.</param>
    /// <param name="innerException">The JSON, base64 or protobuf error underneath.</param>
    public PbLiteException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

namespace GMessages.Protocol.PbLite;

/// <summary>
/// The exception that is thrown when PBLite text cannot be read as, or written from, the requested protobuf message.
/// </summary>
/// <remarks>
/// The Go pblite package returns plain errors here. This type holds the same information: the slot index and the full
/// protobuf field name. Thus a caller can log the malformed field of a server response. The type derives from
/// <see cref="FormatException"/>, so a generic format handler also catches it.
/// </remarks>
public sealed class PbLiteException : FormatException
{
    /// <summary>
    /// Creates the exception with a message that names the field.
    /// </summary>
    /// <param name="message">The error, with the field name when there is one.</param>
    public PbLiteException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with a message and the lower-level error that caused it.
    /// </summary>
    /// <param name="message">The error, with the field name when there is one.</param>
    /// <param name="innerException">The JSON, base64 or protobuf error below this one.</param>
    public PbLiteException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

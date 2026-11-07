using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Http;

/// <summary>
/// The exception that is thrown when Google answers a request with a non-2xx status.
/// </summary>
/// <remarks>
/// Go has 2 error types here: a plain HTTP error, and a request error that also holds the decoded
/// <see cref="ErrorResponse"/>. This 1 class covers both. <see cref="Error"/> is null when the body does not decode
/// or has an empty message. The class keeps the raw body for diagnosis, but never puts it in the exception message.
/// Ported from <c>events.HTTPError</c> and <c>events.RequestError</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/events/ready.go">events/ready.go</see>.
/// </remarks>
/// <param name="statusCode">The HTTP status code.</param>
/// <param name="error">The decoded Google error, or null.</param>
/// <param name="body">The raw response body.</param>
public sealed class GmRequestException(int statusCode, ErrorResponse? error, byte[] body)
    : Exception(error is { Message.Length: > 0 } ? $"HTTP {statusCode}: {error.Message}" : $"HTTP {statusCode}")
{
    /// <summary>Gets the HTTP status code.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>Gets the decoded Google error, or null when the body is not a usable <see cref="ErrorResponse"/>.</summary>
    public ErrorResponse? Error { get; } = error;

    /// <summary>Gets the raw response body.</summary>
    public byte[] Body { get; } = body;
}

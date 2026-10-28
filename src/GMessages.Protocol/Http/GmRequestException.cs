using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Http;

/// <summary>
/// Thrown when Google answers a request with a non-2xx status.
/// </summary>
/// <remarks>
/// Go keeps two error types here: a plain HTTP error, and a request error that also carries Google's decoded
/// <see cref="ErrorResponse"/>. This one class covers both; <see cref="Error"/> is null when the body could not be
/// decoded or had an empty message. The raw body is kept for diagnosis, but it is never put in the exception message.
/// Ported from <c>events.HTTPError</c> and <c>events.RequestError</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/events/ready.go">events/ready.go</see>.
/// </remarks>
/// <param name="statusCode">The HTTP status code.</param>
/// <param name="error">Google's decoded error, or null.</param>
/// <param name="body">The raw response body.</param>
public class GmRequestException(int statusCode, ErrorResponse? error, byte[] body)
    : Exception(error is { Message.Length: > 0 } ? $"HTTP {statusCode}: {error.Message}" : $"HTTP {statusCode}")
{
    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>Google's decoded error, or null when the body was not a usable <see cref="ErrorResponse"/>.</summary>
    public ErrorResponse? Error { get; } = error;

    /// <summary>The raw response body.</summary>
    public byte[] Body { get; } = body;
}

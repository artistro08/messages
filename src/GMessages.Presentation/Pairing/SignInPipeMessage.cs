using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GMessages.Presentation.Pairing;

/// <summary>
/// The one message the sign-in helper sends the app over its named pipe: the captured Google cookies, or "canceled".
/// </summary>
/// <remarks>
/// <para>
/// Wire format: a 4-byte little-endian length, then that many bytes of UTF-8 JSON, either
/// <c>{"cookies":{"SID":"…",…}}</c> or <c>{"canceled":true}</c>. The JSON uses a source-generated context, so it is
/// Native AOT safe.
/// </para>
/// <para>
/// The app treats the helper as untrusted input: a message over <see cref="MaxBodyBytes"/>, malformed JSON, both or
/// neither field, an unknown cookie name or an empty value is refused with <see cref="InvalidDataException"/>.
/// Exceptions never carry cookie values.
/// </para>
/// </remarks>
public static class SignInPipeMessage
{
    /// <summary>The largest JSON body accepted (64 KB).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>The cookies pairing needs; no other name is accepted.</summary>
    public static readonly IReadOnlyList<string> CookieNames = ["SID", "HSID", "SSID", "OSID", "APISID", "SAPISID", "__Secure-1PSIDTS"];

    // =========================================================================
    // WRITE
    // =========================================================================

    /// <summary>
    /// Builds the length-prefixed message.
    /// </summary>
    /// <param name="cookies">The captured cookies, or null for "canceled".</param>
    /// <returns>The bytes to write to the pipe.</returns>
    public static byte[] Encode(IReadOnlyDictionary<string, string>? cookies)
    {
        // Body
        var payload = cookies is null
            ? new Payload { Canceled = true }
            : new Payload { Cookies = cookies.ToDictionary(StringComparer.Ordinal) };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, SignInPipeJsonContext.Default.Payload);

        // Length Prefix
        var message = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(message, body.Length);
        body.CopyTo(message, sizeof(int));
        return message;
    }

    // =========================================================================
    // READ
    // =========================================================================

    /// <summary>
    /// Reads and checks one message.
    /// </summary>
    /// <param name="stream">The pipe.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The cookies, or null when the helper sent "canceled".</returns>
    /// <exception cref="EndOfStreamException">The pipe closed before a whole message arrived.</exception>
    /// <exception cref="InvalidDataException">The message is too large or fails <see cref="Parse"/>.</exception>
    public static async Task<IReadOnlyDictionary<string, string>?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Length Prefix
        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxBodyBytes)
        {
            throw new InvalidDataException($"Sign-in message length {length} is outside 1..{MaxBodyBytes}.");
        }

        // Body
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return Parse(body);
    }

    /// <summary>
    /// Parses and checks a message body (the JSON after the length prefix).
    /// </summary>
    /// <param name="body">The UTF-8 JSON.</param>
    /// <returns>The cookies, or null for "canceled".</returns>
    /// <exception cref="InvalidDataException">
    /// Too large, malformed, unknown fields, both or neither of <c>cookies</c> and <c>canceled</c>, no cookies, an
    /// unknown cookie name, or an empty value.
    /// </exception>
    public static IReadOnlyDictionary<string, string>? Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            throw new InvalidDataException($"Sign-in message is over {MaxBodyBytes} bytes.");
        }

        // JSON
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, SignInPipeJsonContext.Default.Payload);
        }
        catch (JsonException)
        {
            // The Parser's Message Can Quote The Input, So It Isn't Passed On
            throw new InvalidDataException("Sign-in message is not valid JSON.");
        }

        // Canceled
        if (payload is { Canceled: true, Cookies: null })
        {
            return null;
        }

        // Cookies
        if (payload is not { Canceled: false, Cookies: { Count: > 0 } cookies })
        {
            throw new InvalidDataException("Sign-in message must hold either cookies or canceled.");
        }

        foreach ((string name, string value) in cookies)
        {
            if (!CookieNames.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidDataException("Sign-in message holds an unknown cookie name.");
            }

            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidDataException($"Sign-in cookie {name} is empty.");
            }
        }

        return cookies;
    }

    // =========================================================================
    // JSON
    // =========================================================================

    /// <summary>
    /// The JSON shape. Exactly one of the two fields is set.
    /// </summary>
    internal sealed class Payload
    {
        /// <summary>Gets or sets the captured cookies.</summary>
        [JsonPropertyName("cookies")]
        public Dictionary<string, string>? Cookies { get; set; }

        /// <summary>Gets or sets whether the user closed the sign-in window.</summary>
        [JsonPropertyName("canceled")]
        public bool Canceled { get; set; }
    }
}

/// <summary>
/// Source-generated JSON metadata for <see cref="SignInPipeMessage"/>, so no reflection is needed under Native AOT.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SignInPipeMessage.Payload))]
internal sealed partial class SignInPipeJsonContext : JsonSerializerContext;

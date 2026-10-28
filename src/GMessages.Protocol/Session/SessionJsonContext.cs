using System.Text.Json.Serialization;

namespace GMessages.Protocol.Session;

/// <summary>
/// The stored shape of <see cref="AuthData"/>. C#-only; it does not match Go's JSON.
/// </summary>
/// <remarks>
/// Byte arrays (keys, token, devices as binary protobuf) are base64 strings in JSON. A null <see cref="Cookies"/>
/// means "no cookie map", which is different from an empty one.
/// </remarks>
internal sealed class SessionFile
{
    public int Version { get; set; } = 1;

    public byte[]? AesKey { get; set; }

    public byte[]? HmacKey { get; set; }

    public byte[]? RefreshD { get; set; }

    public byte[]? RefreshX { get; set; }

    public byte[]? RefreshY { get; set; }

    public byte[]? Browser { get; set; }

    public byte[]? Mobile { get; set; }

    public byte[]? TachyonToken { get; set; }

    public DateTimeOffset TachyonExpiry { get; set; }

    public long TachyonTtlMicros { get; set; }

    public byte[]? WebEncryptionKey { get; set; }

    public Guid SessionId { get; set; }

    public Guid DestRegId { get; set; }

    public ulong DestPhoneId { get; set; }

    public Guid PairingId { get; set; }

    public Dictionary<string, string>? Cookies { get; set; }
}

/// <summary>
/// Source-generated JSON context for the session file, so saving and loading are AOT-safe.
/// </summary>
/// <remarks>No reflection-based serialization is used anywhere for session data.</remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionFile))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;

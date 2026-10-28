using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Proto;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace GMessages.Protocol.Pairing;

/// <summary>
/// One UKEY2 handshake with a phone: the client's P-256 key, the payloads sent, and the keys that come out of it.
/// </summary>
/// <remarks>
/// Ported from <c>PairingSession</c>, <c>NewPairingSession</c>, <c>PreparePayloads</c>, <c>doHKDF</c>,
/// <c>ProcessServerInit</c>, <c>encryptionKeyInfo</c> and the key derivation block of <c>FinishGaiaPairing</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go">pair_google.go</see>.
/// The client sends a commitment to its public key (client init), the phone answers with its own public key (server
/// init), and both derive the same auth key from the P-256 shared secret. The auth key picks the emoji the user must
/// tap on the phone; the next key becomes the request keys once the phone confirms. Keys, the shared secret and the
/// derivation inputs are secret and never logged.
/// </remarks>
public sealed class PairingSession
{
    // Copied Verbatim From encryptionKeyInfo
    private static readonly byte[] EncryptionKeyInfo = [130, 170, 85, 160, 211, 151, 248, 131, 70, 202, 28, 238, 141, 57, 9, 185, 95, 19, 250, 125, 235, 29, 74, 179, 131, 118, 184, 37, 109, 168, 85, 16];

    private readonly ECParameters _key;

    private PairingSession(Guid id, DateTimeOffset start, ECParameters key, PrimaryDevice destRegDevice)
    {
        Id            = id;
        Start         = start;
        _key          = key;
        DestRegDevice = destRegDevice;
    }

    /// <summary>Gets the pairing attempt ID (Go: <c>UUID</c>).</summary>
    public Guid Id { get; }

    /// <summary>Gets when the attempt started.</summary>
    public DateTimeOffset Start { get; }

    /// <summary>Gets the phone being paired.</summary>
    public PrimaryDevice DestRegDevice { get; }

    /// <summary>Gets the server init the phone sent, or null before <see cref="ProcessServerInit"/>.</summary>
    public GaiaPairingResponseContainer? ServerInit { get; private set; }

    /// <summary>Gets the client init message, or empty before <see cref="PreparePayloads"/>.</summary>
    /// <remarks>The setter is for golden tests, which put Go's payload (with Go's random bytes) in.</remarks>
    public byte[] InitPayload { get; internal set; } = [];

    /// <summary>Gets the client finished message, or empty before <see cref="PreparePayloads"/>.</summary>
    public byte[] FinishPayload { get; private set; } = [];

    /// <summary>Gets the UKEY2 next key, or empty before <see cref="ProcessServerInit"/>.</summary>
    public byte[] NextKey { get; private set; } = [];

    /// <summary>Gets the key derivation version the phone confirmed; 0 before <see cref="ProcessServerInit"/>.</summary>
    public int KeyDerivationVersion => ServerInit?.ConfirmedKeyDerivationVersion ?? 0;

    /// <summary>
    /// Starts a new attempt with a fresh random P-256 key.
    /// </summary>
    /// <remarks>Ported from <c>NewPairingSession</c>.</remarks>
    /// <param name="destRegDevice">The phone being paired.</param>
    /// <param name="time">The clock for the start time.</param>
    /// <returns>The session.</returns>
    public static PairingSession Create(PrimaryDevice destRegDevice, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new PairingSession(Guid.NewGuid(), time.GetUtcNow(), ecdh.ExportParameters(true), destRegDevice);
    }

    /// <summary>
    /// Builds a session with a fixed key, for golden tests.
    /// </summary>
    /// <remarks>Matches Go's <c>ecdh.P256().NewPrivateKey(scalar)</c>; the public key is computed from the scalar.</remarks>
    /// <param name="id">The pairing attempt ID.</param>
    /// <param name="start">The start time.</param>
    /// <param name="p256PrivateScalar">The 32-byte private scalar.</param>
    /// <param name="destRegDevice">The phone being paired.</param>
    /// <returns>The session.</returns>
    internal static PairingSession FromPrivateKey(Guid id, DateTimeOffset start, byte[] p256PrivateScalar, PrimaryDevice destRegDevice)
    {
        using var ecdh = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = p256PrivateScalar });
        return new PairingSession(id, start, ecdh.ExportParameters(true), destRegDevice);
    }

    /// <summary>
    /// Builds the client init and client finished messages and keeps them on the session.
    /// </summary>
    /// <remarks>
    /// Ported from <c>PreparePayloads</c>. The finished message carries the public key, with X and Y each given a
    /// leading zero byte as Go does. The init message commits to it with SHA-512 and carries 32 random bytes, so it
    /// differs every time; the finished message does not.
    /// </remarks>
    /// <returns>The client init and client finished messages.</returns>
    public (byte[] Init, byte[] Finish) PreparePayloads()
    {
        // Client Finished: The Public Key
        var finished = new Ukey2ClientFinished
        {
            PublicKey = new GenericPublicKey
            {
                Type            = PublicKeyType.EcP256,
                EcP256PublicKey = new EcP256PublicKey
                {
                    X = ByteString.CopyFrom([0, .. _key.Q.X!]),
                    Y = ByteString.CopyFrom([0, .. _key.Q.Y!]),
                },
            },
        };
        byte[] finish = new Ukey2Message { MessageType = Ukey2Message.Types.Type.ClientFinish, MessageData = finished.ToByteString() }.ToByteArray();

        // Client Init: Commitment To The Finished Message
        var client_init = new Ukey2ClientInit
        {
            Version      = 1,
            Random       = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            NextProtocol = "AES_256_CBC-HMAC_SHA256",
        };
        client_init.CipherCommitments.Add(new Ukey2ClientInit.Types.CipherCommitment
        {
            HandshakeCipher = Ukey2HandshakeCipher.P256Sha512,
            Commitment      = ByteString.CopyFrom(SHA512.HashData(finish)),
        });
        byte[] init = new Ukey2Message { MessageType = Ukey2Message.Types.Type.ClientInit, MessageData = client_init.ToByteString() }.ToByteArray();

        FinishPayload = finish;
        InitPayload   = init;
        return (init, finish);
    }

    /// <summary>
    /// Checks the phone's server init, derives the shared keys and returns the emoji the user must tap.
    /// </summary>
    /// <remarks>
    /// Ported from <c>ProcessServerInit</c>. The server init must be version 1, P-256/SHA-512, with 32 random bytes and
    /// a valid P-256 point (a 33-byte coordinate must start with a zero byte). The shared secret is SHA-256 of the raw
    /// ECDH X coordinate; HKDF-SHA256 over it, with the init and server init messages as info, gives the auth key
    /// ("UKEY2 v1 auth") and the next key ("UKEY2 v1 next"). The first four auth key bytes pick the emoji from the
    /// confirmed verification code version's list. As in Go, the server init is kept before any check, and the next
    /// key is set before the version check.
    /// </remarks>
    /// <param name="msg">The phone's server init container.</param>
    /// <returns>The emoji.</returns>
    /// <exception cref="InvalidDataException">The server init is invalid (Go's error text).</exception>
    public string ProcessServerInit(GaiaPairingResponseContainer msg)
    {
        ArgumentNullException.ThrowIfNull(msg);

        ServerInit = msg;

        // Unwrap And Check The Server Init
        Ukey2Message message = Parse(Ukey2Message.Parser, msg.Data, "failed to unmarshal server init message");
        if (message.MessageType != Ukey2Message.Types.Type.ServerInit)
        {
            throw new InvalidDataException($"unexpected message type: {EnumName(Ukey2Message.Descriptor.EnumTypes[0], (int)message.MessageType)}");
        }

        Ukey2ServerInit server_init = Parse(Ukey2ServerInit.Parser, message.MessageData, "failed to unmarshal server init payload");
        if (server_init.Version != 1)
        {
            throw new InvalidDataException($"unexpected server init version: {server_init.Version}");
        }

        if (server_init.HandshakeCipher != Ukey2HandshakeCipher.P256Sha512)
        {
            throw new InvalidDataException($"unexpected handshake cipher: {EnumName(UkeyReflection.Descriptor.EnumTypes.First(e => e.Name == "Ukey2HandshakeCipher"), (int)server_init.HandshakeCipher)}");
        }

        if (server_init.Random.Length != 32)
        {
            throw new InvalidDataException($"unexpected random length {server_init.Random.Length}");
        }

        // Server Public Key
        byte[] x = StripPrefix(server_init.PublicKey?.EcP256PublicKey?.X, "x");
        byte[] y = StripPrefix(server_init.PublicKey?.EcP256PublicKey?.Y, "y");
        if (x.Length != 32 || y.Length != 32)
        {
            throw new InvalidDataException($"invalid server key coordinate lengths: {x.Length}, {y.Length}");
        }

        // Shared Secret: SHA-256 Of The Raw ECDH X Coordinate
        byte[] shared_secret;
        try
        {
            using var server = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
            using var client = ECDiffieHellman.Create(_key);
            using ECDiffieHellmanPublicKey server_public = server.PublicKey;
            shared_secret = SHA256.HashData(client.DeriveRawSecretAgreement(server_public));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"invalid server key: {ex.Message}", ex);
        }

        // Auth And Next Keys
        byte[] auth_info = [.. InitPayload, .. msg.Data.Span];
        byte[] auth_key  = Hkdf(shared_secret, "UKEY2 v1 auth"u8.ToArray(), auth_info);
        NextKey          = Hkdf(shared_secret, "UKEY2 v1 next"u8.ToArray(), auth_info);

        return PairingEmojis.Pick(BinaryPrimitives.ReadUInt32BigEndian(auth_key), msg.ConfirmedVerificationCodeVersion);
    }

    /// <summary>
    /// Derives the request encryption keys from the next key.
    /// </summary>
    /// <remarks>
    /// Ported from the key derivation block of <c>FinishGaiaPairing</c>. The client and server keys are HKDF of the
    /// next key with <c>encryptionKeyInfo</c> as salt and "client" / "server" as info. Version 0 uses them directly
    /// (AES = client, HMAC = server). Version 1 hashes <c>encryptionKeyInfo</c> plus both keys, the one with the
    /// smaller Java hash first, and derives AES and HMAC from that hash with the "Ditto" salts.
    /// </remarks>
    /// <returns>The AES and HMAC keys.</returns>
    /// <exception cref="InvalidOperationException"><see cref="ProcessServerInit"/> has not run.</exception>
    /// <exception cref="InvalidDataException">The key derivation version is not 0 or 1 (Go's error text).</exception>
    public (byte[] AesKey, byte[] HmacKey) DeriveRequestKeys()
    {
        if (ServerInit is null)
        {
            throw new InvalidOperationException("The server init has not been processed.");
        }

        byte[] client_key = Hkdf(NextKey, EncryptionKeyInfo, "client"u8.ToArray());
        byte[] server_key = Hkdf(NextKey, EncryptionKeyInfo, "server"u8.ToArray());
        switch (KeyDerivationVersion)
        {
            case 0:
                return (client_key, server_key);

            case 1:
                bool client_first   = JavaHash.ByteHash(client_key) < JavaHash.ByteHash(server_key);
                byte[] concatenated = client_first ? [.. EncryptionKeyInfo, .. client_key, .. server_key] : [.. EncryptionKeyInfo, .. server_key, .. client_key];
                byte[] hash         = SHA256.HashData(concatenated);
                return (Hkdf(hash, "Ditto salt 1"u8.ToArray(), "Ditto info 1"u8.ToArray()), Hkdf(hash, "Ditto salt 2"u8.ToArray(), "Ditto info 2"u8.ToArray()));

            default:
                throw new InvalidDataException($"unsupported key derivation version {KeyDerivationVersion}");
        }
    }

    // Port Of doHKDF: HKDF-SHA256, 32 Bytes Out
    private static byte[] Hkdf(byte[] key, byte[] salt, byte[] info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, info);

    // A 33-Byte Coordinate Must Start With 0x00, Which Is Dropped
    private static byte[] StripPrefix(ByteString? coordinate, string name)
    {
        byte[] bytes = coordinate?.ToByteArray() ?? [];
        if (bytes.Length != 33)
        {
            return bytes;
        }

        if (bytes[0] != 0)
        {
            throw new InvalidDataException($"server key {name} coordinate has unexpected prefix: {bytes[0]}");
        }

        return bytes[1..];
    }

    // Go's %v Of A Proto Enum: The Name, Or The Number When Unknown
    private static string EnumName(EnumDescriptor descriptor, int value) =>
        descriptor.FindValueByNumber(value)?.Name ?? value.ToString(CultureInfo.InvariantCulture);

    private static T Parse<T>(MessageParser<T> parser, ByteString data, string what) where T : IMessage<T>
    {
        try
        {
            return parser.ParseFrom(data);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException($"{what}: {ex.Message}", ex);
        }
    }
}

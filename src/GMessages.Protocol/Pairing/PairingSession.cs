using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Proto;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace GMessages.Protocol.Pairing;

/// <summary>
/// 1 UKEY2 handshake with a phone: the P-256 key of the client, the sent payloads and the keys that result.
/// </summary>
/// <remarks>
/// Ported from <c>PairingSession</c>, <c>NewPairingSession</c>, <c>PreparePayloads</c>, <c>doHKDF</c>,
/// <c>ProcessServerInit</c> and <c>encryptionKeyInfo</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go">pair_google.go</see>.
/// The key derivation block of <c>FinishGaiaPairing</c> is also a port.
/// The client init carries a commitment to the public key of the client. The phone answers with its own public key
/// in the server init. Both sides derive the same auth key from the P-256 shared secret. The auth key picks the emoji
/// that the user must tap on the phone. After the phone confirms, the next key gives the request keys. The keys, the
/// shared secret and the derivation inputs are secret. The log never gets them.
/// </remarks>
public sealed class PairingSession
{
    // A verbatim copy of encryptionKeyInfo.
    private static readonly byte[] EncryptionKeyInfo = [130, 170, 85, 160, 211, 151, 248, 131, 70, 202, 28, 238, 141, 57, 9, 185, 95, 19, 250, 125, 235, 29, 74, 179, 131, 118, 184, 37, 109, 168, 85, 16];

    private readonly ECParameters _key;

    private PairingSession(Guid id, DateTimeOffset start, ECParameters key, PrimaryDevice destRegDevice)
    {
        Id            = id;
        Start         = start;
        _key          = key;
        DestRegDevice = destRegDevice;
    }

    /// <summary>Gets the pairing attempt ID. Go calls it <c>UUID</c>.</summary>
    public Guid Id { get; }

    /// <summary>Gets when the attempt started.</summary>
    public DateTimeOffset Start { get; }

    /// <summary>Gets the phone that the session pairs with.</summary>
    public PrimaryDevice DestRegDevice { get; }

    /// <summary>Gets the server init from the phone, or null before <see cref="ProcessServerInit"/>.</summary>
    public GaiaPairingResponseContainer? ServerInit { get; private set; }

    /// <summary>Gets the client init message, or empty before <see cref="PreparePayloads"/>.</summary>
    /// <remarks>The setter is for golden tests. They put in the payload of Go, with the random bytes of Go.</remarks>
    public byte[] InitPayload { get; internal set; } = [];

    /// <summary>Gets the client finished message, or empty before <see cref="PreparePayloads"/>.</summary>
    public byte[] FinishPayload { get; private set; } = [];

    /// <summary>Gets the UKEY2 next key, or empty before <see cref="ProcessServerInit"/>.</summary>
    public byte[] NextKey { get; private set; } = [];

    /// <summary>Gets the key derivation version that the phone confirmed, or 0 before <see cref="ProcessServerInit"/>.</summary>
    public int KeyDerivationVersion => ServerInit?.ConfirmedKeyDerivationVersion ?? 0;

    /// <summary>
    /// Starts a new attempt with a fresh random P-256 key.
    /// </summary>
    /// <remarks>Ported from <c>NewPairingSession</c>.</remarks>
    /// <param name="destRegDevice">The phone to pair with.</param>
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
    /// <remarks>Matches <c>ecdh.P256().NewPrivateKey(scalar)</c> in Go. The public key comes from the scalar.</remarks>
    /// <param name="id">The pairing attempt ID.</param>
    /// <param name="start">The start time.</param>
    /// <param name="p256PrivateScalar">The 32-byte private scalar.</param>
    /// <param name="destRegDevice">The phone to pair with.</param>
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
    /// Ported from <c>PreparePayloads</c>. The finished message carries the public key. As in Go, X and Y each get a
    /// leading zero byte. The init message commits to the finished message with SHA-512 and carries 32 random bytes.
    /// Thus the init message is different each time, but the finished message is not.
    /// </remarks>
    /// <returns>The client init and client finished messages.</returns>
    public (byte[] Init, byte[] Finish) PreparePayloads()
    {
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

        var clientInit = new Ukey2ClientInit
        {
            Version      = 1,
            Random       = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            NextProtocol = "AES_256_CBC-HMAC_SHA256",
        };
        clientInit.CipherCommitments.Add(new Ukey2ClientInit.Types.CipherCommitment
        {
            HandshakeCipher = Ukey2HandshakeCipher.P256Sha512,
            Commitment      = ByteString.CopyFrom(SHA512.HashData(finish)),
        });
        byte[] init = new Ukey2Message { MessageType = Ukey2Message.Types.Type.ClientInit, MessageData = clientInit.ToByteString() }.ToByteArray();

        FinishPayload = finish;
        InitPayload   = init;
        return (init, finish);
    }

    /// <summary>
    /// Checks the server init of the phone, derives the shared keys and returns the emoji that the user must tap.
    /// </summary>
    /// <remarks>
    /// Ported from <c>ProcessServerInit</c>. The server init must be version 1 and P-256/SHA-512, with 32 random bytes
    /// and a valid P-256 point. A 33-byte coordinate must start with a zero byte. The shared secret is the SHA-256 of
    /// the raw ECDH X coordinate. HKDF-SHA256 of the secret, with the init and server init messages as info, gives 2
    /// keys. The salt "UKEY2 v1 auth" gives the auth key, and the salt "UKEY2 v1 next" gives the next key. The first 4
    /// bytes of the auth key pick the emoji from the list of the confirmed verification code version. As in Go, the
    /// method keeps the server init before any check. It sets the next key before the version check.
    /// </remarks>
    /// <param name="msg">The phone's server init container.</param>
    /// <returns>The emoji.</returns>
    /// <exception cref="InvalidDataException">The server init is invalid (Go's error text).</exception>
    public string ProcessServerInit(GaiaPairingResponseContainer msg)
    {
        ArgumentNullException.ThrowIfNull(msg);

        ServerInit = msg;

        Ukey2Message message = Parse(Ukey2Message.Parser, msg.Data, "failed to unmarshal server init message");
        if (message.MessageType != Ukey2Message.Types.Type.ServerInit)
        {
            throw new InvalidDataException($"unexpected message type: {EnumName(Ukey2Message.Descriptor.EnumTypes[0], (int)message.MessageType)}");
        }

        Ukey2ServerInit serverInit = Parse(Ukey2ServerInit.Parser, message.MessageData, "failed to unmarshal server init payload");
        if (serverInit.Version != 1)
        {
            throw new InvalidDataException($"unexpected server init version: {serverInit.Version}");
        }

        if (serverInit.HandshakeCipher != Ukey2HandshakeCipher.P256Sha512)
        {
            throw new InvalidDataException($"unexpected handshake cipher: {EnumName(UkeyReflection.Descriptor.EnumTypes.First(e => e.Name == "Ukey2HandshakeCipher"), (int)serverInit.HandshakeCipher)}");
        }

        if (serverInit.Random.Length != 32)
        {
            throw new InvalidDataException($"unexpected random length {serverInit.Random.Length}");
        }

        byte[] x = StripPrefix(serverInit.PublicKey?.EcP256PublicKey?.X, "x");
        byte[] y = StripPrefix(serverInit.PublicKey?.EcP256PublicKey?.Y, "y");
        if (x.Length != 32 || y.Length != 32)
        {
            throw new InvalidDataException($"invalid server key coordinate lengths: {x.Length}, {y.Length}");
        }

        byte[] sharedSecret;
        try
        {
            using var server = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
            using var client = ECDiffieHellman.Create(_key);
            using ECDiffieHellmanPublicKey serverPublic = server.PublicKey;
            sharedSecret = SHA256.HashData(client.DeriveRawSecretAgreement(serverPublic));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"invalid server key: {ex.Message}", ex);
        }

        byte[] authInfo  = [.. InitPayload, .. msg.Data.Span];
        byte[] authKey   = Hkdf(sharedSecret, "UKEY2 v1 auth"u8.ToArray(), authInfo);
        NextKey          = Hkdf(sharedSecret, "UKEY2 v1 next"u8.ToArray(), authInfo);

        return PairingEmojis.Pick(BinaryPrimitives.ReadUInt32BigEndian(authKey), msg.ConfirmedVerificationCodeVersion);
    }

    /// <summary>
    /// Derives the request encryption keys from the next key.
    /// </summary>
    /// <remarks>
    /// Ported from the key derivation block of <c>FinishGaiaPairing</c>. The client key and the server key are HKDF of
    /// the next key, with <c>encryptionKeyInfo</c> as salt. The info is "client" or "server". Version 0 uses the client
    /// key as the AES key and the server key as the HMAC key. Version 1 hashes <c>encryptionKeyInfo</c> and both keys,
    /// with the key of the smaller Java hash first. It then derives the AES key and the HMAC key from that hash with the
    /// "Ditto" salts.
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

        byte[] clientKey = Hkdf(NextKey, EncryptionKeyInfo, "client"u8.ToArray());
        byte[] serverKey = Hkdf(NextKey, EncryptionKeyInfo, "server"u8.ToArray());
        switch (KeyDerivationVersion)
        {
            case 0:
                return (clientKey, serverKey);

            case 1:
                bool clientFirst    = JavaHash.ByteHash(clientKey) < JavaHash.ByteHash(serverKey);
                byte[] concatenated = clientFirst ? [.. EncryptionKeyInfo, .. clientKey, .. serverKey] : [.. EncryptionKeyInfo, .. serverKey, .. clientKey];
                byte[] hash         = SHA256.HashData(concatenated);
                return (Hkdf(hash, "Ditto salt 1"u8.ToArray(), "Ditto info 1"u8.ToArray()), Hkdf(hash, "Ditto salt 2"u8.ToArray(), "Ditto info 2"u8.ToArray()));

            default:
                throw new InvalidDataException($"unsupported key derivation version {KeyDerivationVersion}");
        }
    }

    // Port of doHKDF: HKDF-SHA256 with 32 output bytes.
    private static byte[] Hkdf(byte[] key, byte[] salt, byte[] info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, info);

    // A 33-byte coordinate must start with 0x00. The method removes that byte.
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

    // Gives the %v format of Go for a proto enum: the name, or the number for an unknown value.
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

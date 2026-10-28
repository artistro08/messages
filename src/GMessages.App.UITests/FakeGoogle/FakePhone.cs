using System.Buffers.Binary;
using System.Security.Cryptography;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.Proto;
using Google.Protobuf;

namespace GMessages.App.UITests.FakeGoogle;

/// <summary>
/// A fake phone behind <see cref="FakeGoogleServer"/>: the server side of the UKEY2 pairing, then encrypted answers.
/// </summary>
/// <remarks>
/// Mirrors what a real phone does in mautrix-gmessages' pair_google.go, seen from the other end. The client init's
/// commitment is kept; the answer is a server init with the phone's own fresh P-256 key, 32 random bytes and
/// verification and key-derivation versions 1/1. The client finished message is checked against the commitment, and
/// the shared secret is SHA-256 of the raw ECDH X coordinate of the phone's key with the client's public key. HKDF over
/// it (with the client and server init messages as info) gives the auth key, whose first four bytes pick
/// <see cref="ShownEmoji"/>, and the next key, which gives the request keys. The finish answer waits for
/// <see cref="Tap"/>. Answers go onto the open long poll as data events whose session ID is the request ID, which is
/// how the client matches them. Once paired, <c>NOTIFY_DITTO_ACTIVITY</c> pings are answered (encrypted), as a real
/// phone does, so the app never reports the phone as not responding. Unknown actions are only acked by the HTTP
/// reply; the phone sends nothing back.
/// Plan 3 extends it with conversations and messages.
/// </remarks>
public sealed class FakePhone
{
    // Copied Verbatim From encryptionKeyInfo In pair_google.go
    private static readonly byte[] EncryptionKeyInfo = [130, 170, 85, 160, 211, 151, 248, 131, 70, 202, 28, 238, 141, 57, 9, 185, 95, 19, 250, 125, 235, 29, 74, 179, 131, 118, 184, 37, 109, 168, 85, 16];

    private readonly Lock _lock = new();
    private readonly Action<IncomingRPCMessage> _push;
    private TaskCompletionSource _finishReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ECParameters _key;
    private byte[] _clientInit = [];
    private byte[] _serverInit = [];
    private byte[] _commitment = [];
    private byte[] _nextKey    = [];
    private string? _heldFinish;

    /// <summary>
    /// Creates a phone that sends its answers through <paramref name="push"/>.
    /// </summary>
    /// <param name="push">Puts a message on the open long poll.</param>
    internal FakePhone(Action<IncomingRPCMessage> push) => _push = push;

    /// <summary>
    /// Gets the emoji the phone asks the user to tap, or null before the current attempt's client finished message.
    /// </summary>
    public string? ShownEmoji { get; private set; }

    /// <summary>
    /// Gets a task that completes once the current attempt's client finished message arrived and
    /// <see cref="ShownEmoji"/> is set.
    /// </summary>
    /// <remarks>Each client init starts a new attempt with a new task, so a retried pairing can be awaited too.</remarks>
    public Task FinishReceived
    {
        get
        {
            lock (_lock)
            {
                return _finishReceived.Task;
            }
        }
    }

    /// <summary>
    /// Gets the request keys after a correct <see cref="Tap"/>, or null before.
    /// </summary>
    /// <remarks>The same AES and HMAC keys the client derives, so later answers can be encrypted for it.</remarks>
    public AesCtrHelper? RequestCrypto { get; private set; }

    /// <summary>
    /// Answers the held client finished message as if the user tapped an emoji on the phone.
    /// </summary>
    /// <remarks>
    /// A correct tap derives the request keys and answers with success. A wrong one answers with
    /// <c>WRONG_VERIFICATION_CODE_SELECTED</c>.
    /// </remarks>
    /// <param name="correct">True when the user tapped the emoji the computer shows.</param>
    /// <exception cref="InvalidOperationException">No client finished message is waiting.</exception>
    public void Tap(bool correct)
    {
        string request_id;
        lock (_lock)
        {
            request_id  = _heldFinish ?? throw new InvalidOperationException("No pairing is waiting for a tap.");
            _heldFinish = null;
            if (correct)
            {
                RequestCrypto = DeriveRequestKeys(_nextKey);
            }
        }

        // Pairing Answers Are Never Encrypted
        GaiaPairingResponseContainer answer = correct
            ? new GaiaPairingResponseContainer()
            : new GaiaPairingResponseContainer { FinishErrorType = 1, FinishErrorCode = GaiaPairingErrorCode.WrongVerificationCodeSelected };
        Reply(request_id, ActionType.CreateGaiaPairingClientFinished, answer, encrypt: false);
    }

    /// <summary>
    /// Handles one request the client sent through <c>SendMessage</c>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <exception cref="InvalidDataException">A pairing message is invalid.</exception>
    internal void Handle(OutgoingRPCMessage request)
    {
        OutgoingRPCData data = OutgoingRPCData.Parser.ParseFrom(request.Data.MessageData);
        switch (data.Action)
        {
            case ActionType.CreateGaiaPairingClientInit:
                Reply(data.RequestID, data.Action, OnClientInit(data), encrypt: false);
                break;

            case ActionType.CreateGaiaPairingClientFinished:
                OnClientFinished(data);
                break;

            // Ping Answers Keep The Client From Reporting The Phone As Not Responding
            case ActionType.NotifyDittoActivity when RequestCrypto is not null:
                Reply(data.RequestID, data.Action, new NotifyDittoActivityResponse(), encrypt: true);
                break;

            default:
                break;
        }
    }

    // =========================================================================
    // UKEY2 SERVER SIDE
    // =========================================================================

    // Keeps The Client Init And Its Commitment, Answers With A Server Init Using A Fresh Key
    private GaiaPairingResponseContainer OnClientInit(OutgoingRPCData data)
    {
        byte[] client_init     = GaiaPairingRequestContainer.Parser.ParseFrom(data.UnencryptedProtoData).Data.ToByteArray();
        Ukey2ClientInit parsed = Ukey2ClientInit.Parser.ParseFrom(Ukey2Message.Parser.ParseFrom(client_init).MessageData);
        byte[] commitment      = parsed.CipherCommitments.FirstOrDefault(c => c.HandshakeCipher == Ukey2HandshakeCipher.P256Sha512)?.Commitment.ToByteArray()
            ?? throw new InvalidDataException("No P-256/SHA-512 commitment in the client init.");

        // Server Init: Own Key, X/Y With A Leading Zero Byte Like Go
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters key = ecdh.ExportParameters(true);
        var server_init  = new Ukey2ServerInit
        {
            Version         = 1,
            Random          = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            HandshakeCipher = Ukey2HandshakeCipher.P256Sha512,
            PublicKey       = new GenericPublicKey
            {
                Type            = PublicKeyType.EcP256,
                EcP256PublicKey = new EcP256PublicKey { X = ByteString.CopyFrom([0, .. key.Q.X!]), Y = ByteString.CopyFrom([0, .. key.Q.Y!]) },
            },
        };
        byte[] message = new Ukey2Message { MessageType = Ukey2Message.Types.Type.ServerInit, MessageData = server_init.ToByteString() }.ToByteArray();

        // A New Attempt Forgets The Last One's Emoji, Held Answer And Signal
        lock (_lock)
        {
            _key        = key;
            _clientInit = client_init;
            _serverInit = message;
            _commitment = commitment;
            _heldFinish = null;
            ShownEmoji  = null;
            if (_finishReceived.Task.IsCompleted)
            {
                _finishReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new GaiaPairingResponseContainer
        {
            Data                             = ByteString.CopyFrom(message),
            ConfirmedVerificationCodeVersion = 1,
            ConfirmedKeyDerivationVersion    = 1,
        };
    }

    // Checks The Commitment, Derives The Auth And Next Keys, Picks The Emoji, Holds The Answer For Tap
    private void OnClientFinished(OutgoingRPCData data)
    {
        byte[] finish = GaiaPairingRequestContainer.Parser.ParseFrom(data.UnencryptedProtoData).Data.ToByteArray();
        lock (_lock)
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(finish), _commitment))
            {
                throw new InvalidDataException("The client finished message does not match the client init's commitment.");
            }

            // Client Public Key
            EcP256PublicKey client = Ukey2ClientFinished.Parser.ParseFrom(Ukey2Message.Parser.ParseFrom(finish).MessageData).PublicKey.EcP256PublicKey;
            var client_point       = new ECPoint { X = StripPrefix(client.X), Y = StripPrefix(client.Y) };

            // Shared Secret: SHA-256 Of The Raw ECDH X Coordinate, From The Phone's Own Key
            byte[] shared_secret;
            using (var server = ECDiffieHellman.Create(_key))
            using (var peer = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = client_point }))
            using (ECDiffieHellmanPublicKey peer_public = peer.PublicKey)
            {
                shared_secret = SHA256.HashData(server.DeriveRawSecretAgreement(peer_public));
            }

            // Auth Key Picks The Emoji, Next Key Waits For The Tap
            byte[] info     = [.. _clientInit, .. _serverInit];
            byte[] auth_key = Hkdf(shared_secret, "UKEY2 v1 auth"u8.ToArray(), info);
            _nextKey        = Hkdf(shared_secret, "UKEY2 v1 next"u8.ToArray(), info);
            ShownEmoji      = PairingEmojis.Pick(BinaryPrimitives.ReadUInt32BigEndian(auth_key), 1);
            _heldFinish     = data.RequestID;

            // Continuations Run Asynchronously, So Signaling Inside The Lock Is Safe
            _finishReceived.TrySetResult();
        }
    }

    // Key Derivation Version 1: Order Client And Server Keys By Java Hash, Then The "Ditto" HKDFs
    private static AesCtrHelper DeriveRequestKeys(byte[] nextKey)
    {
        byte[] client_key   = Hkdf(nextKey, EncryptionKeyInfo, "client"u8.ToArray());
        byte[] server_key   = Hkdf(nextKey, EncryptionKeyInfo, "server"u8.ToArray());
        bool client_first   = JavaHash.ByteHash(client_key) < JavaHash.ByteHash(server_key);
        byte[] concatenated = client_first ? [.. EncryptionKeyInfo, .. client_key, .. server_key] : [.. EncryptionKeyInfo, .. server_key, .. client_key];
        byte[] hash         = SHA256.HashData(concatenated);
        return new AesCtrHelper(Hkdf(hash, "Ditto salt 1"u8.ToArray(), "Ditto info 1"u8.ToArray()), Hkdf(hash, "Ditto salt 2"u8.ToArray(), "Ditto info 2"u8.ToArray()));
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // An Answer Routed To The Request: Data Event, Session ID = Request ID
    private void Reply(string requestId, ActionType action, IMessage answer, bool encrypt)
    {
        byte[] bytes = answer.ToByteArray();
        var data     = new RPCMessageData { SessionID = requestId, Action = action, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 };
        if (encrypt)
        {
            data.EncryptedData = ByteString.CopyFrom(RequestCrypto!.Encrypt(bytes));
        }
        else
        {
            data.UnencryptedData = ByteString.CopyFrom(bytes);
        }

        _push(new IncomingRPCMessage
        {
            ResponseID  = Guid.NewGuid().ToString(),
            BugleRoute  = BugleRoute.DataEvent,
            MessageType = MessageType.BugleMessage,
            MessageData = data.ToByteString(),
        });
    }

    // HKDF-SHA256, 32 Bytes Out (Go: doHKDF)
    private static byte[] Hkdf(byte[] key, byte[] salt, byte[] info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, info);

    // Go Sends Each Coordinate With A Leading Zero Byte
    private static byte[] StripPrefix(ByteString coordinate) => coordinate.Length == 33 ? coordinate.ToByteArray()[1..] : coordinate.ToByteArray();
}

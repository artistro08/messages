using GMessages.Protocol.Crypto;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Session;

/// <summary>
/// The login and session state of one paired account: keys, device identities, the Tachyon token and the cookies.
/// </summary>
/// <remarks>
/// Ports <c>AuthData</c> from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// An account with a non-empty <see cref="DestRegId"/> is a Google account, paired by a sign-in. Any other account
/// is a QR-paired session with no cookies. Where Go stores <c>uuid.UUID</c> bytes, this class uses
/// <see cref="Guid"/>. The saved file uses RFC 4122 byte order. All of this data is secret and must never go in a log.
/// </remarks>
public sealed class AuthData
{
    private TachyonToken _tachyon = TachyonToken.None;

    /// <summary>
    /// Gets or sets the keys that encrypt the communication with the phone.
    /// </summary>
    public required AesCtrHelper RequestCrypto { get; set; }

    /// <summary>
    /// Gets or sets the key that signs the requests that refresh the Tachyon token.
    /// </summary>
    public required RefreshKey RefreshKey { get; set; }

    /// <summary>
    /// Gets or sets the browser identity of the pairing, or null when not paired.
    /// </summary>
    public Device? Browser { get; set; }

    /// <summary>
    /// Gets or sets the phone identity of the pairing, or null when not paired.
    /// </summary>
    public Device? Mobile { get; set; }

    /// <summary>
    /// Gets the token that authenticates with the server, or null when not paired.
    /// </summary>
    /// <remarks>Part of <see cref="Tachyon"/>.</remarks>
    public byte[]? TachyonAuthToken
    {
        get => Tachyon.Token;
        internal set => Tachyon = Tachyon with { Token = value };
    }

    /// <summary>
    /// Gets when the Tachyon token expires. <c>default</c> means not set.
    /// </summary>
    /// <remarks>Part of <see cref="Tachyon"/>.</remarks>
    public DateTimeOffset TachyonExpiry
    {
        get => Tachyon.Expiry;
        internal set => Tachyon = Tachyon with { Expiry = value };
    }

    /// <summary>
    /// Gets the Tachyon token lifetime in microseconds.
    /// </summary>
    /// <remarks>Part of <see cref="Tachyon"/>.</remarks>
    public long TachyonTtlMicros
    {
        get => Tachyon.TtlMicros;
        internal set => Tachyon = Tachyon with { TtlMicros = value };
    }

    /// <summary>
    /// Gets or sets the Tachyon token, its expiry and its TTL as one value.
    /// </summary>
    /// <remarks>
    /// C#-only. A single write with a memory barrier swaps the 3 parts. Thus a save or a request on a different thread
    /// sees the old token or the new token, never a mix. This is also true on ARM64. Write tokens through this
    /// property, because it swaps all 3 parts at once.
    /// </remarks>
    public TachyonToken Tachyon
    {
        get => Volatile.Read(ref _tachyon);
        set => Volatile.Write(ref _tachyon, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// Gets or sets an unknown encryption key that the server sends. Go keeps it but never uses it.
    /// </summary>
    public byte[]? WebEncryptionKey { get; set; }

    /// <summary>
    /// Gets or sets the session ID.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Gets or sets the destination registration ID. A non-empty value means a Google account.
    /// </summary>
    public Guid DestRegId { get; set; }

    /// <summary>
    /// Gets or sets the destination phone ID.
    /// </summary>
    public ulong DestPhoneId { get; set; }

    /// <summary>
    /// Gets or sets the pairing ID, or empty when not paired.
    /// </summary>
    public Guid PairingId { get; set; }

    /// <summary>
    /// Gets the account cookies. A jar that is not set means "no cookies".
    /// </summary>
    public CookieJar Cookies { get; } = new();

    /// <summary>
    /// Gets whether this is a Google account, meaning <see cref="DestRegId"/> is set.
    /// </summary>
    public bool IsGoogleAccount => DestRegId != Guid.Empty;

    /// <summary>
    /// Gets the network name of the account: <c>GDitto</c> for a Google account, otherwise empty.
    /// </summary>
    public string AuthNetwork => IsGoogleAccount ? GmConstants.GoogleNetwork : "";

    /// <summary>
    /// Gets whether the account has the cookies it needs.
    /// </summary>
    /// <remarks>
    /// The same as Go. An account that is not a Google account never needs cookies, so the value is true for it.
    /// </remarks>
    public bool HasCookies => !IsGoogleAccount || Cookies.HasCookies;

    /// <summary>
    /// Creates auth data with new random keys and nothing else.
    /// </summary>
    /// <remarks>Ported from <c>NewAuthData</c>.</remarks>
    /// <returns>A new instance.</returns>
    public static AuthData CreateNew() => new()
    {
        RequestCrypto = AesCtrHelper.CreateRandom(),
        RefreshKey    = RefreshKey.Generate(),
    };

    /// <summary>
    /// Clears the pairing: devices, token, expiry, TTL, web key and pairing ID. It keeps the keys, the other IDs and the cookies.
    /// </summary>
    /// <remarks>Ported from <c>ClearPairing</c>.</remarks>
    public void ClearPairing()
    {
        Browser          = null;
        Mobile           = null;
        Tachyon          = TachyonToken.None;
        WebEncryptionKey = null;
        PairingId        = Guid.Empty;
    }
}

/// <summary>
/// The Tachyon auth token with its expiry and lifetime. They stay together, so they always match.
/// </summary>
/// <remarks>
/// C#-only. Go stores the 3 values as separate <c>AuthData</c> fields in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>
/// and guards them with the lock of the client. Here the code swaps 1 immutable value. The token is secret and must
/// never go in a log. Thus this record overrides the generated <c>ToString</c> to leave out the token.
/// </remarks>
/// <param name="Token">The token, or null when not paired.</param>
/// <param name="Expiry">When the token expires. <c>default</c> means not set.</param>
/// <param name="TtlMicros">The token lifetime in microseconds.</param>
public sealed record TachyonToken(byte[]? Token, DateTimeOffset Expiry, long TtlMicros)
{
    /// <summary>Gets the empty value: no token, no expiry, no TTL.</summary>
    public static TachyonToken None { get; } = new(null, default, 0);

    /// <summary>
    /// Describes the value without the token.
    /// </summary>
    /// <returns>The expiry, the TTL and whether a token is set.</returns>
    public override string ToString() => $"TachyonToken {{ HasToken = {Token is not null}, Expiry = {Expiry:O}, TtlMicros = {TtlMicros} }}";
}

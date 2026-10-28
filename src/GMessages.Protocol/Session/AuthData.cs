using GMessages.Protocol.Crypto;
using GMessages.Protocol.Proto;

namespace GMessages.Protocol.Session;

/// <summary>
/// The login and session state of one paired account: keys, device identities, the Tachyon token and the cookies.
/// </summary>
/// <remarks>
/// Ports <c>AuthData</c> from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// An account with a non-empty <see cref="DestRegId"/> is a Google account (paired by signing in); otherwise it is a
/// QR-paired session with no cookies. Where Go stores <c>uuid.UUID</c> bytes, this class uses <see cref="Guid"/> and
/// persistence uses RFC 4122 byte order. Everything in here is secret and must never be logged.
/// </remarks>
public sealed class AuthData
{
    private const string GoogleNetwork = "GDitto";

    private TachyonToken _tachyon = TachyonToken.None;

    /// <summary>
    /// Gets or sets the keys used to encrypt communication with the phone.
    /// </summary>
    public required AesCtrHelper RequestCrypto { get; set; }

    /// <summary>
    /// Gets or sets the key used to sign requests that refresh the Tachyon token.
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
    /// Gets the token used to authenticate with the server, or null when not paired.
    /// </summary>
    /// <remarks>Part of <see cref="Tachyon"/>.</remarks>
    public byte[]? TachyonAuthToken
    {
        get => Tachyon.Token;
        internal set => Tachyon = Tachyon with { Token = value };
    }

    /// <summary>
    /// Gets when the Tachyon token expires; <c>default</c> means unset.
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
    /// C#-only. The three parts are swapped in a single write with a memory barrier, so a save or request running on
    /// another thread at the same time sees either the old token or the new one, never a mix (ARM64 included). Write
    /// tokens through this property; it swaps all three parts at once.
    /// </remarks>
    public TachyonToken Tachyon
    {
        get => Volatile.Read(ref _tachyon);
        set => Volatile.Write(ref _tachyon, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// Gets or sets an unknown encryption key the server sends; Go keeps it but never uses it.
    /// </summary>
    public byte[]? WebEncryptionKey { get; set; }

    /// <summary>
    /// Gets or sets the session ID.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Gets or sets the destination registration ID; non-empty means a Google account.
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
    /// Gets the account cookies. An unset jar means "no cookies".
    /// </summary>
    public CookieJar Cookies { get; } = new();

    /// <summary>
    /// Gets whether this is a Google account, meaning <see cref="DestRegId"/> is set.
    /// </summary>
    public bool IsGoogleAccount => DestRegId != Guid.Empty;

    /// <summary>
    /// Gets the network name for this account: <c>GDitto</c> for Google accounts, otherwise empty.
    /// </summary>
    public string AuthNetwork => IsGoogleAccount ? GoogleNetwork : "";

    /// <summary>
    /// Gets whether the account has the cookies it needs.
    /// </summary>
    /// <remarks>
    /// Mirrors Go exactly: non-Google accounts never need cookies, so this is true for them.
    /// </remarks>
    public bool HasCookies => !IsGoogleAccount || Cookies.HasCookies;

    /// <summary>
    /// Creates auth data with fresh random keys and nothing else.
    /// </summary>
    /// <remarks>Mirrors <c>NewAuthData</c>.</remarks>
    /// <returns>A new instance.</returns>
    public static AuthData CreateNew() => new()
    {
        RequestCrypto = AesCtrHelper.CreateRandom(),
        RefreshKey    = RefreshKey.Generate(),
    };

    /// <summary>
    /// Makes auth data for pairing again: cookies and <see cref="DestPhoneId"/> are kept, everything else is new.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>CopyForRepair</c>. The keys are newly generated, so the old pairing cannot be reused. A jar that was
    /// never set stays unset.
    /// </remarks>
    /// <returns>A new instance.</returns>
    public AuthData CopyForRepair()
    {
        AuthData copy = CreateNew();
        if (Cookies.HasCookies)
        {
            copy.Cookies.Set(Cookies.Snapshot());
        }

        copy.DestPhoneId = DestPhoneId;
        return copy;
    }

    /// <summary>
    /// Clears the pairing (devices, token, expiry, TTL, web key and pairing ID) but keeps keys, IDs and cookies.
    /// </summary>
    /// <remarks>Mirrors <c>ClearPairing</c>.</remarks>
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
/// The Tachyon auth token with its expiry and lifetime, kept together so they always match.
/// </summary>
/// <remarks>
/// C#-only: Go stores the three as separate <c>AuthData</c> fields in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>
/// and guards them with the client's lock. Here one immutable value is swapped instead. The token is secret and must
/// never be logged; the record's generated <c>ToString</c> is overridden to leave it out.
/// </remarks>
/// <param name="Token">The token, or null when not paired.</param>
/// <param name="Expiry">When the token expires; <c>default</c> means unset.</param>
/// <param name="TtlMicros">The token lifetime in microseconds.</param>
public sealed record TachyonToken(byte[]? Token, DateTimeOffset Expiry, long TtlMicros)
{
    /// <summary>Gets the empty value: no token, no expiry, no TTL.</summary>
    public static TachyonToken None { get; } = new(null, default, 0);

    /// <summary>
    /// Describes the value without the token.
    /// </summary>
    /// <returns>The expiry and TTL, and whether a token is set.</returns>
    public override string ToString() => $"TachyonToken {{ HasToken = {Token is not null}, Expiry = {Expiry:O}, TtlMicros = {TtlMicros} }}";
}

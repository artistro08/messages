namespace GMessages.Protocol.Http;

/// <summary>
/// The server addresses the client talks to, and every URL built from them.
/// </summary>
/// <remarks>
/// The three bases are separate so tests (and a future UI test harness) can point the whole client at a local fake
/// server. Each URL is computed from its base on demand, which keeps one source of truth for the path fragments.
/// Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/paths.go">util/paths.go</see>.
/// The "Google" variants are the same calls on the <c>clients6.google.com</c> host that Google accounts use.
/// <see cref="AccountsBase"/> (Google's sign-in host, for cookie rotation) is not part of LibGM, so it is an init
/// property that defaults to the real host; a fake server sets it too.
/// </remarks>
/// <param name="MessagesBase">The web app origin, for example <c>https://messages.google.com</c>.</param>
/// <param name="InstantMessagingBase">The instant messaging API origin used by the pairing and QR flow.</param>
/// <param name="InstantMessagingGoogleBase">The instant messaging API origin used by Google account sign-in.</param>
public sealed record GmEndpoints(Uri MessagesBase, Uri InstantMessagingBase, Uri InstantMessagingGoogleBase)
{
    private const string PairingPath   = "/$rpc/google.internal.communications.instantmessaging.v1.Pairing";
    private const string MessagingPath = "/$rpc/google.internal.communications.instantmessaging.v1.Messaging";
    private const string RegistrationPath = "/$rpc/google.internal.communications.instantmessaging.v1.Registration";

    /// <summary>
    /// The real Google servers.
    /// </summary>
    public static GmEndpoints Production { get; } = new(
        new Uri("https://messages.google.com"),
        new Uri("https://instantmessaging-pa.googleapis.com"),
        new Uri("https://instantmessaging-pa.clients6.google.com"));

    /// <summary>Google's sign-in host, for example <c>https://accounts.google.com</c>.</summary>
    public Uri AccountsBase { get; init; } = new("https://accounts.google.com");

    /// <summary>Google's cookie rotation endpoint, which renews <c>__Secure-1PSIDTS</c> (<c>/RotateCookies</c>).</summary>
    public Uri RotateCookies => new(AccountsBase, "/RotateCookies");

    /// <summary>Web app authentication page (<c>/web/authentication</c>).</summary>
    public Uri Authentication => new(MessagesBase, "/web/authentication");

    /// <summary>Web app time source (<c>/web/timesource</c>).</summary>
    public Uri Timesource => new(MessagesBase, "/web/timesource");

    /// <summary>Web app config (<c>/web/config</c>).</summary>
    public Uri Config => new(MessagesBase, "/web/config");

    /// <summary>Media upload endpoint.</summary>
    public Uri UploadMedia => new(InstantMessagingBase, "/upload");

    /// <summary>Pairing: register the phone relay.</summary>
    public Uri RegisterPhoneRelay => new(InstantMessagingBase, PairingPath + "/RegisterPhoneRelay");

    /// <summary>Pairing: refresh the phone relay.</summary>
    public Uri RefreshPhoneRelay => new(InstantMessagingBase, PairingPath + "/RefreshPhoneRelay");

    /// <summary>Pairing: fetch the web encryption key.</summary>
    public Uri GetWebEncryptionKey => new(InstantMessagingBase, PairingPath + "/GetWebEncryptionKey");

    /// <summary>Pairing: revoke the relay pairing.</summary>
    public Uri RevokeRelayPairing => new(InstantMessagingBase, PairingPath + "/RevokeRelayPairing");

    /// <summary>Messaging: long-poll for incoming messages (QR pairing host).</summary>
    public Uri ReceiveMessages => new(InstantMessagingBase, MessagingPath + "/ReceiveMessages");

    /// <summary>Messaging: send a message (QR pairing host).</summary>
    public Uri SendMessage => new(InstantMessagingBase, MessagingPath + "/SendMessage");

    /// <summary>Messaging: acknowledge received messages (QR pairing host).</summary>
    public Uri AckMessages => new(InstantMessagingBase, MessagingPath + "/AckMessages");

    /// <summary>Messaging: long-poll for incoming messages (Google account host).</summary>
    public Uri ReceiveMessagesGoogle => new(InstantMessagingGoogleBase, MessagingPath + "/ReceiveMessages");

    /// <summary>Messaging: send a message (Google account host).</summary>
    public Uri SendMessageGoogle => new(InstantMessagingGoogleBase, MessagingPath + "/SendMessage");

    /// <summary>Messaging: acknowledge received messages (Google account host).</summary>
    public Uri AckMessagesGoogle => new(InstantMessagingGoogleBase, MessagingPath + "/AckMessages");

    /// <summary>Registration: sign in with a Google account.</summary>
    public Uri SignInGaia => new(InstantMessagingGoogleBase, RegistrationPath + "/SignInGaia");

    /// <summary>Registration: refresh the registration token.</summary>
    public Uri RegisterRefresh => new(InstantMessagingGoogleBase, RegistrationPath + "/RegisterRefresh");
}

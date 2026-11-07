namespace GMessages.Protocol.Http;

/// <summary>
/// Holds the server addresses that the client talks to, and builds every URL from them.
/// </summary>
/// <remarks>
/// The 3 bases are separate, so a test can point the whole client at a local fake server. Each URL comes from its base
/// on demand, so the path fragments have 1 source. Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/util/paths.go">util/paths.go</see>.
/// The "Google" variants are the same calls on the <c>clients6.google.com</c> host. Google accounts use that host.
/// </remarks>
/// <param name="MessagesBase">The web app origin, for example <c>https://messages.google.com</c>.</param>
/// <param name="InstantMessagingBase">The instant messaging API origin for the pairing and QR flow.</param>
/// <param name="InstantMessagingGoogleBase">The instant messaging API origin for the Google account sign-in.</param>
public sealed record GmEndpoints(Uri MessagesBase, Uri InstantMessagingBase, Uri InstantMessagingGoogleBase)
{
    private const string MessagingPath    = "/$rpc/google.internal.communications.instantmessaging.v1.Messaging";
    private const string RegistrationPath = "/$rpc/google.internal.communications.instantmessaging.v1.Registration";

    /// <summary>
    /// Gets the real Google servers.
    /// </summary>
    public static GmEndpoints Production { get; } = new(
        new Uri("https://messages.google.com"),
        new Uri("https://instantmessaging-pa.googleapis.com"),
        new Uri("https://instantmessaging-pa.clients6.google.com"));

    /// <summary>Gets the web app authentication page, <c>/web/authentication</c>.</summary>
    public Uri Authentication => new(MessagesBase, "/web/authentication");

    /// <summary>Gets the web app config, <c>/web/config</c>.</summary>
    public Uri Config => new(MessagesBase, "/web/config");

    /// <summary>Gets the media upload endpoint.</summary>
    public Uri UploadMedia => new(InstantMessagingBase, "/upload");

    /// <summary>Gets the messaging long poll for incoming messages on the QR pairing host.</summary>
    public Uri ReceiveMessages => new(InstantMessagingBase, MessagingPath + "/ReceiveMessages");

    /// <summary>Gets the messaging call that sends a message on the QR pairing host.</summary>
    public Uri SendMessage => new(InstantMessagingBase, MessagingPath + "/SendMessage");

    /// <summary>Gets the messaging call that acknowledges received messages on the QR pairing host.</summary>
    public Uri AckMessages => new(InstantMessagingBase, MessagingPath + "/AckMessages");

    /// <summary>Gets the messaging long poll for incoming messages on the Google account host.</summary>
    public Uri ReceiveMessagesGoogle => new(InstantMessagingGoogleBase, MessagingPath + "/ReceiveMessages");

    /// <summary>Gets the messaging call that sends a message on the Google account host.</summary>
    public Uri SendMessageGoogle => new(InstantMessagingGoogleBase, MessagingPath + "/SendMessage");

    /// <summary>Gets the messaging call that acknowledges received messages on the Google account host.</summary>
    public Uri AckMessagesGoogle => new(InstantMessagingGoogleBase, MessagingPath + "/AckMessages");

    /// <summary>Gets the registration call that signs in with a Google account.</summary>
    public Uri SignInGaia => new(InstantMessagingGoogleBase, RegistrationPath + "/SignInGaia");

    /// <summary>Gets the registration call that refreshes the registration token.</summary>
    public Uri RegisterRefresh => new(InstantMessagingGoogleBase, RegistrationPath + "/RegisterRefresh");
}

using GMessages.Presentation.Connection;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Presentation.Tests.Messaging;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Presentation.Tests.Main;

/// <summary>
/// A real <see cref="ChatStore"/> on a fake phone client, plus helpers to build conversations and feed live events.
/// </summary>
internal sealed class MainRig : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public MainRig(MessageDatabase? database = null)
    {
        Controller = new ConnectionController(
            new AppPaths(Path.Combine(Path.GetTempPath(), "main-test-" + Guid.NewGuid().ToString("N"))),
            GmEndpoints.Production,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            a => a());
        Store = new ChatStore(Controller, () => Connected ? Client : null, NullLogger.Instance, database);
    }

    public ConnectionController Controller { get; }

    /// <summary>Gets or sets whether the store sees a phone client.</summary>
    public bool Connected { get; set; } = true;

    public FakeMessagingClient Client { get; } = new();

    public ChatStore Store { get; }

    public static Conversation Conv(string id, string name, long minutesAgo = 0, bool pinned = false, ConversationStatus status = ConversationStatus.Active, string snippet = "hi")
    {
        var c = new Conversation
        {
            ConversationID       = id,
            Name                 = name,
            Status               = status,
            Pinned               = pinned,
            LastMessageTimestamp = Now.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds() * 1000,
            LatestMessage        = new LatestMessage { DisplayContent = snippet },
        };
        c.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "me" }, IsMe = true });
        c.Participants.Add(new Participant { ID = new SmallInfo { ParticipantID = "p-" + id }, FullName = name });
        return c;
    }

    /// <summary>
    /// Every way a request can fail in the real client: phone problems, plus the raw network, timeout, server and
    /// decode errors <c>RpcSession</c> lets through.
    /// </summary>
    public static Exception[] Failures() =>
    [
        new MessagingUnavailableException(),
        new PhoneNotRespondingException(),
        new ConnectionClosedException(),
        new HttpRequestException(),
        new TaskCanceledException(),
        new GmRequestException(500, null, []),
        new InvalidDataException(),
        new InvalidOperationException(),
    ];

    public void Page(params Conversation[] conversations)
    {
        Client.ConversationsPage = new ListConversationsResponse();
        Client.ConversationsPage.Conversations.AddRange(conversations);
    }

    public Task LiveMessage(Message m) => Store.HandleEventAsync(new MessageReceived(m, false));

    public Task Live(Conversation c) => Store.HandleEventAsync(new ConversationUpdated(c));

    public void Dispose() => Store.Dispose();
}

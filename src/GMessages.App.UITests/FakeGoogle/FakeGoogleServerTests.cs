using System.Globalization;
using System.Net;
using GMessages.Protocol;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.App.UITests.FakeGoogle;

/// <summary>
/// Protocol-level checks of <see cref="FakeGoogleServer"/> and <see cref="FakePhone"/>: a real <see cref="GmClient"/>
/// pairs and connects against them over real HTTP on 127.0.0.1.
/// </summary>
/// <remarks>
/// No UI: these run in the normal <c>dotnet test</c> of this project. Every wait is a signal with a 30 s hang guard,
/// never a sleep. The client saves its session to a temp file, never the owner's data folder.
/// </remarks>
[TestClass]
public sealed class FakeGoogleServerTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly string[] InboxIds = ["c-ann", "c-team"];

    private static readonly string[] SpamIds = ["c-spam"];

    private static readonly string[] CookieNames = ["SAPISID", "SID", "HSID", "SSID", "APISID", "OSID", "__Secure-1PSIDTS"];

    // =========================================================================
    // RIG
    // =========================================================================

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Auth = AuthData.CreateNew();
            Auth.Cookies.Set(FakeGoogleServer.SignInCookies);
            SessionPath = Path.Combine(Path.GetTempPath(), "fake-google-test-" + Guid.NewGuid().ToString("N") + ".bin");
            Endpoints   = new GmEndpoints(Server.BaseUrl, Server.BaseUrl, Server.BaseUrl);
            Client      = GmClient.Create(Auth, new SessionStore(SessionPath), Endpoints, TimeProvider.System, NullLoggerFactory.Instance);
            Client.EventRaised += e =>
            {
                lock (Events)
                {
                    Events.Add(e);
                }

                if (e is ListenFatalError)
                {
                    Fatal.TrySetResult();
                }
            };
        }

        public FakeGoogleServer Server { get; } = new();

        public AuthData Auth { get; }

        public GmEndpoints Endpoints { get; }

        public GmClient Client { get; }

        public string SessionPath { get; }

        public List<object> Events { get; } = [];

        public TaskCompletionSource Fatal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync();
            }
            finally
            {
                try
                {
                    await Server.DisposeAsync();
                }
                finally
                {
                    File.Delete(SessionPath);
                }
            }
        }
    }

    // Starts Pairing; The Returned Task Gives The Emoji The Client Showed
    private static (Task Pair, Task<string> Shown) StartPairing(Rig rig)
    {
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pair = rig.Client.PairAsync(e => shown.TrySetResult(e), CancellationToken.None);
        return (pair, shown.Task);
    }

    private static int Count(FakeGoogleServer server, string suffix) => server.RequestedPaths.Count(p => p.EndsWith(suffix, StringComparison.Ordinal));

    // Waits For A Condition Without Sleeping A Fixed Time
    private static void WaitUntil(Func<bool> condition, string what) => Assert.IsTrue(SpinWait.SpinUntil(condition, HangGuard), what);

    // =========================================================================
    // PAIRING
    // =========================================================================

    /// <summary>
    /// The right tap pairs: the client's emoji is the phone's, both sides hold the same request keys, and the client
    /// ends connected.
    /// </summary>
    [TestMethod]
    public async Task Pair_TapCorrect_Pairs_AndEmojiMatches()
    {
        await using var rig = new Rig();

        (Task pair, Task<string> shown) = StartPairing(rig);
        string emoji = await shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);

        Assert.AreEqual(rig.Server.Phone.ShownEmoji, emoji);
        rig.Server.Phone.Tap(true);
        await pair.WaitAsync(HangGuard);

        Assert.IsNotNull(rig.Server.Phone.RequestCrypto);
        CollectionAssert.AreEqual(rig.Server.Phone.RequestCrypto.AesKey, rig.Auth.RequestCrypto.AesKey, "same AES key");
        CollectionAssert.AreEqual(rig.Server.Phone.RequestCrypto.HmacKey, rig.Auth.RequestCrypto.HmacKey, "same HMAC key");
        WaitUntil(() => rig.Client.IsConnected, "connected after pairing");
        Assert.AreEqual(1, Count(rig.Server, "/web/config"));
        Assert.AreEqual(1, Count(rig.Server, "Registration/SignInGaia"));
    }

    /// <summary>
    /// The wrong tap fails the pairing with <see cref="PairingFailure.IncorrectEmoji"/>.
    /// </summary>
    [TestMethod]
    public async Task Pair_TapWrong_IsIncorrectEmoji()
    {
        await using var rig = new Rig();

        (Task pair, Task<string> shown) = StartPairing(rig);
        await shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        rig.Server.Phone.Tap(false);

        var ex = await Assert.ThrowsExactlyAsync<PairingException>(() => pair.WaitAsync(HangGuard));
        Assert.AreEqual(PairingFailure.IncorrectEmoji, ex.Failure);
        Assert.IsNull(rig.Server.Phone.RequestCrypto);
    }

    /// <summary>
    /// After a wrong tap, a second attempt on the same server gets a fresh signal and emoji and pairs.
    /// </summary>
    [TestMethod]
    public async Task Pair_RetryAfterWrongTap_Pairs()
    {
        await using var rig = new Rig();
        (Task first, Task<string> first_shown) = StartPairing(rig);
        await first_shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        rig.Server.Phone.Tap(false);
        await Assert.ThrowsExactlyAsync<PairingException>(() => first.WaitAsync(HangGuard));

        (Task second, Task<string> second_shown) = StartPairing(rig);
        string emoji = await second_shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        Assert.AreEqual(rig.Server.Phone.ShownEmoji, emoji);
        rig.Server.Phone.Tap(true);

        await second.WaitAsync(HangGuard);
        CollectionAssert.AreEqual(rig.Server.Phone.RequestCrypto!.AesKey, rig.Auth.RequestCrypto.AesKey);
    }

    /// <summary>
    /// <see cref="FakeGoogleServer.RejectCookies"/> makes the sign-in a 401.
    /// </summary>
    [TestMethod]
    public async Task Pair_RejectCookies_Is401()
    {
        await using var rig = new Rig();
        rig.Server.RejectCookies = true;

        var ex = await Assert.ThrowsExactlyAsync<GmRequestException>(() => rig.Client.PairAsync(_ => { }, CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual(401, ex.StatusCode);
    }

    // =========================================================================
    // CONNECTION
    // =========================================================================

    /// <summary>
    /// After pairing, connect reopens the long poll; killing it with 401 raises <see cref="ListenFatalError"/>.
    /// </summary>
    [TestMethod]
    public async Task Connect_AfterPairing_ThenKill401_IsListenFatalError()
    {
        await using var rig = new Rig();
        (Task pair, Task<string> shown) = StartPairing(rig);
        await shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        rig.Server.Phone.Tap(true);
        await pair.WaitAsync(HangGuard);
        WaitUntil(() => rig.Client.IsConnected, "connected after pairing");
        int polls = Count(rig.Server, "Messaging/ReceiveMessages");

        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        WaitUntil(() => Count(rig.Server, "Messaging/ReceiveMessages") > polls && rig.Client.IsConnected, "reconnected");

        rig.Server.KillLongPoll(401);
        await rig.Fatal.Task.WaitAsync(HangGuard);
        Assert.IsFalse(rig.Client.IsConnected);
    }

    /// <summary>
    /// Payloads still unread when a second long poll takes over arrive exactly once, in order: a prefix on the old
    /// poll, the rest on the new one.
    /// </summary>
    /// <remarks>
    /// The old poll is not read until after the takeover, and each payload is 64 KB, so the server's write to it
    /// stalls and most payloads are still queued for it when the new poll opens.
    /// </remarks>
    [TestMethod]
    public async Task LongPoll_Takeover_DeliversEveryPayloadOnceInOrder()
    {
        const int Total = 100;
        await using var server = new FakeGoogleServer();
        using var http         = new HttpClient();
        Uri url                = new(server.BaseUrl, "/$rpc/google.internal.communications.instantmessaging.v1.Messaging/ReceiveMessages");
        string padding         = new('x', 64 * 1024);

        // Old Poll Open But Not Read, Payloads Pushed, New Poll Takes Over (Its Headers Come After The Takeover)
        using HttpResponseMessage old_poll = await OpenPollAsync(http, url);
        for (int i = 0; i < Total; i++)
        {
            server.Push(new LongPollingPayload { Data = new IncomingRPCMessage { ResponseID = i.ToString(CultureInfo.InvariantCulture), Mobile = new Device { SourceID = padding } } });
        }

        using HttpResponseMessage new_poll = await OpenPollAsync(http, url);
        server.Push(new LongPollingPayload { Data = new IncomingRPCMessage { ResponseID = "end" } });

        // Read Both
        List<string> first  = await ReadIdsAsync(old_poll).WaitAsync(HangGuard);
        List<string> second = await ReadIdsAsync(new_poll).WaitAsync(HangGuard);

        string[] expected = [.. Enumerable.Range(0, Total).Select(i => i.ToString(CultureInfo.InvariantCulture)), "end"];
        CollectionAssert.AreEqual(expected, first.Concat(second).ToArray());
        Assert.IsGreaterThan(1, second.Count, "some payloads moved to the new poll");
        Assert.IsEmpty(server.Errors);
    }

    private static Task<HttpResponseMessage> OpenPollAsync(HttpClient http, Uri url) =>
        http.SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent([]) }, HttpCompletionOption.ResponseHeadersRead).WaitAsync(HangGuard);

    // Reads One Long Poll's Data Payload IDs Until The Stream Ends Or The "end" Payload
    private static async Task<List<string>> ReadIdsAsync(HttpResponseMessage poll)
    {
        Stream body = await poll.Content.ReadAsStreamAsync();
        var ids     = new List<string>();
        await foreach (LongPollingPayload payload in LongPollStreamReader.ReadAsync(body, HangGuard, TimeProvider.System, CancellationToken.None))
        {
            if (payload.Data is null)
            {
                continue;
            }

            ids.Add(payload.Data.ResponseID);
            if (payload.Data.ResponseID == "end")
            {
                break;
            }
        }

        return ids;
    }

    // =========================================================================
    // SIGN-IN PAGE
    // =========================================================================

    /// <summary>
    /// The sign-in page sets no cookies and has the "signin" button; the login POST sets all seven cookies.
    /// </summary>
    [TestMethod]
    public async Task SignInPage_SetsCookiesOnlyAfterLogin()
    {
        await using var server = new FakeGoogleServer();
        using var handler      = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        using var http         = new HttpClient(handler);

        // Page: No Cookies
        using HttpResponseMessage page = await http.GetAsync(new Uri(server.BaseUrl, "/web/authentication")).WaitAsync(HangGuard);
        string html = await page.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.IsFalse(page.Headers.Contains("Set-Cookie"), "no cookies before login");
        StringAssert.Contains(html, "id=\"signin\"");

        // Login: All Seven
        using HttpResponseMessage login = await http.PostAsync(new Uri(server.BaseUrl, "/web/authentication/login"), null).WaitAsync(HangGuard);
        string[] names = [.. login.Headers.GetValues("Set-Cookie").Select(c => c[..c.IndexOf('=', StringComparison.Ordinal)])];
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        CollectionAssert.AreEquivalent(CookieNames, names);
        StringAssert.Contains(await login.Content.ReadAsStringAsync(), "Signed in");
    }

    // =========================================================================
    // CONVERSATIONS AND MESSAGES
    // =========================================================================

    // Pairs And Waits Until The Client Is Connected; Events Are Collected From The Start
    private static async Task<Rig> PairedAsync()
    {
        var rig = new Rig();
        (Task pair, Task<string> shown) = StartPairing(rig);
        await shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        rig.Server.Phone.Tap(true);
        await pair.WaitAsync(HangGuard);
        WaitUntil(() => rig.Client.IsConnected, "connected after pairing");
        return rig;
    }

    // Waits For The First Event Of A Type That Matches
    private static T WaitForEvent<T>(List<object> events, Func<T, bool> match, string what)
    {
        T? found = default;
        WaitUntil(() =>
        {
            lock (events)
            {
                found = events.OfType<T>().FirstOrDefault(match);
            }

            return found is not null;
        }, what);
        return found!;
    }

    /// <summary>
    /// The inbox lists the active conversations and leaves the spam one out; the spam folder lists only that.
    /// </summary>
    [TestMethod]
    public async Task ListConversations_Inbox_ExcludesSpam()
    {
        await using Rig rig = await PairedAsync();

        ListConversationsResponse inbox = await rig.Client.ListConversationsAsync(ListConversationsRequest.Types.Folder.Inbox, 25, null, CancellationToken.None).WaitAsync(HangGuard);
        ListConversationsResponse spam  = await rig.Client.ListConversationsAsync(ListConversationsRequest.Types.Folder.SpamBlocked, 25, null, CancellationToken.None).WaitAsync(HangGuard);

        CollectionAssert.AreEquivalent(InboxIds, inbox.Conversations.Select(c => c.ConversationID).ToArray());
        CollectionAssert.AreEqual(SpamIds, spam.Conversations.Select(c => c.ConversationID).ToArray());
        Assert.IsTrue(inbox.Conversations.All(c => c.DefaultOutgoingID == "me-1" && c.Participants.Any(p => p.IsMe && p.ID.ParticipantID == "me-1")));
    }

    /// <summary>
    /// A send is answered with success, then echoed as a live message with the request's temporary ID, a new message
    /// ID and the outgoing-complete status.
    /// </summary>
    [TestMethod]
    public async Task SendMessage_EchoesWithTmpId()
    {
        await using Rig rig = await PairedAsync();
        string tmp_id     = GmClient.NewTmpId();
        var request       = new SendMessageRequest
        {
            ConversationID = "c-ann",
            TmpID          = tmp_id,
            SIMPayload     = new SIMPayload { SIMNumber = 1 },
            MessagePayload = new MessagePayload
            {
                TmpID                 = tmp_id,
                ConversationID        = "c-ann",
                ParticipantID         = "me-1",
                MessagePayloadContent = new MessagePayloadContent { MessageContent = new MessageContent { Content = "Hello" } },
            },
        };

        SendMessageResponse answer = await rig.Client.SendMessageAsync(request, CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(SendMessageResponse.Types.Status.Success, answer.Status);
        MessageReceived echo = WaitForEvent<MessageReceived>(rig.Events, e => e.Message.TmpID == tmp_id, "echo");
        Assert.IsFalse(echo.IsOld);
        Assert.IsNotEmpty(echo.Message.MessageID);
        Assert.AreEqual(MessageStatusType.OutgoingComplete, echo.Message.MessageStatus.Status);
        Assert.AreEqual("Hello", echo.Message.MessageInfo[0].MessageContent.Content);
        Assert.HasCount(1, rig.Server.Phone.Sent);
    }

    /// <summary>
    /// A pushed incoming message arrives live (not old), preceded by the conversation with the new snippet.
    /// </summary>
    [TestMethod]
    public async Task PushIncoming_RaisesLiveMessage()
    {
        await using Rig rig = await PairedAsync();

        rig.Server.Phone.PushIncoming("c-team", "Dinner?");

        ConversationUpdated conversation = WaitForEvent<ConversationUpdated>(rig.Events, e => e.Conversation.ConversationID == "c-team", "conversation update");
        MessageReceived message          = WaitForEvent<MessageReceived>(rig.Events, e => e.Message.ConversationID == "c-team" && e.Message.MessageInfo[0].MessageContent.Content == "Dinner?", "live message");
        Assert.AreEqual("Dinner?", conversation.Conversation.LatestMessage.DisplayContent);
        Assert.IsFalse(message.IsOld);
    }

    /// <summary>
    /// A message pushed with <c>old</c> before the client connects comes in the startup catch-up batch, so the client
    /// counts it as old through the startup ack.
    /// </summary>
    [TestMethod]
    public async Task PushIncomingOld_RaisesIsOld()
    {
        await using var rig = new Rig();

        (Task pair, Task<string> shown) = StartPairing(rig);
        await shown.WaitAsync(HangGuard);
        await rig.Server.Phone.FinishReceived.WaitAsync(HangGuard);
        rig.Server.Phone.PushIncoming("c-ann", "While you were away", old: true);
        rig.Server.Phone.Tap(true);
        await pair.WaitAsync(HangGuard);

        MessageReceived message = WaitForEvent<MessageReceived>(rig.Events, e => e.Message.MessageInfo[0].MessageContent.Content == "While you were away", "catch-up message");
        Assert.IsTrue(message.IsOld);

        // Live Ones After The Catch-Up Are Not Old
        rig.Server.Phone.PushIncoming("c-ann", "Now");
        Assert.IsFalse(WaitForEvent<MessageReceived>(rig.Events, e => e.Message.MessageInfo[0].MessageContent.Content == "Now", "live message").IsOld);
    }
}

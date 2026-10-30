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

    private static readonly string[] CookieNames = ["SAPISID", "SID", "HSID", "SSID", "APISID", "OSID", "__Secure-1PSIDTS", "__Secure-1PSID"];

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
            Endpoints   = new GmEndpoints(Server.BaseUrl, Server.BaseUrl, Server.BaseUrl) { AccountsBase = Server.BaseUrl };
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
    /// The sign-in page sets no cookies and has the "signin" button; the login POST sets all eight cookies.
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

        // Login: All Eight
        using HttpResponseMessage login = await http.PostAsync(new Uri(server.BaseUrl, "/web/authentication/login"), null).WaitAsync(HangGuard);
        string[] names = [.. login.Headers.GetValues("Set-Cookie").Select(c => c[..c.IndexOf('=', StringComparison.Ordinal)])];
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        CollectionAssert.AreEquivalent(CookieNames, names);
        StringAssert.Contains(await login.Content.ReadAsStringAsync(), "Signed in");
    }

    /// <summary>
    /// Cookie rotation answers with a fresh <c>__Secure-1PSIDTS</c> for all of google.com, different each time.
    /// </summary>
    [TestMethod]
    public async Task RotateCookies_SetsAFreshPsidtsEachTime()
    {
        await using var server = new FakeGoogleServer();
        using var http         = new HttpClient();
        var url                = new GmEndpoints(server.BaseUrl, server.BaseUrl, server.BaseUrl) { AccountsBase = server.BaseUrl }.RotateCookies;

        using HttpResponseMessage first  = await http.PostAsync(url, new StringContent("[000,\"-0000000000000000000\"]")).WaitAsync(HangGuard);
        using HttpResponseMessage second = await http.PostAsync(url, new StringContent("[000,\"-0000000000000000000\"]")).WaitAsync(HangGuard);
        string a = first.Headers.GetValues("Set-Cookie").Single();
        string b = second.Headers.GetValues("Set-Cookie").Single();

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        StringAssert.StartsWith(a, "__Secure-1PSIDTS=");
        StringAssert.Contains(a, "Domain=.google.com");
        Assert.AreNotEqual(a, b);
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
                TmpID          = tmp_id,
                ConversationID = "c-ann",
                ParticipantID  = "me-1",
            },
        };
        request.MessagePayload.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = "Hello" } });

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
    /// A reaction is answered with success and the message comes back with my reaction; a switch moves it to the new
    /// emoji and a remove takes it off.
    /// </summary>
    [TestMethod]
    public async Task SendReaction_EchoesMessageWithReaction()
    {
        await using Rig rig = await PairedAsync();
        ListMessagesResponse page = await rig.Client.FetchMessagesAsync("c-ann", 10, null, CancellationToken.None).WaitAsync(HangGuard);
        string message_id         = page.Messages[0].MessageID;

        // Add A Thumbs Up
        SendReactionResponse added = await rig.Client.SendReactionAsync(Reaction(message_id, "\U0001F44D", SendReactionRequest.Types.Action.Add), CancellationToken.None).WaitAsync(HangGuard);
        Assert.IsTrue(added.Success);
        MessageReceived like = WaitForEvent<MessageReceived>(rig.Events, e => e.Message.MessageID == message_id && e.Message.Reactions.Count > 0, "message with the like");
        Assert.HasCount(1, like.Message.Reactions);
        Assert.AreEqual(EmojiType.Like, like.Message.Reactions[0].Data.Type);
        Assert.AreEqual("me-1", like.Message.Reactions[0].ParticipantIDs.Single());

        // Switch To A Heart
        await rig.Client.SendReactionAsync(Reaction(message_id, "❤️", SendReactionRequest.Types.Action.Switch), CancellationToken.None).WaitAsync(HangGuard);
        MessageReceived heart = WaitForEvent<MessageReceived>(rig.Events, e => e.Message.MessageID == message_id && e.Message.Reactions.Any(r => r.Data.Type == EmojiType.RedHeart), "message with the heart");
        Assert.HasCount(1, heart.Message.Reactions);

        // Remove It
        lock (rig.Events)
        {
            rig.Events.Clear();
        }

        await rig.Client.SendReactionAsync(Reaction(message_id, "❤️", SendReactionRequest.Types.Action.Remove), CancellationToken.None).WaitAsync(HangGuard);
        MessageReceived removed = WaitForEvent<MessageReceived>(rig.Events, e => e.Message.MessageID == message_id, "message without reactions");
        Assert.IsEmpty(removed.Message.Reactions);
        Assert.HasCount(3, rig.Server.Phone.Reactions);

        static SendReactionRequest Reaction(string id, string emoji, SendReactionRequest.Types.Action action) =>
            new() { MessageID = id, ReactionData = EmojiTypes.MakeReactionData(emoji), Action = action, SIMPayload = new SIMPayload { SIMNumber = 1 } };
    }

    /// <summary>
    /// An archive is answered with success, the conversation comes back archived, and it moves from the inbox to the
    /// archive folder.
    /// </summary>
    [TestMethod]
    public async Task UpdateConversation_Archive_EchoesConversationArchived()
    {
        await using Rig rig = await PairedAsync();
        var request = new UpdateConversationRequest
        {
            ConversationID = "c-ann",
            UpdateData     = new UpdateConversationData { ConversationID = "c-ann", Status = ConversationStatus.Archived },
        };

        UpdateConversationResponse answer = await rig.Client.UpdateConversationAsync(request, CancellationToken.None).WaitAsync(HangGuard);

        Assert.IsTrue(answer.Success);
        WaitForEvent<ConversationUpdated>(rig.Events, e => e.Conversation.ConversationID == "c-ann" && e.Conversation.Status == ConversationStatus.Archived, "archived conversation");
        Assert.HasCount(1, rig.Server.Phone.ConversationUpdates);
        ListConversationsResponse inbox   = await rig.Client.ListConversationsAsync(ListConversationsRequest.Types.Folder.Inbox, 25, null, CancellationToken.None).WaitAsync(HangGuard);
        ListConversationsResponse archive = await rig.Client.ListConversationsAsync(ListConversationsRequest.Types.Folder.Archive, 25, null, CancellationToken.None).WaitAsync(HangGuard);
        Assert.IsFalse(inbox.Conversations.Any(c => c.ConversationID == "c-ann"), "out of the inbox");
        Assert.AreEqual("c-ann", archive.Conversations.Single().ConversationID);

        // A Phone-Side Keep-Archived Move Still Lists In The Archive
        rig.Server.Phone.PushConversationStatus("c-team", ConversationStatus.KeepArchived);
        WaitForEvent<ConversationUpdated>(rig.Events, e => e.Conversation.ConversationID == "c-team" && e.Conversation.Status == ConversationStatus.KeepArchived, "phone-side move");
        archive = await rig.Client.ListConversationsAsync(ListConversationsRequest.Types.Folder.Archive, 25, null, CancellationToken.None).WaitAsync(HangGuard);
        Assert.HasCount(2, archive.Conversations);
    }

    /// <summary>
    /// With <see cref="FakePhone.FailConversationUpdates"/>, a block is answered with no success, is recorded, and the
    /// conversation stays in the inbox.
    /// </summary>
    [TestMethod]
    public async Task UpdateConversation_Fail_ChangesNothing()
    {
        await using Rig rig = await PairedAsync();
        rig.Server.Phone.FailConversationUpdates = true;
        var request = new UpdateConversationRequest
        {
            Action         = ConversationActionStatus.BlockAndReport,
            ConversationID = "c-ann",
            DeleteData     = new DeleteConversationData { ConversationID = "c-ann" },
        };

        UpdateConversationResponse answer = await rig.Client.UpdateConversationAsync(request, CancellationToken.None).WaitAsync(HangGuard);

        Assert.IsFalse(answer.Success);
        Assert.HasCount(1, rig.Server.Phone.ConversationUpdates);
        ListConversationsResponse inbox = await rig.Client.ListConversationsAsync(ListConversationsRequest.Types.Folder.Inbox, 25, null, CancellationToken.None).WaitAsync(HangGuard);
        CollectionAssert.AreEquivalent(InboxIds, inbox.Conversations.Select(c => c.ConversationID).ToArray());

        // A Late Push Would Show Up Within This Bounded Wait
        bool pushed = SpinWait.SpinUntil(() =>
        {
            lock (rig.Events)
            {
                return rig.Events.OfType<ConversationUpdated>().Any(e => e.Conversation.ConversationID == "c-ann");
            }
        }, TimeSpan.FromSeconds(1));
        Assert.IsFalse(pushed, "nothing pushed");
    }

    /// <summary>
    /// Each successful update leaves the phone's conversation in the status its action or update data names, and the
    /// conversation comes back with that status. A mute leaves the status as it was and still comes back.
    /// </summary>
    /// <param name="conversationId">The conversation to update.</param>
    /// <param name="action">The request's action.</param>
    /// <param name="data">The request's data: <c>delete</c>, <c>archive</c> or <c>mute</c>.</param>
    /// <param name="expected">The status the phone ends with.</param>
    [TestMethod]
    [DataRow("c-ann", ConversationActionStatus.Delete, "delete", ConversationStatus.Deleted)]
    [DataRow("c-ann", ConversationActionStatus.Block, "delete", ConversationStatus.BlockedFolder)]
    [DataRow("c-ann", ConversationActionStatus.BlockAndReport, "delete", ConversationStatus.BlockedFolder)]
    [DataRow("c-spam", ConversationActionStatus.Unblock, "delete", ConversationStatus.Active)]
    [DataRow("c-ann", ConversationActionStatus.UnknownActionStatus, "archive", ConversationStatus.Archived)]
    [DataRow("c-spam", ConversationActionStatus.UnknownActionStatus, "mute", ConversationStatus.SpamFolder)]
    public async Task UpdateConversation_Success_AppliesAndEchoesStatus(string conversationId, ConversationActionStatus action, string data, ConversationStatus expected)
    {
        await using Rig rig = await PairedAsync();
        var request = new UpdateConversationRequest { Action = action, ConversationID = conversationId };
        switch (data)
        {
            case "archive":
                request.UpdateData = new UpdateConversationData { ConversationID = conversationId, Status = ConversationStatus.Archived };
                break;

            case "mute":
                request.UpdateData = new UpdateConversationData { ConversationID = conversationId, Mute = ConversationMuteStatus.Mute };
                break;

            default:
                request.DeleteData = new DeleteConversationData { ConversationID = conversationId };
                break;
        }

        UpdateConversationResponse answer = await rig.Client.UpdateConversationAsync(request, CancellationToken.None).WaitAsync(HangGuard);

        Assert.IsTrue(answer.Success);
        WaitForEvent<ConversationUpdated>(rig.Events, e => e.Conversation.ConversationID == conversationId && e.Conversation.Status == expected, "echoed conversation");
        Assert.AreEqual(expected, rig.Server.Phone.Chats.Get(conversationId)?.Status, "phone's stored status");
        Assert.HasCount(1, rig.Server.Phone.ConversationUpdates);
    }

    /// <summary>
    /// A pushed alert raises UserAlert with its type.
    /// </summary>
    [TestMethod]
    public async Task PushAlert_RaisesUserAlert()
    {
        await using Rig rig = await PairedAsync();

        rig.Server.Phone.PushAlert(AlertType.MobileBatteryLow);

        WaitForEvent<UserAlert>(rig.Events, e => e.Alert.AlertType == AlertType.MobileBatteryLow, "battery low alert");
    }

    /// <summary>
    /// Pushed typing raises TypingChanged with the conversation, number and start or stop; the app's own typing
    /// update reaches the phone.
    /// </summary>
    [TestMethod]
    public async Task PushTyping_RaisesTypingChanged()
    {
        await using Rig rig = await PairedAsync();

        rig.Server.Phone.PushTyping("c-ann", "+15550000001", true);
        TypingChanged started = WaitForEvent<TypingChanged>(rig.Events, e => e.Data.Type == TypingTypes.StartedTyping, "started typing");
        rig.Server.Phone.PushTyping("c-ann", "+15550000001", false);
        TypingChanged stopped = WaitForEvent<TypingChanged>(rig.Events, e => e.Data.Type == TypingTypes.StoppedTyping, "stopped typing");

        Assert.AreEqual("c-ann", started.Data.ConversationID);
        Assert.AreEqual("+15550000001", started.Data.User.Number);
        Assert.AreEqual("c-ann", stopped.Data.ConversationID);

        // The App's Own Typing
        await rig.Client.SetTypingAsync("c-ann", new SIMPayload { SIMNumber = 1 }, CancellationToken.None).WaitAsync(HangGuard);
        WaitUntil(() => rig.Server.Phone.Typing.Count == 1, "typing update received");
        Assert.AreEqual("c-ann", rig.Server.Phone.Typing[0].Data.ConversationID);
        Assert.IsTrue(rig.Server.Phone.Typing[0].Data.Typing);
    }

    /// <summary>
    /// The seeded chats carry their types and Ann's tombstone; a pushed reply quotes its message, and a pushed status
    /// arrives on the same message.
    /// </summary>
    [TestMethod]
    public async Task Chats_TypesTombstoneReplyAndStatus()
    {
        await using Rig rig = await PairedAsync();
        ListConversationsResponse inbox = await rig.Client.ListConversationsAsync(ListConversationsRequest.Types.Folder.Inbox, 25, null, CancellationToken.None).WaitAsync(HangGuard);
        ListMessagesResponse ann        = await rig.Client.FetchMessagesAsync("c-ann", 10, null, CancellationToken.None).WaitAsync(HangGuard);
        ListMessagesResponse team       = await rig.Client.FetchMessagesAsync("c-team", 10, null, CancellationToken.None).WaitAsync(HangGuard);

        // Types And The Tombstone
        Assert.AreEqual(ConversationType.Rcs, inbox.Conversations.Single(c => c.ConversationID == "c-ann").Type);
        Assert.AreEqual(ConversationType.Sms, inbox.Conversations.Single(c => c.ConversationID == "c-team").Type);
        Assert.IsTrue(inbox.Conversations.SelectMany(c => c.Participants).All(p => p.ID.Number.Length > 0), "every participant has a number");
        Assert.AreEqual(MessageStatusType.TombstoneEncryptedOneOnOneRcsCreated, ann.Messages[^1].MessageStatus.Status);
        Assert.IsTrue(ann.Messages.SkipLast(1).All(m => m.Type == 4));
        Assert.IsTrue(team.Messages.All(m => m.Type == 2));

        // A Reply, Then A New Status On It
        string quoted = ann.Messages[0].MessageID;
        Message reply = rig.Server.Phone.PushIncoming("c-ann", "Reply", replyToId: quoted);
        Assert.AreEqual(quoted, WaitForEvent<MessageReceived>(rig.Events, e => e.Message.MessageID == reply.MessageID, "reply").Message.ReplyMessage.MessageID);
        rig.Server.Phone.PushStatus("c-ann", reply.MessageID, MessageStatusType.IncomingDisplayed);
        WaitForEvent<MessageReceived>(rig.Events, e => e.Message.MessageID == reply.MessageID && e.Message.MessageStatus.Status == MessageStatusType.IncomingDisplayed, "status update");
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
    /// An old message pushed while connected arrives with IsOld after the long poll is ended and reopened, and a second
    /// connect (a second GET_UPDATES) gets the settings event again.
    /// </summary>
    [TestMethod]
    public async Task Reconnect_DeliversQueuedCatchUpAsOld_AndResendsSettings()
    {
        await using Rig rig = await PairedAsync();
        WaitForEvent<SettingsChanged>(rig.Events, _ => true, "first settings");
        lock (rig.Events)
        {
            rig.Events.Clear();
        }

        rig.Server.Phone.PushIncoming("c-ann", "Queued while connected", old: true);
        rig.Server.KillLongPoll(0);

        MessageReceived message = WaitForEvent<MessageReceived>(rig.Events, e => e.Message.MessageInfo[0].MessageContent.Content == "Queued while connected", "catch-up message");
        Assert.IsTrue(message.IsOld);

        // A Second Connect (A Restarted App) Asks For Updates Again And Gets The Settings Again
        lock (rig.Events)
        {
            rig.Events.Clear();
        }

        await rig.Client.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        WaitForEvent<SettingsChanged>(rig.Events, e => e.Settings.SIMCards[0].SIMParticipant.ID == "me-1", "settings after second connect");
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

using System.Net;
using System.Text;
using System.Text.Json;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Protocol.Tests.LongPoll;

/// <summary>
/// Replays Go's dispatch inputs through <see cref="UpdateDispatcher"/> and compares events and acks, using <c>dispatch.json</c>.
/// </summary>
/// <remarks>
/// The inputs are Go-encrypted messages, so they are fed as Go produced them; nothing is normalized. The sequence
/// covers the skip counter (old vs live), duplicate and edited messages, an old conversation update, typing, the
/// "logged out" bytes, the fake account change from the second encrypted field, and a user alert.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/event_handler.go"/>.
/// </remarks>
[TestClass]
public class DispatchGoldenTests
{
    private sealed record Replay(List<string> Events, int SkipCount, List<string[]> AckBatches);

    private static async Task<Replay> ReplayAsync(JsonElement go)
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        var time      = new FakeTimeProvider();
        var handler   = new FakeHttpHandler { Time = time };
        handler.Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json+protobuf", Encoding.UTF8.GetBytes("[]"));
        var http       = new GmHttpClient(new HttpClient(handler), new HttpClient(handler), auth.Cookies, time, NullLogger.Instance);
        var rpc        = new RpcSession(http, auth, GmEndpoints.Production, time, NullLogger.Instance);
        var dispatcher = new UpdateDispatcher(rpc, NullLogger.Instance);

        var events = new List<string>();
        dispatcher.EventRaised += evt => events.Add(Describe(evt));

        // Replay Go's Steps
        foreach (JsonElement step in go.GetProperty("steps").EnumerateArray())
        {
            if (step.TryGetProperty("skipCount", out JsonElement skip))
            {
                dispatcher.SkipCount = skip.GetInt32();
                continue;
            }

            dispatcher.Handle(PbLiteSerializer.Deserialize<IncomingRPCMessage>(Encoding.UTF8.GetBytes(step.GetProperty("pblite").GetString()!)));
        }

        // The "Logged Out" Step Flushed Acks In The Background; Wait For It, Then Flush The Rest Like Go Does
        Assert.IsTrue(SpinWait.SpinUntil(() => handler.Requests.Count >= 1, TimeSpan.FromSeconds(10)), "logged-out ack flush was not sent");
        await rpc.FlushAcksAsync(CancellationToken.None);

        List<string[]> batches = [.. handler.Requests
            .Where(r => r.Url.EndsWith("/AckMessages", StringComparison.Ordinal))
            .Select(r => PbLiteSerializer.Deserialize<AckMessageRequest>(r.Body).Acks.Select(a => a.RequestID).ToArray())];
        return new Replay(events, dispatcher.SkipCount, batches);
    }

    // Same Shape As Go's goldenEventName: "Type|IsOld|Id"
    private static string Describe(object evt) => evt switch
    {
        MessageReceived e     => $"MessageReceived|{e.IsOld}|{e.Message.MessageID}",
        ConversationUpdated e => $"ConversationUpdated|False|{e.Conversation.ConversationID}",
        TypingChanged e       => $"TypingChanged|False|{e.Data.ConversationID}",
        SettingsChanged       => "SettingsChanged|False|",
        UserAlert e           => $"UserAlert|False|{UserAlertEvent.Descriptor.FindFieldByName("alertType").EnumType.FindValueByNumber((int)e.Alert.AlertType).Name}",
        AccountChanged e      => $"AccountChanged|{e.IsFake}|{e.Change.Account}",
        GaiaLoggedOut         => "GaiaLoggedOut|False|",
        _                     => throw new AssertFailedException($"unexpected event {evt.GetType().Name}"),
    };

    /// <summary>
    /// The events, their order, the old flags and the final skip count match Go's.
    /// </summary>
    [TestMethod]
    public async Task Dispatch_ReplayOfGoInputs_ProducesGoEventSequence()
    {
        JsonElement go = Golden.Load("dispatch");

        Replay replay = await ReplayAsync(go);

        string[] expected = [.. go.GetProperty("events").EnumerateArray().Select(e =>
            $"{e.GetProperty("type").GetString()}|{e.GetProperty("isOld").GetBoolean()}|{e.GetProperty("id").GetString()}")];
        CollectionAssert.AreEqual(expected, replay.Events);
        Assert.AreEqual(go.GetProperty("finalSkipCount").GetInt32(), replay.SkipCount);
    }

    /// <summary>
    /// Every message is acked, and the ack batches (split by the "logged out" flush) match Go's.
    /// </summary>
    [TestMethod]
    public async Task Dispatch_AcksQueued_MatchGo()
    {
        JsonElement go = Golden.Load("dispatch");

        Replay replay = await ReplayAsync(go);

        string[][] expected = [.. go.GetProperty("ackBatches").EnumerateArray().Select(b => b.EnumerateArray().Select(id => id.GetString()!).ToArray())];
        Assert.AreEqual(expected.Length, replay.AckBatches.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            CollectionAssert.AreEqual(expected[i], replay.AckBatches[i], $"batch {i}");
        }
    }
}

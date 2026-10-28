using System.Text;
using System.Text.Json;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Proto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Protocol.Tests.LongPoll;

/// <summary>
/// Checks <see cref="LongPollStreamReader"/> against the payload sequence Go decodes, plus C#-only robustness.
/// </summary>
/// <remarks>
/// The Go stream and expected sequence come from <c>longpoll-stream.json</c>. Cutting reads at odd places, brackets
/// inside strings, bad payloads and the idle timeout are C#-only behavior (Go's reader fails on the first) with
/// hand-written expectations.
/// </remarks>
[TestClass]
public class LongPollStreamReaderTests
{
    // Hands out one queued chunk per read, then ends
    private sealed class ChunkedStream(IEnumerable<byte[]> chunks) : MemoryStream
    {
        private readonly Queue<byte[]> _chunks = new(chunks);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_chunks.Count == 0)
            {
                return ValueTask.FromResult(0);
            }

            byte[] chunk = _chunks.Dequeue();
            chunk.CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }
    }

    // Never has data; waits until the read is cancelled
    private sealed class SilentStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    // Fake clock that signals when a timer is created, so tests advance only once the wait exists
    private sealed class SignalingTimeProvider : FakeTimeProvider
    {
        public SemaphoreSlim TimerCreated { get; } = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            TimerCreated.Release();
            return timer;
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private static byte[][] Pieces(string text, int size) => [.. Encoding.UTF8.GetBytes(text).Chunk(size)];

    private static async Task<List<LongPollingPayload>> ReadAllAsync(Stream body, ILogger? logger = null)
    {
        var result = new List<LongPollingPayload>();
        await foreach (LongPollingPayload p in LongPollStreamReader.ReadAsync(body, TimeSpan.FromMinutes(1), TimeProvider.System, CancellationToken.None, logger))
        {
            result.Add(p);
        }

        return result;
    }

    private static async Task DrainAsync(Stream body, TimeSpan idle, TimeProvider time, CancellationToken ct)
    {
        await foreach (LongPollingPayload _ in LongPollStreamReader.ReadAsync(body, idle, time, ct))
        {
        }
    }

    private static void AssertGoSequence(List<LongPollingPayload> actual)
    {
        JsonElement[] expected = [.. Golden.Load("longpoll-stream").GetProperty("sequence").EnumerateArray()];
        Assert.AreEqual(4, expected.Length);
        Assert.HasCount(expected.Length, actual);

        for (int i = 0; i < expected.Length; i++)
        {
            LongPollingPayload p = actual[i];
            switch (expected[i].GetProperty("kind").GetString())
            {
                case "data":
                    Assert.IsNotNull(p.Data);
                    Assert.AreEqual(expected[i].GetProperty("responseId").GetString(), p.Data.ResponseID);
                    break;
                case "ack":
                    Assert.IsNotNull(p.Ack);
                    Assert.AreEqual(expected[i].GetProperty("count").GetInt32(), p.Ack.Count);
                    break;
                case "heartbeat":
                    Assert.IsNotNull(p.Heartbeat);
                    break;
                case "startRead":
                    Assert.IsNotNull(p.StartRead);
                    break;
                default:
                    Assert.Fail("unknown kind in golden file");
                    break;
            }
        }
    }

    /// <summary>
    /// Go's own read pattern (one payload per read) gives the sequence Go decoded.
    /// </summary>
    [TestMethod]
    public async Task GoStream_OnePayloadPerRead_YieldsGoSequence()
    {
        IEnumerable<string> chunks = Golden.Load("longpoll-stream").GetProperty("chunks").EnumerateArray().Select(e => e.GetString()!);
        AssertGoSequence(await ReadAllAsync(new ChunkedStream(chunks.Select(Encoding.UTF8.GetBytes))));
    }

    /// <summary>
    /// C#-only: three bytes per read still gives the Go sequence.
    /// </summary>
    [TestMethod]
    public async Task GoStream_ThreeBytesPerRead_YieldsGoSequence()
    {
        string body = Golden.Load("longpoll-stream").GetProperty("body").GetString()!;
        AssertGoSequence(await ReadAllAsync(new ChunkedStream(Pieces(body, 3))));
    }

    /// <summary>
    /// C#-only: the whole body in one read still gives the Go sequence.
    /// </summary>
    [TestMethod]
    public async Task GoStream_WholeBodyInOneRead_YieldsGoSequence()
    {
        string body = Golden.Load("longpoll-stream").GetProperty("body").GetString()!;
        AssertGoSequence(await ReadAllAsync(new ChunkedStream([Encoding.UTF8.GetBytes(body)])));
    }

    /// <summary>
    /// C#-only: brackets, quotes and escapes inside a string do not end a payload early.
    /// </summary>
    [TestMethod]
    public async Task StringContainingBrackets_DoesNotConfuseSplitter()
    {
        string body = "[[[null,[\"a]],[[\\\"b\\\\\"]],[null,null,[]]]]";

        List<LongPollingPayload> payloads = await ReadAllAsync(new ChunkedStream(Pieces(body, 1)));

        Assert.HasCount(2, payloads);
        Assert.AreEqual("a]],[[\"b\\", payloads[0].Data.ResponseID);
        Assert.IsNotNull(payloads[1].Heartbeat);
    }

    /// <summary>
    /// C#-only: a body that does not start with <c>[[</c> is rejected.
    /// </summary>
    [TestMethod]
    public async Task MissingOpening_Throws()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(new ChunkedStream([Encoding.UTF8.GetBytes("{\"a\":1}")])));
    }

    /// <summary>
    /// C#-only: a payload that does not decode is logged by length only and skipped; the next one still comes out.
    /// </summary>
    [TestMethod]
    public async Task BadPayload_SkippedAndNextStillYielded()
    {
        var logger = new CollectingLogger();
        string body = "[[[null,\"secret-text\",null],[null,null,[]]]]";

        List<LongPollingPayload> payloads = await ReadAllAsync(new ChunkedStream([Encoding.UTF8.GetBytes(body)]), logger);

        Assert.HasCount(1, payloads);
        Assert.IsNotNull(payloads[0].Heartbeat);
        Assert.HasCount(1, logger.Messages);
        Assert.DoesNotContain("secret-text", logger.Messages[0]);
    }

    // Hands out one queued chunk per release, so a test controls when each read completes
    private sealed class GatedStream(IEnumerable<byte[]> chunks) : MemoryStream
    {
        private readonly Queue<byte[]> _chunks = new(chunks);

        public SemaphoreSlim Gate { get; } = new(0);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken);
            if (_chunks.Count == 0)
            {
                return 0;
            }

            byte[] chunk = _chunks.Dequeue();
            chunk.CopyTo(buffer);
            return chunk.Length;
        }
    }

    /// <summary>
    /// C#-only: the idle timer restarts on every read, so slow reads that each beat the timeout never time out.
    /// </summary>
    [TestMethod]
    public async Task IdleTimer_RestartsOnEveryRead()
    {
        var time      = new SignalingTimeProvider();
        TimeSpan idle = TimeSpan.FromSeconds(30);
        string body   = "[[[null,null,[]],[null,null,[]]]]";
        var stream    = new GatedStream(Pieces(body, (body.Length + 3) / 4));
        var result    = new List<LongPollingPayload>();

        Task read = Task.Run(async () =>
        {
            await foreach (LongPollingPayload p in LongPollStreamReader.ReadAsync(stream, idle, time, CancellationToken.None))
            {
                result.Add(p);
            }
        });

        // Four Reads, 20 s Each: 80 s Total, Over The 30 s Timeout
        for (int i = 0; i < 4; i++)
        {
            Assert.IsTrue(await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10)), "idle timer was not created");
            time.Advance(TimeSpan.FromSeconds(20));
            stream.Gate.Release();
        }

        await read;
        Assert.HasCount(2, result);
    }

    /// <summary>
    /// C#-only: a stray byte between payloads is rejected.
    /// </summary>
    [TestMethod]
    public async Task JunkBetweenPayloads_Throws()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => ReadAllAsync(new ChunkedStream([Encoding.UTF8.GetBytes("[[[null,null,[]]x[null,null,[]]]]")])));
    }

    /// <summary>
    /// C#-only: whitespace between payloads is skipped and a trailing override object inside a payload wins.
    /// </summary>
    [TestMethod]
    public async Task WhitespaceBetweenPayloads_AndTrailingObject_Decode()
    {
        string body = "[[ \r\n[null,null,null,[2],{\"4\":[7]}] \n ,\n [null,null,[]] \n]]";

        List<LongPollingPayload> payloads = await ReadAllAsync(new ChunkedStream(Pieces(body, 2)));

        Assert.HasCount(2, payloads);
        Assert.AreEqual(7, payloads[0].Ack.Count);
        Assert.IsNotNull(payloads[1].Heartbeat);
    }

    /// <summary>
    /// C#-only: one payload past the cap throws; the cap is small here so the test stays light.
    /// </summary>
    [TestMethod]
    public async Task OversizedPayload_Throws()
    {
        string body = "[[[null,[\"" + new string('x', 200) + "\"]]]]";
        var stream  = new ChunkedStream(Pieces(body, 16));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await foreach (LongPollingPayload _ in LongPollStreamReader.ReadWithCapAsync(stream, TimeSpan.FromMinutes(1), TimeProvider.System, null, 64, CancellationToken.None))
            {
            }
        });
    }

    /// <summary>
    /// C#-only: ending mid-payload logs the dropped byte count (length only) and yields nothing.
    /// </summary>
    [TestMethod]
    public async Task EofMidPayload_LogsDroppedLength()
    {
        var logger = new CollectingLogger();

        List<LongPollingPayload> payloads = await ReadAllAsync(new ChunkedStream([Encoding.UTF8.GetBytes("[[[null,null,[]")]), logger);

        Assert.IsEmpty(payloads);
        Assert.HasCount(1, logger.Messages);
        Assert.Contains("13", logger.Messages[0]);
    }

    /// <summary>
    /// C#-only: a zero or negative idle timeout is rejected up front; infinite is allowed.
    /// </summary>
    [TestMethod]
    public void IdleTimeout_MustBePositiveOrInfinite()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LongPollStreamReader.ReadAsync(new MemoryStream(), TimeSpan.Zero, TimeProvider.System, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LongPollStreamReader.ReadAsync(new MemoryStream(), TimeSpan.FromSeconds(-1), TimeProvider.System, CancellationToken.None));
        Assert.IsNotNull(LongPollStreamReader.ReadAsync(new MemoryStream(), Timeout.InfiniteTimeSpan, TimeProvider.System, CancellationToken.None));
    }

    /// <summary>
    /// C#-only: no bytes for the idle timeout throws <see cref="TimeoutException"/>; caller cancel stays a cancel.
    /// </summary>
    [TestMethod]
    public async Task NoBytesForIdleTimeout_ThrowsTimeout()
    {
        var time      = new SignalingTimeProvider();
        TimeSpan idle = TimeSpan.FromSeconds(30);

        // Idle Timeout
        Task read = DrainAsync(new SilentStream(), idle, time, CancellationToken.None);
        Assert.IsTrue(await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10)), "idle timer was not created");
        time.Advance(idle);
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => read);

        // Caller Cancellation
        using var cts = new CancellationTokenSource();
        Task cancelled = DrainAsync(new SilentStream(), idle, time, cts.Token);
        Assert.IsTrue(await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10)), "idle timer was not created");
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
    }
}

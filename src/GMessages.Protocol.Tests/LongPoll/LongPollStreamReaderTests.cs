using System.Text;
using System.Text.Json;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Protocol.Tests.LongPoll;

/// <summary>
/// Checks <see cref="LongPollStreamReader"/> against the payload sequence that Go decodes. Other tests check the robustness that only C# has.
/// </summary>
/// <remarks>
/// The Go stream and the expected sequence come from <c>longpoll-stream.json</c>. Reads cut at odd places, brackets
/// inside strings, bad payloads and the idle timeout have no Go equivalent. The Go reader fails on the first of them.
/// Their expectations are hand-written.
/// </remarks>
[TestClass]
public class LongPollStreamReaderTests
{
    // Returns one queued chunk for each read, then ends.
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

    // Never has data. A read waits until the caller cancels it.
    private sealed class SilentStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
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
        await foreach (LongPollingPayload p in LongPollStreamReader.ReadAsync(body, TimeSpan.FromMinutes(1), TimeProvider.System, logger, CancellationToken.None))
        {
            result.Add(p);
        }

        return result;
    }

    private static async Task DrainAsync(Stream body, TimeSpan idle, TimeProvider time, CancellationToken ct)
    {
        await foreach (LongPollingPayload _ in LongPollStreamReader.ReadAsync(body, idle, time, null, ct))
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
    /// The Go read pattern, one payload for each read, gives the sequence that Go decoded.
    /// </summary>
    [TestMethod]
    public async Task GoStream_OnePayloadPerRead_YieldsGoSequence()
    {
        IEnumerable<string> chunks = Golden.Load("longpoll-stream").GetProperty("chunks").EnumerateArray().Select(e => e.GetString()!);
        AssertGoSequence(await ReadAllAsync(new ChunkedStream(chunks.Select(Encoding.UTF8.GetBytes))));
    }

    /// <summary>
    /// Reads of three bytes still give the Go sequence. Go has no such test.
    /// </summary>
    [TestMethod]
    public async Task GoStream_ThreeBytesPerRead_YieldsGoSequence()
    {
        string body = Golden.Load("longpoll-stream").GetProperty("body").GetString()!;
        AssertGoSequence(await ReadAllAsync(new ChunkedStream(Pieces(body, 3))));
    }

    /// <summary>
    /// A read that returns the whole body still gives the Go sequence. Go has no such test.
    /// </summary>
    [TestMethod]
    public async Task GoStream_WholeBodyInOneRead_YieldsGoSequence()
    {
        string body = Golden.Load("longpoll-stream").GetProperty("body").GetString()!;
        AssertGoSequence(await ReadAllAsync(new ChunkedStream([Encoding.UTF8.GetBytes(body)])));
    }

    /// <summary>
    /// Brackets, quotes and escapes inside a string do not end a payload early. Go has no such test.
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
    /// The reader rejects a body that does not start with <c>[[</c>.
    /// </summary>
    [TestMethod]
    public async Task MissingOpening_Throws()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(new ChunkedStream([Encoding.UTF8.GetBytes("{\"a\":1}")])));
    }

    /// <summary>
    /// The reader logs only the length of a payload that does not decode, and skips it. The next payload still comes out.
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

    // Returns one queued chunk for each gate release, so a test controls when each read completes.
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
    /// The idle timer restarts on every read. Slow reads that each end before the timeout never time out.
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
            await foreach (LongPollingPayload p in LongPollStreamReader.ReadAsync(stream, idle, time, null, CancellationToken.None))
            {
                result.Add(p);
            }
        });

        // Four reads of 20 s each take 80 s in total. This is more than the 30 s timeout.
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
    /// The reader rejects a stray byte between payloads.
    /// </summary>
    [TestMethod]
    public async Task JunkBetweenPayloads_Throws()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => ReadAllAsync(new ChunkedStream([Encoding.UTF8.GetBytes("[[[null,null,[]]x[null,null,[]]]]")])));
    }

    /// <summary>
    /// The reader skips whitespace between payloads. A trailing override object inside a payload wins.
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
    /// A payload over the cap throws. The test uses a small cap to stay light.
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
    /// A stream that ends inside a payload logs the number of dropped bytes, and nothing else. The reader yields no payload.
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
    /// The reader rejects a zero or negative idle timeout at the start. It accepts an infinite timeout.
    /// </summary>
    [TestMethod]
    public void IdleTimeout_MustBePositiveOrInfinite()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LongPollStreamReader.ReadAsync(new MemoryStream(), TimeSpan.Zero, TimeProvider.System, null, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LongPollStreamReader.ReadAsync(new MemoryStream(), TimeSpan.FromSeconds(-1), TimeProvider.System, null, CancellationToken.None));
        Assert.IsNotNull(LongPollStreamReader.ReadAsync(new MemoryStream(), Timeout.InfiniteTimeSpan, TimeProvider.System, null, CancellationToken.None));
    }

    /// <summary>
    /// No bytes for the idle timeout throws <see cref="TimeoutException"/>. A cancel from the caller stays a cancel.
    /// </summary>
    [TestMethod]
    public async Task NoBytesForIdleTimeout_ThrowsTimeout()
    {
        var time      = new SignalingTimeProvider();
        TimeSpan idle = TimeSpan.FromSeconds(30);

        Task read = DrainAsync(new SilentStream(), idle, time, CancellationToken.None);
        Assert.IsTrue(await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10)), "idle timer was not created");
        time.Advance(idle);
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => read);

        using var cts = new CancellationTokenSource();
        Task canceled = DrainAsync(new SilentStream(), idle, time, cts.Token);
        Assert.IsTrue(await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10)), "idle timer was not created");
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
    }
}

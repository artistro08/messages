using System.Runtime.CompilerServices;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.LongPoll;

/// <summary>
/// Splits the long-poll response body into <see cref="LongPollingPayload"/> messages.
/// </summary>
/// <remarks>
/// Ported from <c>readLongPoll</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go">longpoll.go</see>.
/// Google sends 1 endless response: <c>[[</c>, then payloads with commas between them, then <c>]]</c>. Go expects 1
/// payload in each network read and checks the buffered text again with <c>json.Valid</c>. This reader does not
/// depend on read sizes. It scans the bytes for the end of each top-level array. It tracks strings and escapes, so
/// brackets inside text do not confuse it. Thus each split of the stream into reads gives the same payloads. The
/// reader compacts the buffer after each payload, so a connection that runs all day does not grow it.
/// </remarks>
public static partial class LongPollStreamReader
{
    private const int InitialBufferSize = 8 * 1024;
    private const int ShrinkAboveSize   = 1024 * 1024;

    // ponytail: a fixed 32 MB limit on 1 payload in progress. Real payloads are a few KB. Make it a setting when a real payload is larger.
    private const int MaxPayloadBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Reads payloads from the response body until the end marker or the end of the stream.
    /// </summary>
    /// <remarks>
    /// The body must start with <c>[[</c>. The reader decodes each complete top-level array as PBLite. It logs the
    /// length of an array that does not decode, never the contents, and skips it. The read stops cleanly at the closing
    /// <c>]]</c> or at the end of the stream. The idle timer starts again on each read, so only silence causes a
    /// timeout, not the total length.
    /// </remarks>
    /// <param name="body">The response body stream.</param>
    /// <param name="idleTimeout">The longest wait for bytes.</param>
    /// <param name="time">The clock for the idle timer.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <param name="logger">The log for skipped payloads. Null logs nothing.</param>
    /// <returns>The decoded payloads in arrival order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> or <paramref name="time"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleTimeout"/> is zero or negative (and not <see cref="Timeout.InfiniteTimeSpan"/>).</exception>
    /// <exception cref="InvalidDataException">The body does not start with <c>[[</c>, has stray bytes between payloads, or one payload grows past 32 MB.</exception>
    /// <exception cref="TimeoutException">No bytes arrived within <paramref name="idleTimeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public static IAsyncEnumerable<LongPollingPayload> ReadAsync(
        Stream body,
        TimeSpan idleTimeout,
        TimeProvider time,
        CancellationToken ct,
        ILogger? logger = null)
        => ReadWithCapAsync(body, idleTimeout, time, logger, MaxPayloadBytes, ct);

    // The same as the public method, with a payload limit that tests set. Thus tests do not allocate 32 MB.
    internal static IAsyncEnumerable<LongPollingPayload> ReadWithCapAsync(
        Stream body,
        TimeSpan idleTimeout,
        TimeProvider time,
        ILogger? logger,
        int maxPayloadBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(time);
        if (idleTimeout <= TimeSpan.Zero && idleTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "Idle timeout must be positive or infinite.");
        }

        return ReadCoreAsync(body, idleTimeout, time, logger ?? NullLogger.Instance, maxPayloadBytes, ct);
    }

    private static async IAsyncEnumerable<LongPollingPayload> ReadCoreAsync(
        Stream body,
        TimeSpan idleTimeout,
        TimeProvider time,
        ILogger logger,
        int maxPayloadBytes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        byte[] buffer     = new byte[InitialBufferSize];
        int length        = 0;
        int pos           = 0;
        int payloadStart  = -1;
        int depth         = 0;
        bool inString     = false;
        bool escaped      = false;
        bool opened       = false;

        while (true)
        {
            while (opened ? pos < length : length >= 2)
            {
                if (!opened)
                {
                    if (buffer[0] != (byte)'[' || buffer[1] != (byte)'[')
                    {
                        throw new InvalidDataException("Long-poll stream does not start with [[.");
                    }

                    opened = true;
                    pos    = 2;
                    continue;
                }

                byte b = buffer[pos];

                if (depth == 0)
                {
                    // Depth 0 is the space between payloads.
                    if (b == (byte)'[')
                    {
                        payloadStart  = pos;
                        depth         = 1;
                    }
                    else if (b == (byte)']')
                    {
                        // The closing ]] ends the stream.
                        yield break;
                    }
                    else if (b is not ((byte)',' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                    {
                        throw new InvalidDataException("Unexpected byte between long-poll payloads.");
                    }
                }
                else if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (b == (byte)'\\')
                    {
                        escaped = true;
                    }
                    else if (b == (byte)'"')
                    {
                        inString = false;
                    }
                }
                else if (b == (byte)'"')
                {
                    inString = true;
                }
                else if (b is (byte)'[' or (byte)'{')
                {
                    depth++;
                }
                else if (b is (byte)']' or (byte)'}')
                {
                    depth--;

                    if (depth == 0)
                    {
                        LongPollingPayload? payload = Decode(buffer.AsSpan(payloadStart, pos - payloadStart + 1), logger);
                        payloadStart = -1;

                        if (payload is not null)
                        {
                            pos++;
                            yield return payload;
                            continue;
                        }
                    }
                }

                pos++;
            }

            if (payloadStart >= 0 && length - payloadStart > maxPayloadBytes)
            {
                throw new InvalidDataException($"Long-poll payload is larger than {maxPayloadBytes} bytes.");
            }

            int keep = payloadStart >= 0 ? payloadStart : pos;
            if (keep > 0)
            {
                Buffer.BlockCopy(buffer, keep, buffer, 0, length - keep);
                length -= keep;
                pos    -= keep;
                if (payloadStart >= 0)
                {
                    payloadStart = 0;
                }
            }

            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            else if (buffer.Length > ShrinkAboveSize && length < buffer.Length / 4)
            {
                // A huge payload is gone, so give the memory back.
                Array.Resize(ref buffer, Math.Max(InitialBufferSize, length * 2));
            }

            int read;
            using (var idle = new CancellationTokenSource(idleTimeout, time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token))
            {
                try
                {
                    read = await body.ReadAsync(buffer.AsMemory(length), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && idle.IsCancellationRequested)
                {
                    throw new TimeoutException($"No long-poll data for {idleTimeout}.");
                }
            }

            if (read == 0)
            {
                // The stream ended. Log any payload that arrived only in part.
                if (payloadStart >= 0)
                {
                    LogDroppedPartial(logger, length - payloadStart);
                }

                yield break;
            }

            length += read;
        }
    }

    private static LongPollingPayload? Decode(ReadOnlySpan<byte> json, ILogger logger)
    {
        try
        {
            return PbLiteSerializer.Deserialize<LongPollingPayload>(json);
        }
        catch (PbLiteException)
        {
            LogBadPayload(logger, json.Length);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped a long-poll payload that did not decode ({Length} bytes)")]
    private static partial void LogBadPayload(ILogger logger, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Long-poll stream ended mid-payload; dropped {Length} bytes")]
    private static partial void LogDroppedPartial(ILogger logger, int length);
}

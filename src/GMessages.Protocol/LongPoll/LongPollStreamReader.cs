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
/// Google sends one endless response shaped <c>[[</c>, payloads separated by commas, then <c>]]</c>. Go assumes
/// every network read holds exactly one payload and re-checks the buffered text with <c>json.Valid</c>. This reader
/// does not rely on read sizes: it scans the bytes for where each top-level array ends, tracking strings and escapes
/// so brackets inside text never confuse it, so any way of cutting the stream into reads gives the same payloads.
/// The buffer is compacted after each payload so a connection that runs all day does not grow it.
/// </remarks>
public static partial class LongPollStreamReader
{
    private const int InitialBufferSize = 8 * 1024;
    private const int ShrinkAboveSize   = 1024 * 1024;

    /// <summary>
    /// Reads payloads from the response body until the end marker or end of stream.
    /// </summary>
    /// <remarks>
    /// Needs the body to start with <c>[[</c>. Each complete top-level array is decoded as PBLite; one that fails to
    /// decode is logged (length only, never contents) and skipped. A clean stop happens at the closing <c>]]</c> or
    /// when the stream ends. The idle timer restarts on every read, so only silence (not total length) times out.
    /// </remarks>
    /// <param name="body">The response body stream.</param>
    /// <param name="idleTimeout">How long to wait for any bytes before giving up.</param>
    /// <param name="time">The clock used for the idle timer.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <param name="logger">Where skipped payloads are logged; null logs nothing.</param>
    /// <returns>The decoded payloads in arrival order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> or <paramref name="time"/> is null.</exception>
    /// <exception cref="InvalidDataException">The body does not start with <c>[[</c> or has stray bytes between payloads.</exception>
    /// <exception cref="TimeoutException">No bytes arrived within <paramref name="idleTimeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static IAsyncEnumerable<LongPollingPayload> ReadAsync(
        Stream body,
        TimeSpan idleTimeout,
        TimeProvider time,
        CancellationToken ct,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(time);

        return ReadCoreAsync(body, idleTimeout, time, logger ?? NullLogger.Instance, ct);
    }

    private static async IAsyncEnumerable<LongPollingPayload> ReadCoreAsync(
        Stream body,
        TimeSpan idleTimeout,
        TimeProvider time,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Scanner State (kept across reads)
        byte[] buffer     = new byte[InitialBufferSize];
        int length        = 0;
        int pos           = 0;
        int payload_start = -1;
        int depth         = 0;
        bool in_string    = false;
        bool escaped      = false;
        bool opened       = false;

        while (true)
        {
            // Scan Buffered Bytes
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
                    // Between Payloads
                    if (b == (byte)'[')
                    {
                        payload_start = pos;
                        depth         = 1;
                    }
                    else if (b == (byte)']')
                    {
                        // End Marker
                        yield break;
                    }
                    else if (b is not ((byte)',' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                    {
                        throw new InvalidDataException("Unexpected byte between long-poll payloads.");
                    }
                }
                else if (in_string)
                {
                    // Inside A JSON String
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
                        in_string = false;
                    }
                }
                else if (b == (byte)'"')
                {
                    in_string = true;
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
                        // Payload Complete
                        LongPollingPayload? payload = Decode(buffer.AsSpan(payload_start, pos - payload_start + 1), logger);
                        payload_start = -1;

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

            // Compact Consumed Bytes
            int keep = payload_start >= 0 ? payload_start : pos;
            if (keep > 0)
            {
                Buffer.BlockCopy(buffer, keep, buffer, 0, length - keep);
                length -= keep;
                pos    -= keep;
                if (payload_start >= 0)
                {
                    payload_start = 0;
                }
            }

            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            else if (buffer.Length > ShrinkAboveSize && length < buffer.Length / 4)
            {
                // A Huge Payload Is Gone, Give The Memory Back
                Array.Resize(ref buffer, Math.Max(InitialBufferSize, length * 2));
            }

            // Read With Idle Timeout
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
                // Stream Ended
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
}

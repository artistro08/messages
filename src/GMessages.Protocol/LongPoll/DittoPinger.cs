using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.LongPoll;

/// <summary>
/// Pings the phone to tell whether it is still answering, and recovers when it stops.
/// </summary>
/// <remarks>
/// Ported from <c>dittoPinger</c>, <c>onPhoneActivity</c> and <c>requestUpdatesAfterGap</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go">longpoll.go</see>.
/// A ping goes out each minute. A ping also goes out at once when a request that the user waits on is slow. The
/// first unanswered ping raises <see cref="PhoneNotResponding"/>. After that, only <see cref="AlertTimeoutCount"/>
/// misses in a row raise it, because phones doze all the time. While the phone is silent, a recovery loop pings again
/// on doubling intervals, from 1 minute up to 64 minutes. It re-arms the event subscription after 2 misses and
/// reconnects after 3 misses. When pings failed, an answer or data from the phone raises
/// <see cref="PhoneRespondingAgain"/>. The wait for a ping never blocks the main loop. The main loop also runs the
/// data-receive check. That check sends an extra update request when no data arrived for a long time. Each wait uses
/// the <see cref="TimeProvider"/>. 1 pinger lives for 1 <see cref="LongPollLoop.RunAsync"/>. Its counters are in
/// <see cref="PhoneLiveness"/>, which the loop owns. Thus the counters outlive the pinger, as in Go.
/// </remarks>
/// <param name="rpc">Sends the pings and the update requests.</param>
/// <param name="liveness">The shared phone liveness state.</param>
/// <param name="time">The clock for each wait.</param>
/// <param name="log">The logger.</param>
/// <param name="setActiveSession">Re-arms the event subscription of the phone. Go uses <c>SetActiveSession</c>.</param>
/// <param name="reconnect">Drops the long poll and opens it again. Go uses <c>Reconnect</c>.</param>
internal sealed partial class DittoPinger(
    RpcSession rpc,
    PhoneLiveness liveness,
    TimeProvider time,
    ILogger log,
    Func<CancellationToken, Task> setActiveSession,
    Func<Task> reconnect)
{
    /// <summary>Ported from <c>defaultPingTimeout</c>.</summary>
    public static readonly TimeSpan DefaultPingTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Ported from <c>shortPingTimeout</c>.</summary>
    public static readonly TimeSpan ShortPingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Ported from <c>minPingInterval</c>.</summary>
    public static readonly TimeSpan MinPingInterval = TimeSpan.FromSeconds(30);

    /// <summary>Ported from <c>initialRecoveryInterval</c>.</summary>
    public static readonly TimeSpan InitialRecoveryInterval = TimeSpan.FromMinutes(1);

    /// <summary>Ported from <c>maxRepingTickerTime</c>.</summary>
    public static readonly TimeSpan MaxRecoveryInterval = TimeSpan.FromMinutes(64);

    /// <summary>The time without data before an extra update request. Ported from <c>DefaultBugleDefaultCheckInterval</c>.</summary>
    public static readonly TimeSpan DataReceiveCheckInterval = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(55);

    /// <summary>Ported from <c>setActiveSessionAfterTimeouts</c>.</summary>
    public const int SetActiveSessionAfterTimeouts = 2;

    /// <summary>Ported from <c>reconnectAfterTimeouts</c>.</summary>
    public const int ReconnectAfterTimeouts = 3;

    /// <summary>The misses in a row before <see cref="PhoneNotResponding"/>. The Go client default is 4.</summary>
    public const int AlertTimeoutCount = 4;

    private int _recovering;
    private long _pingId;
    private int _answeredPings;
    private TaskCompletionSource? _shortCircuit;
    private CancellationToken _stopToken;

    /// <summary>Occurs for the pinger events: <see cref="PhoneNotResponding"/>, <see cref="PhoneRespondingAgain"/>, <see cref="PingFailed"/>, <see cref="NoDataReceived"/>.</summary>
    public event Action<object>? EventRaised;

    /// <summary>Gets how often the pinger pings normally. The Go client default is 1 minute.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets how many pings got an answer that the pinger handled in full. Tests use it to wait for an answer.</summary>
    internal int AnsweredPings => Volatile.Read(ref _answeredPings);

    /// <summary>Gets whether the recovery loop runs now.</summary>
    internal bool IsRecovering => Volatile.Read(ref _recovering) != 0;

    /// <summary>
    /// Runs the ping loop until <paramref name="ct"/> is canceled.
    /// </summary>
    /// <remarks>
    /// Ported from <c>dittoPinger.Loop</c>. <paramref name="ct"/> also stops each request that the pinger sends in the
    /// background, such as <see cref="RequestUpdatesAfterGapAsync"/>.
    /// </remarks>
    /// <param name="ct">Stops the loop, each pending ping wait and the recovery loop.</param>
    /// <returns>A task that completes after a stop. A stop is not an error.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        _stopToken = ct;
        while (!ct.IsCancellationRequested)
        {
            var shortCircuit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _shortCircuit, shortCircuit);

            bool elapsed = await DelayOrAsync(PingInterval, shortCircuit.Task, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            bool recovering = Volatile.Read(ref _recovering) != 0;
            if (!elapsed && recovering)
            {
                // A user waits during the recovery. Do not make the user wait for the end of the backoff for a notice.
                if (liveness.ShouldMarkNotResponding(AlertTimeoutCount))
                {
                    Raise(new PhoneNotResponding());
                }
            }
            else if (!elapsed)
            {
                await PingAsync(ShortPingTimeout, new PingResetter(time), ct).ConfigureAwait(false);
            }
            else if (!recovering)
            {
                await PingAsync(DefaultPingTimeout, new PingResetter(time), ct).ConfigureAwait(false);
            }

            if (liveness.ShouldDoDataReceiveCheck(DataReceiveCheckInterval))
            {
                LogNoDataReceived(log);
                _ = RequestUpdatesAfterGapAsync("no data received recently");
            }
        }
    }

    /// <summary>
    /// Pings at once when the loop waits. Otherwise the call does nothing, as the non-blocking channel send of Go does.
    /// </summary>
    public void ShortCircuit() => Volatile.Read(ref _shortCircuit)?.TrySetResult();

    /// <summary>
    /// Counts data from the phone outside of pings as a sign of life.
    /// </summary>
    /// <remarks>
    /// Ported from <c>onPhoneActivity</c>. When pings failed, the method stops the recovery. When
    /// <see cref="PhoneNotResponding"/> was raised, it also raises <see cref="PhoneRespondingAgain"/>.
    /// </remarks>
    /// <param name="source">What arrived, for the log.</param>
    public void OnPhoneActivity(string source)
    {
        (bool notRespondingSent, bool wasFailing, bool needsCatchUp) = liveness.OnLiveness();
        if (!wasFailing)
        {
            return;
        }

        LogActivityWhileUnresponsive(log, source, notRespondingSent);
        liveness.CurrentReset?.Done();
        if (notRespondingSent)
        {
            Raise(new PhoneRespondingAgain());
        }

        if (needsCatchUp)
        {
            _ = RequestUpdatesAfterGapAsync("phone started responding again");
        }
    }

    /// <summary>
    /// Moves the next data-receive check to at least <paramref name="after"/> from now.
    /// </summary>
    /// <remarks>Ported from <c>bumpNextDataReceiveCheck</c>.</remarks>
    /// <param name="after">The delay. Null means <see cref="DataReceiveCheckInterval"/>.</param>
    public void BumpDataReceiveCheck(TimeSpan? after = null) => liveness.BumpDataReceiveCheck(after ?? DataReceiveCheckInterval);

    /// <summary>
    /// Raises <see cref="NoDataReceived"/> and asks the phone to send the updates again.
    /// </summary>
    /// <remarks>
    /// Ported from <c>requestUpdatesAfterGap</c>. The method logs failures and never throws them. The request uses the
    /// stop token of the run. Thus no request goes out after a disconnect or a dispose.
    /// </remarks>
    /// <param name="reason">The reason, for the log.</param>
    /// <returns>A task that completes when the request goes out, fails or stops.</returns>
    public async Task RequestUpdatesAfterGapAsync(string reason)
    {
        CancellationToken ct = _stopToken;
        Raise(new NoDataReceived());
        try
        {
            await rpc.SendNoResponseAsync(new SendParams(ActionType.GetUpdates, RequestId: rpc.SessionId, OmitTtl: true), ct).ConfigureAwait(false);
            LogGapRequestSent(log, reason);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // A stop is not a failure.
        }
        catch (Exception ex)
        {
            LogGapRequestFailed(log, ex, reason);
        }
    }

    // =========================================================================
    // PINGS
    // =========================================================================

    // Port of dittoPinger.Ping. The method sends the ping, then waits for the answer in the background.
    private async Task PingAsync(TimeSpan timeout, PingResetter reset, CancellationToken ct)
    {
        long pingId = Interlocked.Increment(ref _pingId);
        if (!liveness.TryStartPing())
        {
            LogPingSkipped(log, pingId);
            return;
        }

        DateTimeOffset start = time.GetUtcNow();
        liveness.CurrentReset = reset;

        var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<IncomingRpc> wait;
        try
        {
            wait = await rpc.SendNoWaitAsync(new SendParams(ActionType.NotifyDittoActivity, new NotifyDittoActivityRequest { Success = true }), waitCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            waitCts.Dispose();

            // During a stop, a failed send is not a ping failure.
            if (ct.IsCancellationRequested)
            {
                return;
            }

            int sendFails = liveness.AddSendFail();
            LogPingSendFailed(log, ex, pingId);
            Raise(new PingFailed(new InvalidOperationException("Failed to notify ditto activity.", ex), sendFails));
            return;
        }

        _ = WaitForResponseAsync(pingId, start, wait, waitCts, timeout, reset, ct);
    }

    // Port of dittoPinger.WaitForResponse.
    private async Task WaitForResponseAsync(long pingId, DateTimeOffset start, Task<IncomingRpc> wait, CancellationTokenSource waitCts, TimeSpan timeout, PingResetter reset, CancellationToken ct)
    {
        using (waitCts)
        {
            Task timer = Task.Delay(timeout, time, waitCts.Token);
            Task first = await Task.WhenAny(wait, timer, reset.Task).ConfigureAwait(false);

            // Stop the wait. This cancels the waiter and the timer. A late answer still counts through OnPhoneActivity.
            await waitCts.CancelAsync().ConfigureAwait(false);

            // Observe a faulted wait now or later, so that a closed connection never gives an unobserved task exception.
            _ = wait.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            // A faulted waiter after a closed connection is not an answer. Go reads a closed channel in <-ping.ch as an
            // answer. That is an accident: session_handler.go lines 215 to 217 treat a closed channel as
            // ErrConnectionClosed. client.go line 383 closes the waiters because no answer can arrive any more.
            if (first == wait && wait.IsCompletedSuccessfully)
            {
                OnRespond(pingId, time.GetUtcNow() - start, reset);
            }
            else if (first == timer)
            {
                OnTimeout(pingId, timeout == ShortPingTimeout, reset);
            }
            else if (first == reset.Task)
            {
                LogPingSuperseded(log, pingId);
            }
        }
    }

    private void OnRespond(long pingId, TimeSpan took, PingResetter reset)
    {
        (bool notRespondingSent, bool wasFailing, bool needsCatchUp) = liveness.OnLiveness();
        LogPingAnswered(log, pingId, took, notRespondingSent, wasFailing);
        if (wasFailing)
        {
            Raise(new PhoneRespondingAgain());
        }

        if (needsCatchUp)
        {
            _ = RequestUpdatesAfterGapAsync("phone started responding to pings again");
        }

        reset.Done();
        Interlocked.Increment(ref _answeredPings);
    }

    private void OnTimeout(long pingId, bool urgent, PingResetter reset)
    {
        (int timeouts, bool sendNotResponding) = liveness.OnTimeout(AlertTimeoutCount);
        LogPingTimeout(log, pingId, timeouts, urgent);
        if (sendNotResponding)
        {
            Raise(new PhoneNotResponding());
        }

        if (Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
        {
            _ = RecoveryLoopAsync(reset);
        }
    }

    // Port of dittoPinger.recoveryLoop. It pings again on doubling intervals, then re-arms, then reconnects.
    private async Task RecoveryLoopAsync(PingResetter reset)
    {
        CancellationToken ct = _stopToken;
        try
        {
            bool didSetActiveSession = false;
            while (true)
            {
                if (!await DelayOrAsync(liveness.GetRecoveryInterval(), reset.Task, ct).ConfigureAwait(false) || ct.IsCancellationRequested)
                {
                    return;
                }

                int timeouts = liveness.TimeoutCount;
                if (timeouts == 0)
                {
                    return;
                }

                bool reconnected = false;
                if (timeouts >= ReconnectAfterTimeouts && liveness.CanDoRecovery())
                {
                    LogReconnecting(log, timeouts);
                    liveness.StartRecovery();
                    await reconnect().ConfigureAwait(false);
                    reconnected = true;
                }
                else if (timeouts >= SetActiveSessionAfterTimeouts && !didSetActiveSession)
                {
                    didSetActiveSession = true;
                    LogRearming(log, timeouts);
                    try
                    {
                        await setActiveSession(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        LogRearmFailed(log, ex);
                    }
                }

                liveness.AdvanceRecoveryInterval();
                if (reconnected)
                {
                    return;
                }

                await PingAsync(DefaultPingTimeout, reset, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // During a stop, a failure is not a recovery failure.
        }
        catch (Exception ex)
        {
            // As in Go, a failed reconnect ends the recovery.
            LogRecoveryFailed(log, ex);
        }
        finally
        {
            Volatile.Write(ref _recovering, 0);
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Waits for the delay or the other task. Returns true when the delay ends first.
    private async Task<bool> DelayOrAsync(TimeSpan delay, Task other, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task timer    = Task.Delay(delay, time, cts.Token);
        Task first    = await Task.WhenAny(timer, other).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        return first == timer && timer.IsCompletedSuccessfully;
    }

    private void Raise(object evt) => ProtocolLog.Raise(log, EventRaised, evt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping ping {PingId} since last one was too recently")]
    private static partial void LogPingSkipped(ILogger logger, long pingId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error sending ping {PingId}")]
    private static partial void LogPingSendFailed(ILogger logger, Exception exception, long pingId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ditto ping {PingId} successful after {Took}, was not responding {NotRespondingSent}, was failing {WasFailing}")]
    private static partial void LogPingAnswered(ILogger logger, long pingId, TimeSpan took, bool notRespondingSent, bool wasFailing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ditto ping {PingId} is taking long, phone may be offline. Timeouts {Timeouts}, urgent {Urgent}")]
    private static partial void LogPingTimeout(ILogger logger, long pingId, int timeouts, bool urgent);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Another ping was successful, giving up on ping {PingId}")]
    private static partial void LogPingSuperseded(ILogger logger, long pingId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Phone hasn't responded to {Timeouts} pings, reconnecting to get a new listener session")]
    private static partial void LogReconnecting(ILogger logger, int timeouts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Phone hasn't responded to {Timeouts} pings, re-arming its event subscription")]
    private static partial void LogRearming(ILogger logger, int timeouts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to set active session while recovering from ping timeouts")]
    private static partial void LogRearmFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ditto ping recovery stopped")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received data from phone ({Source}) while it was considered unresponsive, not responding sent {NotRespondingSent}")]
    private static partial void LogActivityWhileUnresponsive(ILogger logger, string source, bool notRespondingSent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No data received recently, sending extra GET_UPDATES call")]
    private static partial void LogNoDataReceived(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sent extra GET_UPDATES call: {Reason}")]
    private static partial void LogGapRequestSent(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send extra GET_UPDATES call: {Reason}")]
    private static partial void LogGapRequestFailed(ILogger logger, Exception exception, string reason);
}

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
/// A ping goes out every minute, and right away when a request the user waits on is slow. The first unanswered ping
/// raises <see cref="PhoneNotResponding"/>; after that only <see cref="AlertTimeoutCount"/> misses in a row do, since
/// phones doze constantly. While the phone is silent, a recovery loop re-pings on doubling intervals (1 minute up
/// to 64), re-arms the event subscription after 2 misses, and reconnects after 3. Any answer or data from the phone
/// raises <see cref="PhoneRespondingAgain"/> when things were failing. Waiting for a ping never blocks the main loop,
/// which also runs the data-receive check: an extra update request when nothing arrived for a long time. Every wait
/// uses the <see cref="TimeProvider"/>. One pinger lives for one <see cref="LongPollLoop.RunAsync"/>; the counters it
/// works on are in <see cref="PhoneLiveness"/>, which the loop owns, so they outlive the pinger as in Go.
/// </remarks>
/// <param name="rpc">Sends pings and update requests.</param>
/// <param name="liveness">The shared phone liveness state.</param>
/// <param name="time">The clock for every wait.</param>
/// <param name="log">The logger.</param>
/// <param name="setActiveSession">Re-arms the phone's event subscription (Go: <c>SetActiveSession</c>).</param>
/// <param name="reconnect">Drops and reopens the long poll (Go: <c>Reconnect</c>).</param>
internal sealed partial class DittoPinger(
    RpcSession rpc,
    PhoneLiveness liveness,
    TimeProvider time,
    ILogger log,
    Func<CancellationToken, Task> setActiveSession,
    Func<Task> reconnect)
{
    /// <summary>Go: <c>defaultPingTimeout</c>.</summary>
    public static readonly TimeSpan DefaultPingTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Go: <c>shortPingTimeout</c>.</summary>
    public static readonly TimeSpan ShortPingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Go: <c>minPingInterval</c>.</summary>
    public static readonly TimeSpan MinPingInterval = TimeSpan.FromSeconds(30);

    /// <summary>Go: <c>initialRecoveryInterval</c>.</summary>
    public static readonly TimeSpan InitialRecoveryInterval = TimeSpan.FromMinutes(1);

    /// <summary>Go: <c>maxRepingTickerTime</c>.</summary>
    public static readonly TimeSpan MaxRecoveryInterval = TimeSpan.FromMinutes(64);

    /// <summary>Go: <c>setActiveSessionAfterTimeouts</c>.</summary>
    public const int SetActiveSessionAfterTimeouts = 2;

    /// <summary>Go: <c>reconnectAfterTimeouts</c>.</summary>
    public const int ReconnectAfterTimeouts = 3;

    private int _recovering;
    private long _pingId;
    private int _answeredPings;
    private TaskCompletionSource? _shortCircuit;
    private CancellationToken _stopToken;

    /// <summary>Raised for pinger events: <see cref="PhoneNotResponding"/>, <see cref="PhoneRespondingAgain"/>, <see cref="PingFailed"/>, <see cref="NoDataReceived"/>.</summary>
    public event Action<object>? EventRaised;

    /// <summary>How often to ping normally (Go client default: 1 minute).</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Misses in a row before <see cref="PhoneNotResponding"/> (Go client default: 4).</summary>
    public int AlertTimeoutCount { get; init; } = 4;

    /// <summary>How long without data before an extra update request (Go: <c>DefaultBugleDefaultCheckInterval</c>, 2 h 55 min).</summary>
    public TimeSpan DataReceiveCheckInterval { get; init; } = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(55);

    /// <summary>Gets how many pings were answered and fully handled; lets tests wait for an answer deterministically.</summary>
    public int AnsweredPings => Volatile.Read(ref _answeredPings);

    /// <summary>Gets whether the recovery loop is running.</summary>
    public bool IsRecovering => Volatile.Read(ref _recovering) != 0;

    /// <summary>
    /// Runs the ping loop until <paramref name="ct"/> is canceled.
    /// </summary>
    /// <remarks>
    /// Ported from <c>dittoPinger.Loop</c>. <paramref name="ct"/> also stops every request the pinger sends in the
    /// background, such as <see cref="RequestUpdatesAfterGapAsync"/>.
    /// </remarks>
    /// <param name="ct">Stops the loop, every pending ping wait and the recovery loop.</param>
    /// <returns>A task that completes when stopped; stopping is not an error.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        _stopToken = ct;
        while (!ct.IsCancellationRequested)
        {
            var short_circuit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _shortCircuit, short_circuit);

            bool elapsed = await DelayOrAsync(PingInterval, short_circuit.Task, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            bool recovering = Volatile.Read(ref _recovering) != 0;
            if (!elapsed && recovering)
            {
                // A User Waits During Recovery: Do Not Make Them Wait Out The Backoff For A Notice
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
    /// Pings right away, if the loop is waiting; dropped otherwise (Go's non-blocking channel send).
    /// </summary>
    public void ShortCircuit() => Volatile.Read(ref _shortCircuit)?.TrySetResult();

    /// <summary>
    /// Counts data from the phone outside of pings as a sign of life.
    /// </summary>
    /// <remarks>Ported from <c>onPhoneActivity</c>. Stops recovery and raises <see cref="PhoneRespondingAgain"/> when the phone was considered silent.</remarks>
    /// <param name="source">What arrived, for the log.</param>
    public void OnPhoneActivity(string source)
    {
        (bool not_responding_sent, bool was_failing, bool needs_catch_up) = liveness.OnLiveness();
        if (!was_failing)
        {
            return;
        }

        LogActivityWhileUnresponsive(log, source, not_responding_sent);
        liveness.CurrentReset?.Done();
        if (not_responding_sent)
        {
            Raise(new PhoneRespondingAgain());
        }

        if (needs_catch_up)
        {
            _ = RequestUpdatesAfterGapAsync("phone started responding again");
        }
    }

    /// <summary>
    /// Pushes the next data-receive check out to at least <paramref name="after"/> from now.
    /// </summary>
    /// <remarks>Ported from <c>bumpNextDataReceiveCheck</c>.</remarks>
    /// <param name="after">The delay; null means <see cref="DataReceiveCheckInterval"/>.</param>
    public void BumpDataReceiveCheck(TimeSpan? after = null) => liveness.BumpDataReceiveCheck(after ?? DataReceiveCheckInterval);

    /// <summary>
    /// Raises <see cref="NoDataReceived"/> and asks the phone to resend updates.
    /// </summary>
    /// <remarks>
    /// Ported from <c>requestUpdatesAfterGap</c>. Failures are logged, never thrown. The request uses the run's stop
    /// token, so nothing is posted after a disconnect or dispose.
    /// </remarks>
    /// <param name="reason">Why, for the log.</param>
    /// <returns>A task that completes when the request was sent, failed or was stopped.</returns>
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
            // Stopped: Not A Failure
        }
        catch (Exception ex)
        {
            LogGapRequestFailed(log, ex, reason);
        }
    }

    // =========================================================================
    // PINGS
    // =========================================================================

    // Port Of dittoPinger.Ping: Send, Then Wait In The Background
    private async Task PingAsync(TimeSpan timeout, PingResetter reset, CancellationToken ct)
    {
        long ping_id = Interlocked.Increment(ref _pingId);
        if (!liveness.TryStartPing())
        {
            LogPingSkipped(log, ping_id);
            return;
        }

        DateTimeOffset start = time.GetUtcNow();
        liveness.CurrentReset = reset;

        var wait_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<IncomingRpc> wait;
        try
        {
            wait = await rpc.SendAsyncNoWait(new SendParams(ActionType.NotifyDittoActivity, new NotifyDittoActivityRequest { Success = true }), wait_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            wait_cts.Dispose();

            // Stopping: Whatever The Send Failed With, It Is Not A Ping Failure
            if (ct.IsCancellationRequested)
            {
                return;
            }

            int send_fails = liveness.AddSendFail();
            LogPingSendFailed(log, ex, ping_id);
            Raise(new PingFailed(new InvalidOperationException("Failed to notify ditto activity.", ex), send_fails));
            return;
        }

        _ = WaitForResponseAsync(ping_id, start, wait, wait_cts, timeout, reset, ct);
    }

    // Port Of dittoPinger.WaitForResponse
    private async Task WaitForResponseAsync(long pingId, DateTimeOffset start, Task<IncomingRpc> wait, CancellationTokenSource waitCts, TimeSpan timeout, PingResetter reset, CancellationToken ct)
    {
        using (waitCts)
        {
            Task timer = Task.Delay(timeout, time, waitCts.Token);
            Task first = await Task.WhenAny(wait, timer, reset.Task).ConfigureAwait(false);

            // Stop Waiting (Cancels The Waiter And The Timer); A Late Answer Still Counts Through OnPhoneActivity
            await waitCts.CancelAsync().ConfigureAwait(false);

            // Observe A Faulted Wait (Connection Closed), Now Or Later, So It Is Never An Unobserved Task Exception
            _ = wait.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            // Note: A Faulted Waiter (Connection Closed) Is Not An Answer. Go's `<-ping.ch` reads a closed channel as
            // one, but that is accidental: session_handler.go:215-217 treats a closed channel as ErrConnectionClosed,
            // and client.go:383 closes the waiters precisely because no answer can arrive any more.
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
        (bool not_responding_sent, bool was_failing, bool needs_catch_up) = liveness.OnLiveness();
        LogPingAnswered(log, pingId, took, not_responding_sent, was_failing);
        if (was_failing)
        {
            Raise(new PhoneRespondingAgain());
        }

        if (needs_catch_up)
        {
            _ = RequestUpdatesAfterGapAsync("phone started responding to pings again");
        }

        reset.Done();
        Interlocked.Increment(ref _answeredPings);
    }

    private void OnTimeout(long pingId, bool urgent, PingResetter reset)
    {
        (int timeouts, bool send_not_responding) = liveness.OnTimeout(AlertTimeoutCount);
        LogPingTimeout(log, pingId, timeouts, urgent);
        if (send_not_responding)
        {
            Raise(new PhoneNotResponding());
        }

        if (Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
        {
            _ = RecoveryLoopAsync(reset);
        }
    }

    // Port Of dittoPinger.recoveryLoop: Re-Ping On Doubling Intervals, Escalating To Re-Arm, Then Reconnect
    private async Task RecoveryLoopAsync(PingResetter reset)
    {
        CancellationToken ct = _stopToken;
        try
        {
            bool did_set_active_session = false;
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

                // Escalate
                bool reconnected = false;
                if (timeouts >= ReconnectAfterTimeouts && liveness.CanDoRecovery())
                {
                    LogReconnecting(log, timeouts);
                    liveness.StartRecovery();
                    await reconnect().ConfigureAwait(false);
                    reconnected = true;
                }
                else if (timeouts >= SetActiveSessionAfterTimeouts && !did_set_active_session)
                {
                    did_set_active_session = true;
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
            // Stopped: Whatever Failed, It Is Not A Recovery Failure
        }
        catch (Exception ex)
        {
            // Go: A Failed Reconnect Ends Recovery
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

    // Waits For The Delay Or The Other Task; True When The Delay Elapsed First
    private async Task<bool> DelayOrAsync(TimeSpan delay, Task other, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task timer    = Task.Delay(delay, time, cts.Token);
        Task first    = await Task.WhenAny(timer, other).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        return first == timer && timer.IsCompletedSuccessfully;
    }

    private void Raise(object evt)
    {
        try
        {
            EventRaised?.Invoke(evt);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(log, ex, evt.GetType().Name);
        }
    }

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

    [LoggerMessage(Level = LogLevel.Error, Message = "An event subscriber threw for {EventType}")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string eventType);
}

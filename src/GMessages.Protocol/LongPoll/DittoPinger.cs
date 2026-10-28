using GMessages.Protocol.Events;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.LongPoll;

/// <summary>
/// Pings the phone to tell whether it is still answering, and recovers when it stops.
/// </summary>
/// <remarks>
/// Ported from <c>dittoPinger</c>, <c>phoneLiveness</c>, <c>onPhoneActivity</c>, <c>requestUpdatesAfterGap</c> and
/// the data-receive check in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go">longpoll.go</see>.
/// A ping goes out every minute, and right away when a request the user waits on is slow. The first unanswered ping
/// raises <see cref="PhoneNotResponding"/>; after that only <see cref="AlertTimeoutCount"/> misses in a row do, since
/// phones doze constantly. While the phone is silent, a recovery loop re-pings on doubling intervals (1 minute up
/// to 64), re-arms the event subscription after 2 misses, and reconnects after 3. Any answer or data from the phone
/// raises <see cref="PhoneRespondingAgain"/> when things were failing. Waiting for a ping never blocks the main loop,
/// which also runs the data-receive check: an extra update request when nothing arrived for a long time. Every wait
/// uses the <see cref="TimeProvider"/>. The phone liveness state outlives reconnects, as in Go where it sits on the client.
/// </remarks>
/// <param name="rpc">Sends pings and update requests.</param>
/// <param name="time">The clock for every wait.</param>
/// <param name="log">The logger.</param>
/// <param name="setActiveSession">Re-arms the phone's event subscription (Go: <c>SetActiveSession</c>).</param>
/// <param name="reconnect">Drops and reopens the long poll (Go: <c>Reconnect</c>).</param>
internal sealed partial class DittoPinger(RpcSession rpc, TimeProvider time, ILogger log, Func<CancellationToken, Task> setActiveSession, Func<Task> reconnect)
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

    // Liveness State (Go: phoneLiveness)
    private readonly Lock _lock = new();
    private bool _firstPingDone;
    private DateTimeOffset _lastPingTime = DateTimeOffset.MinValue;
    private int _timeouts;
    private int _sendFails;
    private bool _notRespondingSent;
    private TimeSpan _recoveryInterval;
    private DateTimeOffset? _lastRecoveryReconnect;
    private DateTimeOffset _nextDataReceiveCheck = DateTimeOffset.MinValue;
    private Resetter? _currentReset;

    private int _recovering;
    private long _pingId;
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

    /// <summary>
    /// Runs the ping loop until <paramref name="ct"/> is canceled.
    /// </summary>
    /// <remarks>Ported from <c>dittoPinger.Loop</c>.</remarks>
    /// <param name="ct">Stops the loop, every pending ping wait and the recovery loop.</param>
    /// <returns>A task that completes when stopped; stopping is not an error.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        _stopToken = ct;
        try
        {
            await LoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped While Sending A Ping
        }
    }

    // Port Of dittoPinger.Loop
    private async Task LoopAsync(CancellationToken ct)
    {
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
                if (ShouldMarkNotResponding())
                {
                    Raise(new PhoneNotResponding());
                }
            }
            else if (!elapsed)
            {
                await PingAsync(ShortPingTimeout, new Resetter(time), ct).ConfigureAwait(false);
            }
            else if (!recovering)
            {
                await PingAsync(DefaultPingTimeout, new Resetter(time), ct).ConfigureAwait(false);
            }

            if (ShouldDoDataReceiveCheck())
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
        (bool not_responding_sent, bool was_failing, bool needs_catch_up) = OnLiveness();
        if (!was_failing)
        {
            return;
        }

        LogActivityWhileUnresponsive(log, source, not_responding_sent);
        Volatile.Read(ref _currentReset)?.Done();
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
    public void BumpDataReceiveCheck(TimeSpan? after = null)
    {
        DateTimeOffset next = time.GetUtcNow() + (after ?? DataReceiveCheckInterval);
        lock (_lock)
        {
            if (_nextDataReceiveCheck < next)
            {
                _nextDataReceiveCheck = next;
            }
        }
    }

    /// <summary>
    /// Raises <see cref="NoDataReceived"/> and asks the phone to resend updates.
    /// </summary>
    /// <remarks>Ported from <c>requestUpdatesAfterGap</c>. Failures are logged, never thrown.</remarks>
    /// <param name="reason">Why, for the log.</param>
    /// <returns>A task that completes when the request was sent or failed.</returns>
    public async Task RequestUpdatesAfterGapAsync(string reason)
    {
        Raise(new NoDataReceived());
        try
        {
            await rpc.SendNoResponseAsync(new SendParams(ActionType.GetUpdates, RequestId: rpc.SessionId, OmitTtl: true), CancellationToken.None).ConfigureAwait(false);
            LogGapRequestSent(log, reason);
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
    private async Task PingAsync(TimeSpan timeout, Resetter reset, CancellationToken ct)
    {
        long ping_id       = Interlocked.Increment(ref _pingId);
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            if (now - _lastPingTime < MinPingInterval)
            {
                LogPingSkipped(log, ping_id);
                return;
            }

            _lastPingTime = now;
        }

        Volatile.Write(ref _currentReset, reset);

        var wait_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<IncomingRpc> wait;
        try
        {
            wait = await rpc.SendAsyncNoWait(new SendParams(ActionType.NotifyDittoActivity, new NotifyDittoActivityRequest { Success = true }), wait_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            wait_cts.Dispose();
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            int send_fails;
            lock (_lock)
            {
                send_fails = ++_sendFails;
            }

            LogPingSendFailed(log, ex, ping_id);
            Raise(new PingFailed(new InvalidOperationException("Failed to notify ditto activity.", ex), send_fails));
            return;
        }

        _ = WaitForResponseAsync(ping_id, now, wait, wait_cts, timeout, reset, ct);
    }

    // Port Of dittoPinger.WaitForResponse
    private async Task WaitForResponseAsync(long pingId, DateTimeOffset start, Task<IncomingRpc> wait, CancellationTokenSource waitCts, TimeSpan timeout, Resetter reset, CancellationToken ct)
    {
        using (waitCts)
        {
            Task timer = Task.Delay(timeout, time, waitCts.Token);
            Task first = await Task.WhenAny(wait, timer, reset.Task).ConfigureAwait(false);

            // Stop Waiting (Cancels The Waiter And The Timer); A Late Answer Still Counts Through OnPhoneActivity
            await waitCts.CancelAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                return;
            }

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

    private void OnRespond(long pingId, TimeSpan took, Resetter reset)
    {
        (bool not_responding_sent, bool was_failing, bool needs_catch_up) = OnLiveness();
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
    }

    private void OnTimeout(long pingId, bool urgent, Resetter reset)
    {
        int timeouts;
        bool send_not_responding;
        lock (_lock)
        {
            timeouts            = ++_timeouts;
            bool alert          = !_firstPingDone || timeouts >= AlertTimeoutCount;
            send_not_responding = alert && !_notRespondingSent;
            _notRespondingSent |= send_not_responding;
        }

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
    private async Task RecoveryLoopAsync(Resetter reset)
    {
        CancellationToken ct = _stopToken;
        try
        {
            bool did_set_active_session = false;
            while (true)
            {
                TimeSpan interval;
                lock (_lock)
                {
                    interval = _recoveryInterval == TimeSpan.Zero ? (_recoveryInterval = InitialRecoveryInterval) : _recoveryInterval;
                }

                if (!await DelayOrAsync(interval, reset.Task, ct).ConfigureAwait(false) || ct.IsCancellationRequested)
                {
                    return;
                }

                int timeouts;
                bool can_reconnect;
                lock (_lock)
                {
                    timeouts      = _timeouts;
                    can_reconnect = _lastRecoveryReconnect is not { } last || time.GetUtcNow() - last >= _recoveryInterval;
                }

                if (timeouts == 0)
                {
                    return;
                }

                // Escalate
                bool reconnected = false;
                if (timeouts >= ReconnectAfterTimeouts && can_reconnect)
                {
                    LogReconnecting(log, timeouts);
                    lock (_lock)
                    {
                        _lastRecoveryReconnect = time.GetUtcNow();
                    }

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

                lock (_lock)
                {
                    if (_recoveryInterval < MaxRecoveryInterval)
                    {
                        _recoveryInterval *= 2;
                    }
                }

                if (reconnected)
                {
                    return;
                }

                await PingAsync(DefaultPingTimeout, reset, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Go: A Failed Reconnect Ends Recovery
            LogRecoveryFailed(log, ex);
        }
        catch (OperationCanceledException)
        {
            // Stopped
        }
        finally
        {
            Volatile.Write(ref _recovering, 0);
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private (bool NotRespondingSent, bool WasFailing, bool NeedsCatchUp) OnLiveness()
    {
        lock (_lock)
        {
            bool not_responding_sent = _notRespondingSent;
            bool was_failing         = not_responding_sent || _timeouts > 0 || _sendFails > 0;
            bool needs_catch_up      = not_responding_sent || _timeouts >= SetActiveSessionAfterTimeouts;
            _firstPingDone     = true;
            _notRespondingSent = false;
            _timeouts          = 0;
            _sendFails         = 0;
            _recoveryInterval  = TimeSpan.Zero;
            return (not_responding_sent, was_failing, needs_catch_up);
        }
    }

    private bool ShouldMarkNotResponding()
    {
        lock (_lock)
        {
            if (_notRespondingSent || _timeouts < AlertTimeoutCount)
            {
                return false;
            }

            _notRespondingSent = true;
            return true;
        }
    }

    private bool ShouldDoDataReceiveCheck()
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            if (_nextDataReceiveCheck > now)
            {
                return false;
            }

            _nextDataReceiveCheck = now + DataReceiveCheckInterval;
            return true;
        }
    }

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

    // Port Of resetter: Done Releases The Waiters Of One Ping Generation 5 s Later
    private sealed class Resetter(TimeProvider time)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _done;

        public Task Task => _released.Task;

        public void Done()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                _ = ReleaseLaterAsync();
            }
        }

        private async Task ReleaseLaterAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(5), time).ConfigureAwait(false);
            _released.TrySetResult();
        }
    }
}

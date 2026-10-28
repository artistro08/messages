namespace GMessages.Protocol.LongPoll;

/// <summary>
/// Tracks whether the phone is answering, and when to next check that data still arrives.
/// </summary>
/// <remarks>
/// Ported from <c>phoneLiveness</c> and the client's <c>nextDataReceiveCheck</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go">longpoll.go</see>.
/// In Go this state sits on the client, so it outlives reconnects and pinger restarts. Here
/// <see cref="LongPollLoop"/> owns one instance and hands it to the pinger of every run. All members are thread-safe.
/// </remarks>
/// <param name="time">The clock for ping and recovery times.</param>
internal sealed class PhoneLiveness(TimeProvider time)
{
    private readonly Lock _lock = new();
    private bool _firstPingDone;
    private DateTimeOffset _lastPingTime = DateTimeOffset.MinValue;
    private int _timeouts;
    private int _sendFails;
    private bool _notRespondingSent;
    private TimeSpan _recoveryInterval;
    private DateTimeOffset? _lastRecoveryReconnect;
    private DateTimeOffset _nextDataReceiveCheck = DateTimeOffset.MinValue;
    private PingResetter? _currentReset;

    /// <summary>Gets how many pings in a row went unanswered.</summary>
    public int TimeoutCount
    {
        get
        {
            lock (_lock)
            {
                return _timeouts;
            }
        }
    }

    /// <summary>Gets or sets the resetter of the latest ping that was actually sent (Go: <c>currentReset</c>).</summary>
    public PingResetter? CurrentReset
    {
        get => Volatile.Read(ref _currentReset);
        set => Volatile.Write(ref _currentReset, value);
    }

    /// <summary>
    /// Records that the phone answered or sent data.
    /// </summary>
    /// <remarks>Ported from <c>onLiveness</c>: clears every failure counter and the recovery interval.</remarks>
    /// <returns>Whether "not responding" was sent, whether anything was failing, and whether updates may have been missed.</returns>
    public (bool NotRespondingSent, bool WasFailing, bool NeedsCatchUp) OnLiveness()
    {
        lock (_lock)
        {
            bool not_responding_sent = _notRespondingSent;
            bool was_failing         = not_responding_sent || _timeouts > 0 || _sendFails > 0;
            bool needs_catch_up      = not_responding_sent || _timeouts >= DittoPinger.SetActiveSessionAfterTimeouts;
            _firstPingDone     = true;
            _notRespondingSent = false;
            _timeouts          = 0;
            _sendFails         = 0;
            _recoveryInterval  = TimeSpan.Zero;
            return (not_responding_sent, was_failing, needs_catch_up);
        }
    }

    /// <summary>
    /// Records an unanswered ping.
    /// </summary>
    /// <remarks>Ported from <c>onTimeout</c>: the first ping ever, or <paramref name="alertTimeoutCount"/> misses in a row, alert once.</remarks>
    /// <param name="alertTimeoutCount">Misses in a row that justify telling the user.</param>
    /// <returns>The miss count, and whether to raise "not responding" now.</returns>
    public (int Timeouts, bool SendNotResponding) OnTimeout(int alertTimeoutCount)
    {
        lock (_lock)
        {
            int timeouts             = ++_timeouts;
            bool alert               = !_firstPingDone || timeouts >= alertTimeoutCount;
            bool send_not_responding = alert && !_notRespondingSent;
            _notRespondingSent      |= send_not_responding;
            return (timeouts, send_not_responding);
        }
    }

    /// <summary>
    /// Marks "not responding" as sent if the miss threshold is met and it was not sent yet.
    /// </summary>
    /// <remarks>Ported from <c>shouldMarkNotResponding</c>.</remarks>
    /// <param name="alertTimeoutCount">Misses in a row that justify telling the user.</param>
    /// <returns>True when the caller should raise "not responding".</returns>
    public bool ShouldMarkNotResponding(int alertTimeoutCount)
    {
        lock (_lock)
        {
            if (_notRespondingSent || _timeouts < alertTimeoutCount)
            {
                return false;
            }

            _notRespondingSent = true;
            return true;
        }
    }

    /// <summary>
    /// Claims a ping slot unless the last ping was under <see cref="DittoPinger.MinPingInterval"/> ago.
    /// </summary>
    /// <returns>True when a ping may be sent now.</returns>
    public bool TryStartPing()
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            if (now - _lastPingTime < DittoPinger.MinPingInterval)
            {
                return false;
            }

            _lastPingTime = now;
            return true;
        }
    }

    /// <summary>
    /// Records a ping that could not be sent.
    /// </summary>
    /// <returns>Failed sends in a row.</returns>
    public int AddSendFail()
    {
        lock (_lock)
        {
            return ++_sendFails;
        }
    }

    /// <summary>
    /// Gets the current recovery wait, starting at <see cref="DittoPinger.InitialRecoveryInterval"/>.
    /// </summary>
    /// <remarks>Ported from <c>getRecoveryInterval</c>.</remarks>
    /// <returns>The wait before the next recovery step.</returns>
    public TimeSpan GetRecoveryInterval()
    {
        lock (_lock)
        {
            if (_recoveryInterval == TimeSpan.Zero)
            {
                _recoveryInterval = DittoPinger.InitialRecoveryInterval;
            }

            return _recoveryInterval;
        }
    }

    /// <summary>
    /// Doubles the recovery wait, up to <see cref="DittoPinger.MaxRecoveryInterval"/>.
    /// </summary>
    /// <remarks>Ported from <c>advanceRecoveryInterval</c>.</remarks>
    public void AdvanceRecoveryInterval()
    {
        lock (_lock)
        {
            if (_recoveryInterval < DittoPinger.MaxRecoveryInterval)
            {
                _recoveryInterval *= 2;
            }
        }
    }

    /// <summary>
    /// Tells whether a recovery reconnect is allowed: none yet, or the last one is a full recovery interval ago.
    /// </summary>
    /// <remarks>Ported from <c>canDoRecovery</c>.</remarks>
    /// <returns>True when reconnecting is allowed.</returns>
    public bool CanDoRecovery()
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            return _lastRecoveryReconnect is not { } last || now - last >= _recoveryInterval;
        }
    }

    /// <summary>
    /// Records a recovery reconnect now.
    /// </summary>
    /// <remarks>Ported from <c>startRecovery</c>.</remarks>
    public void StartRecovery()
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            _lastRecoveryReconnect = now;
        }
    }

    /// <summary>
    /// Tells whether the data-receive check is due, and if so schedules the next one.
    /// </summary>
    /// <remarks>Ported from <c>shouldDoDataReceiveCheck</c>.</remarks>
    /// <param name="interval">The time until the next check.</param>
    /// <returns>True when the check is due now.</returns>
    public bool ShouldDoDataReceiveCheck(TimeSpan interval)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            if (_nextDataReceiveCheck > now)
            {
                return false;
            }

            _nextDataReceiveCheck = now + interval;
            return true;
        }
    }

    /// <summary>
    /// Pushes the next data-receive check out to at least <paramref name="after"/> from now.
    /// </summary>
    /// <remarks>Ported from <c>bumpNextDataReceiveCheck</c>.</remarks>
    /// <param name="after">The minimum delay.</param>
    public void BumpDataReceiveCheck(TimeSpan after)
    {
        DateTimeOffset next = time.GetUtcNow() + after;
        lock (_lock)
        {
            if (_nextDataReceiveCheck < next)
            {
                _nextDataReceiveCheck = next;
            }
        }
    }
}

/// <summary>
/// Releases the waiters of one ping generation once a ping is answered.
/// </summary>
/// <remarks>Ported from <c>resetter</c> in longpoll.go: <see cref="Done"/> completes <see cref="Task"/> 5 seconds later, once.</remarks>
/// <param name="time">The clock for the 5 second delay.</param>
internal sealed class PingResetter(TimeProvider time)
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _done;

    /// <summary>Gets a task that completes 5 seconds after <see cref="Done"/>.</summary>
    public Task Task => _released.Task;

    /// <summary>Starts the 5 second release; later calls do nothing.</summary>
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

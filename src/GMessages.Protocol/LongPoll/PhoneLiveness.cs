namespace GMessages.Protocol.LongPoll;

/// <summary>
/// Tracks whether the phone is answering, and when to next check that data still arrives.
/// </summary>
/// <remarks>
/// Ported from <c>phoneLiveness</c> and the client's <c>nextDataReceiveCheck</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go">longpoll.go</see>.
/// In Go, this state is on the client, so it outlives reconnects and pinger restarts. Here,
/// <see cref="LongPollLoop"/> owns 1 instance and gives it to the pinger of each run. All members are thread-safe.
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

    /// <summary>Gets or sets the resetter of the latest ping that went out. Ported from <c>currentReset</c>.</summary>
    public PingResetter? CurrentReset
    {
        get => Volatile.Read(ref _currentReset);
        set => Volatile.Write(ref _currentReset, value);
    }

    /// <summary>
    /// Records that the phone answered or sent data.
    /// </summary>
    /// <remarks>Ported from <c>onLiveness</c>. It clears each failure counter and the recovery interval.</remarks>
    /// <returns>Whether "not responding" went out, whether a failure was active, and whether the client can have missed updates.</returns>
    public (bool NotRespondingSent, bool WasFailing, bool NeedsCatchUp) OnLiveness()
    {
        lock (_lock)
        {
            bool notRespondingSent   = _notRespondingSent;
            bool wasFailing          = notRespondingSent || _timeouts > 0 || _sendFails > 0;
            bool needsCatchUp        = notRespondingSent || _timeouts >= DittoPinger.SetActiveSessionAfterTimeouts;
            _firstPingDone     = true;
            _notRespondingSent = false;
            _timeouts          = 0;
            _sendFails         = 0;
            _recoveryInterval  = TimeSpan.Zero;
            return (notRespondingSent, wasFailing, needsCatchUp);
        }
    }

    /// <summary>
    /// Records an unanswered ping.
    /// </summary>
    /// <remarks>Ported from <c>onTimeout</c>. The first ping ever, or <paramref name="alertTimeoutCount"/> misses in a row, cause 1 alert.</remarks>
    /// <param name="alertTimeoutCount">The misses in a row before the user gets a notice.</param>
    /// <returns>The miss count, and whether to raise "not responding" now.</returns>
    public (int Timeouts, bool SendNotResponding) OnTimeout(int alertTimeoutCount)
    {
        lock (_lock)
        {
            int timeouts             = ++_timeouts;
            bool alert               = !_firstPingDone || timeouts >= alertTimeoutCount;
            bool sendNotResponding   = alert && !_notRespondingSent;
            _notRespondingSent      |= sendNotResponding;
            return (timeouts, sendNotResponding);
        }
    }

    /// <summary>
    /// Marks "not responding" as sent when the miss limit is reached and the notice did not go out yet.
    /// </summary>
    /// <remarks>Ported from <c>shouldMarkNotResponding</c>.</remarks>
    /// <param name="alertTimeoutCount">The misses in a row before the user gets a notice.</param>
    /// <returns>True when the caller must raise "not responding".</returns>
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
    /// Takes a ping slot, unless the last ping is less than <see cref="DittoPinger.MinPingInterval"/> old.
    /// </summary>
    /// <returns>True when a ping can go out now.</returns>
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
    /// Records a ping that did not go out.
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
    /// Tells whether a recovery reconnect can happen now. It can when none happened yet, or when the last one is 1 full
    /// recovery interval old.
    /// </summary>
    /// <remarks>Ported from <c>canDoRecovery</c>.</remarks>
    /// <returns>True when a reconnect can happen.</returns>
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
    /// Moves the next data-receive check to at least <paramref name="after"/> from now.
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
/// Releases the waiters of 1 ping generation after a ping gets an answer.
/// </summary>
/// <remarks>Ported from <c>resetter</c> in longpoll.go. <see cref="Done"/> completes <see cref="Task"/> 1 time, 5 seconds later.</remarks>
/// <param name="time">The clock for the 5 second delay.</param>
internal sealed class PingResetter(TimeProvider time)
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _done;

    /// <summary>Gets a task that completes 5 seconds after <see cref="Done"/>.</summary>
    public Task Task => _released.Task;

    /// <summary>Starts the 5 second release. Later calls do nothing.</summary>
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

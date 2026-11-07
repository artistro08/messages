using GMessages.Protocol.Auth;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.LongPoll;

/// <summary>
/// Keeps the long poll open. It reconnects after errors, gives each payload to the dispatcher, sends the acks and pings the phone.
/// </summary>
/// <remarks>
/// Ported from <c>doLongPoll</c> and <c>readLongPoll</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go">longpoll.go</see>,
/// <c>startLongPolling</c> in client.go, <c>startAckInterval</c> in session_handler.go and <c>SetActiveSession</c> in
/// methods.go. Each pass refreshes the token when necessary, then opens <c>ReceiveMessages</c>. A 401 or a 403 raises
/// <see cref="ListenFatalError"/> and ends the loop. A fatal refresh error does the same. Other failures raise
/// <see cref="ListenTemporaryError"/>, and the loop tries again after <c>(errorCount + 1) × 5</c> seconds. The first
/// success after errors raises <see cref="ListenRecovered"/>. As in Go, the error count starts at 1, so the first
/// connect also raises it. After more than 1 minute without a stream, the next connect asks the phone to send the
/// updates again. After 1 minute of silence on the stream, the loop drops the connection and opens it again.
/// <see cref="StreamLifetime"/> does the same after 30 minutes, which is the client timeout in Go. Thus the token
/// refresh check runs at least every 30 minutes.
/// <para>
/// 3 differences from Go keep an always-on tray app stable. The wire requests do not change.
/// <see cref="MaxRetryDelay"/> limits the retry wait to 5 minutes. In Go, the wait grows without limit.
/// <see cref="MinStreamLifetime"/> adds a 5 second pause after a stream that ends within 5 seconds. Thus a server that
/// closes at once cannot spin the loop. The open stops when no response headers arrive within
/// <see cref="ReadIdleTimeout"/>, and <see cref="RequestReconnect"/> can interrupt it. Thus a request on a dead socket
/// cannot block the loop.
/// </para>
/// Go wakes its background mode with push messages. This port does not include that mode, because this app has no
/// push. The loop owns the <see cref="PhoneLiveness"/> state, so the state survives restarts, as the client-level
/// state of Go does. Each wait uses the <see cref="TimeProvider"/>.
/// </remarks>
/// <param name="http">The HTTP client.</param>
/// <param name="rpc">The RPC session. It sends the acks, the update requests and the pings.</param>
/// <param name="auth">The account.</param>
/// <param name="endpoints">The server addresses.</param>
/// <param name="refresher">Refreshes the token before each connect.</param>
/// <param name="dispatcher">Handles each incoming message.</param>
/// <param name="time">The clock for each wait.</param>
/// <param name="log">The logger.</param>
public sealed partial class LongPollLoop(
    GmHttpClient http,
    RpcSession rpc,
    AuthData auth,
    GmEndpoints endpoints,
    TokenRefresher refresher,
    UpdateDispatcher dispatcher,
    TimeProvider time,
    ILogger log)
{
    /// <summary>The longest silence on the stream before the loop reconnects. Go uses the 1 minute <c>closeIn</c> timer.</summary>
    public static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromMinutes(1);

    /// <summary>A disconnect longer than this is a gap that can hide events. Ported from <c>longPollGapCatchUp</c>.</summary>
    public static readonly TimeSpan GapCatchUp = TimeSpan.FromMinutes(1);

    /// <summary>How often the loop sends the queued acks. Ported from <c>startAckInterval</c>.</summary>
    public static readonly TimeSpan AckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest wait between retries. This limit is C#-only, because in Go the wait of <c>(errorCount + 1) × 5</c>
    /// seconds grows without limit.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// After a stream shorter than this, the loop pauses this long before it opens a new one. This pause is C#-only.
    /// Go opens the new stream at once.
    /// </summary>
    public static readonly TimeSpan MinStreamLifetime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest time that 1 stream stays open, from the start of its request. In Go, the 30 minute timeout of the
    /// long-poll client covers the body. Thus each stream ends then, and the token refresh check runs again.
    /// </summary>
    public static readonly TimeSpan StreamLifetime = GmHttpClient.LongPollTimeout;

    private readonly Lock _lock = new();
    private readonly PhoneLiveness _liveness = new(time);
    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _connectionCts;
    private bool _reconnectRequested;
    private DittoPinger? _pinger;
    private int _connected;

    /// <summary>
    /// Occurs for each event from the loop, the dispatcher, the token refresher and the pinger.
    /// </summary>
    public event Action<object>? EventRaised;

    /// <summary>
    /// Gets whether a long-poll stream is open now.
    /// </summary>
    /// <remarks>
    /// Ported from the <c>longPollingConn</c> check in <c>IsConnected</c>. The value is true from the open of a stream
    /// until the stream ends or <see cref="Stop"/> runs. Thus it is false for a short time while the loop reconnects,
    /// as in Go.
    /// </remarks>
    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    /// <summary>
    /// Gets whether a run is active. A run lasts from the start of <see cref="RunAsync"/> to its end, reconnects included.
    /// </summary>
    internal bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _stopCts is not null;
            }
        }
    }

    /// <summary>
    /// Runs the loop until a stop, a cancel or a refusal of the credentials.
    /// </summary>
    /// <remarks>
    /// When <paramref name="loggedIn"/> is false, as during pairing, no pinger and no ack timer run. As in Go, no
    /// listen events occur then. <paramref name="onFirstConnect"/> runs in the background when the first connection
    /// opens. It runs again after a recovery reconnect, because Go then starts a new loop with <c>postConnect</c>.
    /// When <paramref name="loggedIn"/> is false, the method throws a fatal error: a 401, a 403 or a fatal refresh
    /// error. No listen event carries the error then. Without the throw, pairing does not learn why the long poll
    /// stopped.
    /// </remarks>
    /// <param name="loggedIn">True after pairing. It turns on the pinger, the ack timer and the listen events.</param>
    /// <param name="onFirstConnect">Runs after the first successful connect, or null.</param>
    /// <param name="ct">Stops the loop.</param>
    /// <returns>A task that completes when the loop ends. A stop or a cancel ends it normally, without an exception.</returns>
    /// <exception cref="GmRequestException">Not logged in, and the server refused the credentials (401/403).</exception>
    /// <exception cref="InvalidOperationException">Not logged in, and the token refresh failed fatally.</exception>
    public async Task RunAsync(bool loggedIn, Action? onFirstConnect, CancellationToken ct)
    {
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken stop = stopCts.Token;
        lock (_lock)
        {
            _stopCts = stopCts;
        }

        Task background = Task.CompletedTask;
        if (loggedIn)
        {
            var pinger = new DittoPinger(rpc, _liveness, time, log, SetActiveSessionAsync, RequestReconnectAsync);
            pinger.EventRaised += Raise;
            pinger.BumpDataReceiveCheck(TimeSpan.FromMinutes(10));
            dispatcher.Pinger = pinger;
            rpc.PhoneSlow     += OnPhoneSlow;
            _pinger            = pinger;
            background         = Task.WhenAll(pinger.RunAsync(stop), FlushAcksLoopAsync(stop));
        }

        dispatcher.StopToken    = stop;
        dispatcher.EventRaised += Raise;
        refresher.EventRaised  += Raise;
        try
        {
            Exception? fatal = await ListenAsync(loggedIn, onFirstConnect, stop).ConfigureAwait(false);
            if (fatal is not null && !loggedIn)
            {
                throw fatal;
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested)
        {
            // A stop or a cancel is a normal end, whatever error the aborted socket threw.
            LogStopError(log, ex);
        }
        finally
        {
            await stopCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await background.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStopError(log, ex);
            }

            dispatcher.EventRaised -= Raise;
            refresher.EventRaised  -= Raise;
            rpc.PhoneSlow          -= OnPhoneSlow;
            dispatcher.Pinger       = null;

            // Clear only the CTS of this run, so that a newer run stays stoppable.
            lock (_lock)
            {
                if (_stopCts == stopCts)
                {
                    _stopCts = null;
                }
            }
        }
    }

    /// <summary>
    /// Stops the loop and closes the connection.
    /// </summary>
    /// <remarks>Ported from <c>closeLongPolling</c>.</remarks>
    public void Stop()
    {
        Volatile.Write(ref _connected, 0);
        CancellationTokenSource? stopCts;
        lock (_lock)
        {
            stopCts = _stopCts;
        }

        CancelInBackground(stopCts);
    }

    /// <summary>
    /// Starts a new session ID and asks the phone to send updates for it.
    /// </summary>
    /// <remarks>Ported from <c>SetActiveSession</c>: a <c>GET_UPDATES</c> with the new session ID as request ID and no TTL.</remarks>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the server accepted the request.</returns>
    /// <exception cref="GmRequestException">The server refused the request.</exception>
    public Task SetActiveSessionAsync(CancellationToken ct)
    {
        rpc.ResetSessionId();
        return rpc.SendNoResponseAsync(new SendParams(ActionType.GetUpdates, RequestId: rpc.SessionId, OmitTtl: true), ct);
    }

    // =========================================================================
    // LOOP
    // =========================================================================

    // Returns the fatal error that ended the loop, or null after a stop.
    private async Task<Exception?> ListenAsync(bool loggedIn, Action? onFirstConnect, CancellationToken stop)
    {
        Action? firstConnect            = onFirstConnect;
        string listenRequestId          = Guid.NewGuid().ToString();
        int errorCount                  = 1;
        DateTimeOffset? disconnectedAt  = null;

        // A recovery reconnect starts over like a new doLongPoll. In Go, Reconnect starts a new loop with postConnect.
        bool TakeReconnect()
        {
            lock (_lock)
            {
                if (!_reconnectRequested)
                {
                    return false;
                }

                _reconnectRequested = false;
            }

            firstConnect      = onFirstConnect;
            listenRequestId   = Guid.NewGuid().ToString();
            errorCount        = 1;
            disconnectedAt    = null;
            _pinger?.BumpDataReceiveCheck(TimeSpan.FromMinutes(10));
            return true;
        }

        while (!stop.IsCancellationRequested)
        {
            TakeReconnect();

            // A reconnect request cancels the backoff wait or the read of this attempt.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_lock)
            {
                _connectionCts = attemptCts;
            }

            try
            {
                Exception? error = null;
                try
                {
                    await refresher.RefreshIfNeededAsync(stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    var wrapped = new InvalidOperationException("Failed to refresh auth token.", ex);
                    if (TokenRefresher.IsFatal(ex))
                    {
                        LogFatal(log, "token refresh", ex.Message);
                        RaiseIf(loggedIn, new ListenFatalError(wrapped));
                        return wrapped;
                    }

                    error = wrapped;
                }

                // The stream lifetime starts with the request, as the client timeout of Go does.
                using var lifetimeCts = new CancellationTokenSource(StreamLifetime, time);

                // A reconnect request or 1 minute without headers interrupts the open.
                HttpResponseMessage? res = null;
                if (error is null)
                {
                    using var openTimeout  = new CancellationTokenSource(ReadIdleTimeout, time);
                    using var openCts      = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token, openTimeout.Token);
                    try
                    {
                        res = await http.PostAsync(auth.HasCookies ? endpoints.ReceiveMessagesGoogle : endpoints.ReceiveMessages, BuildReceiveRequest(listenRequestId), GmContentType.PbLite, true, openCts.Token).ConfigureAwait(false);
                        int status = (int)res.StatusCode;
                        if (status >= 400)
                        {
                            // The body read disposes the response and never throws, so a 401 or a 403 always stays fatal.
                            HttpResponseMessage failed = res;
                            res = null;
                            var failure = new GmRequestException(status, null, await ReadBodyAsync(failed, stop).ConfigureAwait(false));
                            if (status is 401 or 403)
                            {
                                LogFatal(log, "receive messages", failure.Message);
                                RaiseIf(loggedIn, new ListenFatalError(failure));
                                return failure;
                            }

                            error = failure;
                        }
                    }
                    catch (Exception) when (attemptCts.IsCancellationRequested && !stop.IsCancellationRequested)
                    {
                        // A reconnect request during the open starts over at once.
                        res?.Dispose();
                        continue;
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested)
                    {
                        res?.Dispose();
                        error = openTimeout.IsCancellationRequested
                            ? new TimeoutException($"No response headers within {ReadIdleTimeout.TotalSeconds:0} seconds.", ex)
                            : ex;
                    }
                }

                if (error is not null)
                {
                    RaiseIf(loggedIn, new ListenTemporaryError(error));
                    errorCount++;
                    TimeSpan sleep = RetryDelay(errorCount);
                    LogRetrying(log, error, sleep);
                    await PauseAsync(sleep, attemptCts.Token, stop).ConfigureAwait(false);
                    continue;
                }

                // An open without an error always has a response. This guard only narrows the type.
                if (res is not { } response)
                {
                    continue;
                }

                using (response)
                {
                    // Ported from longpoll.go lines 578 to 583. A reconnect during the open closes this stream and starts over.
                    if (TakeReconnect())
                    {
                        LogReconnectWhileOpening(log);
                        continue;
                    }

                    if (errorCount > 0)
                    {
                        errorCount = 0;
                        RaiseIf(loggedIn, new ListenRecovered());
                    }

                    LogOpened(log);
                    Volatile.Write(ref _connected, 1);
                    if (loggedIn && disconnectedAt is { } since && time.GetUtcNow() - since > GapCatchUp)
                    {
                        _ = _pinger?.RequestUpdatesAfterGapAsync("long polling was disconnected");
                    }

                    if (firstConnect is not null)
                    {
                        _ = RunFirstConnectAsync(firstConnect);
                        firstConnect = null;
                    }

                    DateTimeOffset openedAt = time.GetUtcNow();
                    try
                    {
                        // The end of the lifetime ends the read like any stream end. It is no error, and the next pass checks the token.
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token, lifetimeCts.Token);
                        await ReadAsync(response, readCts.Token, stop).ConfigureAwait(false);
                    }
                    finally
                    {
                        Volatile.Write(ref _connected, 0);
                        TimeSpan lifetime = time.GetUtcNow() - openedAt;
                        LogClosed(log, lifetime);
                    }

                    // A stream that ends almost at once gets a pause. Thus a server that closes at once cannot spin the loop.
                    disconnectedAt = time.GetUtcNow();
                    if (disconnectedAt - openedAt < MinStreamLifetime)
                    {
                        await PauseAsync(MinStreamLifetime, attemptCts.Token, stop).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (_connectionCts == attemptCts)
                    {
                        _connectionCts = null;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the wait before the next retry. The wait is <c>(errorCount + 1) × 5</c> seconds as in Go, but 5 minutes at most.
    /// </summary>
    /// <param name="errorCount">The error count after this error.</param>
    /// <returns>The wait.</returns>
    internal static TimeSpan RetryDelay(int errorCount)
    {
        TimeSpan goDelay = TimeSpan.FromSeconds((errorCount + 1) * 5.0);
        return goDelay < MaxRetryDelay ? goDelay : MaxRetryDelay;
    }

    // A reconnect request ends the wait early, because Go starts the new loop at once.
    private async Task PauseAsync(TimeSpan delay, CancellationToken attempt, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, time, attempt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // A reconnect request ended the wait.
        }
    }

    // Reads the body of a failed response for diagnosis and always disposes the response. A failed read gives an empty body.
    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
        finally
        {
            res.Dispose();
        }
    }

    // Port of go onFirstConnect(). The callback runs in the background, and the method logs a failure.
    private async Task RunFirstConnectAsync(Action firstConnect)
    {
        try
        {
            await Task.Run(firstConnect, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFirstConnectFailed(log, ex);
        }
    }

    // Port of readLongPoll. Any read problem ends this connection.
    private async Task ReadAsync(HttpResponseMessage res, CancellationToken connection, CancellationToken stop)
    {
        try
        {
            Stream body = await res.Content.ReadAsStreamAsync(connection).ConfigureAwait(false);
            await foreach (LongPollingPayload payload in LongPollStreamReader.ReadAsync(body, ReadIdleTimeout, time, connection, log).ConfigureAwait(false))
            {
                // Go checks the data first, then the startup ack count. Heartbeats need no action.
                if (payload.Data is not null)
                {
                    dispatcher.Handle(payload.Data);
                }
                else if (payload.Ack is not null)
                {
                    LogSkipCount(log, payload.Ack.Count);
                    dispatcher.SkipCount = payload.Ack.Count;
                }
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            LogReadStopped(log, ex);
        }
    }

    private ReceiveMessagesRequest BuildReceiveRequest(string requestId) => new()
    {
        Auth = new AuthMessage
        {
            RequestID        = requestId,
            TachyonAuthToken = Google.Protobuf.ByteString.CopyFrom(auth.TachyonAuthToken ?? []),
            Network          = auth.AuthNetwork,
            ConfigVersion    = GmConstants.ConfigVersion,
        },
        Unknown = new ReceiveMessagesRequest.Types.UnknownEmptyObject2 { Unknown = new ReceiveMessagesRequest.Types.UnknownEmptyObject1() },
    };

    // Port of startAckInterval.
    private async Task FlushAcksLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(AckInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                await rpc.FlushAcksAsync(stop).ConfigureAwait(false);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            // The run stopped.
        }
    }

    /// <summary>
    /// Drops the current connection or backoff wait and starts over at once, as a new connect does.
    /// </summary>
    /// <remarks>
    /// The pinger uses the same path, which is <c>Reconnect</c> in Go. The run continues, so there is still 1 long
    /// poll. The next pass refreshes the token when necessary and runs the first-connect callback again. Without an
    /// active run, the first pass of the next run takes the request. That pass starts fresh anyway.
    /// </remarks>
    internal void RequestReconnect() => _ = RequestReconnectAsync();

    // Port of Reconnect as the pinger uses it. It drops the connection, then the loop starts over.
    private Task RequestReconnectAsync()
    {
        CancellationTokenSource? connectionCts;
        lock (_lock)
        {
            _reconnectRequested = true;
            connectionCts       = _connectionCts;
        }

        CancelInBackground(connectionCts);
        return Task.CompletedTask;
    }

    // Cancels outside the lock and off the thread of the caller. Thus loop code never runs on a UI thread.
    private static void CancelInBackground(CancellationTokenSource? cts)
    {
        try
        {
            _ = cts?.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // That run or attempt ended already.
        }
    }

    private void OnPhoneSlow(object? sender, EventArgs e) => _pinger?.ShortCircuit();

    private void RaiseIf(bool loggedIn, object evt)
    {
        if (loggedIn)
        {
            Raise(evt);
        }
    }

    private void Raise(object evt)
    {
        try
        {
            EventRaised?.Invoke(evt);
        }
        catch (Exception ex)
        {
            ProtocolLog.LogHandlerFailed(log, ex, evt.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Long polling stopped: credentials refused by {Step}: {Error}")]
    private static partial void LogFatal(ILogger logger, string step, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Long polling closed after {Lifetime}")]
    private static partial void LogClosed(ILogger logger, TimeSpan lifetime);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error in long polling, retrying in {Sleep}")]
    private static partial void LogRetrying(ILogger logger, Exception exception, TimeSpan sleep);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Long polling stopped while opening stream, closing it")]
    private static partial void LogReconnectWhileOpening(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The first-connect callback failed")]
    private static partial void LogFirstConnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Long polling opened")]
    private static partial void LogOpened(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Got startup ack count message: {Count}")]
    private static partial void LogSkipCount(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stopped reading data from server")]
    private static partial void LogReadStopped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Long polling ended with an error while stopping")]
    private static partial void LogStopError(ILogger logger, Exception exception);
}

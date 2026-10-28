using GMessages.Protocol.Auth;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.LongPoll;

/// <summary>
/// Keeps the long poll open: reconnects on errors, feeds every payload to the dispatcher, flushes acks and pings the phone.
/// </summary>
/// <remarks>
/// Ported from <c>doLongPoll</c> and <c>readLongPoll</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/longpoll.go">longpoll.go</see>,
/// <c>startLongPolling</c> in client.go, <c>startAckInterval</c> in session_handler.go and <c>SetActiveSession</c>
/// in methods.go. Each pass refreshes the token if needed, then opens <c>ReceiveMessages</c>. A 401 or 403 (or a
/// fatal refresh error) raises <see cref="ListenFatalError"/> and ends the loop; other failures raise
/// <see cref="ListenTemporaryError"/> and retry after <c>(errorCount + 1) × 5</c> seconds. The first success after
/// errors raises <see cref="ListenRecovered"/>; the error count starts at 1, so that also happens on the first
/// connect, as in Go. Reopening after more than a minute away asks the phone to resend updates. A minute of silence
/// on the stream drops the connection and reopens it.
/// <para>
/// Three deliberate differences from Go keep an always-on tray app sane: the retry wait stops growing at
/// <see cref="MaxRetryDelay"/> (5 minutes) instead of growing forever; a stream that ends within
/// <see cref="MinStreamLifetime"/> (5 seconds) is followed by a 5 second pause, so a server that keeps closing at once
/// can't cause a hot loop; and opening the stream gives up when no response headers arrive within
/// <see cref="ReadIdleTimeout"/>, and can be interrupted by <see cref="RequestReconnect"/>, so a request stuck on a
/// dead socket can't hold the loop. The wire requests are unchanged.
/// </para>
/// Go's background (push-woken) mode is not ported: this app has
/// no push. The phone liveness state (<see cref="PhoneLiveness"/>) belongs to the loop, not to a run, so it survives
/// restarts like Go's client-level state. Every wait uses the <see cref="TimeProvider"/>.
/// </remarks>
/// <param name="http">The HTTP client.</param>
/// <param name="rpc">The RPC session (acks, update requests, pings).</param>
/// <param name="auth">The account.</param>
/// <param name="endpoints">The server addresses.</param>
/// <param name="refresher">Refreshes the token before each connect.</param>
/// <param name="dispatcher">Handles every incoming message.</param>
/// <param name="time">The clock for every wait.</param>
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
    /// <summary>How long the stream may stay silent before reconnecting (Go: the 1 minute <c>closeIn</c> timer).</summary>
    public static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Away longer than this counts as a gap that may have missed events (Go: <c>longPollGapCatchUp</c>).</summary>
    public static readonly TimeSpan GapCatchUp = TimeSpan.FromMinutes(1);

    /// <summary>How often queued acks are sent (Go: <c>startAckInterval</c>).</summary>
    public static readonly TimeSpan AckInterval = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between retries (C#-only; Go's <c>(errorCount + 1) × 5</c> seconds grows without limit).</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>A stream shorter than this is followed by this long a pause before reopening (C#-only; Go reopens at once).</summary>
    public static readonly TimeSpan MinStreamLifetime = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly PhoneLiveness _liveness = new(time);
    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _connectionCts;
    private bool _reconnectRequested;
    private DittoPinger? _pinger;
    private int _connected;

    /// <summary>
    /// Raised for every event from the loop, the dispatcher, the token refresher and the pinger.
    /// </summary>
    public event Action<object>? EventRaised;

    /// <summary>
    /// Gets whether a long-poll stream is open right now.
    /// </summary>
    /// <remarks>
    /// Ported from Go's <c>longPollingConn</c> check in <c>IsConnected</c>: true from the moment a stream opens until
    /// it ends or <see cref="Stop"/> is called, so it is briefly false while the loop reconnects, as in Go.
    /// </remarks>
    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    /// <summary>
    /// Gets the pinger of the current run, or null when not running logged in.
    /// </summary>
    internal DittoPinger? Pinger => _pinger;

    /// <summary>
    /// Gets the phone liveness state, which outlives every run and its pinger (Go keeps it on the client).
    /// </summary>
    internal PhoneLiveness Liveness => _liveness;

    /// <summary>
    /// Runs the loop until it is stopped, canceled, or the credentials are refused.
    /// </summary>
    /// <remarks>
    /// With <paramref name="loggedIn"/> false (pairing), no pinger or ack timer runs and no listen events are raised,
    /// as in Go. <paramref name="onFirstConnect"/> runs in the background once the first connection opens, and again
    /// after a recovery reconnect (Go starts a new loop with <c>postConnect</c> then). When not logged in, a fatal
    /// error (401/403 or a fatal refresh error) is thrown instead of returning quietly: no listen event carries it,
    /// so pairing would otherwise never learn why the long poll stopped.
    /// </remarks>
    /// <param name="loggedIn">True once paired; turns on the pinger, the ack timer and listen events.</param>
    /// <param name="onFirstConnect">Runs after the first successful connect, or null.</param>
    /// <param name="ct">Stops the loop.</param>
    /// <returns>A task that completes when the loop ends; stopping or canceling ends it normally, without an exception.</returns>
    /// <exception cref="GmRequestException">Not logged in, and the server refused the credentials (401/403).</exception>
    /// <exception cref="InvalidOperationException">Not logged in, and the token refresh failed fatally.</exception>
    public async Task RunAsync(bool loggedIn, Action? onFirstConnect, CancellationToken ct)
    {
        using var stop_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken stop = stop_cts.Token;
        lock (_lock)
        {
            _stopCts = stop_cts;
        }

        // Pinger And Ack Timer (Logged In Only)
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
            // Stopped Or Canceled: A Normal End, Whatever The Aborted Socket Threw
            LogStopError(log, ex);
        }
        finally
        {
            await stop_cts.CancelAsync().ConfigureAwait(false);
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
            // Only Clear Our Own CTS, So A Newer Run Started Meanwhile Stays Stoppable
            lock (_lock)
            {
                if (_stopCts == stop_cts)
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
        CancellationTokenSource? stop_cts;
        lock (_lock)
        {
            stop_cts = _stopCts;
        }

        CancelInBackground(stop_cts);
    }

    /// <summary>
    /// Starts a new session ID and asks the phone to start sending updates for it.
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

    // Returns The Fatal Error That Ended The Loop, Or Null When It Was Stopped
    private async Task<Exception?> ListenAsync(bool loggedIn, Action? onFirstConnect, CancellationToken stop)
    {
        Action? first_connect           = onFirstConnect;
        string listen_request_id        = Guid.NewGuid().ToString();
        int error_count                 = 1;
        DateTimeOffset? disconnected_at = null;

        // Recovery Reconnect: Start Over Like A New doLongPoll (Go: Reconnect Starts A Fresh Loop With postConnect)
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

            first_connect     = onFirstConnect;
            listen_request_id = Guid.NewGuid().ToString();
            error_count       = 1;
            disconnected_at   = null;
            _pinger?.BumpDataReceiveCheck(TimeSpan.FromMinutes(10));
            return true;
        }

        while (!stop.IsCancellationRequested)
        {
            TakeReconnect();

            // One Connection Attempt; A Reconnect Request Cancels Its Backoff Wait Or Its Read
            using var attempt_cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_lock)
            {
                _connectionCts = attempt_cts;
            }

            try
            {
                // Refresh Token
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
                        LogFatal(log, wrapped);
                        RaiseIf(loggedIn, new ListenFatalError(wrapped));
                        return wrapped;
                    }

                    error = wrapped;
                }

                // Open The Stream; A Reconnect Request Or 1 Minute Without Headers Interrupts It
                HttpResponseMessage? res = null;
                if (error is null)
                {
                    using var open_timeout = new CancellationTokenSource(ReadIdleTimeout, time);
                    using var open_cts     = CancellationTokenSource.CreateLinkedTokenSource(attempt_cts.Token, open_timeout.Token);
                    try
                    {
                        res = await http.PostAsync(auth.HasCookies ? endpoints.ReceiveMessagesGoogle : endpoints.ReceiveMessages, BuildReceiveRequest(listen_request_id), GmContentType.PbLite, true, open_cts.Token).ConfigureAwait(false);
                        int status = (int)res.StatusCode;
                        if (status >= 400)
                        {
                            // The Body Read Disposes The Response And Never Throws, So A 401/403 Always Stays Fatal
                            HttpResponseMessage failed = res;
                            res = null;
                            var failure = new GmRequestException(status, null, await ReadBodyAsync(failed, stop).ConfigureAwait(false));
                            if (status is 401 or 403)
                            {
                                LogFatal(log, failure);
                                RaiseIf(loggedIn, new ListenFatalError(failure));
                                return failure;
                            }

                            error = failure;
                        }
                    }
                    catch (Exception) when (attempt_cts.IsCancellationRequested && !stop.IsCancellationRequested)
                    {
                        // Reconnect Requested While Opening: Start Over Right Away
                        res?.Dispose();
                        continue;
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested)
                    {
                        res?.Dispose();
                        error = open_timeout.IsCancellationRequested
                            ? new TimeoutException($"No response headers within {ReadIdleTimeout.TotalSeconds:0} seconds.", ex)
                            : ex;
                    }
                }

                // Back Off On Errors
                if (error is not null)
                {
                    RaiseIf(loggedIn, new ListenTemporaryError(error));
                    error_count++;
                    TimeSpan sleep = RetryDelay(error_count);
                    LogRetrying(log, error, sleep);
                    await PauseAsync(sleep, attempt_cts.Token, stop).ConfigureAwait(false);
                    continue;
                }

                using (res)
                {
                    // Go (longpoll.go:578-583): A Reconnect While Opening Closes This Stream And Starts Over
                    if (TakeReconnect())
                    {
                        LogReconnectWhileOpening(log);
                        continue;
                    }

                    if (error_count > 0)
                    {
                        error_count = 0;
                        RaiseIf(loggedIn, new ListenRecovered());
                    }

                    LogOpened(log);
                    Volatile.Write(ref _connected, 1);
                    if (loggedIn && disconnected_at is { } since && time.GetUtcNow() - since > GapCatchUp)
                    {
                        _ = _pinger!.RequestUpdatesAfterGapAsync("long polling was disconnected");
                    }

                    if (first_connect is not null)
                    {
                        _ = RunFirstConnectAsync(first_connect);
                        first_connect = null;
                    }

                    DateTimeOffset opened_at = time.GetUtcNow();
                    try
                    {
                        await ReadAsync(res!, attempt_cts.Token, stop).ConfigureAwait(false);
                    }
                    finally
                    {
                        Volatile.Write(ref _connected, 0);
                    }

                    // A Stream That Ended Almost At Once Gets A Pause, So A Server Closing Right Away Can't Spin The Loop
                    disconnected_at = time.GetUtcNow();
                    if (disconnected_at - opened_at < MinStreamLifetime)
                    {
                        await PauseAsync(MinStreamLifetime, attempt_cts.Token, stop).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (_connectionCts == attempt_cts)
                    {
                        _connectionCts = null;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the wait before the next retry: <c>(errorCount + 1) × 5</c> seconds as in Go, but at most 5 minutes.
    /// </summary>
    /// <param name="errorCount">The error count after this error.</param>
    /// <returns>The wait.</returns>
    internal static TimeSpan RetryDelay(int errorCount)
    {
        TimeSpan go_delay = TimeSpan.FromSeconds((errorCount + 1) * 5.0);
        return go_delay < MaxRetryDelay ? go_delay : MaxRetryDelay;
    }

    // Waits Out A Backoff Or Pause; A Reconnect Request Ends It Early (Go Starts The New Loop Right Away)
    private async Task PauseAsync(TimeSpan delay, CancellationToken attempt, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, time, attempt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // Reconnect Requested
        }
    }

    // Reads A Failed Response's Body For Diagnosis, Always Disposing It; A Failed Read Gives An Empty Body
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

    // Port Of `go onFirstConnect()`: Runs In The Background; A Failure Is Logged, Not Lost
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

    // Port Of readLongPoll: Any Read Problem Just Ends This Connection
    private async Task ReadAsync(HttpResponseMessage res, CancellationToken connection, CancellationToken stop)
    {
        try
        {
            Stream body = await res.Content.ReadAsStreamAsync(connection).ConfigureAwait(false);
            await foreach (LongPollingPayload payload in LongPollStreamReader.ReadAsync(body, ReadIdleTimeout, time, connection, log).ConfigureAwait(false))
            {
                // Go Checks Data First, Then The Startup Ack Count; Heartbeats Need Nothing
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

    // Port Of startAckInterval
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
            // Stopped
        }
    }

    /// <summary>
    /// Drops the current connection or backoff wait and starts over at once, as a fresh connect would.
    /// </summary>
    /// <remarks>
    /// The same path the pinger uses (Go: <c>Reconnect</c>). The run keeps going, so there is still one long poll;
    /// the next pass refreshes the token if needed and runs the first-connect callback again. Without an active run,
    /// the request is consumed by the next run's first pass, which starts fresh anyway.
    /// </remarks>
    internal void RequestReconnect() => _ = RequestReconnectAsync();

    // Port Of Reconnect As Used By The Pinger: Drop The Connection, Then Start Over
    private Task RequestReconnectAsync()
    {
        CancellationTokenSource? connection_cts;
        lock (_lock)
        {
            _reconnectRequested = true;
            connection_cts      = _connectionCts;
        }

        CancelInBackground(connection_cts);
        return Task.CompletedTask;
    }

    // Cancels Outside The Lock And Off The Caller's Thread, So Loop Code Never Runs On A UI Thread
    private static void CancelInBackground(CancellationTokenSource? cts)
    {
        try
        {
            _ = cts?.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // That Run Or Attempt Already Ended
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
            LogHandlerFailed(log, ex, evt.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Long polling stopped: credentials refused")]
    private static partial void LogFatal(ILogger logger, Exception exception);

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

    [LoggerMessage(Level = LogLevel.Error, Message = "An event subscriber threw for {EventType}")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string eventType);
}

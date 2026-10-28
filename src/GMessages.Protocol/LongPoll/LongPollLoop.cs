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
/// on the stream drops the connection and reopens it. Go's background (push-woken) mode is not ported: this app has
/// no push. Every wait uses the <see cref="TimeProvider"/>.
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

    private readonly Lock _lock = new();
    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _connectionCts;
    private bool _reconnectRequested;
    private DittoPinger? _pinger;

    /// <summary>
    /// Raised for every event from the loop, the dispatcher, the token refresher and the pinger.
    /// </summary>
    public event Action<object>? EventRaised;

    /// <summary>
    /// Gets the pinger of the current run, or null when not running logged in.
    /// </summary>
    internal DittoPinger? Pinger => _pinger;

    /// <summary>
    /// Runs the loop until it is stopped, canceled, or the credentials are refused.
    /// </summary>
    /// <remarks>
    /// With <paramref name="loggedIn"/> false (pairing), no pinger or ack timer runs and no listen events are raised,
    /// as in Go. <paramref name="onFirstConnect"/> runs in the background once the first connection opens, and again
    /// after a recovery reconnect (Go starts a new loop with <c>postConnect</c> then).
    /// </remarks>
    /// <param name="loggedIn">True once paired; turns on the pinger, the ack timer and listen events.</param>
    /// <param name="onFirstConnect">Runs after the first successful connect, or null.</param>
    /// <param name="ct">Stops the loop.</param>
    /// <returns>A task that completes when the loop ends; stopping or canceling ends it normally, without an exception.</returns>
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
            var pinger = new DittoPinger(rpc, time, log, SetActiveSessionAsync, RequestReconnectAsync);
            pinger.EventRaised += Raise;
            pinger.BumpDataReceiveCheck(TimeSpan.FromMinutes(10));
            dispatcher.Pinger = pinger;
            rpc.PhoneSlow     += OnPhoneSlow;
            _pinger            = pinger;
            background         = Task.WhenAll(pinger.RunAsync(stop), FlushAcksLoopAsync(stop));
        }

        dispatcher.EventRaised += Raise;
        refresher.EventRaised  += Raise;
        try
        {
            await ListenAsync(loggedIn, onFirstConnect, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped Or Canceled: A Normal End
        }
        finally
        {
            await stop_cts.CancelAsync().ConfigureAwait(false);
            await background.ConfigureAwait(false);
            dispatcher.EventRaised -= Raise;
            refresher.EventRaised  -= Raise;
            rpc.PhoneSlow          -= OnPhoneSlow;
            dispatcher.Pinger       = null;
            lock (_lock)
            {
                _stopCts = null;
            }
        }
    }

    /// <summary>
    /// Stops the loop and closes the connection.
    /// </summary>
    /// <remarks>Ported from <c>closeLongPolling</c>.</remarks>
    public void Stop()
    {
        lock (_lock)
        {
            _stopCts?.Cancel();
        }
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

    private async Task ListenAsync(bool loggedIn, Action? onFirstConnect, CancellationToken stop)
    {
        Action? first_connect          = onFirstConnect;
        string listen_request_id       = Guid.NewGuid().ToString();
        int error_count                = 1;
        DateTimeOffset? disconnected_at = null;

        while (!stop.IsCancellationRequested)
        {
            // Refresh Token
            Exception? error = null;
            try
            {
                await refresher.RefreshIfNeededAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                if (TokenRefresher.IsFatal(ex))
                {
                    LogFatal(log, ex);
                    RaiseIf(loggedIn, new ListenFatalError(ex));
                    return;
                }

                error = ex;
            }

            // Open The Stream
            HttpResponseMessage? res = null;
            if (error is null)
            {
                try
                {
                    res = await http.PostAsync(auth.HasCookies ? endpoints.ReceiveMessagesGoogle : endpoints.ReceiveMessages, BuildReceiveRequest(listen_request_id), GmContentType.PbLite, true, stop).ConfigureAwait(false);
                    int status = (int)res.StatusCode;
                    if (status is 401 or 403)
                    {
                        var fatal = new GmRequestException(status, null, await res.Content.ReadAsByteArrayAsync(stop).ConfigureAwait(false));
                        res.Dispose();
                        LogFatal(log, fatal);
                        RaiseIf(loggedIn, new ListenFatalError(fatal));
                        return;
                    }

                    if (status >= 400)
                    {
                        res.Dispose();
                        error = new GmRequestException(status, null, []);
                    }
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    error = ex;
                }
            }

            // Back Off On Errors
            if (error is not null)
            {
                RaiseIf(loggedIn, new ListenTemporaryError(error));
                error_count++;
                TimeSpan sleep = TimeSpan.FromSeconds((error_count + 1) * 5);
                LogRetrying(log, error, sleep);
                await Task.Delay(sleep, time, stop).ConfigureAwait(false);
                continue;
            }

            using (res)
            {
                if (error_count > 0)
                {
                    error_count = 0;
                    RaiseIf(loggedIn, new ListenRecovered());
                }

                LogOpened(log);
                if (loggedIn && disconnected_at is { } since && time.GetUtcNow() - since > GapCatchUp)
                {
                    _ = _pinger!.RequestUpdatesAfterGapAsync("long polling was disconnected");
                }

                if (first_connect is not null)
                {
                    _ = Task.Run(first_connect, CancellationToken.None);
                    first_connect = null;
                }

                await ReadAsync(res!, stop).ConfigureAwait(false);
            }

            disconnected_at = time.GetUtcNow();

            // Recovery Reconnect: Start Over Like A New doLongPoll
            lock (_lock)
            {
                if (_reconnectRequested)
                {
                    _reconnectRequested = false;
                    first_connect       = onFirstConnect;
                    listen_request_id   = Guid.NewGuid().ToString();
                    error_count         = 1;
                    disconnected_at     = null;
                    _pinger?.BumpDataReceiveCheck(TimeSpan.FromMinutes(10));
                }
            }
        }
    }

    // Port Of readLongPoll: Any Read Problem Just Ends This Connection
    private async Task ReadAsync(HttpResponseMessage res, CancellationToken stop)
    {
        using var connection_cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_lock)
        {
            _connectionCts = connection_cts;
        }

        try
        {
            Stream body = await res.Content.ReadAsStreamAsync(connection_cts.Token).ConfigureAwait(false);
            await foreach (LongPollingPayload payload in LongPollStreamReader.ReadAsync(body, ReadIdleTimeout, time, connection_cts.Token, log).ConfigureAwait(false))
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
        finally
        {
            lock (_lock)
            {
                _connectionCts = null;
            }
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
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped
        }
    }

    // Port Of Reconnect As Used By The Pinger: Drop The Connection, Then Start Over
    private Task RequestReconnectAsync()
    {
        lock (_lock)
        {
            _reconnectRequested = true;
            _connectionCts?.Cancel();
        }

        return Task.CompletedTask;
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Long polling opened")]
    private static partial void LogOpened(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Got startup ack count message: {Count}")]
    private static partial void LogSkipCount(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stopped reading data from server")]
    private static partial void LogReadStopped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "An event subscriber threw for {EventType}")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string eventType);
}

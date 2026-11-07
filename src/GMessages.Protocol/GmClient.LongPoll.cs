using GMessages.Protocol.Events;

namespace GMessages.Protocol;

/// <summary>
/// The long poll part of <see cref="GmClient"/>. It starts, stops and reconnects the long poll, and runs the work after a connect.
/// </summary>
public sealed partial class GmClient
{
    private bool IsRunning
    {
        get
        {
            lock (_runLock)
            {
                return _runCts is not null && !_run.IsCompleted;
            }
        }
    }

    // Port of Disconnect. The caller holds _gate.
    private async Task DisconnectCoreAsync()
    {
        await StopLongPollingAsync().ConfigureAwait(false);
        Rpc.FailAllWaiters();
    }

    // Port of startLongPolling. The loop runs the pinger and the ack timer. postConnect runs when the long poll opens.
    // The caller holds _gate.
    private async Task StartLongPollingAsync()
    {
        await StopLongPollingAsync().ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        lock (_runLock)
        {
            _runCts = cts;
            _run    = RunLongPollAsync(cts.Token);
        }
    }

    private async Task RunLongPollAsync(CancellationToken ct)
    {
        try
        {
            await _loop.RunAsync(true, () => TrackPostConnect(PostConnectAsync(ct)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogLongPollFailed(_log, ex);
        }
    }

    // The method keeps each postConnect run, so the stop can wait for it.
    private void TrackPostConnect(Task postConnect)
    {
        lock (_runLock)
        {
            _postConnect = _postConnect.IsCompleted ? postConnect : Task.WhenAll(_postConnect, postConnect);
        }
    }

    // Port of closeLongPolling. It also waits for the run and postConnect to end. The caller holds _gate.
    private async Task StopLongPollingAsync()
    {
        CancellationTokenSource? cts;
        Task run;
        lock (_runLock)
        {
            cts     = _runCts;
            run     = _run;
            _runCts = null;
            _run    = Task.CompletedTask;
        }

        if (cts is null)
        {
            return;
        }

        _loop.Stop();
        await cts.CancelAsync().ConfigureAwait(false);
        await AwaitStoppingAsync(run).ConfigureAwait(false);

        Task postConnect;
        lock (_runLock)
        {
            postConnect  = _postConnect;
            _postConnect = Task.CompletedTask;
        }

        await AwaitStoppingAsync(postConnect).ConfigureAwait(false);
        cts.Dispose();
    }

    // A stop can cause any error, such as the IOException of an aborted socket. The method logs it and never throws.
    private async Task AwaitStoppingAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStopError(_log, ex);
        }
    }

    // Port of Reconnect: close, check, start. It does not refresh the token. The caller holds _gate.
    private async Task ReconnectAsync()
    {
        await StopLongPollingAsync().ConfigureAwait(false);
        try
        {
            ThrowIfNotLoggedIn();
        }
        catch (InvalidOperationException ex)
        {
            LogReconnectFailed(_log, ex);
            Raise(new ListenFatalError(new InvalidOperationException("Failed to reconnect.", ex)));
            return;
        }

        await StartLongPollingAsync().ConfigureAwait(false);
    }

    // Port of postConnect without the Bugle default check. It never throws.
    private async Task PostConnectAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(PostConnectDelay, _time, ct).ConfigureAwait(false);

            // Wait up to 3 more seconds for the startup skip count to go to 0.
            if (_dispatcher.SkipCount > 0)
            {
                LogSkipCountWaiting(_log, _dispatcher.SkipCount);
                for (int i = 0; i < 3 && _dispatcher.SkipCount > 0; i++)
                {
                    await Task.Delay(SkipCountDelay, _time, ct).ConfigureAwait(false);
                }

                if (_dispatcher.SkipCount > 0)
                {
                    LogSkipCountStillSet(_log, _dispatcher.SkipCount);
                }

                Raise(new HackySetActiveMayFail());
            }

            await Rpc.FlushAcksAsync(ct).ConfigureAwait(false);
            await Task.Delay(BeforeUpdatesWait, _time, ct).ConfigureAwait(false);
            try
            {
                await _loop.SetActiveSessionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogSetActiveSessionFailed(_log, ex);
                Raise(new PingFailed(new InvalidOperationException("Failed to set active session.", ex), 0));
                return;
            }

            LogSetActiveSessionSent(_log);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client disconnected before the steps finished.
        }
        catch (Exception ex)
        {
            LogPostConnectFailed(_log, ex);
        }
    }
}

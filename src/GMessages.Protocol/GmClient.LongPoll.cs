using System.Security.Cryptography;
using System.Text;
using GMessages.Protocol.Auth;
using GMessages.Protocol.Events;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol;

/// <summary>
/// The long poll: starting, stopping and reconnecting it, and the work that follows a connect.
/// </summary>
/// <remarks>
/// Part of <see cref="GmClient"/>; the other members are in GmClient.cs.
/// </remarks>
public sealed partial class GmClient
{

    // =========================================================================
    // LONG POLL
    // =========================================================================

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
    // Port Of Disconnect; Caller Holds _gate
    private async Task DisconnectCoreAsync()
    {
        await StopLongPollingAsync().ConfigureAwait(false);
        Rpc.FailAllWaiters();
    }

    // Port Of startLongPolling: The Loop Runs The Pinger And Ack Timer; postConnect Runs Once It Opens; Caller Holds _gate
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

    // Keeps Every postConnect Run So Stopping Can Wait For It
    private void TrackPostConnect(Task postConnect)
    {
        lock (_runLock)
        {
            _postConnect = _postConnect.IsCompleted ? postConnect : Task.WhenAll(_postConnect, postConnect);
        }
    }

    // Port Of closeLongPolling Plus Waiting For The Run And postConnect To End; Caller Holds _gate
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

    // While Stopping, Any Error (Such As An Aborted Socket's IOException) Is Expected: Logged, Never Thrown
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

    // Port Of Reconnect: Close, Check, Start (No Token Refresh); Caller Holds _gate
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

    // Port Of postConnect (Without The Bugle Default Check); Never Throws
    private async Task PostConnectAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(PostConnectDelay, _time, ct).ConfigureAwait(false);

            // Wait Up To 3 s More For The Startup Skip Count To Drain
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

            // Acks, Then GET_UPDATES
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
            // Disconnected Before It Finished
        }
        catch (Exception ex)
        {
            LogPostConnectFailed(_log, ex);
        }
    }
}

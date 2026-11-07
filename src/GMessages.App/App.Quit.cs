using System.Diagnostics;
using GMessages.App.Windowing;
using Microsoft.Windows.System.Power;
using Windows.Networking.Connectivity;

namespace GMessages.App;

/// <summary>
/// Shutting down: Dispose, Quit (disconnect and flush the saves) and the hard exit that follows.
/// </summary>
/// <remarks>
/// Part of <see cref="App"/>; the other members are in App.xaml.cs.
/// </remarks>
public sealed partial class App
{

    /// <summary>
    /// Stops the watchers and timer, removes the tray icon and closes the windows and the log.
    /// </summary>
    /// <remarks>
    /// The connection controller is disposed by Quit before this, since that needs an await.
    /// </remarks>
    public void Dispose()
    {
        // A show request already queued must not open windows that are shutting down
        _quitting = true;

        _memoryTrim?.Cancel();
        _quitWait?.Unregister(null);
        _showWait?.Unregister(null);
        PowerManager.SystemSuspendStatusChanged -= OnSystemSuspendStatusChanged;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _networkDebounce?.Stop();
        _launchNotificationTimeout?.Stop();
        _notifications?.Dispose();
        _badge?.Dispose();
        _trayIcon?.Dispose();
        _trayMenu?.Close();
        CloseWindows();
        _store?.Dispose();
        _media?.Dispose();
        _logProvider?.Dispose();
        GC.SuppressFinalize(this);
    }

    // Async void: called from event handlers; it always ends in Exit
    private async void Quit()
    {
        if (_quitting)
        {
            return;
        }

        AppLog.Quit(_logger);
        _quitting = true;
        long started = Stopwatch.GetTimestamp();
        CloseWindows();
        long windowsMs    = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long connectionMs = 0;
        long savesMs      = 0;
        bool savesDone    = false;

        // The client closes its long poll and writes a pending session save while the log is still open.
        // Each step has its own limit, so a step that fails or hangs does not skip the steps after it.
        try
        {
            // Test runs remove their toasts, so no toast leaks into the next test. Real runs keep their toasts.
            if (Program.IsTestRun && _notifications is { } notifications)
            {
                await QuitStepAsync(notifications.RemoveAllAsync, TimeSpan.FromSeconds(5));
            }

            long step = Stopwatch.GetTimestamp();
            if (_connection is { } connection)
            {
                await QuitStepAsync(() => connection.DisposeAsync().AsTask(), ConnectionLimit);
            }

            connectionMs = (long)Stopwatch.GetElapsedTime(step).TotalMilliseconds;

            // After a catch-up, the queue can hold hundreds of message saves. The database is a cache.
            // When the limit cuts off a save, the app fetches that message again from the phone.
            step      = Stopwatch.GetTimestamp();
            savesDone = _store is { } store && await QuitStepAsync(store.FlushAsync, SavesLimit);
            savesMs   = (long)Stopwatch.GetElapsedTime(step).TotalMilliseconds;
        }
        finally
        {
            long totalMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            AppLog.QuitTimed(_logger, windowsMs, connectionMs, savesMs, savesDone, totalMs);

            // No Decrypted File Outlives The App
            _media?.Clear();
            Dispose();
            XamlWindow.ExitMessageLoop();
            ExitSoon();
        }
    }

    // Returns false when the step fails or passes its limit. The log holds the error type only.
    private async Task<bool> QuitStepAsync(Func<Task> step, TimeSpan limit)
    {
        try
        {
            await step().WaitAsync(limit);
            return true;
        }
        catch (Exception ex)
        {
            // Nothing is left to do but exit.
            AppLog.QuitStepFailed(_logger, ex.GetType().Name);
            return false;
        }
    }

    // ExitMessageLoop Ends The Message Loop, Then The Process Ends Once Main Returns; Anything Native Still Winding Down (A Media
    // Job, A Notification Or Network Teardown) Could Hold The Process Up For About A Minute (0.6.4). Nothing Is Left To
    // Save By Now, So The Process Ends For Sure Shortly After
    // ponytail: a hard exit after a fixed wait; find the native holder in a dump if the wait ever fires in the log-free gap
    private static void ExitSoon() =>
        _ = Task.Delay(ExitGrace).ContinueWith(_ => Environment.Exit(0), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
}

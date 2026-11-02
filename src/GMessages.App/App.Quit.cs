using System.Diagnostics;
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

        // Let the client close its long poll and flush a save (while the log is still open), but never hang the quit on it
        try
        {
            // Test Runs Take Their Toasts With Them, So None Leak Into The Next Test; Real Runs Keep Theirs
            if (Program.IsTestRun && _notifications is not null)
            {
                await _notifications.RemoveAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            long step = Stopwatch.GetTimestamp();
            if (_connection is not null)
            {
                await _connection.DisposeAsync().AsTask().WaitAsync(ConnectionLimit);
            }

            connectionMs = (long)Stopwatch.GetElapsedTime(step).TotalMilliseconds;

            // The Message Saves Still Queued (After A Catch-Up There Can Be Hundreds) Are Written Before The Exit, Up To
            // A Limit: The Database Is A Cache, So A Save Cut Off Is Fetched Again From The Phone
            step = Stopwatch.GetTimestamp();
            if (_store is not null)
            {
                await _store.FlushAsync().WaitAsync(SavesLimit);
                savesDone = true;
            }

            savesMs = (long)Stopwatch.GetElapsedTime(step).TotalMilliseconds;
        }
#pragma warning disable CA1031 // Quitting regardless
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Nothing Left To Do But Exit; Only The Type Is Logged
            AppLog.QuitStepFailed(_logger, ex.GetType().Name);
        }
        finally
        {
            long totalMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            AppLog.QuitTimed(_logger, windowsMs, connectionMs, savesMs, savesDone, totalMs);

            // No Decrypted File Outlives The App
            _media?.Clear();
            Dispose();
            Exit();
            ExitSoon();
        }
    }

    // Exit Ends The Message Loop, Then The Process Ends Once Main Returns; Anything Native Still Winding Down (A Media
    // Job, A Notification Or Network Teardown) Could Hold The Process Up For About A Minute (0.6.4). Nothing Is Left To
    // Save By Now, So The Process Ends For Sure Shortly After
    // ponytail: a hard exit after a fixed wait; find the native holder in a dump if the wait ever fires in the log-free gap
    private static void ExitSoon() =>
        _ = Task.Delay(ExitGrace).ContinueWith(_ => Environment.Exit(0), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
}

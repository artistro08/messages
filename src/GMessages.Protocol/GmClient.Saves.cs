using GMessages.Protocol.Events;

namespace GMessages.Protocol;

/// <summary>
/// Session saves: writing the session file after a cookie or token change, on a timer that can be suspended.
/// </summary>
/// <remarks>
/// Part of <see cref="GmClient"/>; the other members are in GmClient.cs.
/// </remarks>
public sealed partial class GmClient
{

    // =========================================================================
    // SESSION SAVES
    // =========================================================================

    private void OnCookiesChanged(object? sender, EventArgs e) => RequestSave();

    private void OnRefresherEvent(object evt)
    {
        if (evt is AuthTokenRefreshed)
        {
            RequestSave();
        }
    }

    // Saves Now When The Last Save Started 5 s Ago Or More; Otherwise Once At The End Of That Window
    private void RequestSave()
    {
        lock (_saveLock)
        {
            if (_savesStopped || _savesSuspended || _saveTimer is not null)
            {
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();
            TimeSpan wait      = _lastSaveStart + SaveDebounce - now;
            if (wait > TimeSpan.Zero)
            {
                _saveTimer = _time.CreateTimer(_ => SaveFromTimer(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }

            _lastSaveStart = now;
        }

        WriteSession();
    }

    private void SaveFromTimer()
    {
        lock (_saveLock)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            if (_savesStopped || _savesSuspended)
            {
                return;
            }

            _lastSaveStart = _time.GetUtcNow();
        }

        WriteSession();
    }

    // Saves Right Away, Closing Any Open Window (After Pairing, And The Flush On Dispose)
    private void SaveNow()
    {
        lock (_saveLock)
        {
            _saveTimer?.Dispose();
            _saveTimer     = null;
            _lastSaveStart = _time.GetUtcNow();
        }

        WriteSession();
    }

    // One Writer At A Time; A Failure Is Logged And Raised (Outside The Lock), Never Thrown (It May Run On A Timer Thread)
    private void WriteSession()
    {
        Exception? failure = null;
        lock (_writeLock)
        {
            // An Immediate Save That Raced The Pairing Pause Must Not Write Mid-Pairing
            if (Volatile.Read(ref _savesStopped) || Volatile.Read(ref _savesSuspended))
            {
                return;
            }

            try
            {
                OnWritingForTests?.Invoke();
                _store.Save(_auth);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (failure is not null)
        {
            LogSaveFailed(_log, failure);
            Raise(new SessionSaveFailed(failure.GetType().Name));
        }
    }

    // Pausing Also Drops An Open Window, So Nothing Saved Before Pairing Is Written Mid-Pairing
    private void SetSavesSuspended(bool suspended)
    {
        lock (_saveLock)
        {
            _savesSuspended = suspended;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }

    private void StopSaves()
    {
        lock (_saveLock)
        {
            _savesStopped = true;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }
}

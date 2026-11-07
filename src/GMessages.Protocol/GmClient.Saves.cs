using GMessages.Protocol.Events;

namespace GMessages.Protocol;

/// <summary>
/// The session save part of <see cref="GmClient"/>. It writes the session file after a cookie or token change, on a timer that can stop.
/// </summary>
public sealed partial class GmClient
{
    private void OnCookiesChanged(object? sender, EventArgs e) => RequestSave();

    private void OnRefresherEvent(object evt)
    {
        if (evt is AuthTokenRefreshed)
        {
            RequestSave();
        }
    }

    // The save runs now when the last save started 5 seconds ago or more. Otherwise it runs at the end of that window.
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

    // The save runs at once and closes an open window. Pairing and dispose use it.
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

    // The method can run on a timer thread, so it never throws. It logs a failure and raises it outside the lock.
    private void WriteSession()
    {
        Exception? failure = null;
        lock (_writeLock)
        {
            // An immediate save can race the pairing pause. It must not write during the pairing.
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

    // The pause also drops an open window, so a save from before the pairing cannot write during the pairing.
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

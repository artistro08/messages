using System.Runtime;
using Microsoft.UI.Dispatching;

namespace GMessages.App;

/// <summary>
/// Hands memory the idle tray app no longer uses back to Windows.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>App.ScheduleMemoryTrim</c> / <c>App.TrimMemory</c>. Waits a few seconds so a
/// quick reopen doesn't pay for it and closed windows finish tearing down, then runs a full compacting
/// collection and empties the working set (Windows pages memory back in if it's needed again).
/// </remarks>
internal sealed class MemoryTrim
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<bool> _isIdle;

    /// <summary>
    /// Creates the trim timer on the UI thread's dispatcher.
    /// </summary>
    /// <param name="dispatcher">The UI thread's dispatcher queue.</param>
    /// <param name="isIdle">True when nothing is on screen; the trim is skipped otherwise.</param>
    public MemoryTrim(DispatcherQueue dispatcher, Func<bool> isIdle)
    {
        _isIdle = isIdle;
        _timer  = dispatcher.CreateTimer();
        _timer.Interval    = TimeSpan.FromSeconds(5);
        _timer.IsRepeating = false;
        _timer.Tick       += (_, _) => Trim();
    }

    /// <summary>
    /// Trims in a few seconds, restarting the wait if one is already pending.
    /// </summary>
    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// Drops a pending trim.
    /// </summary>
    public void Cancel() => _timer.Stop();

    private void Trim()
    {
        if (!_isIdle())
        {
            return;
        }

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Finalizers release the WinRT objects behind closed windows; collect what they let go
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _ = NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1);
    }
}

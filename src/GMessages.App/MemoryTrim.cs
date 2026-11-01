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
    public void Schedule() => Schedule(TimeSpan.FromSeconds(5));

    /// <summary>
    /// Trims after a given wait, restarting the wait if one is already pending, so a burst of calls ends in one trim.
    /// </summary>
    /// <param name="delay">How long to wait before the trim.</param>
    public void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
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

        // Aggressive Also Compacts The Large Object Heap
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Finalizers release the WinRT objects behind closed windows; collect what they let go, and hand it back too
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Native Heap: Merge Free Blocks So Windows Can Take The Pages Back
        _ = NativeMethods.HeapCompact(NativeMethods.GetProcessHeap(), 0);
        _ = NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1);

        // Test Hook: Tells The UI Tests The Trim Ran (A Debug-Build Note, "trim:done")
        TestHooks.NoteState("trim-done", "1");
    }
}

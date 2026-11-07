using Windows.System;

namespace GMessages.App;

/// <summary>
/// Gives the memory that the idle tray app no longer uses back to Windows.
/// </summary>
/// <remarks>
/// Ported from <c>App.ScheduleMemoryTrim</c> and <c>App.TrimMemory</c> of Magic Buds. The trim waits a few seconds,
/// so a quick reopen does not pay for it. Closed windows can also finish their teardown in that time. Then the trim
/// runs a full compacting collection and empties the working set. Windows pages the memory in again when the app
/// needs it.
/// </remarks>
internal sealed class MemoryTrim
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<bool> _isIdle;

    /// <summary>
    /// Creates the trim timer on the dispatcher of the UI thread.
    /// </summary>
    /// <param name="dispatcher">The dispatcher queue of the UI thread.</param>
    /// <param name="isIdle">Returns true when nothing is on screen. The trim does nothing otherwise.</param>
    public MemoryTrim(DispatcherQueue dispatcher, Func<bool> isIdle)
    {
        _isIdle = isIdle;
        _timer  = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick       += (_, _) => Trim();
    }

    /// <summary>
    /// Trims in 5 seconds, and starts the wait again when a trim is already pending.
    /// </summary>
    public void Schedule() => Schedule(TimeSpan.FromSeconds(5));

    /// <summary>
    /// Trims after a given wait, and starts the wait again when a trim is already pending. Thus a burst of calls ends
    /// in a single trim.
    /// </summary>
    /// <param name="delay">The wait before the trim.</param>
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

        // Aggressive also compacts the large object heap.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Finalizers release the WinRT objects behind closed windows. The second collection gives that memory back too.
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // HeapCompact merges the free blocks of the native heap, so Windows can take the pages back.
        _ = NativeMethods.HeapCompact(NativeMethods.GetProcessHeap(), 0);
        _ = NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1);

        // This note tells the UI tests that the trim ran. Only Debug builds write it.
        TestHooks.NoteState("trim-done", "1");
    }
}

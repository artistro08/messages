using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Protocol.Tests.Fakes;

/// <summary>
/// A fake clock that signals whenever a timer is created.
/// </summary>
/// <remarks>
/// A test waits on <see cref="TimerCreated"/> before it advances the clock. The clock moves only when the code under
/// test waits, so the test needs no real sleep.
/// </remarks>
internal sealed class SignalingTimeProvider : FakeTimeProvider
{
    /// <summary>Gets a semaphore that releases once for each created timer.</summary>
    public SemaphoreSlim TimerCreated { get; } = new(0);

    /// <summary>Gets the due time of every created timer, in creation order.</summary>
    public ConcurrentQueue<TimeSpan> DueTimes { get; } = new();

    /// <summary>
    /// Counts the created timers with the given due time.
    /// </summary>
    /// <param name="dueTime">The due time to count.</param>
    /// <returns>The number of timers created with that due time.</returns>
    public int CountTimers(TimeSpan dueTime) => DueTimes.Count(d => d == dueTime);

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        DueTimes.Enqueue(dueTime);
        TimerCreated.Release();
        return timer;
    }
}

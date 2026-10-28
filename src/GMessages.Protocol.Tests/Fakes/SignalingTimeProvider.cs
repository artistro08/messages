using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Protocol.Tests.Fakes;

/// <summary>
/// A fake clock that signals whenever a timer is created.
/// </summary>
/// <remarks>
/// Test-only. Tests wait on <see cref="TimerCreated"/> before advancing, so the clock only moves once the code under
/// test is actually waiting. No real sleeps are needed.
/// </remarks>
internal sealed class SignalingTimeProvider : FakeTimeProvider
{
    /// <summary>Released once per created timer.</summary>
    public SemaphoreSlim TimerCreated { get; } = new(0);

    /// <summary>The due time of every created timer, in creation order.</summary>
    public ConcurrentQueue<TimeSpan> DueTimes { get; } = new();

    /// <summary>
    /// Counts the created timers with the given due time.
    /// </summary>
    /// <param name="dueTime">The due time to count.</param>
    /// <returns>How many timers with that due time were created.</returns>
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

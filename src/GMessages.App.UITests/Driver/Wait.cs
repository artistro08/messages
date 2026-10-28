namespace GMessages.App.UITests.Driver;

/// <summary>
/// Polls a condition until it holds or a timeout passes. The UI tests' only way to wait: no fixed sleeps.
/// </summary>
public static class Wait
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Polls <paramref name="condition"/> every 200 ms until it returns true.
    /// </summary>
    /// <param name="condition">The condition; it is checked once more at the deadline.</param>
    /// <param name="timeout">How long to poll.</param>
    /// <param name="failure">Builds the failure message, so it can name what was last seen.</param>
    /// <exception cref="AssertFailedException">The condition never held.</exception>
    public static void Until(Func<bool> condition, TimeSpan timeout, Func<string> failure)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(failure);

        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out after {timeout.TotalSeconds:0} s. {failure()}");
            }

            Thread.Sleep(PollInterval);
        }
    }
}

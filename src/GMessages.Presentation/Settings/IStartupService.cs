namespace GMessages.Presentation.Settings;

/// <summary>
/// The "Start with Windows" switch. Ported from Magic Buds' <c>IStartupTaskService</c>.
/// </summary>
/// <remarks>
/// The App picks the MSIX startup task when packaged and the current user's Run key when not, so the interface
/// covers two real implementations (plus a marker file under UI test).
/// </remarks>
public interface IStartupService
{
    /// <summary>
    /// Reads whether the app currently starts with Windows.
    /// </summary>
    /// <returns>True when it starts with Windows.</returns>
    Task<bool> IsEnabledAsync();

    /// <summary>
    /// Turns starting with Windows on or off.
    /// </summary>
    /// <remarks>
    /// Windows can refuse an enable when the user or a policy turned it off in Settings, so the result is what
    /// actually took effect.
    /// </remarks>
    /// <param name="enabled">The wanted state.</param>
    /// <returns>Whether it ended up enabled.</returns>
    Task<bool> SetEnabledAsync(bool enabled);
}

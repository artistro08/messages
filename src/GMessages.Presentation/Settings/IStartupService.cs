namespace GMessages.Presentation.Settings;

/// <summary>
/// Controls the "Start with Windows" switch. Ported from <c>IStartupTaskService</c> of Magic Buds.
/// </summary>
/// <remarks>
/// The App uses the MSIX startup task in a package, and the Run key of the current user without a package. Thus the
/// interface has 2 real implementations. Under UI test, a marker file replaces the switch.
/// </remarks>
public interface IStartupService
{
    /// <summary>
    /// Reads whether the app starts with Windows.
    /// </summary>
    /// <returns>True when the app starts with Windows.</returns>
    Task<bool> IsEnabledAsync();

    /// <summary>
    /// Sets whether the app starts with Windows.
    /// </summary>
    /// <remarks>
    /// Windows can refuse an enable when the user or a policy disabled the switch in Settings. Thus the result is the
    /// state that took effect.
    /// </remarks>
    /// <param name="enabled">The requested state.</param>
    /// <returns>True when the start with Windows is on after the call.</returns>
    Task<bool> SetEnabledAsync(bool enabled);
}

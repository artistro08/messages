using GMessages.Presentation;
using GMessages.Presentation.Settings;
using Microsoft.Win32;

namespace GMessages.App.Startup;

/// <summary>
/// Implements <see cref="IStartupService"/> over the Run key of the current user. The unpackaged install uses it,
/// because it has no MSIX startup task.
/// </summary>
/// <remarks>
/// Ported from the <c>RegistryStartupService</c> of Magic Buds. At sign-in, Windows starts each entry under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. The name of the value is
/// <see cref="AppInfo.DisplayName"/>, and the value holds the quoted path of this exe.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys"/>
internal sealed class RegistryStartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _command;

    /// <summary>
    /// Creates the service for an exe.
    /// </summary>
    /// <param name="executablePath">The full path of this exe (<see cref="Environment.ProcessPath"/>).</param>
    public RegistryStartupService(string executablePath)
    {
        _command = $"\"{executablePath}\"";
    }

    /// <inheritdoc />
    public Task<bool> IsEnabledAsync()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return Task.FromResult(key?.GetValue(AppInfo.DisplayName) is string);
    }

    /// <inheritdoc />
    public Task<bool> SetEnabledAsync(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(AppInfo.DisplayName, _command);
        }
        else
        {
            key.DeleteValue(AppInfo.DisplayName, throwOnMissingValue: false);
        }

        return Task.FromResult(enabled);
    }
}

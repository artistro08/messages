using GMessages.Presentation;
using GMessages.Presentation.Settings;
using Microsoft.Win32;

namespace GMessages.App.Startup;

/// <summary>
/// <see cref="IStartupService"/> over the current user's Run key. Used by the unpackaged install, which has no
/// MSIX startup task.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>RegistryStartupService</c>. Windows starts everything listed under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> at sign-in. The value is named
/// <see cref="AppInfo.DisplayName"/> and holds the quoted path of this exe.
/// https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys
/// </remarks>
internal sealed class RegistryStartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _command;

    /// <summary>
    /// Creates the service for an exe.
    /// </summary>
    /// <param name="executablePath">Full path to this exe (<see cref="Environment.ProcessPath"/>).</param>
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

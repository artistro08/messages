using GMessages.Presentation;
using GMessages.Presentation.Settings;
using StartupTask      = Windows.ApplicationModel.StartupTask;
using StartupTaskState = Windows.ApplicationModel.StartupTaskState;

namespace GMessages.App.Startup;

/// <summary>
/// <see cref="IStartupService"/> over the MSIX startup task. Used by the packaged install.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>StartupTaskService</c>. The task ID is <see cref="AppInfo.DisplayName"/>, which the
/// manifest's <c>uap5:StartupTask TaskId</c> must match.
/// https://learn.microsoft.com/uwp/api/windows.applicationmodel.startuptask
/// </remarks>
internal sealed class StartupTaskService : IStartupService
{
    // TODO: Task 11 declares the uap5:StartupTask with this ID in Package.appxmanifest; until then GetAsync throws and the switch stays off

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync()
    {
        var task = await StartupTask.GetAsync(AppInfo.DisplayName);
        return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    /// <inheritdoc />
    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        var task = await StartupTask.GetAsync(AppInfo.DisplayName);
        if (!enabled)
        {
            task.Disable();
            return false;
        }

        var state = await task.RequestEnableAsync();
        return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }
}

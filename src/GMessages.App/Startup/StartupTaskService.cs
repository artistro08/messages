using GMessages.Presentation;
using GMessages.Presentation.Settings;
using StartupTask      = Windows.ApplicationModel.StartupTask;
using StartupTaskState = Windows.ApplicationModel.StartupTaskState;

namespace GMessages.App.Startup;

/// <summary>
/// Implements <see cref="IStartupService"/> over the MSIX startup task. The packaged install uses it.
/// </summary>
/// <remarks>
/// Ported from the <c>StartupTaskService</c> of Magic Buds. The task ID is <see cref="AppInfo.DisplayName"/>. The
/// <c>uap5:StartupTask TaskId</c> of the manifest must match it.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/uwp/api/windows.applicationmodel.startuptask"/>
internal sealed class StartupTaskService : IStartupService
{
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

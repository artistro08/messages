#if EnableTestHooks
using GMessages.Presentation.Settings;

namespace GMessages.App.Startup;

/// <summary>
/// Implements <see cref="IStartupService"/> for UI test runs. A <c>startup-enabled</c> marker file in the test data
/// folder takes the place of the Run key. Thus tests can check the file and never touch the registry.
/// </summary>
internal sealed class MarkerFileStartupService : IStartupService
{
    private readonly string _markerFile;

    /// <summary>
    /// Creates the service for a test data folder.
    /// </summary>
    /// <param name="dataFolder">The <c>--test-data-dir</c> folder.</param>
    public MarkerFileStartupService(string dataFolder)
    {
        _markerFile = Path.Combine(dataFolder, "startup-enabled");
    }

    /// <inheritdoc />
    public Task<bool> IsEnabledAsync() => Task.FromResult(File.Exists(_markerFile));

    /// <inheritdoc />
    public Task<bool> SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_markerFile)!);
            File.WriteAllText(_markerFile, string.Empty);
        }
        else
        {
            File.Delete(_markerFile);
        }

        return Task.FromResult(enabled);
    }
}
#endif

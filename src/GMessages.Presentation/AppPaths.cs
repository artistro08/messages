namespace GMessages.Presentation;

/// <summary>
/// Where the app keeps its files: the session, the settings and the logs.
/// </summary>
/// <remarks>
/// Never points at the owner's dev pairing folder; every run uses its own folder.
/// </remarks>
public sealed class AppPaths
{
    /// <summary>
    /// Wraps a data folder.
    /// </summary>
    /// <param name="dataFolder">Folder that holds every app file.</param>
    public AppPaths(string dataFolder)
    {
        DataFolder = dataFolder;
    }

    /// <summary>
    /// The folder that holds every app file.
    /// </summary>
    public string DataFolder { get; }

    /// <summary>
    /// The saved pairing session.
    /// </summary>
    public string SessionFile => Path.Combine(DataFolder, "session.bin");

    /// <summary>
    /// The settings file (unpackaged installs; packaged installs use the package settings store).
    /// </summary>
    public string SettingsFile => Path.Combine(DataFolder, "settings.json");

    /// <summary>
    /// The folder for rolling log files.
    /// </summary>
    public string LogsFolder => Path.Combine(DataFolder, "Logs");

    /// <summary>
    /// Picks the data folder.
    /// </summary>
    /// <remarks>
    /// <paramref name="testDataDir"/> wins when set. Otherwise a packaged app uses <paramref name="packagedLocalFolder"/>,
    /// which the App passes in from <c>ApplicationData.Current.LocalFolder</c> so this project needs no WinRT. Anything
    /// else uses <c>%LOCALAPPDATA%\{AppInfo.DisplayName}</c>.
    /// </remarks>
    /// <param name="packagedLocalFolder">The package's local folder when running packaged, else null.</param>
    /// <param name="testDataDir">The <c>--test-data-dir</c> value, else null.</param>
    /// <returns>Paths under the chosen folder.</returns>
    public static AppPaths Resolve(string? packagedLocalFolder, string? testDataDir)
    {
        // Test Folder Wins
        if (!string.IsNullOrEmpty(testDataDir))
        {
            return new AppPaths(testDataDir);
        }

        // Packaged
        if (!string.IsNullOrEmpty(packagedLocalFolder))
        {
            return new AppPaths(packagedLocalFolder);
        }

        // Unpackaged
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, AppInfo.DisplayName));
    }
}

namespace GMessages.Presentation;

/// <summary>
/// Gives the paths of the app files: the session, the settings, the logs and the photos of the contacts.
/// </summary>
/// <remarks>
/// The paths never point at the dev pairing folder of the owner. Each run uses its own folder.
/// </remarks>
public sealed class AppPaths
{
    /// <summary>
    /// Wraps a data folder.
    /// </summary>
    /// <param name="dataFolder">The folder that holds every app file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataFolder"/> is null.</exception>
    public AppPaths(string dataFolder)
    {
        ArgumentNullException.ThrowIfNull(dataFolder);

        DataFolder = dataFolder;
    }

    /// <summary>
    /// Gets the folder that holds every app file.
    /// </summary>
    public string DataFolder { get; }

    /// <summary>
    /// Gets the path of the saved pairing session.
    /// </summary>
    public string SessionFile => Path.Combine(DataFolder, "session.bin");

    /// <summary>
    /// Gets the path of the settings file for an unpackaged install. A packaged install uses the settings store of the
    /// package.
    /// </summary>
    public string SettingsFile => Path.Combine(DataFolder, "settings.json");

    /// <summary>
    /// Gets the folder for the rolling log files.
    /// </summary>
    public string LogsFolder => Path.Combine(DataFolder, "Logs");

    /// <summary>
    /// Gets the folder for the photos of the contacts that the notifications use. The folder goes with the session.
    /// </summary>
    public string AvatarsFolder => Path.Combine(DataFolder, "Avatars");

    /// <summary>
    /// Gets the path of the encrypted message database. The database goes with the session.
    /// </summary>
    public string MessagesDatabase => Path.Combine(DataFolder, "messages.db");

    /// <summary>
    /// Picks the data folder.
    /// </summary>
    /// <remarks>
    /// A set <paramref name="testDataDir"/> comes first. Otherwise a packaged app uses
    /// <paramref name="packagedLocalFolder"/>, which is the LocalState folder of the package. The App gives that folder
    /// from <c>ApplicationData.Current.LocalFolder</c>, so this project needs no WinRT. Any other run uses
    /// <c>%LOCALAPPDATA%\{AppInfo.DisplayName}</c>.
    /// </remarks>
    /// <param name="packagedLocalFolder">The local folder of the package for a packaged run, else null.</param>
    /// <param name="testDataDir">The <c>--test-data-dir</c> value, else null.</param>
    /// <returns>The paths under the chosen folder.</returns>
    public static AppPaths Resolve(string? packagedLocalFolder, string? testDataDir)
    {
        if (!string.IsNullOrEmpty(testDataDir))
        {
            return new AppPaths(testDataDir);
        }

        if (!string.IsNullOrEmpty(packagedLocalFolder))
        {
            return new AppPaths(packagedLocalFolder);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, AppInfo.DisplayName));
    }
}

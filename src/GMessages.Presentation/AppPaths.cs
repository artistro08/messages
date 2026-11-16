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
    /// Gets the path of the settings file.
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
    /// A <paramref name="testDataDir"/> value comes first. Any other run uses
    /// <c>%LOCALAPPDATA%\{AppInfo.DisplayName}</c>, with <c>.Debug</c> after it for a Debug build, so a dev build
    /// never shares the pairing of the installed app.
    /// </remarks>
    /// <param name="testDataDir">The <c>--test-data-dir</c> value, else null.</param>
    /// <returns>The paths under the chosen folder.</returns>
    public static AppPaths Resolve(string? testDataDir)
    {
        if (!string.IsNullOrEmpty(testDataDir))
        {
            return new AppPaths(testDataDir);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
#if DEBUG
        return new AppPaths(Path.Combine(local, AppInfo.DisplayName + ".Debug"));
#else
        return new AppPaths(Path.Combine(local, AppInfo.DisplayName));
#endif
    }

}

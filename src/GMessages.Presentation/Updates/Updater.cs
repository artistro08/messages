using System.ComponentModel;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Updates;

/// <summary>
/// The steps of an update check.
/// </summary>
public enum UpdateState
{
    /// <summary>No check has run yet.</summary>
    Idle,

    /// <summary>A check is running.</summary>
    Checking,

    /// <summary>The app is the newest release.</summary>
    UpToDate,

    /// <summary>A newer release has an installer for this PC.</summary>
    Available,

    /// <summary>The installer is downloading.</summary>
    Downloading,

    /// <summary>The installer is downloaded, checked and about to run.</summary>
    Ready,

    /// <summary>The last check or download failed.</summary>
    Failed,
}

/// <summary>
/// Finds, downloads and checks a newer Messages MSI from the GitHub releases of the app.
/// </summary>
/// <remarks>
/// <para>
/// A release is the latest GitHub release (drafts and pre-releases never count). It's newer when its tag, such as
/// <c>v1.0.1</c>, is a higher version than <see cref="AppInfo.Version"/>. Its installer is the asset named
/// <c>{AppInfo.DisplayName}-{version}-{arch}.msi</c>, which scripts/Build-Installer.ps1 makes and the release workflow
/// uploads.
/// </para>
/// <para>
/// A download is kept only when its SHA-256 matches the digest GitHub computed on upload, so a broken or swapped file
/// never runs. A release without a digest is refused. The App runs the checked MSI (see App.Updates.cs).
/// </para>
/// <para>
/// Call the methods on the UI thread: the state changes, and <see cref="PropertyChanged"/>, come back there. The log
/// holds only versions and error type names.
/// </para>
/// </remarks>
public sealed partial class Updater : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// The GitHub API address of the latest release of the app.
    /// </summary>
    public const string LatestReleaseUrl = "https://api.github.com/repos/artistro08/messages/releases/latest";

    // No installer this big is real
    private const long MaxInstallerBytes = 512L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _downloadFolder;
    private readonly string _architecture;
    private readonly ILogger _logger;

    private UpdateState _state;
    private GitHubAsset? _installer;
    private string? _availableVersion;
    private string? _releasePage;

    /// <summary>
    /// Creates the updater.
    /// </summary>
    /// <param name="downloadFolder">Where the installer downloads to.</param>
    /// <param name="architecture">The installer architecture of this PC: <c>x64</c> or <c>arm64</c>.</param>
    /// <param name="logger">The app log.</param>
    /// <param name="handler">The HTTP handler; tests pass a fake. Null uses the system one.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public Updater(string downloadFolder, string architecture, ILogger logger, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(downloadFolder);
        ArgumentNullException.ThrowIfNull(architecture);
        ArgumentNullException.ThrowIfNull(logger);

        _downloadFolder = downloadFolder;
        _architecture   = architecture;
        _logger         = logger;
        _http           = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // GitHub requires a User-Agent; a generic one, with no user details
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.DisplayName}-Updater/{AppInfo.Version}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the step the updater is at.
    /// </summary>
    public UpdateState State
    {
        get => _state;
        private set
        {
            if (value == _state)
            {
                return;
            }

            _state = value;
            Raise();
            Raise(nameof(IsAvailable));
            Raise(nameof(IsBusy));
            Raise(nameof(CanCheck));
            Raise(nameof(ShowsCheck));
            Raise(nameof(StatusText));
        }
    }

    /// <summary>
    /// Gets the version of the newer release, or null.
    /// </summary>
    public string? AvailableVersion => _availableVersion;

    /// <summary>
    /// Gets the page of the newer release, or null.
    /// </summary>
    public string? ReleasePage => _releasePage;

    /// <summary>
    /// Gets whether a newer release waits to install.
    /// </summary>
    public bool IsAvailable => _state == UpdateState.Available;

    /// <summary>
    /// Gets whether a check or a download is running.
    /// </summary>
    public bool IsBusy => _state is UpdateState.Checking or UpdateState.Downloading or UpdateState.Ready;

    /// <summary>
    /// Gets whether Check for updates can run now.
    /// </summary>
    public bool CanCheck => !IsBusy;

    /// <summary>
    /// Gets whether Settings shows Check for updates; it shows Install instead while an update waits.
    /// </summary>
    public bool ShowsCheck => !IsAvailable;

    /// <summary>
    /// Gets the line Settings shows for the current step.
    /// </summary>
    public string StatusText => _state switch
    {
        UpdateState.Checking    => "Checking for updates…",
        UpdateState.UpToDate    => "You're up to date.",
        UpdateState.Available   => $"Version {_availableVersion} is available.",
        UpdateState.Downloading => $"Downloading version {_availableVersion}…",
        UpdateState.Ready       => $"Installing version {_availableVersion}…",
        UpdateState.Failed      => "Couldn't check for updates. Try again later.",
        _                       => $"Version {AppInfo.Version}",
    };

    // =========================================================================
    // CHECK AND DOWNLOAD
    // =========================================================================

    /// <summary>
    /// Asks GitHub for the latest release. A newer one with an installer for this PC sets
    /// <see cref="UpdateState.Available"/>.
    /// </summary>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <returns>True when a newer release is available.</returns>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return false;
        }

        State = UpdateState.Checking;
        try
        {
            GitHubRelease? release = await FetchLatestAsync(cancellationToken);
            GitHubAsset? installer = release is null ? null : PickInstaller(release, AppInfo.Version, AppInfo.DisplayName, _architecture);
            if (release is null || installer is null)
            {
                State = UpdateState.UpToDate;
                return false;
            }

            _installer        = installer;
            _availableVersion = ReleaseVersion(release)!.ToString();
            _releasePage      = release.HtmlUrl;
            Raise(nameof(AvailableVersion));
            Raise(nameof(ReleasePage));
            LogAvailable(_logger, _availableVersion);
            State = UpdateState.Available;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            LogCheckFailed(_logger, ex.GetType().Name);
            State = UpdateState.Failed;
            return false;
        }
    }

    /// <summary>
    /// Downloads the installer of the available release and checks its SHA-256 against the GitHub digest.
    /// </summary>
    /// <param name="cancellationToken">Stops the download.</param>
    /// <returns>The path of the checked MSI, or null when there's none or the download failed.</returns>
    public async Task<string?> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_state != UpdateState.Available || _installer is not { } installer)
        {
            return null;
        }

        State = UpdateState.Downloading;
        try
        {
            string path = await DownloadCheckedAsync(installer, cancellationToken);
            State = UpdateState.Ready;
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            LogDownloadFailed(_logger, _availableVersion ?? "?", ex.GetType().Name);
            State = UpdateState.Available;
            return null;
        }
    }

    /// <summary>
    /// Goes back to <see cref="UpdateState.Available"/> when the App couldn't start the installer.
    /// </summary>
    public void InstallFailed()
    {
        if (_state == UpdateState.Ready)
        {
            State = UpdateState.Available;
        }
    }

    /// <summary>
    /// Picks the installer of a release for this PC, when the release is newer than the running app.
    /// </summary>
    /// <param name="release">The latest release.</param>
    /// <param name="currentVersion">The running version (<see cref="AppInfo.Version"/>).</param>
    /// <param name="appName">The app name that starts the installer file name.</param>
    /// <param name="architecture"><c>x64</c> or <c>arm64</c>.</param>
    /// <returns>The installer asset, or null when the release isn't newer or has no installer for this PC.</returns>
    public static GitHubAsset? PickInstaller(GitHubRelease release, string currentVersion, string appName, string architecture)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (ReleaseVersion(release) is not { } latest || !Version.TryParse(currentVersion, out var current) || Normalize(latest) <= Normalize(current))
        {
            return null;
        }

        string name = $"{appName}-{latest}-{architecture}.msi";
        return release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // The tag without its v, as a version; null for a tag that isn't one
    private static Version? ReleaseVersion(GitHubRelease release) =>
        Version.TryParse(release.TagName.TrimStart('v', 'V'), out var version) ? version : null;

    // 1.0 and 1.0.0 compare equal: missing parts count as 0
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private async Task<GitHubRelease?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);

        // No release published yet
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubRelease, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadCheckedAsync(GitHubAsset installer, CancellationToken cancellationToken)
    {
        // The checksum GitHub computed on upload
        if (installer.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release has no SHA-256 digest.");
        }

        if (installer.Size is <= 0 or > MaxInstallerBytes)
        {
            throw new InvalidDataException("The installer size is out of range.");
        }

        Directory.CreateDirectory(_downloadFolder);
        string path = Path.Combine(_downloadFolder, Path.GetFileName(installer.Name));
        string part = path + ".part";

        using (var response = await _http.GetAsync(installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = File.Create(part);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        byte[] hash;
        await using (var file = File.OpenRead(part))
        {
            hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(Convert.ToHexString(hash), digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidDataException("The installer doesn't match its digest.");
        }

        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Deletes the downloaded installers of earlier updates. The App calls it at start.
    /// </summary>
    public void DeleteOldDownloads()
    {
        try
        {
            if (Directory.Exists(_downloadFolder))
            {
                Directory.Delete(_downloadFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still in use goes at the next start
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Update {Version} is available")]
    private static partial void LogAvailable(ILogger logger, string version);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Warning, Message = "Update check failed ({ErrorType})")]
    private static partial void LogCheckFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 1503, Level = LogLevel.Warning, Message = "Update {Version} download failed ({ErrorType})")]
    private static partial void LogDownloadFailed(ILogger logger, string version, string errorType);
}

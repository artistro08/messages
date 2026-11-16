using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GMessages.App.Startup;
using GMessages.Presentation;
using GMessages.Presentation.Updates;
using Windows.System;

namespace GMessages.App;

/// <summary>
/// Holds the update side of the app: the GitHub release check, and the run of a downloaded MSI.
/// </summary>
/// <remarks>
/// <para>
/// The app checks a minute after it starts, then every 6 hours. A newer release shows in Settings and as an item in
/// the tray menu, and the person installs it from either. With "Install updates automatically" on, the app downloads
/// it on its own and starts the install once the main window is gone, so it never restarts under the person.
/// </para>
/// <para>
/// The MSI installs into Program Files, so Windows asks for admin permission (UAC) every time, automatic updates
/// included. A hidden PowerShell runs the installer with a progress bar, waits for it, then starts the app again as
/// the person, never as admin. A click brings the window back; an automatic update comes back in the background (the
/// startup argument). A declined prompt also starts the old version again. The app quits as the installer starts, so
/// its files aren't in use. UI test runs never check, so they never reach GitHub.
/// </para>
/// </remarks>
public sealed partial class App
{
    private static readonly TimeSpan FirstUpdateCheck    = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private Updater? _updater;
    private DispatcherQueueTimer? _updateTimer;

    /// <summary>
    /// Makes the updater and starts the timed checks.
    /// </summary>
    private void StartUpdates(DispatcherQueue dispatcher)
    {
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        _updater            = new Updater(Path.Combine(Path.GetTempPath(), $"{AppInfo.DisplayName}-Update"), architecture, _logProvider!.CreateLogger("GMessages.Updates"));
        _updater.DeleteOldDownloads();
        _updater.PropertyChanged += OnUpdaterChanged;

        if (Program.IsTestRun)
        {
            return;
        }

        _updateTimer             = dispatcher.CreateTimer();
        _updateTimer.Interval    = FirstUpdateCheck;
        _updateTimer.IsRepeating = true;
        _updateTimer.Tick       += (_, _) =>
        {
            _updateTimer.Interval = UpdateCheckInterval;
            _ = CheckForUpdatesAsync();
        };
        _updateTimer.Start();
    }

    // A timed check. A found update installs on its own when the person turned that on and the window is gone.
    private async Task CheckForUpdatesAsync()
    {
        if (_updater is not { } updater || _settings is not { } settings)
        {
            return;
        }

        if (!updater.IsAvailable && !await updater.CheckAsync())
        {
            return;
        }

        if (settings.AutoInstallUpdates && _mainWindow is null)
        {
            await InstallUpdateAsync(silent: true);
        }
    }

    /// <summary>
    /// Downloads the available update, starts its installer, then quits.
    /// </summary>
    /// <param name="silent">True for an automatic update: the app comes back in the background.</param>
    private async Task InstallUpdateAsync(bool silent)
    {
        if (_quitting || _updater is not { } updater || await updater.DownloadAsync() is not { } msi)
        {
            return;
        }

        // Install (msiexec asks for admin itself, so this PowerShell stays a normal user), then start the app again
        string relaunch = silent ? $" -ArgumentList '{RegistryStartupService.StartupArgument}'" : "";
        string script   = $"Start-Process msiexec.exe -ArgumentList '/i \"{msi}\" /passive' -Wait; Start-Process '{Environment.ProcessPath}'{relaunch}";
        string encoded  = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        try
        {
            using var installer = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
        }
        catch (Win32Exception)
        {
            updater.InstallFailed();
            return;
        }

        Quit();
    }

    // The tray menu shows the install item only while an update waits.
    private void OnUpdaterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Updater.IsAvailable))
        {
            _trayMenu?.ShowUpdate(_updater?.IsAvailable == true ? _updater.AvailableVersion : null);
        }
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Messaging;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Settings;

/// <summary>
/// The settings window: theme, Start with Windows, notification switches, unpair and the logs folder.
/// </summary>
/// <remarks>
/// <para>
/// Adapted from Magic Buds' <c>SettingsViewModel</c>. Theme and the notification switches write straight to
/// <see cref="AppSettings"/>; the App watches <see cref="AppSettings.PropertyChanged"/> to re-theme every open window
/// and the tray menu live. Start with Windows is owned by the <see cref="IStartupService"/>, read once by
/// <see cref="LoadAsync"/> and written on every flip; the switch then shows what Windows actually allowed.
/// </para>
/// <para>
/// <see cref="UnpairAsync"/> does no confirming itself: the window shows the confirm dialog first. Once unpaired, the
/// controller lands in <see cref="ConnectionState.NotPaired"/> and the App opens the pairing window.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IStartupService _startup;
    private readonly ConnectionController _controller;
    private readonly Action<string> _openFolder;
    private readonly ILogger _logger;

    private bool _startWithWindows;
    private string? _startupMessage;
    private bool _isUnpairing;

    /// <summary>
    /// Creates the view model. Call <see cref="LoadAsync"/> when the window opens.
    /// </summary>
    /// <param name="settings">The stored app settings.</param>
    /// <param name="startup">Owns the Start with Windows state.</param>
    /// <param name="controller">Does the unpair.</param>
    /// <param name="openFolder">Opens a folder in Explorer.</param>
    /// <param name="logsFolder">The rolling log folder (<see cref="AppPaths.LogsFolder"/>).</param>
    /// <param name="logger">Writes startup service failures.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public SettingsViewModel(
        AppSettings settings,
        IStartupService startup,
        ConnectionController controller,
        Action<string> openFolder,
        string logsFolder,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(openFolder);
        ArgumentNullException.ThrowIfNull(logsFolder);
        ArgumentNullException.ThrowIfNull(logger);

        _settings   = settings;
        _startup    = startup;
        _controller = controller;
        _openFolder = openFolder;
        _logger     = logger;
        LogsFolder  = logsFolder;

        // Unpair Shows Or Hides As The Pairing State Changes (Such As Pairing Finishing While Settings Is Open)
        _controller.StateChanged += OnStateChanged;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    // =========================================================================
    // APPEARANCE
    // =========================================================================

    /// <summary>
    /// Gets or sets the app theme.
    /// </summary>
    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (value == _settings.Theme)
            {
                return;
            }

            _settings.Theme = value;
            Raise();
            Raise(nameof(ThemeIndex));
        }
    }

    /// <summary>
    /// Gets or sets the theme dropdown's selection. The items are in <see cref="AppTheme"/> order; any
    /// other index is ignored.
    /// </summary>
    public int ThemeIndex
    {
        get => (int)Theme;
        set
        {
            if (!Enum.IsDefined((AppTheme)value))
            {
                return;
            }

            Theme = (AppTheme)value;
        }
    }

    // =========================================================================
    // NOTIFICATIONS
    // =========================================================================

    /// <summary>
    /// Gets or sets whether new messages raise Windows notifications.
    /// </summary>
    public bool NotificationsEnabled
    {
        get => _settings.NotificationsEnabled;
        set
        {
            if (value == _settings.NotificationsEnabled)
            {
                return;
            }

            _settings.NotificationsEnabled = value;
            Raise();
        }
    }

    /// <summary>
    /// Gets or sets whether a notification still shows for the chat that is open on screen. Off by default.
    /// </summary>
    public bool NotifyForViewedChat
    {
        get => _settings.NotifyForViewedChat;
        set
        {
            if (value == _settings.NotifyForViewedChat)
            {
                return;
            }

            _settings.NotifyForViewedChat = value;
            Raise();
        }
    }

    /// <summary>
    /// Gets the snooze length dropdown's items, in <see cref="AppSettings.SnoozeChoices"/> order.
    /// </summary>
    public IReadOnlyList<string> SnoozeChoices { get; } = [ChatCopy.Minutes5, ChatCopy.Minutes15, ChatCopy.Hour1, ChatCopy.Hours4];

    /// <summary>
    /// Gets or sets the snooze length dropdown's selection: how long a toast's Snooze waits before showing it again.
    /// Any index outside the choices is ignored.
    /// </summary>
    public int SnoozeIndex
    {
        get => AppSettings.SnoozeChoices.ToList().IndexOf(_settings.SnoozeMinutes);
        set
        {
            if (value < 0 || value >= AppSettings.SnoozeChoices.Count || value == SnoozeIndex)
            {
                return;
            }

            _settings.SnoozeMinutes = AppSettings.SnoozeChoices[value];
            Raise();
        }
    }

    // =========================================================================
    // MAIN WINDOW
    // =========================================================================

    /// <summary>
    /// Gets or sets the conversation list's width as the user last dragged it, or null until they do (see
    /// <see cref="AppSettings.ListWidth"/>). Not on the settings page: the main window reads and writes it.
    /// </summary>
    public double? ListWidth
    {
        get => _settings.ListWidth;
        set => _settings.ListWidth = value;
    }

    // =========================================================================
    // START WITH WINDOWS
    // =========================================================================

    /// <summary>
    /// Gets or sets whether the app starts with Windows. Setting it asks the startup service, then shows what took
    /// effect.
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (value == _startWithWindows)
            {
                return;
            }

            _startWithWindows = value;
            Raise();
            _ = ApplyStartWithWindowsAsync(value);
        }
    }

    /// <summary>
    /// Gets why Start with Windows couldn't be turned on, or null. Set when Windows refuses an enable (turned off by the
    /// user or a policy in Settings) and cleared by the next flip.
    /// </summary>
    public string? StartupMessage
    {
        get => _startupMessage;
        private set
        {
            if (value == _startupMessage)
            {
                return;
            }

            _startupMessage = value;
            Raise();
            Raise(nameof(HasStartupMessage));
        }
    }

    /// <summary>
    /// Gets whether <see cref="StartupMessage"/> shows (for bindings, which skip their function when an argument is null).
    /// </summary>
    public bool HasStartupMessage => _startupMessage is not null;

    /// <summary>
    /// Reads the Start with Windows state. Called when the window opens.
    /// </summary>
    /// <returns>A task that completes when the switch shows the current state.</returns>
    public async Task LoadAsync()
    {
        try
        {
            _startWithWindows = await _startup.IsEnabledAsync().ConfigureAwait(true);
            Raise(nameof(StartWithWindows));
        }
#pragma warning disable CA1031 // The Switch Just Stays Off; The Failure Is Logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStartupReadFailed(_logger, ex);
        }
    }

    // Windows Can Refuse An Enable, So The Switch Follows The Result And A Refusal Says Why
    private async Task ApplyStartWithWindowsAsync(bool enabled)
    {
        StartupMessage = null;

        bool actual;
        try
        {
            actual = await _startup.SetEnabledAsync(enabled).ConfigureAwait(true);
            if (enabled && !actual)
            {
                StartupMessage = ChatCopy.StartupRefused;
            }
        }
#pragma warning disable CA1031 // A Failed Change Puts The Switch Back; The Failure Is Logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStartupChangeFailed(_logger, ex);
            actual = !enabled;
        }

        if (actual != _startWithWindows)
        {
            _startWithWindows = actual;
            Raise(nameof(StartWithWindows));
        }
    }

    // =========================================================================
    // UNPAIR AND LOGS
    // =========================================================================

    /// <summary>
    /// Gets the rolling log folder.
    /// </summary>
    public string LogsFolder { get; }

    /// <summary>
    /// Gets whether Unpair is offered: hidden once there is nothing to unpair (<see cref="ConnectionState.NotPaired"/>).
    /// </summary>
    /// <remarks>Read from the controller each time; raised on every connection state change and after an unpair.</remarks>
    public bool CanUnpair => _controller.State != ConnectionState.NotPaired;

    /// <summary>
    /// Gets whether an unpair is running; the button is disabled and shows progress meanwhile.
    /// </summary>
    public bool IsUnpairing
    {
        get => _isUnpairing;
        private set
        {
            _isUnpairing = value;
            Raise();
        }
    }

    /// <summary>
    /// Unpairs this PC. The window confirms with the user before calling this.
    /// </summary>
    /// <remarks>
    /// <see cref="IsUnpairing"/> is true while it runs. The controller ends in <see cref="ConnectionState.NotPaired"/>
    /// even when Google refuses the request, so that error is only logged.
    /// </remarks>
    /// <returns>A task that completes when the saved session is gone.</returns>
    public async Task UnpairAsync()
    {
        IsUnpairing = true;
        try
        {
            await _controller.UnpairAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Unpaired Locally Regardless; The Server Error Is Logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnpairFailed(_logger, ex);
        }
        finally
        {
            IsUnpairing = false;
            Raise(nameof(CanUnpair));
        }
    }

    /// <summary>
    /// Opens <see cref="LogsFolder"/> in Explorer.
    /// </summary>
    public void OpenLogsFolder() => _openFolder(LogsFolder);

    /// <summary>
    /// Stops following the connection state. The controller outlives every window, so call it when the window closes.
    /// </summary>
    public void Dispose() => _controller.StateChanged -= OnStateChanged;

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Raised On The UI Thread (The Controller Posts Its Events There)
    private void OnStateChanged(object? sender, EventArgs e) => Raise(nameof(CanUnpair));

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Start with Windows state could not be read")]
    private static partial void LogStartupReadFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Start with Windows could not be changed")]
    private static partial void LogStartupChangeFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unpair request failed; unpaired locally")]
    private static partial void LogUnpairFailed(ILogger logger, Exception ex);
}

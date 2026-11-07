using System.ComponentModel;
using System.Runtime.CompilerServices;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Messaging;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Settings;

/// <summary>
/// Holds the state of the settings window: the theme, Start with Windows, the notification switches, unpair and the
/// logs folder.
/// </summary>
/// <remarks>
/// <para>
/// Adapted from <c>SettingsViewModel</c> of Magic Buds. The theme and the notification switches write directly to
/// <see cref="AppSettings"/>. The App watches <see cref="AppSettings.PropertyChanged"/> and changes the theme of every
/// open window and the tray menu at once. The <see cref="IStartupService"/> owns Start with Windows.
/// <see cref="LoadAsync"/> reads that state once, and every flip writes it. The switch then shows the state that
/// Windows allowed.
/// </para>
/// <para>
/// <see cref="UnpairAsync"/> asks for no confirmation. The window shows the confirm dialog first. After the unpair,
/// the controller is in <see cref="ConnectionState.NotPaired"/>, and the App opens the pairing window.
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
    private List<(string Id, string Name)> _microphones = [];

    /// <summary>
    /// Creates the view model. Call <see cref="LoadAsync"/> when the window opens.
    /// </summary>
    /// <param name="settings">The stored app settings.</param>
    /// <param name="startup">Owns the Start with Windows state.</param>
    /// <param name="controller">Runs the unpair.</param>
    /// <param name="openFolder">Opens a folder in Explorer.</param>
    /// <param name="logsFolder">The rolling log folder (<see cref="AppPaths.LogsFolder"/>).</param>
    /// <param name="logger">Logs the failures of the startup service.</param>
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

        // Unpair follows the connection state. The state can change while Settings is open, as at the end of a pairing.
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
    /// Gets or sets the selection of the theme dropdown. The items are in <see cref="AppTheme"/> order. The setter
    /// ignores any other index.
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

    /// <summary>
    /// Gets or sets whether the inbox shows pinned conversations as a grid above the list. The default is off.
    /// </summary>
    public bool ShowPinnedGrid
    {
        get => _settings.ShowPinnedGrid;
        set
        {
            if (value == _settings.ShowPinnedGrid)
            {
                return;
            }

            _settings.ShowPinnedGrid = value;
            Raise();
        }
    }

    /// <summary>
    /// Gets or sets whether the app shows its icon in the system tray. The default is on.
    /// </summary>
    public bool ShowTrayIcon
    {
        get => _settings.ShowTrayIcon;
        set
        {
            if (value == _settings.ShowTrayIcon)
            {
                return;
            }

            _settings.ShowTrayIcon = value;
            Raise();
        }
    }

    /// <summary>
    /// Gets or sets whether the app continues to run after its window closes. The default is on.
    /// </summary>
    public bool RunInBackground
    {
        get => _settings.RunInBackground;
        set
        {
            if (value == _settings.RunInBackground)
            {
                return;
            }

            _settings.RunInBackground = value;
            Raise();
        }
    }

    /// <summary>
    /// Gets or sets whether the app copies one-time passcodes from new messages to the clipboard. The default is off.
    /// </summary>
    public bool CopyCodes
    {
        get => _settings.CopyCodes;
        set
        {
            if (value == _settings.CopyCodes)
            {
                return;
            }

            _settings.CopyCodes = value;
            Raise();
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
    /// Gets or sets whether a notification also shows for the chat on screen. The default is off.
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

    // =========================================================================
    // LINK PREVIEWS
    // =========================================================================

    /// <summary>
    /// Gets or sets whether links in received messages get preview cards. The default is on. With the setting off, this
    /// PC does not fetch the pages that senders chose.
    /// </summary>
    public bool ShowReceivedLinkPreviews
    {
        get => _settings.ShowReceivedLinkPreviews;
        set
        {
            if (value == _settings.ShowReceivedLinkPreviews)
            {
                return;
            }

            _settings.ShowReceivedLinkPreviews = value;
            Raise();
        }
    }

    /// <summary>
    /// Gets the items of the snooze length dropdown, in the order of <see cref="AppSettings.SnoozeChoices"/>.
    /// </summary>
    public IReadOnlyList<string> SnoozeChoices { get; } = [ChatCopy.Minutes5, ChatCopy.Minutes15, ChatCopy.Hour1, ChatCopy.Hours4];

    /// <summary>
    /// Gets or sets the selection of the snooze length dropdown. The length is the time that Snooze waits before it
    /// shows a toast again. The setter ignores any index outside the choices.
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
    // MICROPHONE
    // =========================================================================

    /// <summary>
    /// Gets the items of the microphone dropdown. "Windows default" comes first, then the name of each microphone from
    /// <see cref="SetMicrophones"/>.
    /// </summary>
    public IReadOnlyList<string> MicrophoneChoices => [ChatCopy.WindowsDefault, .. _microphones.Select(mic => mic.Name)];

    /// <summary>
    /// Gets or sets the selection of the microphone dropdown. Index 0 is the default microphone of Windows. Index 1 and
    /// higher are the microphones from <see cref="SetMicrophones"/>.
    /// </summary>
    /// <remarks>
    /// A saved microphone that is not connected shows 0 but stays saved. Thus the app selects that microphone again
    /// when the microphone returns. A set of the index on screen does nothing, so a 0 that the dropdown writes back
    /// keeps the saved choice. The setter ignores any index outside the choices.
    /// </remarks>
    public int MicrophoneIndex
    {
        get => _microphones.FindIndex(mic => mic.Id == _settings.MicrophoneId) + 1;
        set
        {
            if (value < 0 || value > _microphones.Count || value == MicrophoneIndex)
            {
                return;
            }

            _settings.MicrophoneId = value == 0 ? null : _microphones[value - 1].Id;
            Raise();
        }
    }

    /// <summary>
    /// Sets the microphones that the dropdown offers. The settings page calls this method each time the page opens.
    /// The first call has no microphones, so the box holds only "Windows default". The next call has the active audio
    /// capture devices of Windows, after the page finds them.
    /// </summary>
    /// <param name="microphones">
    /// The Windows device ID and the friendly name of each microphone, in the order to show them.
    /// </param>
    public void SetMicrophones(IReadOnlyList<(string Id, string Name)> microphones)
    {
        ArgumentNullException.ThrowIfNull(microphones);

        _microphones = [.. microphones];
        Raise(nameof(MicrophoneChoices));
        Raise(nameof(MicrophoneIndex));
    }

    // =========================================================================
    // MAIN WINDOW
    // =========================================================================

    /// <summary>
    /// Gets or sets the last width of the conversation list that the user dragged, or null before the first drag. The
    /// settings page does not show this value. The main window reads and writes the value. See
    /// <see cref="AppSettings.ListWidth"/>.
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
    /// Gets or sets whether the app starts with Windows. A set asks the startup service, then shows the state that took
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
    /// Gets the reason that Start with Windows stays off, or null. The value is set when Windows refuses an enable,
    /// because the user or a policy disabled the switch in Settings. The next flip clears the value.
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
    /// Gets whether <see cref="StartupMessage"/> shows. Bindings need this property, because a binding skips its
    /// function for a null argument.
    /// </summary>
    public bool HasStartupMessage => _startupMessage is not null;

    /// <summary>
    /// Reads the Start with Windows state. The window calls this method when the window opens.
    /// </summary>
    /// <returns>A task that completes when the switch shows the current state.</returns>
    public async Task LoadAsync()
    {
        try
        {
            _startWithWindows = await _startup.IsEnabledAsync();
            Raise(nameof(StartWithWindows));
        }
#pragma warning disable CA1031 // The switch stays off, and the log keeps the failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStartupReadFailed(_logger, ex);
        }
    }

    // Windows can refuse an enable. Thus the switch follows the result, and a refusal shows the reason.
    private async Task ApplyStartWithWindowsAsync(bool enabled)
    {
        StartupMessage = null;

        bool actual;
        try
        {
            actual = await _startup.SetEnabledAsync(enabled);
            if (enabled && !actual)
            {
                StartupMessage = ChatCopy.StartupRefused;
            }
        }
#pragma warning disable CA1031 // A failed change restores the switch, and the log keeps the failure.
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
    /// Gets whether the window offers Unpair. Unpair hides in the <see cref="ConnectionState.NotPaired"/> state,
    /// because no pairing is left.
    /// </summary>
    /// <remarks>
    /// The property reads the controller each time. The view model raises a change for the property on every
    /// connection state change and after an unpair.
    /// </remarks>
    public bool CanUnpair => _controller.State != ConnectionState.NotPaired;

    /// <summary>
    /// Gets whether an unpair runs. During the unpair, the button is disabled and shows progress.
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
    /// Unpairs this PC. The window asks the user to confirm before the window calls this method.
    /// </summary>
    /// <remarks>
    /// <see cref="IsUnpairing"/> is true while the method runs. The controller ends in
    /// <see cref="ConnectionState.NotPaired"/> also when Google refuses the request. Thus the method only logs that
    /// error.
    /// </remarks>
    /// <returns>A task that completes when the saved session is gone.</returns>
    public async Task UnpairAsync()
    {
        IsUnpairing = true;
        try
        {
            await _controller.UnpairAsync();
        }
#pragma warning disable CA1031 // The local unpair is complete in all cases, and the log keeps the server error.
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
    /// Stops the watch on the connection state. The controller lives longer than every window, so call this method
    /// when the window closes.
    /// </summary>
    public void Dispose() => _controller.StateChanged -= OnStateChanged;

    // =========================================================================
    // HELPERS
    // =========================================================================

    // The controller posts its events to the UI thread, so this handler runs there.
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

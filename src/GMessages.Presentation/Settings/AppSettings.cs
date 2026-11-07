using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Settings;

/// <summary>
/// Gives typed access to every stored app setting. Ported from Magic Buds.
/// </summary>
/// <remarks>
/// The class does not store "Start with Windows", because the startup service of Windows owns that switch. The class
/// raises <see cref="PropertyChanged"/> only when a value changes.
/// </remarks>
public sealed class AppSettings : INotifyPropertyChanged
{
    private const string ThemeKey                    = "Theme";
    private const string NotificationsEnabledKey     = "NotificationsEnabled";
    private const string NotifyForViewedChatKey      = "NotifyForViewedChat";
    private const string LogLevelKey                 = "LogLevel";
    private const string SnoozeMinutesKey            = "SnoozeMinutes";
    private const string ListWidthKey                = "ListWidth";
    private const string WindowBoundsKey             = "WindowBounds";
    private const string WindowMaximizedKey          = "WindowMaximized";
    private const string MicrophoneIdKey             = "MicrophoneId";
    private const string ShowReceivedLinkPreviewsKey = "ShowReceivedLinkPreviews";
    private const string ShowPinnedGridKey           = "ShowPinnedGrid";
    private const string ShowTrayIconKey             = "ShowTrayIcon";
    private const string RunInBackgroundKey          = "RunInBackground";
    private const string CopyCodesKey                = "CopyCodes";

    private const int DefaultSnoozeMinutes = 15;

    private readonly ISettingsStore _store;

    /// <summary>
    /// Wraps a store.
    /// </summary>
    /// <param name="store">The store of the values.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public AppSettings(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets the app theme. The default is <see cref="AppTheme.System"/>, and an unknown saved value also gives
    /// System.
    /// </summary>
    public AppTheme Theme
    {
        get => Enum.TryParse<AppTheme>(_store.GetString(ThemeKey), out var theme) && Enum.IsDefined(theme) ? theme : AppTheme.System;
        set => Set(ThemeKey, value.ToString());
    }

    /// <summary>
    /// Gets or sets whether new messages raise Windows notifications. The default is true.
    /// </summary>
    public bool NotificationsEnabled
    {
        get => GetBool(NotificationsEnabledKey, true);
        set => Set(NotificationsEnabledKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets or sets whether a notification also shows for the chat on screen. The default is false.
    /// </summary>
    public bool NotifyForViewedChat
    {
        get => GetBool(NotifyForViewedChatKey, false);
        set => Set(NotifyForViewedChatKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets or sets whether a link in a received message gets a preview card. For the card, this PC fetches the page
    /// that the sender chose. The default is true. A link in a message from the user always gets a card.
    /// </summary>
    public bool ShowReceivedLinkPreviews
    {
        get => GetBool(ShowReceivedLinkPreviewsKey, true);
        set => Set(ShowReceivedLinkPreviewsKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets or sets whether the inbox shows pinned conversations as a grid of large pictures above the list, as
    /// iMessage does. Otherwise the pinned conversations are rows at the top of the list. The default is false.
    /// </summary>
    public bool ShowPinnedGrid
    {
        get => GetBool(ShowPinnedGridKey, false);
        set => Set(ShowPinnedGridKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets or sets whether the app shows its icon in the system tray. The default is true. With the icon off, the user
    /// opens the app from Start and quits the app from the account menu. This setting is separate from
    /// <see cref="RunInBackground"/>.
    /// </summary>
    public bool ShowTrayIcon
    {
        get => GetBool(ShowTrayIconKey, true);
        set => Set(ShowTrayIconKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets or sets whether the app continues to run after its window closes, so new messages still notify. The
    /// default is true. With the setting off, a close of the window quits the app. A start with Windows then opens the
    /// window. This setting is separate from <see cref="ShowTrayIcon"/>.
    /// </summary>
    public bool RunInBackground
    {
        get => GetBool(RunInBackgroundKey, true);
        set => Set(RunInBackgroundKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets or sets whether the app copies a one-time passcode from a new received message to the clipboard.
    /// <see cref="Messaging.OneTimeCode"/> finds the code. The default is false, because the copy replaces the contents
    /// of the clipboard.
    /// </summary>
    public bool CopyCodes
    {
        get => GetBool(CopyCodesKey, false);
        set => Set(CopyCodesKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets or sets the minimum level that the file log writes. The default is Information, and an unknown saved value
    /// also gives Information.
    /// </summary>
    public LogLevel LogLevel
    {
        get => Enum.TryParse<LogLevel>(_store.GetString(LogLevelKey), out var level) && Enum.IsDefined(level) ? level : LogLevel.Information;
        set => Set(LogLevelKey, value.ToString());
    }

    /// <summary>
    /// Gets or sets the time in minutes that Snooze waits before it shows a toast again. The value is one of
    /// <see cref="SnoozeChoices"/>. The default is 15, and an unknown saved value also gives 15.
    /// </summary>
    public int SnoozeMinutes
    {
        get => int.TryParse(_store.GetString(SnoozeMinutesKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) && SnoozeChoices.Contains(minutes) ? minutes : DefaultSnoozeMinutes;
        set => Set(SnoozeMinutesKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets or sets the width of the conversation list in the main window, in effective pixels. The value is the last
    /// width that the user dragged, or null before the first drag. The window keeps the width in its own limits.
    /// </summary>
    public double? ListWidth
    {
        get => double.TryParse(_store.GetString(ListWidthKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double width) && double.IsFinite(width) ? width : null;
        set => Set(ListWidthKey, value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets the last position and size of the main window on screen, in physical pixels. The value comes from
    /// the last move or resize by the user. Before that, the value is null. The window makes sure that the bounds are
    /// still on a display.
    /// </summary>
    public (int Left, int Top, int Width, int Height)? WindowBounds
    {
        get
        {
            string[] parts = (_store.GetString(WindowBoundsKey) ?? string.Empty).Split(',');
            return parts.Length == 4
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int left)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int top)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
                && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)
                && width > 0 && height > 0
                ? (left, top, width, height)
                : null;
        }
        set => Set(WindowBoundsKey, value is { } b ? string.Create(CultureInfo.InvariantCulture, $"{b.Left},{b.Top},{b.Width},{b.Height}") : string.Empty);
    }

    /// <summary>
    /// Gets or sets whether the main window was maximized at the last close. The <see cref="WindowBounds"/> then give
    /// the position for a restore. The default is false.
    /// </summary>
    public bool WindowMaximized
    {
        get => GetBool(WindowMaximizedKey, false);
        set => Set(WindowMaximizedKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Gets or sets the Windows device ID of the microphone for voice messages, or null for the default microphone of
    /// Windows. The setting stays while that device is unplugged, and the recording then uses the default microphone.
    /// The app uses the device again when the device is connected again. The app never logs the ID.
    /// </summary>
    public string? MicrophoneId
    {
        get => _store.GetString(MicrophoneIdKey) is { Length: > 0 } id ? id : null;
        set => Set(MicrophoneIdKey, value ?? string.Empty);
    }

    /// <summary>
    /// Gets the snooze lengths in minutes that Settings offers, shortest first.
    /// </summary>
    public static IReadOnlyList<int> SnoozeChoices { get; } = [5, 15, 60, 240];

    private bool GetBool(string key, bool fallback) =>
        bool.TryParse(_store.GetString(key), out bool value) ? value : fallback;

    private void Set(string key, string value, [CallerMemberName] string? property = null)
    {
        if (_store.GetString(key) == value)
        {
            return;
        }

        _store.SetString(key, value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Settings;

/// <summary>
/// Typed access to every stored app setting. Ported from Magic Buds.
/// </summary>
/// <remarks>
/// "Start with Windows" is not stored here: the OS startup service owns it. Raises <see cref="PropertyChanged"/>
/// when a value actually changes.
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

    // Snooze Lengths, In Minutes (The Choices Settings Offers), And The Default
    private const int DefaultSnoozeMinutes = 15;

    private readonly ISettingsStore _store;

    /// <summary>
    /// Wraps a store.
    /// </summary>
    /// <param name="store">Where values are kept.</param>
    public AppSettings(ISettingsStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// App theme. Defaults to <see cref="AppTheme.System"/>; an unknown saved value also gives System.
    /// </summary>
    public AppTheme Theme
    {
        get => Enum.TryParse<AppTheme>(_store.GetString(ThemeKey), out var theme) && Enum.IsDefined(theme) ? theme : AppTheme.System;
        set => Set(ThemeKey, value.ToString());
    }

    /// <summary>
    /// Whether new messages raise Windows notifications. Defaults to true.
    /// </summary>
    public bool NotificationsEnabled
    {
        get => GetBool(NotificationsEnabledKey, true);
        set => Set(NotificationsEnabledKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Whether a notification still shows for the chat that is open on screen. Defaults to false.
    /// </summary>
    public bool NotifyForViewedChat
    {
        get => GetBool(NotifyForViewedChatKey, false);
        set => Set(NotifyForViewedChatKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Whether a link in a received message gets a preview card, which means this PC fetches the page the sender chose.
    /// Defaults to true; links in the user's own messages always get one.
    /// </summary>
    public bool ShowReceivedLinkPreviews
    {
        get => GetBool(ShowReceivedLinkPreviewsKey, true);
        set => Set(ShowReceivedLinkPreviewsKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Whether the inbox shows pinned conversations as a grid of large pictures above the list, like iMessage, instead
    /// of as rows at its top. Defaults to false.
    /// </summary>
    public bool ShowPinnedGrid
    {
        get => GetBool(ShowPinnedGridKey, false);
        set => Set(ShowPinnedGridKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Whether the app shows its icon in the system tray. Defaults to true; off, it opens from Start and quits from the
    /// account menu. Separate from <see cref="RunInBackground"/>.
    /// </summary>
    public bool ShowTrayIcon
    {
        get => GetBool(ShowTrayIconKey, true);
        set => Set(ShowTrayIconKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Whether the app keeps running after its window closes, so new messages still notify. Defaults to true; off,
    /// closing the window quits, and starting with Windows opens the window. Separate from <see cref="ShowTrayIcon"/>.
    /// </summary>
    public bool RunInBackground
    {
        get => GetBool(RunInBackgroundKey, true);
        set => Set(RunInBackgroundKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Minimum level the file log writes. Defaults to Information; an unknown saved value also gives Information.
    /// </summary>
    public LogLevel LogLevel
    {
        get => Enum.TryParse<LogLevel>(_store.GetString(LogLevelKey), out var level) && Enum.IsDefined(level) ? level : LogLevel.Information;
        set => Set(LogLevelKey, value.ToString());
    }

    /// <summary>
    /// How long a toast's Snooze waits before showing it again, in minutes: one of <see cref="SnoozeChoices"/>.
    /// Defaults to 15; an unknown saved value also gives 15.
    /// </summary>
    public int SnoozeMinutes
    {
        get => int.TryParse(_store.GetString(SnoozeMinutesKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) && SnoozeChoices.Contains(minutes) ? minutes : DefaultSnoozeMinutes;
        set => Set(SnoozeMinutesKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The conversation list's width in the main window, in effective pixels, as the user last dragged it; null until
    /// they do. The window keeps it within its own limits.
    /// </summary>
    public double? ListWidth
    {
        get => double.TryParse(_store.GetString(ListWidthKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double width) && double.IsFinite(width) ? width : null;
        set => Set(ListWidthKey, value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <summary>
    /// The main window's last place and size on screen, in physical pixels (left, top, width, height), as the user last
    /// moved or sized it; null until they do. The window checks it still lands on a display.
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
    /// Whether the main window was maximized when last closed (its <see cref="WindowBounds"/> are then where it goes back
    /// to when restored). Defaults to false.
    /// </summary>
    public bool WindowMaximized
    {
        get => GetBool(WindowMaximizedKey, false);
        set => Set(WindowMaximizedKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The Windows device ID of the microphone that records voice messages, or null for Windows' default microphone.
    /// Kept while that device is unplugged (recording falls back to the default), so it's used again once it's back.
    /// Never logged.
    /// </summary>
    public string? MicrophoneId
    {
        get => _store.GetString(MicrophoneIdKey) is { Length: > 0 } id ? id : null;
        set => Set(MicrophoneIdKey, value ?? string.Empty);
    }

    /// <summary>
    /// Gets the snooze lengths Settings offers, in minutes, shortest first.
    /// </summary>
    public static IReadOnlyList<int> SnoozeChoices { get; } = [5, 15, 60, 240];

    // Read A Bool With A Fallback
    private bool GetBool(string key, bool fallback) =>
        bool.TryParse(_store.GetString(key), out var value) ? value : fallback;

    // Write A Value, Raising PropertyChanged Only On A Real Change
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

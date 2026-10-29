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
    private const string ThemeKey                = "Theme";
    private const string NotificationsEnabledKey = "NotificationsEnabled";
    private const string NotifyForViewedChatKey  = "NotifyForViewedChat";
    private const string LogLevelKey             = "LogLevel";
    private const string SnoozeMinutesKey        = "SnoozeMinutes";
    private const string ListWidthKey            = "ListWidth";

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

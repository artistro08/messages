using GMessages.Presentation.Settings;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace GMessages.App;

/// <summary>
/// Settings in the package's local app data. Needs package identity (the installed MSIX). Ported from Magic Buds.
/// </summary>
public sealed class LocalSettingsStore : ISettingsStore
{
    private readonly IPropertySet _values = ApplicationData.Current.LocalSettings.Values;

    /// <inheritdoc />
    public string? GetString(string key) => _values.TryGetValue(key, out var value) ? value as string : null;

    /// <inheritdoc />
    public void SetString(string key, string? value)
    {
        // Skip Unchanged Values
        if (GetString(key) == value)
        {
            return;
        }

        // Remove On Null
        if (value is null)
        {
            _values.Remove(key);
            return;
        }

        _values[key] = value;
    }
}

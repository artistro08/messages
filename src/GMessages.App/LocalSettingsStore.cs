using GMessages.Presentation.Settings;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace GMessages.App;

/// <summary>
/// Keeps the settings in the local app data of the package. It needs package identity, so only the installed MSIX
/// uses it. Ported from Magic Buds.
/// </summary>
internal sealed class LocalSettingsStore : ISettingsStore
{
    private readonly IPropertySet _values = ApplicationData.Current.LocalSettings.Values;

    /// <inheritdoc />
    public string? GetString(string key) => _values.TryGetValue(key, out var value) ? value as string : null;

    /// <inheritdoc />
    public void SetString(string key, string? value)
    {
        if (GetString(key) == value)
        {
            return;
        }

        if (value is null)
        {
            _values.Remove(key);
            return;
        }

        _values[key] = value;
    }
}

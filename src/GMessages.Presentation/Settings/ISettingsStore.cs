namespace GMessages.Presentation.Settings;

/// <summary>
/// String key/value storage for app settings. Ported from Magic Buds.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// The stored value, or null when the key isn't set.
    /// </summary>
    /// <param name="key">Setting name.</param>
    string? GetString(string key);

    /// <summary>
    /// Stores the value, or removes the key when <paramref name="value"/> is null.
    /// </summary>
    /// <param name="key">Setting name.</param>
    /// <param name="value">New value, or null to remove.</param>
    void SetString(string key, string? value);
}

/// <summary>
/// Settings kept in memory. Used by tests.
/// </summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = [];

    /// <inheritdoc />
    public string? GetString(string key) => _values.GetValueOrDefault(key);

    /// <inheritdoc />
    public void SetString(string key, string? value)
    {
        // Remove On Null
        if (value is null)
        {
            _values.Remove(key);
            return;
        }

        _values[key] = value;
    }
}

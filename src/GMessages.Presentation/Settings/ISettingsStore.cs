namespace GMessages.Presentation.Settings;

/// <summary>
/// Keeps the app settings as string keys and string values. Ported from Magic Buds.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Gets the stored value, or null when the key has no value.
    /// </summary>
    /// <param name="key">The setting name.</param>
    string? GetString(string key);

    /// <summary>
    /// Stores the value, or removes the key when <paramref name="value"/> is null.
    /// </summary>
    /// <param name="key">The setting name.</param>
    /// <param name="value">The new value, or null to remove the key.</param>
    void SetString(string key, string? value);
}

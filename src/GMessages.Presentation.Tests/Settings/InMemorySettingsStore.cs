using GMessages.Presentation.Settings;

namespace GMessages.Presentation.Tests.Settings;

/// <summary>
/// Settings kept in memory. Used by tests.
/// </summary>
internal sealed class InMemorySettingsStore : ISettingsStore
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

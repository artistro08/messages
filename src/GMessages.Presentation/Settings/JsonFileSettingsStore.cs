using System.Text.Json;

namespace GMessages.Presentation.Settings;

/// <summary>
/// Settings in a JSON file. Used by the unpackaged install, which has no package identity.
/// </summary>
/// <remarks>
/// Ported from Magic Buds. Written in full on every change, through a temporary file so a crash mid-write can't
/// leave half a file. A missing or unreadable file starts empty.
/// </remarks>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values;
    private readonly Lock _gate = new();

    /// <summary>
    /// Opens the store, loading the file if it exists.
    /// </summary>
    /// <param name="path">Settings file, e.g. <c>%LOCALAPPDATA%\Messages\settings.json</c>.</param>
    public JsonFileSettingsStore(string path)
    {
        _path   = path;
        _values = Load(path);
    }

    /// <inheritdoc />
    public string? GetString(string key)
    {
        lock (_gate)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    /// <inheritdoc />
    public void SetString(string key, string? value)
    {
        lock (_gate)
        {
            // Skip Unchanged Values
            if (_values.GetValueOrDefault(key) == value)
            {
                return;
            }

            // Update Memory
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            // Save Through A Temporary File
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_values, SettingsJsonContext.Default.DictionaryStringString));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File locked (antivirus) or disk full: keep the value in memory, the next change saves it
            }
        }
    }

    // Load: Missing Or Corrupt Files Start Empty
    private static Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.DictionaryStringString) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

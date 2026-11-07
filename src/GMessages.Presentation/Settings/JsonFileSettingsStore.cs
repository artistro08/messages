using System.Text.Json;

namespace GMessages.Presentation.Settings;

/// <summary>
/// Keeps the settings in a JSON file. The unpackaged install uses this store, because that install has no package
/// identity.
/// </summary>
/// <remarks>
/// Ported from Magic Buds. The store writes the full file on every change, through a temporary file. Thus a crash
/// during a write cannot leave half a file. A missing or unreadable file gives an empty store.
/// </remarks>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values;
    private readonly Lock _gate = new();

    /// <summary>
    /// Opens the store and loads the file when the file exists.
    /// </summary>
    /// <param name="path">The settings file, such as <c>%LOCALAPPDATA%\Messages\settings.json</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public JsonFileSettingsStore(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

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
            if (_values.GetValueOrDefault(key) == value)
            {
                return;
            }

            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_values, SettingsJsonContext.Default.DictionaryStringString));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An antivirus can lock the file, or the disk can be full. The value stays in memory for the next save.
            }
        }
    }

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

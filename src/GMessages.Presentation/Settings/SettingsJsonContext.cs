using System.Text.Json.Serialization;

namespace GMessages.Presentation.Settings;

/// <summary>
/// Gives the source-generated JSON metadata for the settings file, so Native AOT needs no reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

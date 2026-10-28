using System.Text.Json.Serialization;

namespace GMessages.Presentation.Settings;

/// <summary>
/// Source-generated JSON metadata for the settings file, so no reflection is needed under Native AOT.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

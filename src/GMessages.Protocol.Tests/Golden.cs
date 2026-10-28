using System.Text.Json;

namespace GMessages.Protocol.Tests;

/// <summary>
/// Loads golden vectors exported from the Go LibGM by <c>tools/golden/Export-Golden.ps1</c>.
/// </summary>
/// <remarks>
/// The JSON files are checked in and copied to the test output folder, so running the tests never needs Go.
/// </remarks>
internal static class Golden
{
    /// <summary>
    /// Reads <c>Golden/&lt;area&gt;.json</c> from the test output folder.
    /// </summary>
    /// <remarks>
    /// Each area matches one export function in <c>golden_export_test.go</c>.
    /// </remarks>
    /// <param name="area">The file name without extension, for example <c>proto</c>.</param>
    /// <returns>The root element of the parsed JSON document.</returns>
    /// <exception cref="FileNotFoundException">The golden file was not copied to the output folder.</exception>
    public static JsonElement Load(string area)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", area + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Decodes a lowercase hex JSON string into bytes.
    /// </summary>
    /// <remarks>
    /// Golden files store every byte array as hex so the files stay readable and diffable.
    /// </remarks>
    /// <param name="element">A JSON string element holding hex.</param>
    /// <returns>The decoded bytes.</returns>
    public static byte[] Hex(JsonElement element) => Convert.FromHexString(element.GetString()!);
}

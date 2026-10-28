namespace GMessages.Presentation.Tests;

/// <summary>
/// Checks the MSIX manifest the build generates from the <c>$AppDisplayName$</c> template.
/// </summary>
/// <remarks>
/// <c>GMessages.App.csproj</c> writes <c>Package.generated.appxmanifest</c> into its <c>obj</c> folder on every build,
/// so this needs the app built first (any configuration). Without it the test is inconclusive rather than failing.
/// </remarks>
[TestClass]
public sealed class PackagingTests
{
    private const string GeneratedManifestName = "Package.generated.appxmanifest";

    [TestMethod]
    [TestCategory("RequiresBuild")]
    public void GeneratedManifest_HasTheDisplayNameAndNoPlaceholder()
    {
        var text = File.ReadAllText(FindGeneratedManifest());

        Assert.IsFalse(text.Contains("$AppDisplayName$", StringComparison.Ordinal), "The generated manifest still holds the $AppDisplayName$ placeholder.");
        StringAssert.Contains(text, "DisplayName=\"Messages\"");
        StringAssert.Contains(text, "<DisplayName>Messages</DisplayName>");
    }

    [TestMethod]
    [TestCategory("RequiresBuild")]
    public void GeneratedManifest_StartupTaskIdMatchesTheDisplayName()
    {
        // StartupTaskService looks the task up by AppInfo.DisplayName
        var text = File.ReadAllText(FindGeneratedManifest());

        StringAssert.Contains(text, $"TaskId=\"{AppInfo.DisplayName}\"");
    }

    // Generated Manifest: the newest copy under src/GMessages.App/obj, whichever configuration and platform built it
    private static string FindGeneratedManifest()
    {
        var obj      = Path.Combine(AppInfoTests.FindRepoRoot(), "src", "GMessages.App", "obj");
        var manifest = Directory.Exists(obj)
            ? Directory.EnumerateFiles(obj, GeneratedManifestName, SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (manifest is null)
        {
            Assert.Inconclusive($"No {GeneratedManifestName} under {obj}. Build src/GMessages.App first (dotnet build, or scripts/Build-Package.ps1).");
        }

        return manifest.FullName;
    }
}

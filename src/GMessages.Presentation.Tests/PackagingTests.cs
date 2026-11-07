namespace GMessages.Presentation.Tests;

/// <summary>
/// Checks the MSIX manifest the build generates from the <c>$AppDisplayName$</c> template.
/// </summary>
/// <remarks>
/// <c>GMessages.App.csproj</c> writes <c>Package.generated.appxmanifest</c> into its <c>obj</c> folder on every build.
/// This test needs the app built first, in any configuration. Without it the test is inconclusive and does not fail.
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
        StringAssert.Contains(text, $"DisplayName=\"{AppInfo.DisplayName}\"");
        StringAssert.Contains(text, $"<DisplayName>{AppInfo.DisplayName}</DisplayName>");
    }

    [TestMethod]
    [TestCategory("RequiresBuild")]
    public void GeneratedManifest_StartupTaskIdMatchesTheDisplayName()
    {
        // StartupTaskService looks up the task by AppInfo.DisplayName.
        var text = File.ReadAllText(FindGeneratedManifest());

        StringAssert.Contains(text, $"TaskId=\"{AppInfo.DisplayName}\"");
    }

    [TestMethod]
    [TestCategory("RequiresBuild")]
    public void GeneratedManifest_IdentityVersionMatchesTheAppVersion()
    {
        // AppInfo.Version, which the log and the settings page use, must agree with the package version.
        var text = File.ReadAllText(FindGeneratedManifest());

        StringAssert.Matches(text, new System.Text.RegularExpressions.Regex($"<Identity[^>]*\\sVersion=\"{System.Text.RegularExpressions.Regex.Escape(AppInfo.Version)}[.\"]"));
    }

    // The generated manifest is the newest copy under src/GMessages.App/obj, from any configuration and platform.
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

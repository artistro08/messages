namespace GMessages.Presentation.Tests;

/// <summary>
/// Checks that the display name comes from the single build property and is not hard-coded anywhere.
/// </summary>
[TestClass]
public sealed class AppInfoTests
{
    private static readonly string[] ScannedProjects = ["GMessages.App", "GMessages.Presentation"];
    private static readonly string[] ScannedPatterns = ["*.cs", "*.xaml", "*.appxmanifest"];

    [TestMethod]
    public void DisplayName_ComesFromTheSingleBuildProperty() => Assert.AreEqual("Messages", AppInfo.DisplayName);

    [TestMethod]
    public void Version_IsNotEmpty() => Assert.IsFalse(string.IsNullOrWhiteSpace(AppInfo.Version));

    [TestMethod]
    public void NoSourceFileHardCodesTheDisplayName()
    {
        // Scans the app and presentation sources for the name quoted or as element text; Directory.Build.props isn't scanned
        var src = Path.Combine(FindRepoRoot(), "src");
        var self = Path.GetFullPath(Path.Combine(src, "GMessages.Presentation.Tests", "AppInfoTests.cs"));

        // AppInfo.cs Holds The Window Title, "Messages", Which Is What The App Is For, Not Its Name (They Match Today)
        var info = Path.GetFullPath(Path.Combine(src, "GMessages.Presentation", "AppInfo.cs"));
        var offenders = new List<string>();

        foreach (var project in ScannedProjects)
        {
            var files = ScannedPatterns.SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(src, project), pattern, SearchOption.AllDirectories));
            foreach (var file in files)
            {
                // Skip build output, this test and AppInfo.cs
                var relative = Path.GetRelativePath(src, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")
                    || string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFullPath(file), info, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                if (text.Contains($"\"{AppInfo.DisplayName}\"", StringComparison.Ordinal) || text.Contains($">{AppInfo.DisplayName}<", StringComparison.Ordinal))
                {
                    offenders.Add(relative);
                }
            }
        }

        Assert.AreEqual(0, offenders.Count, "Hard-coded display name in: " + string.Join(", ", offenders));
    }

    // Repo Root: the folder above src that holds GMessages.sln
    internal static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GMessages.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("GMessages.sln not found above the test output.");
    }
}

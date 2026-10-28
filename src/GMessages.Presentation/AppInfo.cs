using System.Reflection;

namespace GMessages.Presentation;

/// <summary>
/// App-wide identity values, read from the build so nothing hard-codes them.
/// </summary>
/// <remarks>
/// The display name is the MSBuild property <c>AppDisplayName</c> in <c>src/Directory.Build.props</c>. The build
/// stamps it into every assembly as <see cref="AssemblyMetadataAttribute"/> with the key <c>AppDisplayName</c>.
/// </remarks>
public static class AppInfo
{
    private const string DisplayNameKey = "AppDisplayName";

    /// <summary>
    /// The name people see: tray tooltip, window titles, settings page and startup task.
    /// </summary>
    public static string DisplayName { get; } = typeof(AppInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == DisplayNameKey)?.Value
        ?? throw new InvalidOperationException($"The build did not stamp '{DisplayNameKey}' into the assembly.");

    /// <summary>
    /// The app version: the assembly informational version without build metadata, else the assembly version.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    // Version: informational version minus any "+commit" suffix
    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}

using System.Reflection;

namespace GMessages.Presentation;

/// <summary>
/// Gives the identity values of the app. The build supplies the values, so no code hard-codes them.
/// </summary>
/// <remarks>
/// The display name is the MSBuild property <c>AppDisplayName</c> in <c>src/Directory.Build.props</c>. The build puts
/// that name into every assembly as an <see cref="AssemblyMetadataAttribute"/> with the key <c>AppDisplayName</c>.
/// </remarks>
public static class AppInfo
{
    private const string DisplayNameKey = "AppDisplayName";

    /// <summary>
    /// The title of the main window, which the taskbar button also shows. The owner chose a title that gives the
    /// purpose of the app, not its name.
    /// </summary>
    public const string WindowTitle = "Messages";

    /// <summary>
    /// Gets the name of the app for the tray tooltip, the Start menu, the settings page and the startup task. The
    /// window shows <see cref="WindowTitle"/> instead.
    /// </summary>
    public static string DisplayName { get; } = typeof(AppInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == DisplayNameKey)?.Value
        ?? throw new InvalidOperationException($"The build did not stamp '{DisplayNameKey}' into the assembly.");

    /// <summary>
    /// Gets the app version: the informational version of the assembly without build metadata, else the assembly
    /// version.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        Assembly assembly     = typeof(AppInfo).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}

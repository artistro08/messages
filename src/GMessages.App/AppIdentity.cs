namespace GMessages.App;

/// <summary>
/// Whether the app runs from the MSIX package or as a plain (unpackaged) exe.
/// </summary>
/// <remarks>
/// Ported from Magic Buds' <c>AppIdentity.cs</c>; the data folder, version and classic app ID parts were
/// dropped (<c>AppPaths</c> and <c>AppInfo</c> cover the first two, notifications come later).
/// https://learn.microsoft.com/windows/apps/desktop/modernize/package-identity-overview
/// </remarks>
internal static class AppIdentity
{
    /// <summary>
    /// True when running from the installed MSIX.
    /// </summary>
    public static bool IsPackaged { get; } = HasPackageIdentity();

    private static bool HasPackageIdentity()
    {
        var length = 0;
        return NativeMethods.GetCurrentPackageFullName(ref length, null) != NativeMethods.APPMODEL_ERROR_NO_PACKAGE;
    }
}

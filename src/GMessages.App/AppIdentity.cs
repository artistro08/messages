namespace GMessages.App;

/// <summary>
/// Tells whether the app runs from the MSIX package or as a plain exe without a package.
/// </summary>
/// <remarks>
/// Ported from the <c>AppIdentity.cs</c> of Magic Buds, without the data folder, the version and the classic app ID.
/// <c>AppPaths</c> holds the data folder, and <c>AppInfo</c> holds the version.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/apps/desktop/modernize/package-identity-overview"/>
internal static class AppIdentity
{
    /// <summary>
    /// Gets whether the app runs from the installed MSIX.
    /// </summary>
    public static bool IsPackaged { get; } = HasPackageIdentity();

    private static bool HasPackageIdentity()
    {
        var length = 0;
        return NativeMethods.GetCurrentPackageFullName(ref length, null) != NativeMethods.APPMODEL_ERROR_NO_PACKAGE;
    }
}

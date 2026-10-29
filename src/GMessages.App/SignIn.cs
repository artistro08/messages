using GMessages.Presentation.Pairing;
using Microsoft.Extensions.Logging;

namespace GMessages.App;

/// <summary>
/// Google sign-in values for the sign-in helper's WebView2, proven by the sign-in spike, plus its profile folders.
/// </summary>
/// <remarks>
/// See <c>docs/superpowers/notes/sign-in-spike.md</c>. Google blocks WebView2's own user agent ("This browser or app
/// may not be secure"), so a desktop Chrome one is set before navigating. Keep its Chrome version current, because
/// Google may start rejecting old versions. Each sign-in uses a fresh profile folder,
/// <c>%TEMP%\{InstanceKey}-signin-{guid}</c>, so a test copy and a real copy never share names.
/// </remarks>
internal static class SignIn
{
    /// <summary>The desktop Chrome user agent set on the sign-in WebView2.</summary>
    public const string ChromeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

    /// <summary>The browser arguments for the sign-in WebView2: hides client hints and device-bound sessions, and sets the user agent at browser level.</summary>
    public const string BrowserArguments = "--disable-features=UserAgentClientHint,DeviceBoundSessionCredentials --user-agent=\"" + ChromeUserAgent + "\"";

    /// <summary>The Google origin that also holds the account cookies in production.</summary>
    public const string GoogleOrigin = "https://google.com";

    /// <summary>The cookie that means the user is signed in.</summary>
    public const string SignedInCookie = "SAPISID";

    /// <summary>The fresh-session cookie waited for (up to <see cref="SessionCookieWait"/>) once signed in.</summary>
    public const string SessionCookie = "__Secure-1PSIDTS";

    /// <summary>How long to wait for <see cref="SessionCookie"/> after <see cref="SignedInCookie"/> appears.</summary>
    public static readonly TimeSpan SessionCookieWait = TimeSpan.FromSeconds(5);

    /// <summary>The cookies pairing needs; every other cookie is ignored (and refused on the pipe).</summary>
    public static IReadOnlyList<string> WantedNames => SignInPipeMessage.CookieNames;

    // =========================================================================
    // PROFILE FOLDERS
    // =========================================================================

    /// <summary>
    /// Gets a new, unused profile folder path for one sign-in.
    /// </summary>
    /// <returns>The folder path (not yet made).</returns>
    public static string NewProfileFolder() => Path.Combine(Path.GetTempPath(), $"{ProfilePrefix}{Guid.NewGuid():N}");

    /// <summary>
    /// Deletes sign-in profile folders an earlier sign-in left behind (a kill, quit or crash while signing in ends the
    /// helper before its delete finishes), in the background.
    /// </summary>
    /// <remarks>
    /// Called by the app at launch and after it kills a helper, and by the helper before it makes its own folder. The
    /// folders are listed now, so a sign-in started later is never touched.
    /// </remarks>
    /// <param name="log">Writes a warning for a folder that can't be deleted (never its contents).</param>
    public static void DeleteLeftoverProfiles(ILogger log)
    {
        string[] leftovers;
        try
        {
            leftovers = Directory.GetDirectories(Path.GetTempPath(), $"{ProfilePrefix}*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string folder in leftovers)
        {
            _ = Task.Run(() => DeleteProfileAsync(folder, log));
        }
    }

    /// <summary>
    /// Deletes one profile folder (a live Google session), retrying while the browser processes still hold its files.
    /// </summary>
    /// <param name="folder">The profile folder.</param>
    /// <param name="log">Writes a warning when it can't be deleted.</param>
    /// <returns>A task that completes when the folder is gone or the retries ran out.</returns>
    public static async Task DeleteProfileAsync(string folder, ILogger log)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }

        if (Directory.Exists(folder))
        {
            AppLog.ProfileNotDeleted(log, folder);
        }
    }

    // Per Instance Key, So The .Test Copy Under UI Tests And A Real Copy Never Clean Up Each Other's Profiles
    private static string ProfilePrefix => $"{Program.InstanceKey}-signin-";
}

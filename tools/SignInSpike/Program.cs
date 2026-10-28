using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SignInSpike;

/// <summary>
/// Throwaway spike: signs in to Google Messages inside WebView2 and saves the auth cookies.
/// </summary>
/// <remarks>
/// Switches: <c>--chrome-ua</c> spoofs a desktop Chrome user agent, <c>--no-dbsc</c> disables
/// Device Bound Session Credentials. Cookie values are never printed, only names.
/// </remarks>
internal static class Program
{
    private const string ChromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

    private static readonly string[] WantedNames =
        ["SID", "HSID", "SSID", "OSID", "APISID", "SAPISID", "__Secure-1PSIDTS"];

    /// <summary>
    /// Runs the sign-in form, then removes the temporary profile folder.
    /// </summary>
    /// <remarks>
    /// Output goes to <c>%LOCALAPPDATA%\GMessages.Dev\spike-cookies.bin</c> (DPAPI, current user).
    /// </remarks>
    [STAThread]
    private static void Main(string[] args)
    {
        // Parse Switches
        bool use_chrome_ua = args.Contains("--chrome-ua");
        bool no_dbsc       = args.Contains("--no-dbsc");
        Console.WriteLine($"Switches: --chrome-ua={use_chrome_ua}, --no-dbsc={no_dbsc}");

        // Paths
        string root_dir     = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GMessages.Dev");
        string profile_dir  = Path.Combine(root_dir, "SpikeProfile");
        string cookie_file  = Path.Combine(root_dir, "spike-cookies.bin");
        Directory.CreateDirectory(root_dir);

        // Clear Stale State
        File.Delete(cookie_file);
        if (Directory.Exists(profile_dir)) { Directory.Delete(profile_dir, true); }

        // Run Form
        ApplicationConfiguration.Initialize();
        Application.Run(new SpikeForm(profile_dir, cookie_file, use_chrome_ua, no_dbsc));

        // Delete Profile (browser may hold files briefly after dispose)
        for (int attempt = 0; attempt < 10 && Directory.Exists(profile_dir); attempt++)
        {
            try { Directory.Delete(profile_dir, true); }
            catch (IOException) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
        }

        if (Directory.Exists(profile_dir))
        {
            Console.WriteLine($"WARNING: could not delete profile folder, it holds a live Google session: {profile_dir}");
        }

        // Report Result
        if (!File.Exists(cookie_file))
        {
            Console.WriteLine("No cookies captured");
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// Single form that hosts the WebView2 and watches for the signed-in cookies.
    /// </summary>
    /// <remarks>
    /// On every navigation it checks for SAPISID; once found it saves the cookies and closes.
    /// </remarks>
    private sealed class SpikeForm : Form
    {
        private readonly WebView2 web_view = new() { Dock = DockStyle.Fill };
        private readonly string profile_dir;
        private readonly string cookie_file;
        private readonly bool use_chrome_ua;
        private readonly bool no_dbsc;
        private bool done;

        public SpikeForm(string profile_dir, string cookie_file, bool use_chrome_ua, bool no_dbsc)
        {
            this.profile_dir   = profile_dir;
            this.cookie_file   = cookie_file;
            this.use_chrome_ua = use_chrome_ua;
            this.no_dbsc       = no_dbsc;

            Text = "Sign-in Spike";
            Width  = 1000;
            Height = 800;
            Controls.Add(web_view);
            Load += async (_, _) => await StartAsync();
        }

        /// <summary>
        /// Creates the WebView2 environment with the chosen switches and opens the sign-in page.
        /// </summary>
        private async Task StartAsync()
        {
            // Environment Options
            var options = new CoreWebView2EnvironmentOptions();
            if (no_dbsc)
            {
                options.AdditionalBrowserArguments = "--disable-features=DeviceBoundSessionCredentials";
            }

            // Create Environment
            var env = await CoreWebView2Environment.CreateAsync(null, profile_dir, options);
            await web_view.EnsureCoreWebView2Async(env);

            // User Agent
            if (use_chrome_ua)
            {
                web_view.CoreWebView2.Settings.UserAgent = ChromeUa;
            }

            // Navigate
            web_view.NavigationCompleted += async (_, _) => await CheckCookiesAsync();
            web_view.CoreWebView2.Navigate("https://messages.google.com/web/authentication");
        }

        /// <summary>
        /// Looks for SAPISID after each navigation and saves the cookies once it appears.
        /// </summary>
        /// <remarks>
        /// Reads messages.google.com first, then google.com for any wanted names still missing.
        /// </remarks>
        private async Task CheckCookiesAsync()
        {
            // Guard
            if (done) { return; }

            try
            {
                // Collect Cookies
                var jar = new Dictionary<string, string>();
                foreach (string url in new[] { "https://messages.google.com", "https://google.com" })
                {
                    var cookies = await web_view.CoreWebView2.CookieManager.GetCookiesAsync(url);
                    foreach (var cookie in cookies)
                    {
                        if (WantedNames.Contains(cookie.Name)) { jar.TryAdd(cookie.Name, cookie.Value); }
                    }
                }

                // Re-check After Awaits (another navigation may have finished first)
                if (done || !jar.ContainsKey("SAPISID")) { return; }
                done = true;

                // Save Cookies (DPAPI, current user)
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(jar);
                File.WriteAllBytes(cookie_file, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));

                // Print Names Only
                Console.WriteLine("Cookies saved: " + string.Join(" ", jar.Keys));

                // Dispose And Exit
                web_view.Dispose();
                Close();
            }
            catch (Exception ex)
            {
                // A late failure after a successful save must not crash the process
                if (!done) { Console.WriteLine($"Cookie check failed: {ex.GetType().Name}"); }
            }
        }
    }
}

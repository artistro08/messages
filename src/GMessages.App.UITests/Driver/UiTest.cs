using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;

namespace GMessages.App.UITests.Driver;

/// <summary>
/// Base for the UI tests: a fresh <see cref="FakeGoogleServer"/>, a fresh app with its own temp data folder, and the
/// tray driver, all torn down after each test (also a failed one).
/// </summary>
/// <remarks>
/// Also holds the steps several tests share: signing in on the fake page, reading the emoji and pairing. A failed test
/// gets the app's log in its output.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "StopApp, the TestCleanup, disposes them after every test.")]
public abstract class UiTest
{
    /// <summary>The fake sign-in page's button text.</summary>
    protected const string SignInButtonName = "Sign in";

    private FakeGoogleServer? _server;
    private AppDriver? _app;
    private TrayDriver? _tray;

    /// <summary>Gets or sets the MSTest context (set by MSTest).</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Gets the fake Google server the app talks to.</summary>
    protected FakeGoogleServer Server => _server!;

    /// <summary>Gets the running app.</summary>
    protected AppDriver App => _app!;

    /// <summary>Gets the tray icon driver.</summary>
    protected TrayDriver Tray => _tray!;

    /// <summary>Gets the app's <c>session.bin</c> path.</summary>
    protected string SessionFile => Path.Combine(App.DataDir, "session.bin");

    /// <summary>
    /// Starts a fresh fake server and a fresh app.
    /// </summary>
    [TestInitialize]
    public void StartApp()
    {
        _server = new FakeGoogleServer();
        _app    = AppDriver.Start(_server);
        _tray   = new TrayDriver(_app.Automation);
    }

    /// <summary>
    /// Quits the app, deletes its data folder and stops the server. Adds the app log to a failed test's output.
    /// </summary>
    /// <returns>A task that completes when everything is cleaned up.</returns>
    [TestCleanup]
    public async Task StopApp()
    {
        try
        {
            _tray?.CloseOverflow();
            if (_app is not null && TestContext.CurrentTestOutcome != UnitTestOutcome.Passed)
            {
                TestContext.WriteLine(ReadLog());
                TestContext.WriteLine($"Server paths: {string.Join(" ", _server?.RequestedPaths ?? [])}");
                TestContext.WriteLine($"Server errors: {string.Join(" | ", _server?.Errors.Select(e => e.Message) ?? [])}");
            }
        }
        finally
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }

            if (_server is not null)
            {
                await _server.DisposeAsync();
            }
        }
    }

    // =========================================================================
    // SHARED STEPS
    // =========================================================================

    /// <summary>
    /// Waits until the window's XAML title bar (found by AutomationId) shows the expected title.
    /// </summary>
    /// <param name="automationId">The title bar's AutomationId, such as <c>"Main.TitleBar"</c>.</param>
    /// <param name="expectedTitle">The wanted title text.</param>
    protected void AssertTitleBar(string automationId, string expectedTitle) => App.WaitForText(automationId, expectedTitle);

    /// <summary>
    /// Waits for the main window showing pairing (its Cancel button is there) and returns the window.
    /// </summary>
    /// <param name="timeout">How long to wait; <see cref="AppDriver.DefaultTimeout"/> when null.</param>
    /// <returns>The main window.</returns>
    protected Window WaitForPairing(TimeSpan? timeout = null)
    {
        Window window = App.WaitForWindow(AppInfo.DisplayName, timeout ?? AppDriver.DefaultTimeout);
        App.Find("Pairing.Cancel", timeout ?? AppDriver.DefaultTimeout);
        return window;
    }

    /// <summary>
    /// Waits for the main window showing the settings page (its Back button is there) and returns the window.
    /// </summary>
    /// <returns>The main window.</returns>
    protected Window WaitForSettings()
    {
        Window window = App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout);
        App.Find("Settings.Back");
        return window;
    }

    /// <summary>
    /// Waits until no control with the given AutomationId shows (a page was switched away or covered).
    /// </summary>
    /// <param name="automationId">The AutomationId, such as <c>Pairing.Cancel</c>.</param>
    protected void WaitForGone(string automationId) =>
        Wait.Until(() => App.TryFind(automationId) is null, AppDriver.DefaultTimeout, () => $"'{automationId}' still shows.");

    /// <summary>
    /// Closes the main window with its close button and waits until it is gone.
    /// </summary>
    protected void CloseMainWindow()
    {
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).Close();
        App.WaitForNoWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout);
    }

    /// <summary>
    /// Waits for the fake sign-in page inside the pairing page's WebView2 and returns its "Sign in" button.
    /// </summary>
    /// <remarks>WebView2 content is exposed to UI Automation through the Chromium accessibility tree.</remarks>
    /// <returns>The button.</returns>
    protected AutomationElement WaitForSignInButton()
    {
        AutomationElement? button = null;
        Wait.Until(() =>
        {
            AutomationElement? web = App.TryFind("Pairing.SignIn");
            button = web is null ? null : AppDriver.Search(web, e => e.ControlType == ControlType.Button && AppDriver.SafeName(e) == SignInButtonName);
            return button is not null;
        }, AppDriver.DefaultTimeout, () => $"The fake sign-in page's '{SignInButtonName}' button never showed in 'Pairing.SignIn'.");
        return button!;
    }

    /// <summary>
    /// Clicks "Sign in" on the fake page until the browser posts the login.
    /// </summary>
    /// <remarks>
    /// Chromium can expose the button before the page takes input, and then an Invoke is dropped. So the click is
    /// repeated (button found again) until the fake server sees the login POST.
    /// </remarks>
    protected void ClickSignIn()
    {
        const string LoginPath = "/web/authentication/login";
        int before             = LoginPosts();
        Wait.Until(() =>
        {
            try
            {
                WaitForSignInButton().Patterns.Invoke.Pattern.Invoke();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
            {
                // The Page Changed Under The Click; Try Again
            }

            DateTime posted_by = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < posted_by && LoginPosts() == before)
            {
                // Short Settle Wait For The Form Post To Reach The Fake Server
                Thread.Sleep(100);
            }

            return LoginPosts() > before;
        }, AppDriver.DefaultTimeout, () => $"Clicking '{SignInButtonName}' never posted {LoginPath}.");

        int LoginPosts() => Server.RequestedPaths.Count(p => p == LoginPath);
    }

    /// <summary>
    /// Clicks "Sign in" on the fake page, then waits for the phone to show its emoji and the app to show the same one.
    /// </summary>
    /// <returns>The emoji.</returns>
    protected async Task<string> SignInAndWaitForEmojiAsync()
    {
        ClickSignIn();

        // The Fake Phone Picks The Emoji Once The Client Finished Message Arrives
        await Server.Phone.FinishReceived.WaitAsync(TimeSpan.FromSeconds(30));
        string emoji = Server.Phone.ShownEmoji!;
        App.WaitForText("Pairing.Emoji", emoji);
        return emoji;
    }

    /// <summary>
    /// Pairs from the main window's pairing page: sign in, matching emoji, correct tap, the window switches to the
    /// chats, session saved. Then closes the window, so the test starts from the tray alone.
    /// </summary>
    /// <returns>A task that completes once paired.</returns>
    protected async Task PairAsync()
    {
        WaitForPairing();
        await SignInAndWaitForEmojiAsync();
        Server.Phone.Tap(true);
        App.Find("Main.ConversationList");
        WaitForGone("Pairing.Cancel");
        Wait.Until(() => File.Exists(SessionFile), AppDriver.DefaultTimeout, () => $"No session.bin in {App.DataDir}.");
        CloseMainWindow();
    }

    /// <summary>
    /// Pairs, then opens the main window from the tray and waits for its conversation list.
    /// </summary>
    /// <returns>A task that completes once the list shows.</returns>
    protected async Task OpenMainAsync()
    {
        await PairAsync();
        Tray.LeftClick();
        App.Find("Main.ConversationList");
    }

    /// <summary>
    /// Opens the tray menu with a right-click and invokes one of its items.
    /// </summary>
    /// <param name="automationId">The item, such as <c>TrayMenu.Settings</c>.</param>
    protected void InvokeTrayMenu(string automationId)
    {
        Tray.RightClick();
        App.Find(automationId).Patterns.Invoke.Pattern.Invoke();
        Tray.CloseOverflow();
    }

    /// <summary>
    /// Reads the app's log files (the app keeps writing, so they are opened shared).
    /// </summary>
    /// <returns>The log text, or a note when there is none.</returns>
    protected string ReadLog()
    {
        string logs = Path.Combine(App.DataDir, "Logs");
        if (!Directory.Exists(logs))
        {
            return "(no log folder)";
        }

        var text = new System.Text.StringBuilder();
        foreach (string file in Directory.GetFiles(logs).Order(StringComparer.Ordinal))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }
}

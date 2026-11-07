using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using GMessages.App.UITests.FakeGoogle;
using GMessages.Presentation;

namespace GMessages.App.UITests.Driver;

/// <summary>
/// Base for the UI tests. Each test gets a fresh <see cref="FakeGoogleServer"/>, a fresh app with its own temp data
/// folder, and the tray driver. The base class tears them down after each test, also after a failed test.
/// </summary>
/// <remarks>
/// The class also holds the steps that several tests share: sign-in on the fake page, the emoji and pairing.
/// The output of a failed test includes the log of the app.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "StopApp, the TestCleanup, disposes them after every test.")]
public abstract class UiTest
{
    /// <summary>The button text of the fake sign-in page.</summary>
    protected const string SignInButtonName = "Sign in";

    private FakeGoogleServer? _server;
    private AppDriver? _app;
    private TrayDriver? _tray;

    /// <summary>Gets or sets the MSTest context. MSTest sets it.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Gets the fake Google server the app talks to.</summary>
    protected FakeGoogleServer Server => _server!;

    /// <summary>Gets the running app.</summary>
    protected AppDriver App => _app!;

    /// <summary>Gets the tray icon driver.</summary>
    protected TrayDriver Tray => _tray!;

    /// <summary>Gets the path of the <c>session.bin</c> file of the app.</summary>
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
    /// Quits the app, deletes its data folder and stops the server. Adds the app log to the output of a failed test.
    /// </summary>
    /// <returns>A task that completes when the cleanup is done.</returns>
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

    /// <summary>
    /// Waits until the XAML title bar of the window shows the expected title.
    /// The method finds the title bar by AutomationId.
    /// </summary>
    /// <param name="automationId">The AutomationId of the title bar, such as <c>"Main.TitleBar"</c>.</param>
    /// <param name="expectedTitle">The expected title text.</param>
    protected void AssertTitleBar(string automationId, string expectedTitle) => App.WaitForText(automationId, expectedTitle);

    /// <summary>
    /// Waits until the main window shows the pairing page, which has a Cancel button, and returns the window.
    /// </summary>
    /// <param name="timeout">How long to wait. The default is <see cref="AppDriver.DefaultTimeout"/>.</param>
    /// <returns>The main window.</returns>
    protected Window WaitForPairing(TimeSpan? timeout = null)
    {
        Window window = App.WaitForWindow(AppInfo.WindowTitle, timeout ?? AppDriver.DefaultTimeout);
        App.Find("Pairing.Cancel", timeout ?? AppDriver.DefaultTimeout);
        return window;
    }

    /// <summary>
    /// Waits until the main window shows the settings page, which has the Back button <c>Settings.Back</c>, and returns
    /// the window.
    /// </summary>
    /// <returns>The main window.</returns>
    protected Window WaitForSettings()
    {
        Window window = App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
        App.Find("Settings.Back");
        return window;
    }

    /// <summary>
    /// Waits until no control with the given AutomationId shows, because another page replaced or covered it.
    /// </summary>
    /// <param name="automationId">The AutomationId, such as <c>Pairing.Cancel</c>.</param>
    protected void WaitForGone(string automationId) =>
        Wait.Until(() => App.TryFind(automationId) is null, AppDriver.DefaultTimeout, () => $"'{automationId}' still shows.");

    /// <summary>
    /// Waits until the keyboard focus is on a control or inside it. The focus of a list sits on one of its items.
    /// </summary>
    /// <param name="automationId">The AutomationId of the control, such as <c>Main.ConversationList</c>.</param>
    protected void WaitForFocusIn(string automationId)
    {
        string last = "";
        Wait.Until(() => FocusPath(out last).Contains(automationId), AppDriver.DefaultTimeout,
            () => $"Focus wasn't in '{automationId}'; it was in [{last}].");
    }

    private List<string> FocusPath(out string text)
    {
        var ids = new List<string>();
        try
        {
            var walker = App.Automation.TreeWalkerFactory.GetControlViewWalker();
            for (AutomationElement? e = App.Automation.FocusedElement(); e is not null && ids.Count < 50; e = walker.GetParent(e))
            {
                ids.Add(e.Properties.AutomationId.ValueOrDefault ?? "");
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            // The focus moved during the walk. The caller reads again.
        }

        text = string.Join(" < ", ids);
        return ids;
    }

    /// <summary>
    /// Closes the main window with its close button and waits until it is gone.
    /// </summary>
    protected void CloseMainWindow()
    {
        App.WaitForWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout).Close();
        App.WaitForNoWindow(AppInfo.WindowTitle, AppDriver.DefaultTimeout);
    }

    /// <summary>
    /// Waits for the fake sign-in page inside the WebView2 of the pairing page and returns its "Sign in" button.
    /// </summary>
    /// <remarks>UI Automation reaches WebView2 content through the Chromium accessibility tree.</remarks>
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
    /// Chromium can expose the button before the page takes input, and the page then drops an Invoke.
    /// The method therefore repeats the click, with a new search for the button.
    /// It stops when the fake server sees the login POST.
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
                // The page changed during the click. The next poll tries again.
            }

            DateTime postedBy = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < postedBy && LoginPosts() == before)
            {
                // The form post needs a short time to reach the fake server.
                Thread.Sleep(100);
            }

            return LoginPosts() > before;
        }, AppDriver.DefaultTimeout, () => $"Clicking '{SignInButtonName}' never posted {LoginPath}.");

        int LoginPosts() => Server.RequestedPaths.Count(p => p == LoginPath);
    }

    /// <summary>
    /// Clicks "Sign in" on the fake page. Then waits until the phone shows its emoji and the app shows the same emoji.
    /// </summary>
    /// <returns>The emoji.</returns>
    protected async Task<string> SignInAndWaitForEmojiAsync()
    {
        ClickSignIn();

        // The fake phone picks the emoji after the Finish message of the client arrives.
        await Server.Phone.FinishReceived.WaitAsync(TimeSpan.FromSeconds(30));
        string emoji = Server.Phone.ShownEmoji!;
        App.WaitForText("Pairing.Emoji", emoji);
        return emoji;
    }

    /// <summary>
    /// Pairs from the pairing page of the main window: sign in, match the emoji, tap the correct emoji.
    /// The window then switches to the chats and the app saves the session. The method then closes the window,
    /// so the test starts from the tray alone.
    /// </summary>
    /// <returns>A task that completes when pairing is done.</returns>
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
    /// <returns>A task that completes when the list shows.</returns>
    protected async Task OpenMainAsync()
    {
        await PairAsync();
        Tray.LeftClick();
        App.Find("Main.ConversationList");
    }

    /// <summary>
    /// Opens the tray menu with a right-click and invokes one of its items.
    /// </summary>
    /// <param name="automationId">The AutomationId of the item, such as <c>TrayMenu.Settings</c>.</param>
    protected void InvokeTrayMenu(string automationId)
    {
        Tray.RightClick();
        App.Find(automationId).Patterns.Invoke.Pattern.Invoke();
        Tray.CloseOverflow();
    }

    /// <summary>
    /// Reads the log files of the app. The app keeps writing to them, so the method opens them shared.
    /// </summary>
    /// <returns>The log text, or a note when no log exists.</returns>
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

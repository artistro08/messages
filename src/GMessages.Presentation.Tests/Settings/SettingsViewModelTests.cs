using GMessages.Presentation.Connection;
using GMessages.Presentation.Settings;
using GMessages.Presentation.Tests.Connection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Presentation.Tests.Settings;

/// <summary>
/// Checks that <see cref="SettingsViewModel"/> saves every setting, drives the startup service, unpairs through the
/// controller and opens the logs folder.
/// </summary>
[TestClass]
public sealed class SettingsViewModelTests
{
    private const string LogsFolder = @"C:\Data\Logs";

    // =========================================================================
    // RIG
    // =========================================================================

    // Records Every Change; Can Refuse An Enable The Way Windows Does
    private sealed class FakeStartupService : IStartupService
    {
        public bool Enabled { get; set; }

        public bool RefuseEnable { get; set; }

        public List<bool> Requests { get; } = [];

        public Task<bool> IsEnabledAsync() => Task.FromResult(Enabled);

        public Task<bool> SetEnabledAsync(bool enabled)
        {
            Requests.Add(enabled);
            Enabled = enabled && !RefuseEnable;
            return Task.FromResult(Enabled);
        }
    }

    private static SettingsViewModel Create(
        ISettingsStore store,
        IStartupService? startup = null,
        ConnectionController? controller = null,
        Action<string>? openFolder = null) =>
        new(new AppSettings(store), startup ?? new FakeStartupService(), controller ?? IdleController(), openFolder ?? (_ => { }), LogsFolder, NullLogger.Instance);

    // Never Started, So It Touches No Files And Opens No Connections
    private static ConnectionController IdleController() =>
        new(new AppPaths(Path.Combine(Path.GetTempPath(), "settings-test-unused")), Protocol.Http.GmEndpoints.Production, TimeProvider.System, NullLoggerFactory.Instance, a => a());

    // =========================================================================
    // SETTINGS
    // =========================================================================

    /// <summary>
    /// Theme and both notification switches land in the store and survive a reload.
    /// </summary>
    [TestMethod]
    public void PersistsEverySetting()
    {
        var store = new InMemorySettingsStore();
        var vm    = Create(store);

        vm.Theme                = AppTheme.Dark;
        vm.NotificationsEnabled = false;
        vm.NotifyForViewedChat  = true;

        var reloaded = new AppSettings(store);
        Assert.AreEqual(AppTheme.Dark, reloaded.Theme);
        Assert.IsFalse(reloaded.NotificationsEnabled);
        Assert.IsTrue(reloaded.NotifyForViewedChat);
    }

    /// <summary>
    /// The theme dropdown index maps to the theme in enum order, and a bad index is ignored.
    /// </summary>
    [TestMethod]
    public void ThemeIndex_MapsToTheme()
    {
        var vm = Create(new InMemorySettingsStore());

        vm.ThemeIndex = (int)AppTheme.Light;
        Assert.AreEqual(AppTheme.Light, vm.Theme);

        vm.ThemeIndex = -1;
        Assert.AreEqual(AppTheme.Light, vm.Theme);
    }

    /// <summary>
    /// A fresh install leaves notifications on and the viewed-chat notification off.
    /// </summary>
    [TestMethod]
    public void NotifyForViewedChat_DefaultsOff()
    {
        var vm = Create(new InMemorySettingsStore());

        Assert.IsTrue(vm.NotificationsEnabled);
        Assert.IsFalse(vm.NotifyForViewedChat);
    }

    // =========================================================================
    // START WITH WINDOWS
    // =========================================================================

    /// <summary>
    /// Load reads the service; flipping the switch asks the service, both ways.
    /// </summary>
    [TestMethod]
    public async Task StartWithWindows_CallsTheStartupService()
    {
        var startup = new FakeStartupService { Enabled = true };
        var vm      = Create(new InMemorySettingsStore(), startup);

        await vm.LoadAsync();
        Assert.IsTrue(vm.StartWithWindows);

        vm.StartWithWindows = false;
        vm.StartWithWindows = true;

        CollectionAssert.AreEqual(new List<bool> { false, true }, startup.Requests);
        Assert.IsTrue(startup.Enabled);
    }

    /// <summary>
    /// When Windows refuses the enable, the switch goes back off.
    /// </summary>
    [TestMethod]
    public void StartWithWindows_ShowsWhatWindowsAllowed()
    {
        var startup = new FakeStartupService { RefuseEnable = true };
        var vm      = Create(new InMemorySettingsStore(), startup);

        vm.StartWithWindows = true;

        Assert.IsFalse(vm.StartWithWindows);
    }

    // =========================================================================
    // UNPAIR AND LOGS
    // =========================================================================

    /// <summary>
    /// Unpair goes through the controller: the session file is gone and the app is not paired.
    /// </summary>
    [TestMethod]
    public async Task Unpair_CallsTheController()
    {
        await using var rig = new ConnectionControllerTests.Rig();
        rig.SaveSession(TimeSpan.FromDays(1));
        await rig.Controller.StartAsync().WaitAsync(ConnectionControllerTests.HangGuard);
        var vm = Create(new InMemorySettingsStore(), controller: rig.Controller);

        await vm.UnpairAsync().WaitAsync(ConnectionControllerTests.HangGuard);

        Assert.AreEqual(ConnectionState.NotPaired, rig.Controller.State);
        Assert.IsFalse(File.Exists(rig.Paths.SessionFile));
    }

    /// <summary>
    /// Open logs hands the logs folder to the opener.
    /// </summary>
    [TestMethod]
    public void OpenLogsFolder_OpensTheLogsFolder()
    {
        string? opened = null;
        var vm         = Create(new InMemorySettingsStore(), openFolder: folder => opened = folder);

        vm.OpenLogsFolder();

        Assert.AreEqual(LogsFolder, opened);
    }
}

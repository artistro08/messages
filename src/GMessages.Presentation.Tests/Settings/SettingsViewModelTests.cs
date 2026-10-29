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
    /// The snooze length starts at 15 minutes; picking a choice saves its minutes, and an index past the choices is
    /// ignored.
    /// </summary>
    [TestMethod]
    public void SnoozeIndex_DefaultsTo15Minutes_SavesTheChoice()
    {
        var store = new InMemorySettingsStore();
        var vm    = Create(store);

        Assert.AreEqual(1, vm.SnoozeIndex);
        Assert.AreEqual("5 minutes|15 minutes|1 hour|4 hours", string.Join("|", vm.SnoozeChoices));

        vm.SnoozeIndex = 3;
        vm.SnoozeIndex = 9;

        Assert.AreEqual(240, new AppSettings(store).SnoozeMinutes);
        Assert.AreEqual(3, Create(store).SnoozeIndex);
    }

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

    /// <summary>
    /// A refused enable says why with the owner's copy; the next flip clears it.
    /// </summary>
    [TestMethod]
    public void StartWithWindows_Refused_ShowsWhy_NextFlipClears()
    {
        var startup = new FakeStartupService { RefuseEnable = true };
        var vm      = Create(new InMemorySettingsStore(), startup);
        Assert.IsNull(vm.StartupMessage);
        Assert.IsFalse(vm.HasStartupMessage);

        vm.StartWithWindows = true;

        Assert.AreEqual("You'll have to manually enable startup. Turn it on in Settings > Apps > Startup.", vm.StartupMessage);
        Assert.IsTrue(vm.HasStartupMessage);

        startup.RefuseEnable = false;
        vm.StartWithWindows  = true;

        Assert.IsTrue(vm.StartWithWindows);
        Assert.IsNull(vm.StartupMessage);
        Assert.IsFalse(vm.HasStartupMessage);
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
    /// A window opened while not paired shows Unpair once pairing (here a start with a saved session) finishes, and stops
    /// following the controller once disposed.
    /// </summary>
    [TestMethod]
    public async Task CanUnpair_FollowsStateChanges_UntilDisposed()
    {
        await using var rig = new ConnectionControllerTests.Rig();
        var vm              = Create(new InMemorySettingsStore(), controller: rig.Controller);
        var raised          = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.IsFalse(vm.CanUnpair);

        rig.SaveSession(TimeSpan.FromDays(1));
        await rig.Controller.StartAsync().WaitAsync(ConnectionControllerTests.HangGuard);

        Assert.IsTrue(vm.CanUnpair);
        CollectionAssert.Contains(raised, nameof(SettingsViewModel.CanUnpair));

        vm.Dispose();
        raised.Clear();
        await rig.Controller.UnpairAsync().WaitAsync(ConnectionControllerTests.HangGuard);
        Assert.IsEmpty(raised, "nothing raised after dispose");
    }

    /// <summary>
    /// Unpair is offered only while paired, and is busy while it runs.
    /// </summary>
    [TestMethod]
    public async Task Unpair_IsUnpairingWhileRunning_CanUnpairOnlyWhenPaired()
    {
        Assert.IsFalse(Create(new InMemorySettingsStore()).CanUnpair, "nothing to unpair");

        await using var rig = new ConnectionControllerTests.Rig();
        rig.SaveSession(TimeSpan.FromDays(1));
        await rig.Controller.StartAsync().WaitAsync(ConnectionControllerTests.HangGuard);
        var vm = Create(new InMemorySettingsStore(), controller: rig.Controller);
        Assert.IsTrue(vm.CanUnpair);
        Assert.IsFalse(vm.IsUnpairing);

        // Busy Seen From Inside The Call; Both Flags Raised
        var busy_seen = new List<bool>();
        var raised    = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            raised.Add(e.PropertyName);
            busy_seen.Add(vm.IsUnpairing);
        };

        await vm.UnpairAsync().WaitAsync(ConnectionControllerTests.HangGuard);

        Assert.IsTrue(busy_seen.Contains(true), "busy while unpairing");
        Assert.IsFalse(vm.IsUnpairing);
        Assert.IsFalse(vm.CanUnpair);
        CollectionAssert.Contains(raised, nameof(SettingsViewModel.IsUnpairing));
        CollectionAssert.Contains(raised, nameof(SettingsViewModel.CanUnpair));
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

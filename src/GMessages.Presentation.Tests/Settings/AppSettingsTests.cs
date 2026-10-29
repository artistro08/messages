using GMessages.Presentation.Settings;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Tests.Settings;

[TestClass]
public sealed class AppSettingsTests
{
    private string _folder = null!;

    [TestInitialize]
    public void CreateFolder() => _folder = Directory.CreateTempSubdirectory("GMessagesTests").FullName;

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(_folder, true);

    [TestMethod]
    public void UsesDefaultsWhenNothingIsSaved()
    {
        var settings = new AppSettings(new InMemorySettingsStore());

        Assert.AreEqual(AppTheme.System, settings.Theme);
        Assert.IsTrue(settings.NotificationsEnabled);
        Assert.IsFalse(settings.NotifyForViewedChat);
        Assert.AreEqual(LogLevel.Information, settings.LogLevel);
        Assert.AreEqual(15, settings.SnoozeMinutes);
        Assert.IsNull(settings.ListWidth);
    }

    [TestMethod]
    public void SnoozeMinutes_OutsideTheChoices_Gives15()
    {
        var store = new InMemorySettingsStore();
        store.SetString("SnoozeMinutes", "7");

        Assert.AreEqual(15, new AppSettings(store).SnoozeMinutes);
    }

    [TestMethod]
    public void RoundTripsEveryValueThroughTheJsonFile()
    {
        var path = Path.Combine(_folder, "settings.json");
        _ = new AppSettings(new JsonFileSettingsStore(path))
        {
            Theme                = AppTheme.Dark,
            NotificationsEnabled = false,
            NotifyForViewedChat  = true,
            LogLevel             = LogLevel.Debug,
            SnoozeMinutes        = 60,
            ListWidth            = 412.5,
        };

        var reloaded = new AppSettings(new JsonFileSettingsStore(path));

        Assert.AreEqual(AppTheme.Dark, reloaded.Theme);
        Assert.IsFalse(reloaded.NotificationsEnabled);
        Assert.IsTrue(reloaded.NotifyForViewedChat);
        Assert.AreEqual(LogLevel.Debug, reloaded.LogLevel);
        Assert.AreEqual(60, reloaded.SnoozeMinutes);
        Assert.AreEqual(412.5, reloaded.ListWidth);
    }

    [TestMethod]
    public void CorruptFileLoadsDefaultsWithoutThrowing()
    {
        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, "{ not json");

        var settings = new AppSettings(new JsonFileSettingsStore(path));

        Assert.AreEqual(AppTheme.System, settings.Theme);
        Assert.IsTrue(settings.NotificationsEnabled);
        Assert.IsFalse(settings.NotifyForViewedChat);
        Assert.AreEqual(LogLevel.Information, settings.LogLevel);
    }

    [TestMethod]
    public void UnknownSavedValuesFallBackToDefaults()
    {
        var store = new InMemorySettingsStore();
        store.SetString("Theme", "7");
        store.SetString("LogLevel", "Loud");

        var settings = new AppSettings(store);

        Assert.AreEqual(AppTheme.System, settings.Theme);
        Assert.AreEqual(LogLevel.Information, settings.LogLevel);
    }

    [TestMethod]
    public void RaisesPropertyChangedOnlyOnRealChange()
    {
        var settings = new AppSettings(new InMemorySettingsStore());
        var raised   = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.Theme = AppTheme.Light;
        settings.Theme = AppTheme.Light;
        settings.NotifyForViewedChat = true;

        CollectionAssert.AreEqual(new[] { nameof(AppSettings.Theme), nameof(AppSettings.NotifyForViewedChat) }, raised);
    }

    [TestMethod]
    public void JsonFileStoreNullRemovesTheValueAndCreatesItsFolder()
    {
        var path  = Path.Combine(_folder, "nested", "settings.json");
        var store = new JsonFileSettingsStore(path);

        store.SetString("Theme", "Dark");
        Assert.IsTrue(File.Exists(path));

        store.SetString("Theme", null);
        Assert.IsNull(new JsonFileSettingsStore(path).GetString("Theme"));
    }
}

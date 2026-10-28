#pragma warning disable CA1848, CA1873 // Tests call the logger directly on purpose

using GMessages.Presentation.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace GMessages.Presentation.Tests.Logging;

[TestClass]
public sealed class RollingFileLoggerTests
{
    private string _directory = "";

    [TestInitialize]
    public void CreateDirectory() => _directory = Path.Combine(Path.GetTempPath(), "GMessagesTests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void DeleteDirectory()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public void WritesFormattedLine()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 15, 4, 5, 123, TimeSpan.Zero));
        using var provider = new RollingFileLoggerProvider(_directory, new LogLevelSwitch(), time);

        provider.CreateLogger("Tests").Log(LogLevel.Information, "hello {Name}", "world");

        Assert.AreEqual("2026-09-23T15:04:05.123Z [Information] Tests: hello world" + Environment.NewLine, File.ReadAllText(provider.CurrentFilePath));
    }

    [TestMethod]
    public void SkipsDebugUntilSwitchAllowsIt()
    {
        var levelSwitch = new LogLevelSwitch();
        using var provider = new RollingFileLoggerProvider(_directory, levelSwitch, TimeProvider.System);
        var logger = provider.CreateLogger("Tests");

        logger.Log(LogLevel.Debug, "hidden");
        Assert.IsFalse(File.Exists(provider.CurrentFilePath));

        levelSwitch.MinimumLevel = LogLevel.Debug;
        logger.Log(LogLevel.Debug, "shown");
        StringAssert.Contains(File.ReadAllText(provider.CurrentFilePath), "shown");
    }

    [TestMethod]
    public void RollsAndKeepsFiveFiles()
    {
        using var provider = new RollingFileLoggerProvider(_directory, new LogLevelSwitch(), TimeProvider.System, maxFileBytes: 200, maxFiles: 5);
        var logger = provider.CreateLogger("Tests");

        for (var i = 0; i < 40; i++)
        {
            logger.Log(LogLevel.Information, "line {Number} padded to take up room in the file", i);
        }

        var name  = AppInfo.DisplayName;
        var files = Directory.GetFiles(_directory).Select(Path.GetFileName).Order().ToList();
        var expected = new[] { $"{name}.1.log", $"{name}.2.log", $"{name}.3.log", $"{name}.4.log", $"{name}.log" }.Order().ToList();
        CollectionAssert.AreEqual(expected, files);
        Assert.IsTrue(Directory.GetFiles(_directory).All(file => new FileInfo(file).Length <= 200));
        StringAssert.Contains(File.ReadAllText(provider.CurrentFilePath), "line 39");
    }

    /// <summary>
    /// The provider has no secret handling. This pins that a line holds only what the caller passes, so callers must
    /// log safe values (ids, counts), never tokens or message text.
    /// </summary>
    [TestMethod]
    public void LogsOnlyWhatTheCallerPassesNeverAnUnloggedSecret()
    {
        const string Secret = "s3cr3t-pairing-token";
        using var provider = new RollingFileLoggerProvider(_directory, new LogLevelSwitch(), TimeProvider.System);
        var logger = provider.CreateLogger("Tests");

        // The Secret Exists In Scope But Is Not Passed To The Logger
        _ = Secret.Length;
        logger.Log(LogLevel.Information, "paired device {Count}", 1);

        var text = string.Concat(Directory.GetFiles(_directory).Select(File.ReadAllText));
        Assert.IsFalse(text.Contains(Secret, StringComparison.Ordinal));
    }
}

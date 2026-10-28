namespace GMessages.Presentation.Tests;

/// <summary>
/// Tests the test-hook command line checks.
/// </summary>
[TestClass]
public sealed class TestHookArgsTests
{
    /// <summary>No flags is a normal run.</summary>
    [TestMethod]
    public void Validate_NoFlags_IsNormalRun()
    {
        var (endpoint, dir, error) = TestHookArgs.Validate(["Messages.exe"]);

        Assert.IsNull(endpoint);
        Assert.IsNull(dir);
        Assert.IsNull(error);
    }

    /// <summary>Both flags valid: read in any case and order, data folder made a full path.</summary>
    [TestMethod]
    public void Validate_BothValid_ReadsValues()
    {
        var (endpoint, dir, error) = TestHookArgs.Validate(["x.exe", "--TEST-DATA-DIR", @"C:\t\d", "--test-endpoints", "http://127.0.0.1:9"]);

        Assert.IsNull(error);
        Assert.AreEqual(new Uri("http://127.0.0.1:9"), endpoint);
        Assert.AreEqual(@"C:\t\d", dir);
    }

    /// <summary>A relative data folder becomes a full path.</summary>
    [TestMethod]
    public void Validate_RelativeDir_IsMadeFull()
    {
        var (_, dir, error) = TestHookArgs.Validate(["x.exe", "--test-endpoints", "https://h", "--test-data-dir", "rel"]);

        Assert.IsNull(error);
        Assert.IsTrue(Path.IsPathFullyQualified(dir!));
    }

    /// <summary>Every bad combination is refused with a reason and no values.</summary>
    [TestMethod]
    [DataRow("--test-data-dir", @"C:\d")]
    [DataRow("--test-endpoints", "http://h")]
    [DataRow("--test-endpoints", "not a url", "--test-data-dir", @"C:\d")]
    [DataRow("--test-endpoints", "ftp://h", "--test-data-dir", @"C:\d")]
    [DataRow("--test-endpoints", "/relative", "--test-data-dir", @"C:\d")]
    [DataRow("--test-endpoints", "http://h", "--test-data-dir")]
    [DataRow("--test-endpoints", "http://h", "--test-data-dir", "--quit")]
    [DataRow("--test-endpoints", "http://h", "--test-data-dir", "")]
    [DataRow("--test-endpoints", "http://h", "--test-data-dir", "C:\\a|b")]
    [DataRow("--test-endpoints", "--quit", "--test-data-dir", @"C:\d")]
    [DataRow("--test-endpoints", "http://h", "--test-endpoints", "http://h2", "--test-data-dir", @"C:\d")]
    [DataRow("--test-endpoints", "http://h", "--test-data-dir", @"C:\a", "--test-data-dir", @"C:\b")]
    public void Validate_Bad_IsRefused(params string[] tail)
    {
        var (endpoint, dir, error) = TestHookArgs.Validate(["x.exe", .. tail]);

        Assert.IsFalse(string.IsNullOrEmpty(error));
        Assert.IsNull(endpoint);
        Assert.IsNull(dir);
    }
}

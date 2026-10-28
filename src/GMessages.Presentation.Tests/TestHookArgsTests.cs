namespace GMessages.Presentation.Tests;

/// <summary>
/// Tests the test-hook command line parser.
/// </summary>
[TestClass]
public sealed class TestHookArgsTests
{
    /// <summary>No flags gives nothing.</summary>
    [TestMethod]
    public void Parse_NoFlags_GivesNulls()
    {
        var (endpoint, dir) = TestHookArgs.Parse(["Messages.exe"]);

        Assert.IsNull(endpoint);
        Assert.IsNull(dir);
    }

    /// <summary>Both flags are read, in any case and order.</summary>
    [TestMethod]
    public void Parse_BothFlags_ReadsValues()
    {
        var (endpoint, dir) = TestHookArgs.Parse(["x.exe", "--TEST-DATA-DIR", @"C:\t\d", "--test-endpoints", "http://127.0.0.1:9"]);

        Assert.AreEqual(new Uri("http://127.0.0.1:9"), endpoint);
        Assert.AreEqual(@"C:\t\d", dir);
    }

    /// <summary>A flag with no value, or whose value is another flag, is rejected.</summary>
    [TestMethod]
    [DataRow("--test-data-dir")]
    [DataRow("--test-data-dir", "--quit")]
    [DataRow("--test-data-dir", "")]
    public void Parse_BadDataDir_IsRejected(params string[] tail)
    {
        var (_, dir) = TestHookArgs.Parse(["x.exe", .. tail]);

        Assert.IsNull(dir);
    }

    /// <summary>A bad or non-absolute endpoint is rejected.</summary>
    [TestMethod]
    [DataRow("--test-endpoints")]
    [DataRow("--test-endpoints", "--quit")]
    [DataRow("--test-endpoints", "not a url")]
    [DataRow("--test-endpoints", "ftp://host")]
    public void Parse_BadEndpoint_IsRejected(params string[] tail)
    {
        var (endpoint, _) = TestHookArgs.Parse(["x.exe", .. tail]);

        Assert.IsNull(endpoint);
    }
}

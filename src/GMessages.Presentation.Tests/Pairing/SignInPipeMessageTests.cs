using System.Buffers.Binary;
using System.Text;
using GMessages.Presentation.Pairing;
using GMessages.Presentation.Settings;

namespace GMessages.Presentation.Tests.Pairing;

/// <summary>
/// Tests the sign-in helper's pipe message and command line.
/// </summary>
[TestClass]
public sealed class SignInPipeMessageTests
{
    private static readonly Dictionary<string, string> AllCookies = SignInPipeMessage.CookieNames.ToDictionary(n => n, n => "v-" + n);

    // =========================================================================
    // PIPE MESSAGE
    // =========================================================================

    /// <summary>Cookies written are read back unchanged.</summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Cookies_RoundTrip()
    {
        var read = await ReadAsync(SignInPipeMessage.Encode(AllCookies));

        Assert.IsNotNull(read);
        CollectionAssert.AreEquivalent(AllCookies, read.ToDictionary());
    }

    /// <summary>"Canceled" is read back as null.</summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Canceled_RoundTrip()
    {
        byte[] message = SignInPipeMessage.Encode(null);

        Assert.AreEqual("{\"canceled\":true}", Encoding.UTF8.GetString(message, 4, message.Length - 4));
        Assert.IsNull(await ReadAsync(message));
    }

    /// <summary>A length prefix over 64 KB is refused before the body is read.</summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Oversize_IsRefused()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, SignInPipeMessage.MaxBodyBytes + 1);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAsync(prefix));
        Assert.ThrowsExactly<InvalidDataException>(() => SignInPipeMessage.Parse(new byte[SignInPipeMessage.MaxBodyBytes + 1]));
    }

    /// <summary>A zero or negative length is refused.</summary>
    /// <param name="length">The length prefix.</param>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task BadLength_IsRefused(int length)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAsync(prefix));
    }

    /// <summary>A pipe closed mid-message is the end of the stream, not a message.</summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task Truncated_IsEndOfStream()
    {
        byte[] message = SignInPipeMessage.Encode(AllCookies);

        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => ReadAsync(message[..^3]));
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => ReadAsync([]));
    }

    /// <summary>Every malformed or disallowed body is refused, without its values in the error.</summary>
    /// <param name="json">The body.</param>
    [TestMethod]
    [DataRow("not json")]
    [DataRow("{\"cookies\":")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"canceled\":false}")]
    [DataRow("{\"cookies\":{}}")]
    [DataRow("{\"cookies\":{\"SID\":\"secret\"},\"canceled\":true}")]
    [DataRow("{\"cookies\":{\"SID\":\"secret\",\"NID\":\"secret\"}}")]
    [DataRow("{\"cookies\":{\"SID\":\"\"}}")]
    [DataRow("{\"cookies\":{\"SID\":null}}")]
    [DataRow("{\"cookies\":{\"SID\":\"secret\"},\"extra\":1}")]
    [DataRow("{\"cookies\":{\"SID\":1}}")]
    public void Bad_IsRefused(string json)
    {
        var error = Assert.ThrowsExactly<InvalidDataException>(() => SignInPipeMessage.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.IsFalse(error.Message.Contains("secret", StringComparison.Ordinal), "The error quoted a cookie value.");
    }

    /// <summary>A subset of the known cookies is fine (__Secure-1PSIDTS may be missing).</summary>
    [TestMethod]
    public void KnownSubset_IsAccepted()
    {
        var read = SignInPipeMessage.Parse("{\"cookies\":{\"SID\":\"a\",\"SAPISID\":\"b\"}}"u8);

        Assert.AreEqual(2, read!.Count);
    }

    // =========================================================================
    // COMMAND LINE
    // =========================================================================

    /// <summary>The built helper arguments parse back to the same values.</summary>
    [TestMethod]
    public void HelperArgs_RoundTrip()
    {
        string[] args = SignInHelperArgs.Build("pipe1", @"C:\a b\Logs", AppTheme.Dark);

        Assert.AreEqual(("pipe1", @"C:\a b\Logs", AppTheme.Dark), SignInHelperArgs.Parse(["x.exe", .. args]));
    }

    /// <summary>No <c>--sign-in</c> is a normal launch; a bad theme falls back to System.</summary>
    [TestMethod]
    public void HelperArgs_Absent_Or_BadTheme()
    {
        Assert.IsNull(SignInHelperArgs.Parse(["x.exe", "--quit"]));
        Assert.IsNull(SignInHelperArgs.Parse(["x.exe", "--sign-in"]));
        Assert.AreEqual(("p", null, AppTheme.System), SignInHelperArgs.Parse(["x.exe", "--sign-in", "p", "--theme", "Purple"]));
    }

    /// <summary>Joined arguments split back the way the C runtime splits a command line.</summary>
    /// <param name="argument">One argument.</param>
    /// <param name="expected">How it is quoted.</param>
    [TestMethod]
    [DataRow("plain", "plain")]
    [DataRow("", "\"\"")]
    [DataRow(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [DataRow("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [DataRow(@"C:\no\space\", @"C:\no\space\")]
    public void Join_Quotes(string argument, string expected) =>
        Assert.AreEqual("--x " + expected, SignInHelperArgs.Join(["--x", argument]));

    private static async Task<IReadOnlyDictionary<string, string>?> ReadAsync(byte[] message)
    {
        using var stream = new MemoryStream(message);
        return await SignInPipeMessage.ReadAsync(stream, CancellationToken.None);
    }
}

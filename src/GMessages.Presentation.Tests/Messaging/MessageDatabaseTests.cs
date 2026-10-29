using System.Text;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class MessageDatabaseTests
{
    private const long BaseTime = 1_761_700_000_000_000;

    private string _folder = "";
    private string _path   = "";

    [TestInitialize]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "msgdb-test-" + Guid.NewGuid().ToString("N"));
        _path   = Path.Combine(_folder, "messages.db");
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private MessageDatabase Db() => new(_path, NullLogger.Instance);

    private static Message Msg(string id, long ts, string text = "hi", MessageStatusType status = MessageStatusType.OutgoingComplete)
    {
        var m = new Message { MessageID = id, ConversationID = "c-1", Timestamp = ts, MessageStatus = new MessageStatus { Status = status } };
        m.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
        return m;
    }

    private static List<Message> Range(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Msg("m-" + i, BaseTime + i, "text " + i))];

    // Runs SQL Straight On The File, Bypassing The Class
    private void RawSql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    // =========================================================================
    // TESTS
    // =========================================================================

    [TestMethod]
    public void Messages_RoundTrip_NewestCountOldestFirst()
    {
        var db            = Db();
        List<Message> all = Range(30);
        db.SaveMessages("c-1", all);

        IReadOnlyList<Message> loaded = db.LoadMessages("c-1", 25);

        CollectionAssert.AreEqual(all.Skip(5).ToList(), loaded.ToList());
    }

    [TestMethod]
    public void SameMessageId_UpdatesInPlace()
    {
        var db = Db();
        db.SaveMessages("c-1", [Msg("m-1", BaseTime)]);
        db.SaveMessages("c-1", [Msg("m-1", BaseTime, status: MessageStatusType.OutgoingDisplayed)]);

        IReadOnlyList<Message> loaded = db.LoadMessages("c-1", 10);

        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual(MessageStatusType.OutgoingDisplayed, loaded[0].MessageStatus.Status);
        Assert.AreEqual(MessageStatusType.OutgoingDisplayed, db.LoadMessage("c-1", "m-1")!.MessageStatus.Status);
    }

    [TestMethod]
    public void KeepsNewest100PerConversation()
    {
        var db            = Db();
        List<Message> all = Range(130);
        db.SaveMessages("c-1", all.Take(70).ToList());
        db.SaveMessages("c-1", all.Skip(70).ToList());
        db.SaveMessages("c-2", [Msg("other", BaseTime)]);

        IReadOnlyList<Message> loaded = db.LoadMessages("c-1", 500);

        Assert.AreEqual(MessageDatabase.KeepPerConversation, loaded.Count);
        CollectionAssert.AreEqual(all.Skip(30).ToList(), loaded.ToList());
        Assert.IsNull(db.LoadMessage("c-1", "m-29"));
        Assert.AreEqual(1, db.LoadMessages("c-2", 10).Count);
    }

    [TestMethod]
    public void Content_IsNotStoredInPlainText()
    {
        var db = Db();
        db.SaveMessages("c-1", [Msg("m-1", BaseTime, "secret text 123")]);
        db.SaveConversation(new Conversation { ConversationID = "c-1", Name = "Ann Secret", LastMessageTimestamp = BaseTime });

        byte[] raw = File.ReadAllBytes(_path);

        foreach (string secret in new[] { "secret text 123", "Ann Secret" })
        {
            Assert.IsFalse(Contains(raw, Encoding.UTF8.GetBytes(secret)), secret + " (UTF-8)");
            Assert.IsFalse(Contains(raw, Encoding.Unicode.GetBytes(secret)), secret + " (UTF-16)");
        }

        Assert.IsTrue(Contains(raw, Encoding.UTF8.GetBytes("m-1")));
        Assert.AreEqual("Ann Secret", db.LoadConversations().Single().Name);
    }

    [TestMethod]
    public void Encryption_KeepsTheNewer()
    {
        var db = Db();
        db.SaveEncryption("c-1", encrypted: true, timestampMicros: 200);
        db.SaveEncryption("c-1", encrypted: false, timestampMicros: 100);
        db.SaveEncryption("c-2", encrypted: false, timestampMicros: 100);
        db.SaveEncryption("c-2", encrypted: true, timestampMicros: 300);

        IReadOnlyDictionary<string, (bool Encrypted, long At)> loaded = db.LoadEncryption();

        Assert.AreEqual((true, 200L), loaded["c-1"]);
        Assert.AreEqual((true, 300L), loaded["c-2"]);
    }

    [TestMethod]
    public void Delete_RemovesFileAndSidecars_ThenStartsEmpty()
    {
        var db = Db();
        db.SaveMessages("c-1", Range(3));
        db.SaveConversation(new Conversation { ConversationID = "c-1", LastMessageTimestamp = BaseTime });
        foreach (string sidecar in new[] { "-journal", "-wal", "-shm" })
        {
            File.WriteAllBytes(_path + sidecar, [1]);
        }

        db.Delete();

        Assert.IsFalse(File.Exists(_path));
        foreach (string sidecar in new[] { "-journal", "-wal", "-shm" })
        {
            Assert.IsFalse(File.Exists(_path + sidecar), sidecar);
        }

        Assert.AreEqual(0, db.LoadConversations().Count);
        Assert.AreEqual(0, db.LoadMessages("c-1", 10).Count);
        db.SaveMessages("c-1", Range(2));
        Assert.AreEqual(2, db.LoadMessages("c-1", 10).Count);
    }

    [TestMethod]
    public void CorruptFile_StartsEmpty()
    {
        byte[] garbage = new byte[8192];
        new Random(7).NextBytes(garbage);
        File.WriteAllBytes(_path, garbage);
        var db = Db();

        Assert.AreEqual(0, db.LoadConversations().Count);
        Assert.AreEqual(0, db.LoadMessages("c-1", 10).Count);
        Assert.AreEqual(0, db.LoadEncryption().Count);

        db.SaveMessages("c-1", Range(2));
        Assert.AreEqual(2, db.LoadMessages("c-1", 10).Count);
    }

    [TestMethod]
    public void OtherSchemaVersion_StartsEmpty()
    {
        RawSql("CREATE TABLE conversations (id TEXT PRIMARY KEY, other INTEGER); INSERT INTO conversations VALUES ('c-1', 1); PRAGMA user_version = 99;");
        var db = Db();

        Assert.AreEqual(0, db.LoadConversations().Count);

        db.SaveConversation(new Conversation { ConversationID = "c-1", LastMessageTimestamp = BaseTime });
        Assert.AreEqual(1, db.LoadConversations().Count);
    }

    [TestMethod]
    public void UnreadableRow_StartsEmpty()
    {
        var db = Db();
        db.SaveMessages("c-1", Range(3));
        RawSql("UPDATE messages SET body = x'0102030405060708' WHERE id = 'm-1'");

        Assert.AreEqual(0, db.LoadMessages("c-1", 10).Count);
        Assert.AreEqual(0, db.LoadMessages("c-1", 10).Count);
    }

    [TestMethod]
    public void NonBlobBody_StartsEmpty()
    {
        var db = Db();
        db.SaveMessages("c-1", Range(3));
        db.SaveConversation(new Conversation { ConversationID = "c-1", LastMessageTimestamp = BaseTime });
        RawSql("UPDATE conversations SET body = 'plain text'");

        Assert.AreEqual(0, db.LoadConversations().Count);
        Assert.AreEqual(0, db.LoadMessages("c-1", 10).Count);
    }

    [TestMethod]
    public void LockedFile_FailsTheCall_KeepsTheDatabase()
    {
        var log = new CollectingLogger();
        var db  = new MessageDatabase(_path, log, busyTimeoutSeconds: 1);
        db.SaveMessages("c-1", Range(3));

        // Another Process Holds An Exclusive Lock
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()))
        {
            holder.Open();
            using SqliteCommand command = holder.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE";
            command.ExecuteNonQuery();

            Assert.AreEqual(0, db.LoadMessages("c-1", 10).Count);
            Assert.IsNull(db.LoadMessage("c-1", "m-1"));
            db.SaveMessages("c-1", [Msg("m-new", BaseTime + 100)]);
        }

        Assert.IsTrue(File.Exists(_path));
        CollectionAssert.AreEqual(Range(3), db.LoadMessages("c-1", 10).ToList());
        Assert.AreEqual(3, log.Messages.Length);
        Assert.IsTrue(log.Messages.All(m => m == "Message database call failed; file kept (SqliteException 5)"), string.Join(" | ", log.Messages));
    }

    [TestMethod]
    public void Arguments_EmptyIdAndNegativeCount_Throw()
    {
        var db = Db();
        db.SaveMessages("c-1", Range(3));

        Assert.ThrowsExactly<ArgumentException>(() => db.SaveConversation(new Conversation { LastMessageTimestamp = BaseTime }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => db.LoadMessages("c-1", -1));
        Assert.AreEqual(0, db.LoadMessages("c-1", 0).Count);
    }
}

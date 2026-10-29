using System.Security.Cryptography;
using Google.Protobuf;
using GMessages.Protocol.Proto;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// The local SQLite database of conversations and recent messages, so a chat opens instantly even right after a restart.
/// </summary>
/// <remarks>
/// <para>
/// Every row's <c>body</c> is the protobuf bytes encrypted with DPAPI for the current Windows user, so names, numbers
/// and text never sit on disk in plain text. IDs and timestamps stay plain so lookups and ordering work. Only the
/// newest <see cref="KeepPerConversation"/> messages of each conversation are kept.
/// </para>
/// <para>
/// Runs on Windows' own <c>winsqlite3.dll</c>. A connection opens per call with pooling off, so nothing is held while
/// idle; one lock serializes every call. The database is a cache, never a reason to fail: a damaged file, a file from
/// another schema version, or a row that won't decrypt or parse deletes the file, logs only the error type, and the
/// call answers empty (loads) or drops the write (saves). A passing problem (the file locked by another process, disk
/// full, an I/O error) fails only that call the same way and keeps the file. Content is never logged.
/// </para>
/// </remarks>
public sealed partial class MessageDatabase
{
    /// <summary>
    /// The schema this class writes, kept in <c>PRAGMA user_version</c>. A file with another version starts over.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// How many of a conversation's newest messages are kept.
    /// </summary>
    public const int KeepPerConversation = 100;

    private const string Schema = """
        CREATE TABLE conversations (id TEXT PRIMARY KEY, last_time INTEGER NOT NULL, body BLOB NOT NULL);
        CREATE TABLE messages (conversation_id TEXT NOT NULL, id TEXT NOT NULL, time INTEGER NOT NULL, body BLOB NOT NULL,
                               PRIMARY KEY (conversation_id, id));
        CREATE INDEX messages_by_time ON messages (conversation_id, time);
        CREATE TABLE encryption (conversation_id TEXT PRIMARY KEY, encrypted INTEGER NOT NULL, at INTEGER NOT NULL);
        PRAGMA user_version = 1;
        """;

    private readonly string _path;
    private readonly string _connectionString;
    private readonly ILogger _log;
    private readonly Lock _lock = new();

    // Binds Windows' winsqlite3 Explicitly: Microsoft.Data.Sqlite Otherwise Finds It By Reflection, Which AOT Trims
    static MessageDatabase()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    /// <summary>
    /// Wraps the database file; nothing is opened until the first call.
    /// </summary>
    /// <param name="path">The database file (<see cref="AppPaths.MessagesDatabase"/>).</param>
    /// <param name="log">Gets error type names only.</param>
    public MessageDatabase(string path, ILogger log)
        : this(path, log, busyTimeoutSeconds: 30)
    {
    }

    // Tests Shorten How Long A Call Waits On A File Another Process Has Locked
    internal MessageDatabase(string path, ILogger log, int busyTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(log);

        _path             = path;
        _log              = log;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = busyTimeoutSeconds }.ToString();
    }

    // =========================================================================
    // CONVERSATIONS
    // =========================================================================

    /// <summary>
    /// Loads every saved conversation, most recent first.
    /// </summary>
    /// <returns>The conversations, or empty when there are none or the file was unreadable.</returns>
    public IReadOnlyList<Conversation> LoadConversations() => Run<IReadOnlyList<Conversation>>(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM conversations ORDER BY last_time DESC";

        List<Conversation> conversations = [];
        using SqliteDataReader reader    = command.ExecuteReader();
        while (reader.Read())
        {
            conversations.Add(Conversation.Parser.ParseFrom(Unprotect(reader)));
        }

        return conversations;
    }, []);

    /// <summary>
    /// Saves a conversation, replacing the one with the same ID.
    /// </summary>
    /// <param name="conversation">The conversation; its <c>LastMessageTimestamp</c> is stored plain for ordering.</param>
    public void SaveConversation(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrEmpty(conversation.ConversationID, nameof(conversation));

        Run(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO conversations (id, last_time, body) VALUES ($id, $time, $body)";
            command.Parameters.AddWithValue("$id", conversation.ConversationID);
            command.Parameters.AddWithValue("$time", conversation.LastMessageTimestamp);
            command.Parameters.AddWithValue("$body", Protect(conversation));
            command.ExecuteNonQuery();
            return true;
        }, false);
    }

    // =========================================================================
    // MESSAGES
    // =========================================================================

    /// <summary>
    /// Loads a conversation's newest messages.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="count">How many of the newest to load.</param>
    /// <returns>The newest <paramref name="count"/> messages, oldest first; empty when none or unreadable.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public IReadOnlyList<Message> LoadMessages(string conversationId, int count)
    {
        // SQLite Reads A Negative LIMIT As No Limit
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return Run<IReadOnlyList<Message>>(connection => LoadMessages(connection, conversationId, count), []);
    }

    private static List<Message> LoadMessages(SqliteConnection connection, string conversationId, int count)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT body FROM (SELECT body, time, id FROM messages WHERE conversation_id = $c ORDER BY time DESC, id DESC LIMIT $n)
            ORDER BY time, id
            """;
        command.Parameters.AddWithValue("$c", conversationId);
        command.Parameters.AddWithValue("$n", count);

        List<Message> messages        = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(Message.Parser.ParseFrom(Unprotect(reader)));
        }

        return messages;
    }

    /// <summary>
    /// Loads one message.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <returns>The message, or null when it isn't saved or the file was unreadable.</returns>
    public Message? LoadMessage(string conversationId, string messageId) => Run(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM messages WHERE conversation_id = $c AND id = $id";
        command.Parameters.AddWithValue("$c", conversationId);
        command.Parameters.AddWithValue("$id", messageId);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Message.Parser.ParseFrom(Unprotect(reader)) : null;
    }, (Message?)null);

    /// <summary>
    /// Saves messages in one transaction, replacing those with the same ID, then keeps only the newest
    /// <see cref="KeepPerConversation"/> of the conversation.
    /// </summary>
    /// <param name="conversationId">The conversation the messages belong to.</param>
    /// <param name="messages">The messages; each one's <c>Timestamp</c> is stored plain for ordering.</param>
    public void SaveMessages(string conversationId, IReadOnlyList<Message> messages)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(messages);

        Run(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            // Upsert
            using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT OR REPLACE INTO messages (conversation_id, id, time, body) VALUES ($c, $id, $time, $body)";
                SqliteParameter c    = insert.Parameters.Add("$c", SqliteType.Text);
                SqliteParameter id   = insert.Parameters.Add("$id", SqliteType.Text);
                SqliteParameter time = insert.Parameters.Add("$time", SqliteType.Integer);
                SqliteParameter body = insert.Parameters.Add("$body", SqliteType.Blob);
                c.Value              = conversationId;
                foreach (Message message in messages)
                {
                    id.Value   = message.MessageID;
                    time.Value = message.Timestamp;
                    body.Value = Protect(message);
                    insert.ExecuteNonQuery();
                }
            }

            // Keep The Newest
            using (SqliteCommand trim = connection.CreateCommand())
            {
                trim.CommandText = """
                    DELETE FROM messages WHERE conversation_id = $c AND id NOT IN
                        (SELECT id FROM messages WHERE conversation_id = $c ORDER BY time DESC, id DESC LIMIT $keep)
                    """;
                trim.Parameters.AddWithValue("$c", conversationId);
                trim.Parameters.AddWithValue("$keep", KeepPerConversation);
                trim.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        }, false);
    }

    // =========================================================================
    // ENCRYPTION STATE
    // =========================================================================

    /// <summary>
    /// Loads whether each conversation is end-to-end encrypted, and when the phone reported it.
    /// </summary>
    /// <returns>Conversation ID to encrypted and its time in microseconds; empty when none or unreadable.</returns>
    public IReadOnlyDictionary<string, (bool Encrypted, long At)> LoadEncryption() => Run<IReadOnlyDictionary<string, (bool Encrypted, long At)>>(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_id, encrypted, at FROM encryption";

        Dictionary<string, (bool Encrypted, long At)> states = [];
        using SqliteDataReader reader                        = command.ExecuteReader();
        while (reader.Read())
        {
            states[reader.GetString(0)] = (reader.GetInt64(1) != 0, reader.GetInt64(2));
        }

        return states;
    }, new Dictionary<string, (bool Encrypted, long At)>());

    /// <summary>
    /// Saves whether a conversation is end-to-end encrypted, only when newer than the state already stored.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="encrypted">Whether it's encrypted.</param>
    /// <param name="timestampMicros">When the phone reported it, in microseconds.</param>
    public void SaveEncryption(string conversationId, bool encrypted, long timestampMicros)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        Run(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO encryption (conversation_id, encrypted, at) VALUES ($c, $e, $at)
                ON CONFLICT (conversation_id) DO UPDATE SET encrypted = excluded.encrypted, at = excluded.at
                WHERE excluded.at > encryption.at
                """;
            command.Parameters.AddWithValue("$c", conversationId);
            command.Parameters.AddWithValue("$e", encrypted ? 1 : 0);
            command.Parameters.AddWithValue("$at", timestampMicros);
            command.ExecuteNonQuery();
            return true;
        }, false);
    }

    // =========================================================================
    // FILE
    // =========================================================================

    /// <summary>
    /// Deletes the database file and its <c>-journal</c>, <c>-wal</c> and <c>-shm</c> sidecars; the next call starts a
    /// new file. A file that can't be deleted is logged by error type and left.
    /// </summary>
    public void Delete()
    {
        lock (_lock)
        {
            DeleteFiles();
        }
    }

    // Caller Holds The Lock
    private void DeleteFiles()
    {
        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogDeleteFailed(ex.GetType().Name);
            }
        }
    }

    // Opens The File, Creating The Schema On A New One And Starting Over On Another Version. Caller Holds The Lock
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();

            long version = UserVersion(connection);
            if (version != 0 && version != SchemaVersion)
            {
                LogReset("SchemaVersion" + version);
                connection.Dispose();
                DeleteFiles();
                connection = new SqliteConnection(_connectionString);
                connection.Open();
                version = 0;
            }

            if (version == 0)
            {
                using SqliteCommand create = connection.CreateCommand();
                create.CommandText = Schema;
                create.ExecuteNonQuery();
            }

            return connection;
        }
        catch
        {
            // Release The File So The Caller Can Delete It
            connection.Dispose();
            throw;
        }
    }

    private static long UserVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)command.ExecuteScalar()!;
    }

    // Runs One Call Under The Lock. A Damaged Or Foreign File Or Row Deletes The Database; A Passing Problem (Locked,
    // Disk Full, I/O) Only Fails This Call. Either Way The Call Answers The Fallback
    private T Run<T>(Func<SqliteConnection, T> work, T fallback)
    {
        lock (_lock)
        {
            try
            {
                using SqliteConnection connection = Open();
                return work(connection);
            }
            catch (SqliteException ex) when (!IsDamage(ex))
            {
                LogCallFailed(ex.GetType().Name, ex.SqliteErrorCode);
                return fallback;
            }
            catch (Exception ex) when (ex is SqliteException or CryptographicException or InvalidProtocolBufferException or InvalidDataException)
            {
                LogReset(ex.GetType().Name);
                DeleteFiles();
                return fallback;
            }
        }
    }

    // SQLITE_ERROR (Missing Table Or Column), SQLITE_CORRUPT, SQLITE_CONSTRAINT, SQLITE_MISMATCH, SQLITE_NOTADB: The File
    // Isn't Ours Or Is Broken. Anything Else (BUSY, LOCKED, IOERR, FULL, CANTOPEN, ...) May Pass On Its Own
    private static bool IsDamage(SqliteException ex) =>
        (ex.SqliteErrorCode & 0xFF) is 1 or 11 or 19 or 20 or 26;

    private static byte[] Protect(IMessage value) =>
        ProtectedData.Protect(value.ToByteArray(), null, DataProtectionScope.CurrentUser);

    // A Body That Isn't A Blob (NULL, Text, Number) Is A Bad Row
    private static byte[] Unprotect(SqliteDataReader reader) =>
        ProtectedData.Unprotect(reader.GetValue(0) as byte[] ?? throw new InvalidDataException("Body is not a blob"), null, DataProtectionScope.CurrentUser);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message database call failed; file kept ({ErrorType} {ErrorCode})")]
    private partial void LogCallFailed(string errorType, int errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message database reset ({ErrorType})")]
    private partial void LogReset(string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message database file could not be deleted ({ErrorType})")]
    private partial void LogDeleteFailed(string errorType);
}

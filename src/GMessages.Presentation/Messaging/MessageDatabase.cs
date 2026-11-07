using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using GMessages.Protocol.Proto;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Keeps conversations and recent messages in a local SQLite database, so a chat opens at once, also after a restart.
/// </summary>
/// <remarks>
/// <para>
/// The <c>body</c> of each row is the protobuf bytes, encrypted with DPAPI for the current Windows user. Thus names,
/// numbers and text are never on disk as plain text. IDs and timestamps stay plain, so lookups and ordering work.
/// The database keeps only the newest <see cref="KeepPerConversation"/> messages of each conversation.
/// </para>
/// <para>
/// It uses the <c>winsqlite3.dll</c> of Windows. Each call opens a connection with pooling off, so nothing stays open
/// while idle. A single lock lets 1 call run at a time. The database is a cache and never a reason to fail. A damaged
/// file deletes the file. A file of another schema version, or a row that does not decrypt or parse, does the same.
/// Then the call logs only the error type. A load answers empty, and a save drops the write. A temporary problem
/// fails only that call in the same way, and the file stays. Examples are a lock by another process, a full disk or
/// an I/O error. The database never logs content.
/// </para>
/// </remarks>
public sealed partial class MessageDatabase
{
    private const int SchemaVersion = 2;

    /// <summary>
    /// The number of the newest messages of a conversation that the database keeps.
    /// </summary>
    public const int KeepPerConversation = 100;

    // Version 2 added drafts. Open adds this table to a version 1 file in place.
    private const string DraftsTable = "CREATE TABLE drafts (conversation_id TEXT PRIMARY KEY, body BLOB NOT NULL);";

    private const string Schema = """
        CREATE TABLE conversations (id TEXT PRIMARY KEY, last_time INTEGER NOT NULL, body BLOB NOT NULL);
        CREATE TABLE messages (conversation_id TEXT NOT NULL, id TEXT NOT NULL, time INTEGER NOT NULL, body BLOB NOT NULL,
                               PRIMARY KEY (conversation_id, id));
        CREATE INDEX messages_by_time ON messages (conversation_id, time);
        CREATE TABLE encryption (conversation_id TEXT PRIMARY KEY, encrypted INTEGER NOT NULL, at INTEGER NOT NULL);
        """ + DraftsTable + "PRAGMA user_version = 2;";

    private readonly string _path;
    private readonly string _connectionString;
    private readonly ILogger _log;
    private readonly Lock _lock = new();

    // This binds the winsqlite3 of Windows explicitly. Otherwise Microsoft.Data.Sqlite finds it by reflection, which
    // AOT trims.
    static MessageDatabase()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    /// <summary>
    /// Wraps the database file. Nothing opens until the first call.
    /// </summary>
    /// <param name="path">The database file, see <see cref="AppPaths.MessagesDatabase"/>.</param>
    /// <param name="log">The logger, which gets error type names only.</param>
    public MessageDatabase(string path, ILogger log)
        : this(path, log, busyTimeoutSeconds: 30)
    {
    }

    // Tests make the wait shorter for a file that another process has locked.
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
    /// Loads all saved conversations, most recent first.
    /// </summary>
    /// <returns>The conversations, or empty when there are none or the file was not readable.</returns>
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
    /// Saves a conversation and replaces the one with the same ID.
    /// </summary>
    /// <param name="conversation">The conversation. Its <c>LastMessageTimestamp</c> stays plain for ordering.</param>
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

    /// <summary>
    /// Changes a saved conversation in place: read, archived, muted, moved to spam or deleted here.
    /// </summary>
    /// <remarks>
    /// A change made here touches only the slim cached copy. Without this method, the saved row keeps its old state.
    /// The next start then shows the old state until the page from the phone replaces it. The chat is then bold, or in
    /// the wrong list. The method rewrites only the body and keeps the saved order. It does nothing when the
    /// conversation is not saved or the change keeps it the same.
    /// </remarks>
    /// <param name="change">
    /// Changes the saved conversation. It returns false when the conversation already had the change.
    /// </param>
    public void UpdateConversation(string conversationId, Func<Conversation, bool> change)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(change);

        Run(connection =>
        {
            Conversation conversation;
            using (SqliteCommand select = connection.CreateCommand())
            {
                select.CommandText = "SELECT body FROM conversations WHERE id = $id";
                select.Parameters.AddWithValue("$id", conversationId);

                using SqliteDataReader reader = select.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                conversation = Conversation.Parser.ParseFrom(Unprotect(reader));
            }

            if (!change(conversation))
            {
                return false;
            }

            using SqliteCommand update = connection.CreateCommand();
            update.CommandText = "UPDATE conversations SET body = $body WHERE id = $id";
            update.Parameters.AddWithValue("$id", conversationId);
            update.Parameters.AddWithValue("$body", Protect(conversation));
            update.ExecuteNonQuery();
            return true;
        }, false);
    }

    /// <summary>
    /// Removes a conversation deleted here, with its saved messages, encryption state and draft.
    /// </summary>
    public void DeleteConversation(string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        Run(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM conversations WHERE id = $id;
                DELETE FROM messages WHERE conversation_id = $id;
                DELETE FROM encryption WHERE conversation_id = $id;
                DELETE FROM drafts WHERE conversation_id = $id;
                """;
            command.Parameters.AddWithValue("$id", conversationId);
            command.ExecuteNonQuery();
            return true;
        }, false);
    }

    // =========================================================================
    // MESSAGES
    // =========================================================================

    /// <summary>
    /// Deletes 1 saved message after its delete on the phone.
    /// </summary>
    public void DeleteMessage(string conversationId, string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        Run(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM messages WHERE conversation_id = $c AND id = $id";
            command.Parameters.AddWithValue("$c", conversationId);
            command.Parameters.AddWithValue("$id", messageId);
            command.ExecuteNonQuery();
            return true;
        }, false);
    }

    /// <summary>
    /// Loads the newest messages of a conversation.
    /// </summary>
    /// <param name="count">The number of the newest messages to load.</param>
    /// <returns>
    /// The newest <paramref name="count"/> messages, oldest first, or empty when there are none or the file was not
    /// readable.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public IReadOnlyList<Message> LoadMessages(string conversationId, int count)
    {
        // SQLite reads a negative LIMIT as no limit.
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return Run<IReadOnlyList<Message>>(connection =>
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
        }, []);
    }

    /// <summary>
    /// Loads 1 message.
    /// </summary>
    /// <returns>The message, or null when it is not saved or the file was not readable.</returns>
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
    /// Saves messages in 1 transaction and replaces those with the same ID. Then it keeps only the newest
    /// <see cref="KeepPerConversation"/> messages of the conversation.
    /// </summary>
    /// <param name="conversationId">The conversation that the messages belong to.</param>
    /// <param name="messages">The messages. The <c>Timestamp</c> of each one stays plain for ordering.</param>
    public void SaveMessages(string conversationId, IReadOnlyList<Message> messages)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(messages);

        Run(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

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
    /// <returns>
    /// The encrypted flag and its time in microseconds by conversation ID. It is empty when there are none or the
    /// file was not readable.
    /// </returns>
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
    /// Saves whether a conversation is end-to-end encrypted, only when the state is newer than the stored state.
    /// </summary>
    /// <param name="timestampMicros">When the phone reported the state, in microseconds.</param>
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
    // DRAFTS
    // =========================================================================

    /// <summary>
    /// Loads the unsent draft of each conversation.
    /// </summary>
    /// <returns>The draft text by conversation ID, or empty when there are none or the file was not readable.</returns>
    public IReadOnlyDictionary<string, string> LoadDrafts() => Run<IReadOnlyDictionary<string, string>>(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT body, conversation_id FROM drafts";

        Dictionary<string, string> drafts = [];
        using SqliteDataReader reader     = command.ExecuteReader();
        while (reader.Read())
        {
            drafts[reader.GetString(1)] = Encoding.UTF8.GetString(Unprotect(reader));
        }

        return drafts;
    }, new Dictionary<string, string>());

    /// <summary>
    /// Saves the unsent draft of a conversation, encrypted like every other body. An empty draft deletes the row.
    /// </summary>
    /// <param name="text">The draft text, or empty for none.</param>
    public void SaveDraft(string conversationId, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(text);

        Run(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = text.Length == 0
                ? "DELETE FROM drafts WHERE conversation_id = $c"
                : "INSERT OR REPLACE INTO drafts (conversation_id, body) VALUES ($c, $body)";
            command.Parameters.AddWithValue("$c", conversationId);
            if (text.Length > 0)
            {
                command.Parameters.AddWithValue("$body", Protect(Encoding.UTF8.GetBytes(text)));
            }

            command.ExecuteNonQuery();
            return true;
        }, false);
    }

    // =========================================================================
    // FILE
    // =========================================================================

    /// <summary>
    /// Deletes the database file and its <c>-journal</c>, <c>-wal</c> and <c>-shm</c> files. The next call starts a
    /// new file. When a file does not delete, this method logs the error type and leaves the file.
    /// </summary>
    public void Delete()
    {
        lock (_lock)
        {
            DeleteFiles();
        }
    }

    // The caller holds the lock.
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

    // Creates the schema in a new file and adds the drafts table to a version 1 file. Any other version starts again.
    // The caller holds the lock.
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();

            long version = UserVersion(connection);
            if (version == 1)
            {
                using SqliteCommand upgrade = connection.CreateCommand();
                upgrade.CommandText = DraftsTable + "PRAGMA user_version = 2;";
                upgrade.ExecuteNonQuery();
                version = SchemaVersion;
            }

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
            // The connection releases the file, so the caller can delete it.
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

    // Runs 1 call under the lock. A damaged or foreign file or row deletes the database. A temporary problem, such as
    // a lock, a full disk or an I/O error, fails only this call. In both cases, the call answers the fallback.
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

    // These codes mean that the file is not ours or is broken: SQLITE_ERROR for a missing table or column,
    // SQLITE_CORRUPT, SQLITE_CONSTRAINT, SQLITE_MISMATCH and SQLITE_NOTADB. Other codes can be temporary, such as
    // BUSY, LOCKED, IOERR, FULL or CANTOPEN.
    private static bool IsDamage(SqliteException ex) =>
        (ex.SqliteErrorCode & 0xFF) is 1 or 11 or 19 or 20 or 26;

    private static byte[] Protect(IMessage value) => Protect(value.ToByteArray());

    private static byte[] Protect(byte[] value) =>
        ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser);

    // A body that is not a blob, such as NULL, text or a number, makes the row bad.
    private static byte[] Unprotect(SqliteDataReader reader) =>
        ProtectedData.Unprotect(reader.GetValue(0) as byte[] ?? throw new InvalidDataException("Body is not a blob"), null, DataProtectionScope.CurrentUser);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message database call failed; file kept ({ErrorType} {ErrorCode})")]
    private partial void LogCallFailed(string errorType, int errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message database reset ({ErrorType})")]
    private partial void LogReset(string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message database file could not be deleted ({ErrorType})")]
    private partial void LogDeleteFailed(string errorType);
}

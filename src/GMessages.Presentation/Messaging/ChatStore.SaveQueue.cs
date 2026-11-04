using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// The database side: the background save queue (a locked file can block a call), dropping pages, and the saves it runs.
/// </summary>
/// <remarks>
/// Part of <see cref="ChatStore"/>; the other members are in ChatStore.cs.
/// </remarks>
public sealed partial class ChatStore
{

    // =========================================================================
    // DATABASE
    // =========================================================================

    private int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Drops the cached message pages (the window closed), after the queued saves are written, so the next open reads them
    /// back from the database.
    /// </summary>
    /// <remarks>
    /// A page holding a pending send (empty ID) or a failed one stays: those live only in memory. Never throws; a failure
    /// is logged by type.
    /// </remarks>
    /// <returns>A task that completes when the pages are dropped.</returns>
    public async Task DropPagesAsync()
    {
        // Without A Database Nothing Could Bring The Pages Back
        if (_database is null)
        {
            return;
        }

        try
        {
            await FlushAsync().ConfigureAwait(false);

            lock (_pagesLock)
            {
                foreach (string id in _pages.Where(p => !p.Value.Exists(m => m.Id.Length == 0 || m.IsFailed)).Select(p => p.Key).ToList())
                {
                    _droppedIds.UnionWith(_pages[id].Where(m => m.Id.Length > 0).Select(m => m.Id));
                    _pages.Remove(id);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogActionFailed("drop message pages", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Waits for every save queued so far to be written (the app's quit, bounded by its own timeout; also tests).
    /// </summary>
    /// <returns>A task that completes when the queue is empty up to now.</returns>
    public Task FlushAsync()
    {
        lock (_queueLock)
        {
            return _queue;
        }
    }

    /// <summary>
    /// Runs a database call on the background queue, after every one queued before it (saves; tests also queue here).
    /// </summary>
    /// <remarks>Without a database nothing runs. A failure logs its error type only.</remarks>
    /// <param name="work">The call.</param>
    /// <returns>A task that completes when the call has run.</returns>
    internal Task EnqueueAsync(Action<MessageDatabase> work)
    {
        if (_database is not { } database)
        {
            return Task.CompletedTask;
        }

        lock (_queueLock)
        {
            _queue = _queue.ContinueWith(
                _ =>
                {
                    try
                    {
                        work(database);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        LogActionFailed("use the message database", ex.GetType().Name);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return _queue;
        }
    }

    // A Read Someone Is Waiting On (A Chat Being Opened, A Reply's Quote), Run At Once On The Thread Pool Instead Of Behind
    // Every Save Queued Before It: After A Catch-Up Or A Resync Hundreds Of Saves Can Be Queued, Each Its Own Transaction,
    // And A Chat Opened Then Waited For All Of Them Before Its Saved Page Showed. The Database's Own Lock Still Lets One
    // Call In At A Time, So It Waits For One Save At Most. A Read Just After A Reset Is Skipped By Its Generation Check
    // ponytail: the check and the read aren't atomic, so a read racing a Reset can recreate an empty file after its
    // delete; harmless (the next account's saves use it), add a reset gate in MessageDatabase if it ever matters
    private Task ReadNowAsync(Action<MessageDatabase> work)
    {
        if (_database is not { } database)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                work(database);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogActionFailed("read the message database", ex.GetType().Name);
            }
        });
    }

    // Queues A Conversation Save, Dropped When A Reset Came After The Generation Was Taken
    private void SaveConversation(Conversation conversation, int generation)
    {
        if (_database is null || conversation.ConversationID.Length == 0)
        {
            return;
        }

        _ = EnqueueAsync(database =>
        {
            if (generation == Generation)
            {
                database.SaveConversation(conversation);
            }
        });
    }

    // Queues A Write To One Saved Conversation (A Change Through MessageDatabase.UpdateConversation, Or Its Delete),
    // Dropped When A Reset Came After The Generation Was Taken
    private void SaveIfCurrent(string conversationId, int generation, Action<MessageDatabase> write)
    {
        if (_database is null || conversationId.Length == 0)
        {
            return;
        }

        _ = EnqueueAsync(database =>
        {
            if (generation == Generation)
            {
                write(database);
            }
        });
    }

    // Queues A Message Save, Dropped When A Reset Came After The Generation Was Taken
    private void SaveMessages(string conversationId, IReadOnlyList<Message> messages, int generation)
    {
        List<Message> saved = [.. messages.Where(m => m.MessageID.Length > 0)];
        if (_database is null || conversationId.Length == 0 || saved.Count == 0)
        {
            return;
        }

        _ = EnqueueAsync(database =>
        {
            if (generation == Generation)
            {
                database.SaveMessages(conversationId, saved);
            }
        });
    }

    // Keeps The Newest Encryption Line Of These Messages When It Is Newer Than The One Known, And Queues Its Save (The
    // Database Keeps Only The Newer Too). Dropped When A Reset Came After The Generation Was Taken
    private void NoteEncryption(string conversationId, IEnumerable<ChatMessage> messages, int generation)
    {
        ChatMessage? newest = messages.Where(m => m.EncryptionChange is not null).MaxBy(m => m.Time);
        if (newest is null || conversationId.Length == 0 || generation != Generation)
        {
            return;
        }

        // Under The Reset Lock, So Reset Can't Land Between The Check And The Write (As In LoadSavedAsync)
        (bool Encrypted, long At) state = (newest.EncryptionChange == true, newest.Time.ToUnixTimeMilliseconds() * 1000);
        lock (_resetLock)
        {
            if (generation != Generation)
            {
                return;
            }

            _encryption.AddOrUpdate(conversationId, state, (_, old) => state.At > old.At ? state : old);
        }

        _ = EnqueueAsync(database =>
        {
            if (generation == Generation)
            {
                database.SaveEncryption(conversationId, state.Encrypted, state.At);
            }
        });
    }
}

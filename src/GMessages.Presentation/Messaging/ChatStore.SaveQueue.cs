using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds the database side: the background save queue, the drop of cached pages, and the saves that the queue runs.
/// </summary>
/// <remarks>
/// This part of <see cref="ChatStore"/> holds the database side. ChatStore.cs holds the other members. The queue
/// exists because a locked file can block a call.
/// </remarks>
public sealed partial class ChatStore
{
    private int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Drops the cached message pages when the window closes, after the queue writes the saves. The next chat open
    /// reads them back from the database.
    /// </summary>
    /// <remarks>
    /// A page that holds a pending send with an empty ID, or a failed send, stays. Those messages are only in memory.
    /// This method never throws. It logs a failure by type.
    /// </remarks>
    /// <returns>A task that completes when the pages are dropped.</returns>
    public async Task DropPagesAsync()
    {
        // Without a database, nothing can bring the pages back.
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
    /// Waits until the queue writes all saves queued so far. The app calls it on quit with its own timeout, and tests
    /// also call it.
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
    /// Runs a database call on the background queue, after all calls queued before it. Saves and tests use it.
    /// </summary>
    /// <remarks>Without a database, nothing runs. A failure logs its error type only.</remarks>
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

    // A user waits for this read, such as a chat open or the quote of a reply. It runs at once on the thread pool and
    // does not wait behind the queued saves. After a catch-up or a resync, the queue can hold hundreds of saves, each
    // in its own transaction. In the queue, a chat open waits for all of them before its saved page shows. The lock
    // of the database still lets 1 call in at a time. Thus the read waits for 1 save at most. The generation check
    // skips a read just after a reset.
    // ponytail: the check and the read are not atomic. A read that races a Reset can make an empty file again after
    // the delete. That does no harm, because the saves of the next account use the file. Add a reset gate in
    // MessageDatabase if it becomes a problem.
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

    // Each queued save below does nothing when a Reset came after the store took the generation.
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

    // Queues a write to 1 saved conversation, such as a MessageDatabase.UpdateConversation change or its delete.
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

    // Keeps the newest encryption line of these messages when it is newer than the known line, and queues its save.
    // The database also keeps only the newer line.
    private void NoteEncryption(string conversationId, IEnumerable<ChatMessage> messages, int generation)
    {
        ChatMessage? newest = messages.Where(m => m.EncryptionChange is not null).MaxBy(m => m.Time);
        if (newest is null || conversationId.Length == 0 || generation != Generation)
        {
            return;
        }

        // The reset lock keeps a Reset out between the check and the write, as in LoadSavedAsync.
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

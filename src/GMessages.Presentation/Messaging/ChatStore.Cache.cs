using System.Diagnostics;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds the memory caches: slim conversation records, the newest page of recent chats, and the ring of notified IDs.
/// </summary>
/// <remarks>
/// This part of <see cref="ChatStore"/> holds the memory caches. ChatStore.cs holds the other members.
/// </remarks>
public sealed partial class ChatStore
{
    // Caches and saves a protocol conversation. It raises ConversationChanged when the cache changed.
    private ConversationInfo CacheConversation(Conversation c, int generation)
    {
        ConversationInfo info = WithSnippetMedia(ConversationInfo.From(c));
        SaveConversation(c, generation);
        if (Upsert(info, live: false))
        {
            ConversationChanged?.Invoke(this, info);
        }

        return info;
    }

    // The phone sends no snippet text for a photo, video, voice message or file with no caption. The label comes from
    // the cached copy of the same message. Else it comes from that message seen live, or from its page in memory.
    // This never waits for the database, so events stay in order and the list never waits for queued saves.
    // LoadSavedAsync names the saved copies at start-up.
    private ConversationInfo WithSnippetMedia(ConversationInfo info)
    {
        if (info.Snippet.Length > 0 || info.SnippetMedia.Length > 0 || info.LatestMessageId.Length == 0)
        {
            return info;
        }

        if (_cache.GetValueOrDefault(info.Id) is { SnippetMedia.Length: > 0 } old && old.LatestMessageId == info.LatestMessageId)
        {
            return info with { SnippetMedia = old.SnippetMedia };
        }

        if (_liveMedia.TryGetValue(info.Id, out (string MessageId, string Label) live) && live.MessageId == info.LatestMessageId)
        {
            return info with { SnippetMedia = live.Label };
        }

        MediaPart? media;
        lock (_pagesLock)
        {
            media = _pages.TryGetValue(info.Id, out List<ChatMessage>? page) ? page.FirstOrDefault(m => m.Id == info.LatestMessageId)?.Media : null;
        }

        return media is null ? info : info with { SnippetMedia = ChatFormat.MediaLabel(media) };
    }

    // True when the conversation differs from the cached copy, with the participants compared by value. A live change
    // gets a timestamp. Thus a list page asked for before the change does not put the older copy back.
    private bool Upsert(ConversationInfo info, bool live = true)
    {
        _me ??= info.Participants.FirstOrDefault(p => p.IsMe);
        if (live)
        {
            _liveAt[info.Id] = Stopwatch.GetTimestamp();
        }

        ConversationInfo? old = _cache.GetValueOrDefault(info.Id);
        _cache[info.Id]       = info;

        return old is null
            || old with { Participants = info.Participants } != info
            || !old.Participants.SequenceEqual(info.Participants);
    }

    // The page becomes the most recently used, and the least recently used page past 20 goes. Some messages arrived
    // while the page was on its way. They stay below the page when they are newer than all of it and not in it. The
    // generation check runs under the lock, so a page cannot land after Reset cleared the pages.
    private void CachePage(string conversationId, List<ChatMessage> page, bool onlyIfAbsent, int generation)
    {
        lock (_pagesLock)
        {
            if (generation != Generation || (onlyIfAbsent && _pages.ContainsKey(conversationId)))
            {
                return;
            }

            List<ChatMessage> merged = [.. page];
            if (_pages.Remove(conversationId, out List<ChatMessage>? old) && page.Count > 0)
            {
                DateTimeOffset newest = page.Max(m => m.Time);
                merged.AddRange(old.Where(m => m.Time > newest && !page.Any(p => SameMessage(p, m))));
                if (merged.Count > PageSize)
                {
                    merged.RemoveRange(0, merged.Count - PageSize);
                }
            }

            _pages.Add(conversationId, merged);
            if (_pages.Count > CachedChatCount)
            {
                _pages.RemoveAt(0);
            }
        }
    }

    // Only a conversation that the user opened before has a cached page. The message replaces the copy with its ID,
    // or the pending copy with its temporary ID. It goes in by time. The page keeps its newest 25 messages.
    private void CacheMessage(ChatMessage message)
    {
        lock (_pagesLock)
        {
            if (!_pages.TryGetValue(message.ConversationId, out List<ChatMessage>? page))
            {
                return;
            }

            // A pending copy never replaces the delivered copy, because the echo can arrive before the send call
            // returns.
            if (message.Id.Length == 0 && page.Any(m => m.Id.Length > 0 && SameMessage(m, message)))
            {
                return;
            }

            page.RemoveAll(m => SameMessage(m, message));

            int at = page.FindLastIndex(m => m.Time <= message.Time) + 1;
            page.Insert(at, message);
            if (page.Count > PageSize)
            {
                page.RemoveAt(0);
            }
        }
    }

    // Each SIM has its own participant ID for me. Thus the check also uses the IDs of the chat, as the bubble view
    // does.
    private bool HasMyReaction(ChatMessage message)
    {
        ConversationInfo? conversation = Find(message.ConversationId);
        return message.Reactions.Any(r => r.ParticipantIds.Any(id =>
            (id.Length > 0 && id == _me?.Id) || conversation?.IsMine(id) == true));
    }

    private bool IsCached(ChatMessage message)
    {
        lock (_pagesLock)
        {
            return message.Id.Length > 0
                && (_droppedIds.Contains(message.Id)
                    || (_pages.TryGetValue(message.ConversationId, out List<ChatMessage>? page)
                        && page.Exists(m => m.Id == message.Id)));
        }
    }

    // A pending copy and its echo share the temporary ID.
    private static bool SameMessage(ChatMessage a, ChatMessage b) =>
        (a.Id.Length > 0 && a.Id == b.Id) || (a.TmpId.Length > 0 && a.TmpId == b.TmpId);

    // True the first time the store sees the message ID.
    private bool MarkNotified(string messageId)
    {
        lock (_ringLock)
        {
            if (!_ringSet.Add(messageId))
            {
                return false;
            }

            _ring.Enqueue(messageId);
            if (_ring.Count > NotifiedRingSize)
            {
                _ringSet.Remove(_ring.Dequeue());
            }

            return true;
        }
    }
}

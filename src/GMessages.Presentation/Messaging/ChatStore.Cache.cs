using System.Diagnostics;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// The memory caches: slim conversation records, the newest page of recent chats, and the ring of notified message IDs.
/// </summary>
/// <remarks>
/// Part of <see cref="ChatStore"/>; the other members are in ChatStore.cs.
/// </remarks>
public sealed partial class ChatStore
{

    // =========================================================================
    // CACHE
    // =========================================================================

    // Caches And Saves A Protocol Conversation, Raising ConversationChanged When The Cache Changed
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

    // Names The Newest Message's Media When The Phone Sent No Snippet Text (A Photo, Video, Voice Message Or File With
    // No Caption): Kept From The Cached Copy Of The Same Message, Else From That Message Seen Live, Else From Its Page In
    // Memory. Never Waits (No Database Here): Events Stay In Order, And The List Never Waits Behind Queued Saves. The
    // Saved Copies Are Named At Start-Up (LoadSavedAsync)
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

    // Stores A Conversation; True When It Differs From What Was Cached (Participants Compared By Value). A Live Change
    // (Not A Fetched Copy) Is Timed, So A List Page Asked For Before It Doesn't Put The Older Copy Back (LoadFolderAsync)
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

    // Keeps A Conversation's Newest Page As The Most Recently Used, Dropping The Least Recently Used Past 20. Messages
    // Cached While The Page Was On Its Way (Newer Than Anything In It, And Not In It) Stay Below It. The Generation Is
    // Checked Under The Lock, So A Page Can't Land After Reset Cleared The Pages
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

    // Puts An Arrived Or Sent Message Into Its Cached Page (Only Conversations Opened Before Have One): It Replaces The
    // Copy With Its ID, Or Its Pending Copy By Temporary ID, And Goes In By Time. The Page Keeps Its Newest 25
    private void CacheMessage(ChatMessage message)
    {
        lock (_pagesLock)
        {
            if (!_pages.TryGetValue(message.ConversationId, out List<ChatMessage>? page))
            {
                return;
            }

            // A Pending Copy Never Replaces The Delivered One: The Echo Can Arrive Before The Send Call Returns
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

    // True When One Of The Message's Reactions Is Mine (My Participant ID Is Known Once Any Conversation Has Loaded)
    private bool HasMyReaction(ChatMessage message) =>
        _me is { } me && message.Reactions.Any(r => r.ParticipantIds.Contains(me.Id));

    // True When A Message With This ID Is Already In Its Conversation's Cached Page
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

    // The Same Message: The Same ID, Or The Same Temporary ID (A Pending Copy And Its Echo)
    private static bool SameMessage(ChatMessage a, ChatMessage b) =>
        (a.Id.Length > 0 && a.Id == b.Id) || (a.TmpId.Length > 0 && a.TmpId == b.TmpId);

    // Remembers A Message ID; True The First Time It Is Seen
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

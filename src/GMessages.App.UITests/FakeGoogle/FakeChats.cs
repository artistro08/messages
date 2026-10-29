using GMessages.Protocol.Proto;

namespace GMessages.App.UITests.FakeGoogle;

/// <summary>
/// The conversations, messages and contacts of the <see cref="FakePhone"/>.
/// </summary>
/// <remarks>
/// Starts with three conversations: <c>c-ann</c> (Ann Lee, one on one, three messages, unread), <c>c-team</c> (Team, a
/// group with Bo and Cy, pinned) and <c>c-spam</c> (Prize Bot, in the spam folder), plus the contacts Ann, Bo and Cy.
/// Use <see cref="Clear"/>, <see cref="Add"/> and <see cref="AddContact"/> to seed something else. Every conversation
/// has the phone's own participant (<see cref="MyParticipantId"/>, <c>IsMe</c>) and that ID as its default outgoing
/// ID. Timestamps are microseconds since the epoch. Safe to use from any thread; everything handed out is a copy.
/// </remarks>
public sealed class FakeChats
{
    /// <summary>The phone's own participant ID, also the ID of its one SIM.</summary>
    public const string MyParticipantId = "me-1";

    private readonly Lock _lock = new();
    private readonly List<Conversation> _conversations = [];
    private readonly Dictionary<string, List<Message>> _messages = [];
    private readonly List<Contact> _contacts = [];
    private long _clock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    private int _nextId;

    /// <summary>
    /// Creates the store with the default conversations and contacts.
    /// </summary>
    public FakeChats()
    {
        AddContact("p-ann", "Ann Lee", "+15550000001");
        AddContact("p-bo", "Bo", "+15550000002");
        AddContact("p-cy", "Cy", "+15550000003");

        // Ann: Three Messages, The Last One Unread
        Conversation ann = NewConversation("c-ann", "Ann Lee", false, ConversationStatus.Active, false, true, Person("p-ann", "Ann Lee", "+15550000001"));
        Add(ann, NewMessage(ann, "p-ann", "Hi, it's Ann"), NewMessage(ann, MyParticipantId, "Hey Ann"), NewMessage(ann, "p-ann", "Are you free later?"));

        // Team: A Pinned Group
        Conversation team = NewConversation("c-team", "Team", true, ConversationStatus.Active, true, false, Person("p-bo", "Bo", "+15550000002"), Person("p-cy", "Cy", "+15550000003"));
        Add(team, NewMessage(team, "p-bo", "Lunch at noon?"), NewMessage(team, "p-cy", "Works for me"));

        // Spam: One Message From A Bot
        Conversation spam = NewConversation("c-spam", "Prize Bot", false, ConversationStatus.SpamFolder, false, false, Person("p-bot", "Prize Bot", "+15550000009"));
        Add(spam, NewMessage(spam, "p-bot", "You won a prize!"));
    }

    // =========================================================================
    // SEEDING
    // =========================================================================

    /// <summary>
    /// Removes every conversation, message and contact.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _conversations.Clear();
            _messages.Clear();
            _contacts.Clear();
        }
    }

    /// <summary>
    /// Adds a conversation with its messages, oldest first.
    /// </summary>
    /// <remarks>The conversation's snippet, latest message ID and last message time are taken from the last message.</remarks>
    /// <param name="conversation">The conversation.</param>
    /// <param name="messages">Its messages, oldest first.</param>
    public void Add(Conversation conversation, params Message[] messages)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        lock (_lock)
        {
            Conversation own = conversation.Clone();
            _conversations.Add(own);
            _messages[own.ConversationID] = [.. messages.Select(m => m.Clone())];
            if (messages.Length > 0)
            {
                Refresh(own, messages[^1]);
            }
        }
    }

    /// <summary>
    /// Adds a contact.
    /// </summary>
    /// <param name="participantId">The contact's participant ID.</param>
    /// <param name="name">The contact's name.</param>
    /// <param name="number">The contact's phone number.</param>
    public void AddContact(string participantId, string name, string number)
    {
        lock (_lock)
        {
            _contacts.Add(new Contact { ParticipantID = participantId, Name = name, NameAgain = name, ContactID = "ct-" + participantId, Number = new ContactNumber { Number = number } });
        }
    }

    /// <summary>
    /// Adds a long one-on-one chat with Bo, for tests that scroll through older pages.
    /// </summary>
    /// <remarks>
    /// The messages read <c>long 001</c> to <c>long {count}</c>, oldest first, one minute apart, the newest a minute
    /// ago. Like a real chat, Bo and the phone take turns in runs of one to three, and every fourth message carries a
    /// longer line after its number that wraps. Message IDs are <c>{id}-001</c> to <c>{id}-{count}</c>, so a test
    /// can find <c>Bubble.{id}-096</c>.
    /// </remarks>
    /// <param name="id">The conversation ID.</param>
    /// <param name="count">How many messages.</param>
    public void AddLongChat(string id = "c-history", int count = 120)
    {
        Conversation chat = NewConversation(id, "Long Chat", false, ConversationStatus.Active, false, false, Person("p-bo", "Bo", "+15550000002"));
        long start        = DateTimeOffset.UtcNow.AddMinutes(-count).ToUnixTimeMilliseconds() * 1000;
        Message[] texts   = [.. Enumerable.Range(1, count).Select(Line)];
        Add(chat, texts);

        Message Line(int i)
        {
            // Runs Of One To Three From Each Side; Every Fourth Message Wraps
            bool from_me = (i / 3 + i / 5) % 2 == 0;
            string text  = i % 4 == 0 ? $"long {i:000} " + string.Concat(Enumerable.Repeat("and a longer line that wraps ", 4)).TrimEnd() : $"long {i:000}";
            var message  = new Message
            {
                MessageID      = $"{id}-{i:000}",
                ConversationID = id,
                ParticipantID  = from_me ? MyParticipantId : "p-bo",
                Timestamp      = start + ((i - 1) * 60_000_000L),
                MessageStatus  = new MessageStatus { Status = from_me ? MessageStatusType.OutgoingComplete : MessageStatusType.IncomingComplete },
            };
            message.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
            return message;
        }
    }

    // =========================================================================
    // ANSWERS
    // =========================================================================

    // The Conversations Of A Folder, Newest First; Inbox = Active, Spam And Blocked = Spam Or Blocked Folder
    internal List<Conversation> List(ListConversationsRequest.Types.Folder folder)
    {
        lock (_lock)
        {
            return [.. _conversations
                .Where(c => folder switch
                {
                    ListConversationsRequest.Types.Folder.Inbox       => c.Status == ConversationStatus.Active,
                    ListConversationsRequest.Types.Folder.Archive     => c.Status == ConversationStatus.Archived,
                    ListConversationsRequest.Types.Folder.SpamBlocked => c.Status is ConversationStatus.SpamFolder or ConversationStatus.BlockedFolder,
                    _                                                 => false,
                })
                .OrderByDescending(c => c.LastMessageTimestamp)
                .Select(c => c.Clone())];
        }
    }

    internal Conversation? Get(string conversationId)
    {
        lock (_lock)
        {
            return _conversations.FirstOrDefault(c => c.ConversationID == conversationId)?.Clone();
        }
    }

    // One Page Of Messages, Newest First, After The Cursor. Like Google, It Sends No Cursor Back: The App Must Page On
    // From The Oldest Message It Got, And An Empty Page Is The End
    internal (List<Message> Page, Cursor? Next) Page(string conversationId, long count, Cursor? cursor)
    {
        lock (_lock)
        {
            IEnumerable<Message> newest_first = _messages.TryGetValue(conversationId, out List<Message>? all) ? all.AsEnumerable().Reverse() : [];
            if (cursor is { LastItemID.Length: > 0 })
            {
                newest_first = newest_first.SkipWhile(m => m.MessageID != cursor.LastItemID).Skip(1);
            }

            List<Message> rest = [.. newest_first];
            List<Message> page = count > 0 ? [.. rest.Take((int)count)] : rest;
            return ([.. page.Select(m => m.Clone())], null);
        }
    }

    internal List<Contact> Contacts()
    {
        lock (_lock)
        {
            return [.. _contacts.Select(c => c.Clone())];
        }
    }

    // =========================================================================
    // CHANGES
    // =========================================================================

    // The Phone's Own Message, Sent From The App
    internal Message AddOutgoing(string conversationId, string text, string tmpId)
    {
        lock (_lock)
        {
            Conversation conversation = Find(conversationId);
            Message message           = NewMessage(conversation, MyParticipantId, text, tmpId);
            _messages[conversationId].Add(message);
            Refresh(conversation, message);
            return message.Clone();
        }
    }

    // A Message From The Other Side, Which Leaves The Conversation Unread
    internal (Conversation Conversation, Message Message) AddIncoming(string conversationId, string text)
    {
        lock (_lock)
        {
            Conversation conversation = Find(conversationId);
            string sender             = conversation.OtherParticipants.FirstOrDefault() ?? "p-unknown";
            Message message           = NewMessage(conversation, sender, text);
            _messages[conversationId].Add(message);
            Refresh(conversation, message);
            conversation.Unread = true;
            return (conversation.Clone(), message.Clone());
        }
    }

    internal void MarkRead(string conversationId)
    {
        lock (_lock)
        {
            _conversations.FirstOrDefault(c => c.ConversationID == conversationId)?.Unread = false;
        }
    }

    // The One Conversation With Exactly These Numbers, Or A New Active One
    internal Conversation FindOrCreate(IReadOnlyList<ContactNumber> numbers)
    {
        lock (_lock)
        {
            string[] wanted = [.. numbers.Select(n => n.Number).Order()];
            Conversation? found = _conversations.FirstOrDefault(c =>
                c.Participants.Where(p => !p.IsMe).Select(p => p.ID.Number).Order().SequenceEqual(wanted));
            if (found is not null)
            {
                return found.Clone();
            }

            string number_text = string.Join(", ", wanted);
            string id          = "c-new-" + ++_nextId;
            Conversation fresh = NewConversation(id, number_text, wanted.Length > 1, ConversationStatus.Active, false, false, [.. wanted.Select(n => Person("p-" + n, n, n))]);
            _conversations.Add(fresh);
            _messages[id] = [];
            return fresh.Clone();
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private Conversation Find(string conversationId) =>
        _conversations.FirstOrDefault(c => c.ConversationID == conversationId) ?? throw new KeyNotFoundException($"No conversation {conversationId}.");

    internal static Participant Person(string id, string name, string number) => new()
    {
        ID        = new SmallInfo { Type = IdentifierType.Phone, Number = number, ParticipantID = id },
        FullName  = name,
        FirstName = name.Split(' ')[0],
        IsVisible = true,
    };

    internal static Conversation NewConversation(string id, string name, bool group, ConversationStatus status, bool pinned, bool unread, params Participant[] others)
    {
        var conversation = new Conversation
        {
            ConversationID    = id,
            Name              = name,
            IsGroupChat       = group,
            Status            = status,
            Pinned            = pinned,
            Unread            = unread,
            DefaultOutgoingID = MyParticipantId,
            Type              = ConversationType.Rcs,
        };
        conversation.Participants.Add(new Participant { ID = new SmallInfo { Type = IdentifierType.Phone, Number = "+15550000000", ParticipantID = MyParticipantId }, FullName = "Me", IsMe = true, IsVisible = true });
        conversation.Participants.AddRange(others);
        conversation.OtherParticipants.AddRange(others.Select(p => p.ID.ParticipantID));
        return conversation;
    }

    // A Message A Little Later Than The Last One; From Me It Is Outgoing, Otherwise Incoming
    private Message NewMessage(Conversation conversation, string participantId, string text, string tmpId = "")
    {
        bool from_me = participantId == MyParticipantId;
        var message  = new Message
        {
            MessageID      = "m-" + ++_nextId,
            ConversationID = conversation.ConversationID,
            ParticipantID  = participantId,
            Timestamp      = _clock += 1_000_000,
            TmpID          = tmpId,
            MessageStatus  = new MessageStatus { Status = from_me ? MessageStatusType.OutgoingComplete : MessageStatusType.IncomingComplete },
        };
        message.MessageInfo.Add(new MessageInfo { MessageContent = new MessageContent { Content = text } });
        return message;
    }

    // Snippet, Latest Message And Time Follow The Newest Message
    private static void Refresh(Conversation conversation, Message latest)
    {
        string text = latest.MessageInfo.FirstOrDefault()?.MessageContent?.Content ?? "";
        conversation.LatestMessage        = new LatestMessage { DisplayContent = text, FromMe = latest.ParticipantID == MyParticipantId ? 1 : 0 };
        conversation.LatestMessageID      = latest.MessageID;
        conversation.LastMessageTimestamp = latest.Timestamp;
    }
}

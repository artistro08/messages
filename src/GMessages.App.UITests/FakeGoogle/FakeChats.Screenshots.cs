using GMessages.Protocol.Proto;

namespace GMessages.App.UITests.FakeGoogle;

/// <summary>
/// The mock phone the store screenshots show (see <c>StoreScreenshots</c>).
/// </summary>
public sealed partial class FakeChats
{
    /// <summary>
    /// Replaces everything with a lifelike phone: ten conversations over the last week, with photos, a reply, reactions
    /// and read receipts, for the store screenshots.
    /// </summary>
    /// <remarks>
    /// Today's messages are minutes before now, older ones at set times on earlier days. The open chats are
    /// <c>c-maya</c> (Maya Chen, pinned, end-to-end encrypted) and <c>c-family</c> (a group with Mom, Dad and Jordan).
    /// Photos come in as JPEGs with their sizes, as a phone reports them.
    /// </remarks>
    /// <param name="photos">The photos by name (<c>farm</c>, <c>sprout</c>, <c>city</c>, <c>statue</c>): JPEG bytes and size.</param>
    public void AddScreenshotChats(IReadOnlyDictionary<string, (byte[] Jpeg, int Width, int Height)> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);

        Clear();
        DateTimeOffset now = DateTimeOffset.Now;

        // People
        Participant maya   = Person("p-maya", "Maya Chen", "+15550100101");
        Participant mom    = Person("p-mom", "Mom", "+15550100102");
        Participant dad    = Person("p-dad", "Dad", "+15550100103");
        Participant jordan = Person("p-jordan", "Jordan Ellis", "+15550100104");
        Participant alex   = Person("p-alex", "Alex Rivera", "+15550100105");
        Participant priya  = Person("p-priya", "Priya Patel", "+15550100106");
        Participant sam    = Person("p-sam", "Sam Okafor", "+15550100107");
        Participant elena  = Person("p-elena", "Elena Rossi", "+15550100108");
        Participant dental = Person("p-dental", "Riverside Dental", "+15550100109");
        foreach (Participant person in (Participant[])[maya, mom, dad, jordan, alex, priya, sam, elena])
        {
            AddContact(person.ID.ParticipantID, person.FullName, person.ID.Number);
        }

        // Maya: Weekend Plans, A Photo From Her Garden, A Reply, Reactions, Read
        Conversation mayaChat = NewConversation("c-maya", "Maya Chen", false, ConversationStatus.Active, true, false, maya);
        Message coffee        = Text(mayaChat, "p-maya", "Perfect. I'll bring coffee 🙌", Day(1, 18, 44));
        Message sprout        = Photo(mayaChat, "p-maya", photos["sprout"], "sprout.jpg", Ago(now, 41));
        sprout.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Love }, ParticipantIDs = { MyParticipantId } });
        Message basil         = Text(mayaChat, MyParticipantId, "No way, the basil finally came up!", Ago(now, 38));
        Message thermos       = Text(mayaChat, MyParticipantId, "Bring the big thermos please, it's going to be cold up there", Ago(now, 3));
        thermos.ReplyMessage  = new ReplyMessage { MessageID = coffee.MessageID, ConversationID = mayaChat.ConversationID };
        thermos.MessageStatus = new MessageStatus { Status = MessageStatusType.OutgoingDisplayed };
        Message laugh         = Text(mayaChat, "p-maya", "Took long enough 😂", Ago(now, 36));
        laugh.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Laugh }, ParticipantIDs = { MyParticipantId } });
        Add(mayaChat,
            NewTombstone(mayaChat, MessageStatusType.TombstoneEncryptedOneOnOneRcsCreated, Day(1, 18, 30)),
            Text(mayaChat, "p-maya", "Are we still on for the hike Saturday?", Day(1, 18, 40)),
            Text(mayaChat, MyParticipantId, "Yes! Trailhead at 8?", Day(1, 18, 42)),
            coffee,
            Text(mayaChat, "p-maya", "Look what came up in the garden", Ago(now, 42)),
            sprout,
            basil,
            laugh,
            Text(mayaChat, "p-maya", "I'll pick you up at 7:30", Ago(now, 5)),
            thermos);

        // Family: A Group With Dad's Photo From The Farm
        Conversation family = NewConversation("c-family", "Family", true, ConversationStatus.Active, true, false, mom, dad, jordan);
        Message farm        = Photo(family, "p-dad", photos["farm"], "farm.jpg", Ago(now, 70));
        farm.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Love }, ParticipantIDs = { "p-mom", "p-jordan" } });
        Message sweetCorn   = Text(family, "p-jordan", "Save me some sweet corn!", Ago(now, 64));
        sweetCorn.Reactions.Add(new ReactionEntry { Data = new ReactionData { Type = EmojiType.Laugh }, ParticipantIDs = { "p-mom" } });
        Add(family,
            Text(family, "p-dad", "Harvest is in full swing at Uncle Ray's", Ago(now, 71)),
            farm,
            Text(family, "p-mom", "That truck is older than all of you", Ago(now, 66)),
            sweetCorn,
            Text(family, MyParticipantId, "Sunday dinner at the farm?", Ago(now, 30)),
            Text(family, "p-mom", "Already planning it ❤️", Ago(now, 25)));

        // Jordan: Unread, A Photo From The Trip
        Conversation jordanChat = NewConversation("c-jordan", "Jordan Ellis", false, ConversationStatus.Active, false, true, jordan);
        Add(jordanChat,
            Text(jordanChat, MyParticipantId, "Have the best time in New York!", Day(2, 20, 15)),
            Photo(jordanChat, "p-jordan", photos["statue"], "statue.jpg", Ago(now, 95)),
            Text(jordanChat, "p-jordan", "Made it! She's even bigger in person", Ago(now, 94)));

        // Alex: A Photo From The Walk To Work
        Conversation alexChat = NewConversation("c-alex", "Alex Rivera", false, ConversationStatus.Active, false, false, alex);
        Add(alexChat,
            Photo(alexChat, "p-alex", photos["city"], "city.jpg", Ago(now, 180)),
            Text(alexChat, "p-alex", "The light downtown this morning was unreal", Ago(now, 179)),
            Text(alexChat, MyParticipantId, "That's a wallpaper right there", Ago(now, 150)),
            Text(alexChat, "p-alex", "Grabbing lunch near the office, want anything?", Ago(now, 140)));

        // Book Club: A Group, Yesterday
        Conversation bookClub = NewConversation("c-books", "Book Club", true, ConversationStatus.Active, false, false, priya, sam, elena);
        Add(bookClub,
            Text(bookClub, "p-elena", "Did everyone finish chapter 12?", Day(1, 20, 2)),
            Text(bookClub, "p-sam", "Chapter 12 had me shook", Day(1, 20, 9)));

        // The Rest: One Line Each, Over The Week
        Conversation priyaChat = NewConversation("c-priya", "Priya Patel", false, ConversationStatus.Active, false, false, priya);
        Add(priyaChat, Text(priyaChat, "p-priya", "Sending the slides over tonight", Day(1, 16, 30)));

        Conversation dentalChat = NewConversation("c-dental", "Riverside Dental", false, ConversationStatus.Active, false, false, dental);
        dentalChat.Type         = ConversationType.Sms;
        Add(dentalChat, Text(dentalChat, "p-dental", "Reminder: your cleaning is Thursday at 2:30 PM. Reply C to confirm.", Day(3, 9, 0)));

        Conversation samChat = NewConversation("c-sam", "Sam Okafor", false, ConversationStatus.Active, false, false, sam);
        Add(samChat, Text(samChat, MyParticipantId, "Good game last night 🏀", Day(4, 10, 12)));

        Conversation elenaChat = NewConversation("c-elena", "Elena Rossi", false, ConversationStatus.Active, false, false, elena);
        Add(elenaChat, Text(elenaChat, "p-elena", "Thanks for the recommendation! Booked a table for Friday", Day(5, 19, 45)));

        Conversation codeChat = NewConversation("c-code", "+1 555-010-0110", false, ConversationStatus.Active, false, false, Person("p-code", "+1 555-010-0110", "+15550100110"));
        codeChat.Type         = ConversationType.Sms;
        Add(codeChat, Text(codeChat, "p-code", "Your package was delivered to the front door.", Day(6, 13, 20)));
    }

    // =========================================================================
    // SCREENSHOT HELPERS
    // =========================================================================

    // A Time Some Minutes Before Now, Or At A Set Time Some Days Ago, In Microseconds Since The Epoch
    private static long Ago(DateTimeOffset now, int minutes) => now.AddMinutes(-minutes).ToUnixTimeMilliseconds() * 1000;

    private static long Day(int daysAgo, int hour, int minute) =>
        new DateTimeOffset(DateTime.Today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute)).ToUnixTimeMilliseconds() * 1000;

    // A Text At A Given Time; From Me It Is Delivered, Otherwise Received
    private Message Text(Conversation conversation, string participantId, string text, long at)
    {
        Message message   = NewMessage(conversation, participantId, text);
        message.Timestamp = at;
        if (participantId == MyParticipantId)
        {
            message.MessageStatus = new MessageStatus { Status = MessageStatusType.OutgoingDelivered };
        }

        return message;
    }

    // A Photo At A Given Time, With Its Size
    private Message Photo(Conversation conversation, string participantId, (byte[] Jpeg, int Width, int Height) photo, string fileName, long at)
    {
        MediaContent content = StoreMedia(photo.Jpeg, fileName, "image/jpeg");
        content.Dimensions   = new Dimensions { Width = photo.Width, Height = photo.Height };
        Message message      = NewMediaMessage(conversation, participantId, content);
        message.Timestamp    = at;
        return message;
    }

    private Message NewTombstone(Conversation conversation, MessageStatusType status, long at)
    {
        Message tombstone   = NewTombstone(conversation, status);
        tombstone.Timestamp = at;
        return tombstone;
    }
}

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Every piece of user-facing chat text, in one place.
/// </summary>
public static class ChatCopy
{
    // Spec Copy
    public const string StartChat      = "Start chat";
    public const string YouPrefix      = "You: ";
    public const string SpamAndBlocked = "Spam & blocked";
    public const string Archived       = "Archived";

    // Conversation Menu Items (Controller Copy; Owner Signs Off)
    public const string Archive            = "Archive";
    public const string Unarchive          = "Unarchive";
    public const string Mute               = "Mute";
    public const string Unmute             = "Unmute";
    public const string BlockAndReportSpam = "Block & report spam";
    public const string Unblock            = "Unblock";
    public const string MoveToTrash        = "Move to trash";
    public const string Details            = "Details";

    // Hover Buttons And Icons (Controller Copy; Owner Signs Off)
    public const string MoreOptions = "More options";
    public const string Muted       = "Muted";

    // Block Dialog; Its Title Is BlockAndReportSpam (Controller Copy; Owner Signs Off)
    public const string BlockText  = "You won't get messages from this conversation, and it moves to Spam & blocked.";
    public const string ReportSpam = "Report spam";
    public const string Block      = "Block";
    public const string Cancel     = "Cancel";

    // Trash Dialog; Its Button Is MoveToTrash (Controller Copy; Owner Signs Off)
    public const string MoveToTrashTitle = "Move to trash?";
    public const string MoveToTrashText  = "It moves to trash on your phone too.";

    // Details Dialog; Its Title Is Details, Its Toggle Mute And Its Button BlockAndReportSpam (Controller Copy; Owner Signs Off)
    public const string People = "People";
    public const string Close  = "Close";

    // Conversation Action Failure Banner (Controller Copy; Owner Signs Off)
    public const string ActionFailed = "Couldn't update this conversation. Try again.";

    // Phone Battery Banner (From The Owner's Screenshot; The Owner Confirms Google's Exact Text)
    public const string PhoneBatteryLow = "Phone battery low";

    // Message Status (Spec Copy)
    public const string Sent      = "Sent";
    public const string Delivered = "Delivered";
    public const string Read      = "Read";

    // Reactions, Replies, Encryption And Typing (Controller Copy; Owner Signs Off)
    public const string React             = "React";
    public const string Selected          = "Selected";
    public const string CancelReply       = "Cancel reply";
    public const string EndToEndEncrypted = "End-to-end encrypted";
    public const string Typing            = "Typing";
    public const string OriginalMessage   = "Original message";
    public const string Copy              = "Copy";

    // The Reaction Picker's Any-Emoji Button (Controller Copy, Plan 7; Owner Signs Off)
    public const string MoreReactions = "More reactions";

    // The Undo Bar After Archiving A Conversation (Controller Copy, Plan 7 Fixes; Owner Signs Off)
    public const string ConversationArchived = "Conversation archived";
    public const string Undo                 = "Undo";

    // The Emoji Picker More Reactions Opens: Its Name, Search Box, Empty Search And Categories (Controller Copy, Plan 7
    // Fixes; Owner Signs Off)
    public const string EmojiPicker      = "Emoji";
    public const string SearchEmoji      = "Search emoji";
    public const string NoEmojiFound     = "No emoji found";
    public const string EmojiSmileys     = "Smileys & emotion";
    public const string EmojiPeople      = "People & gestures";
    public const string EmojiHearts      = "Hearts";
    public const string EmojiAnimals     = "Animals & nature";
    public const string EmojiFood        = "Food & drink";
    public const string EmojiActivities  = "Activities";
    public const string EmojiTravel      = "Travel & places";
    public const string EmojiObjects     = "Objects";
    public const string EmojiSymbols     = "Symbols";

    // Link Preview Card Menu (Controller Copy; Owner Signs Off)
    public const string CopyLink = "Copy link";

    // Who Reacted, In Groups: My Own Row (Controller Copy; Owner Signs Off)
    public const string You = "You";

    // A Bubble's Accessible Item Type, RCS Or SMS (Controller Copy; Owner Signs Off)
    public const string ChatMessage = "Chat message";
    public const string TextMessage = "Text message";

    // Attachment Labels
    public const string Photo              = "Photo";
    public const string Video              = "Video";
    public const string VoiceMessage       = "Voice message";
    public const string File               = "File";
    public const string UnsupportedMessage = "Unsupported message";

    // Send Failures
    public const string NotSentRetry = "Not sent. Click to try again.";
    public const string ReplyNotSent = "Your reply wasn't sent. Open the chat to try again.";

    // Connection Banners
    public const string Connecting      = "Connecting…";
    public const string WaitingForPhone = "Waiting for your phone…";

    // Snooze Choices
    public const string Minutes5  = "5 minutes";
    public const string Minutes15 = "15 minutes";
    public const string Hour1     = "1 hour";
    public const string Hours4    = "4 hours";

    // Snooze (Toast Button And Its Settings Card; Owner Signs Off)
    public const string Snooze                  = "Snooze";
    public const string SnoozeLength            = "Snooze length";
    public const string SnoozeLengthDescription = "How long Snooze on a notification waits before showing it again.";

    // Microphone (Settings Card; Owner Signs Off)
    public const string Microphone            = "Microphone";
    public const string MicrophoneDescription = "Which microphone records voice messages.";
    public const string WindowsDefault        = "Windows default";

    // Notifications And New Chat
    public const string Reply              = "Reply";
    public const string Send               = "Send";
    public const string StartAConversation = "Start a conversation";
    public const string TypeNameOrNumber   = "Type a name or phone number";
    public const string SendToFormat       = "Send to {0}";

    // Start Chat's Go Button (Owner Copy, Verbatim)
    public const string StartChatGo = "Start Chat";

    // Start Chat Failure (R11; Controller Copy, The Owner May Edit It)
    public const string StartFailed = "Couldn't start this conversation. Try again.";

    // Media In The Chat (Plan 6; Controller Copy, The Owner Signs Off At The Gate)
    public const string Open            = "Open";
    public const string SaveAs          = "Save as…";
    public const string Downloading     = "Downloading…";
    public const string PhotoLoadFailed = "Couldn't load this photo.";
    public const string FileOpenFailed  = "Couldn't open this file.";
    public const string PhotoPending    = "Waiting for the photo from your phone…";

    // Photo Viewer And Download (Plan 7; Controller Copy, The Owner Signs Off At The Gate)
    public const string Download         = "Download";
    public const string SavedToDownloads = "Saved to Downloads";
    public const string ShowInFolder     = "Show in folder";
    public const string OpenInPhotos     = "Open in Photos";

    // Videos (Plan 7; Controller Copy, The Owner Signs Off At The Gate)
    public const string PlayVideo        = "Play video";
    public const string OpenVideo        = "Open video";
    public const string OpenInDefaultApp = "Open in default app";
    public const string VideoPlayFailed  = "Couldn't play this video.";

    // Reply Quote Labels For Media (Plan 7; Controller Copy, The Owner Signs Off At The Gate)
    public const string QuoteImage = "Image";
    public const string QuoteVideo = "Video";
    public const string QuoteGif   = "GIF";

    // A Reply Quote's Jump To The Quoted Message, Its Accessible Name (Controller Copy, Batch F; Owner Signs Off)
    public const string GoToOriginalMessage = "Go to original message";

    // Attaching (Plan 6; Controller Copy, The Owner Signs Off At The Gate)
    public const string Attach           = "Attach";
    public const string AttachPhoto      = "Attach photo";
    public const string RemoveAttachment = "Remove attachment";
    public const string AttachSendFailed = "Couldn't send this attachment. Try again.";

    // Attachment Limits (Owner Copy, Verbatim)
    public const string AttachTooBig      = "Your attachment is too big (100 MB max).";
    public const string AttachTotalTooBig = "Your attachment is too big (200 MB max).";

    // Voice Messages (Plan 6; Controller Copy, The Owner Signs Off At The Gate)
    public const string RecordVoiceMessage = "Record voice message";
    public const string Done               = "Done";
    public const string CancelRecording    = "Cancel recording";
    public const string Play               = "Play";
    public const string Pause              = "Pause";
    public const string Resume             = "Resume";
    public const string RecordingFailed    = "Couldn't save this recording. Try again.";

    // Microphone Dialog (Controller Copy, The Owner Signs Off)
    public const string MicrophoneTitle = "Use your microphone?";
    public const string MicrophoneText  = "Messages needs your microphone to record voice messages. Turn it on for Messages in Windows Settings, then try again.";
    public const string OpenSettings    = "Open settings";

    // Startup
    public const string StartupRefused = "You'll have to manually enable startup. Turn it on in Settings > Apps > Startup.";
}

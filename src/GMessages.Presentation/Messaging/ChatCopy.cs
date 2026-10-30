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

    // Notifications And New Chat
    public const string Reply              = "Reply";
    public const string Send               = "Send";
    public const string StartAConversation = "Start a conversation";
    public const string TypeNameOrNumber   = "Type a name or phone number";
    public const string SendToFormat       = "Send to {0}";

    // Start Chat Failure (R11; Controller Copy, The Owner May Edit It)
    public const string StartFailed = "Couldn't start this conversation. Try again.";

    // Media In The Chat (Plan 6; Controller Copy, The Owner Signs Off At The Gate)
    public const string Open            = "Open";
    public const string SaveAs          = "Save as…";
    public const string Downloading     = "Downloading…";
    public const string PhotoLoadFailed = "Couldn't load this photo.";
    public const string FileOpenFailed  = "Couldn't open this file.";
    public const string PhotoPending    = "Waiting for the photo from your phone…";

    // Attaching (Plan 6; Controller Copy, The Owner Signs Off At The Gate)
    public const string Attach           = "Attach";
    public const string AttachPhoto      = "Attach photo";
    public const string RemoveAttachment = "Remove attachment";
    public const string AttachTooBig     = "This file is too big to send (100 MB max).";
    public const string AttachSendFailed = "Couldn't send this attachment. Try again.";

    // Startup
    public const string StartupRefused = "You'll have to manually enable startup. Turn it on in Settings > Apps > Startup.";
}

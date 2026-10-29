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

    // Message Status (Spec Copy)
    public const string Sent      = "Sent";
    public const string Delivered = "Delivered";
    public const string Read      = "Read";

    // Reactions, Replies, Encryption And Typing (Controller Copy; Owner Signs Off)
    public const string React             = "React";
    public const string CancelReply       = "Cancel reply";
    public const string EndToEndEncrypted = "End-to-end encrypted";
    public const string Typing            = "Typing";
    public const string OriginalMessage   = "Original message";

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

    // Startup
    public const string StartupRefused = "You'll have to manually enable startup. Turn it on in Settings > Apps > Startup.";
}

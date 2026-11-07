namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds every piece of user-facing chat text in 1 place.
/// </summary>
/// <remarks>
/// The copy is verbatim, and only the owner changes it. A comment above a group tells where its copy comes from.
/// </remarks>
public static class ChatCopy
{
    // The spec gives this copy.
    public const string StartChat      = "Start chat";
    public const string YouPrefix      = "You: ";
    public const string DraftPrefix    = "Draft: ";
    public const string SpamAndBlocked = "Spam & blocked";
    public const string Archived       = "Archived";

    // The controller wrote the items of the conversation menu. The owner approves them.
    public const string Archive            = "Archive";
    public const string Unarchive          = "Unarchive";
    public const string Mute               = "Mute";
    public const string Unmute             = "Unmute";
    public const string BlockAndReportSpam = "Block & report spam";
    public const string Unblock            = "Unblock";
    public const string MoveToTrash        = "Move to trash";
    public const string Details            = "Details";

    // The controller wrote the copy of the hover buttons and icons. The owner approves it.
    public const string MoreOptions = "More options";
    public const string Muted       = "Muted";

    // The controller wrote the copy of the block dialog. The owner approves it. The title of the dialog is
    // BlockAndReportSpam.
    public const string BlockText  = "You won't get messages from this conversation, and it moves to Spam & blocked.";
    public const string ReportSpam = "Report spam";
    public const string Block      = "Block";
    public const string Cancel     = "Cancel";

    // The controller wrote the copy of the trash dialog. The owner approves it. The button of the dialog is
    // MoveToTrash.
    public const string MoveToTrashTitle = "Move to trash?";
    public const string MoveToTrashText  = "It moves to trash on your phone too.";

    // The controller wrote the note of a toast that its one-time code is on the clipboard. The owner approves it.
    public const string CodeCopied = "Code copied";

    // The controller wrote the copy of the delete action of a message and its dialog. The owner approves it.
    public const string Delete             = "Delete";
    public const string DeleteMessageTitle = "Delete this message?";
    public const string DeleteMessageText  = "It's deleted from your phone too. Others in the chat keep their copy.";

    // The controller wrote the copy of the details dialog. The owner approves it. The title of the dialog is Details,
    // its toggle is Mute, and its button is BlockAndReportSpam.
    public const string People = "People";
    public const string Close  = "Close";

    // The controller wrote the banner for a failed conversation action. The owner approves it.
    public const string ActionFailed = "Couldn't update this conversation. Try again.";

    // This banner comes from a screenshot of the owner. The owner confirms the exact text of Google.
    public const string PhoneBatteryLow = "Phone battery low";

    // The spec gives the copy of the message status.
    public const string Sent      = "Sent";
    public const string Delivered = "Delivered";
    public const string Read      = "Read";

    // The controller wrote the copy for reactions, replies, encryption and typing. The owner approves it.
    public const string React             = "React";
    public const string Selected          = "Selected";
    public const string CancelReply       = "Cancel reply";
    public const string EndToEndEncrypted = "End-to-end encrypted";
    public const string Typing            = "Typing";
    public const string RcsMessage        = "RCS Message";
    public const string Sms               = "SMS";
    public const string TypingFormat      = "{0} is typing";
    public const string OriginalMessage   = "Original message";
    public const string Copy              = "Copy";

    // The controller wrote the name of the any-emoji button of the reaction picker in Plan 7. The owner approves it.
    public const string MoreReactions = "More reactions";

    // The controller wrote the undo bar after the archive of a conversation in the Plan 7 fixes. The owner approves it.
    public const string ConversationArchived = "Chat archived";
    public const string Undo                 = "Undo";

    // The controller wrote the copy of the emoji picker that More reactions opens, in the Plan 7 fixes. The owner
    // approves it. The copy is the name, the search box, the empty search and the categories.
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

    // The controller wrote the menu of the link preview card. The owner approves it.
    public const string CopyLink = "Copy link";

    // The controller wrote the label of my own row in the list of who reacted in a group. The owner approves it.
    public const string You = "You";

    // The controller wrote the accessible item type of a bubble, for RCS or SMS. The owner approves it.
    public const string ChatMessage = "Chat message";
    public const string TextMessage = "Text message";

    public const string Photo              = "Photo";
    public const string Video              = "Video";
    public const string VoiceMessage       = "Voice message";
    public const string File               = "File";
    public const string UnsupportedMessage = "Unsupported message";

    public const string NotSentRetry = "Not sent. Click to try again.";
    public const string ReplyNotSent = "Your reply wasn't sent. Open the chat to try again.";

    public const string Connecting      = "Connecting…";
    public const string WaitingForPhone = "Waiting for your phone…";

    public const string Minutes5  = "5 minutes";
    public const string Minutes15 = "15 minutes";
    public const string Hour1     = "1 hour";
    public const string Hours4    = "4 hours";

    // The controller wrote the copy of Snooze for the toast button and its settings card. The owner approves it.
    public const string Snooze                  = "Snooze";
    public const string SnoozeLength            = "Snooze length";
    public const string SnoozeLengthDescription = "How long Snooze on a notification waits before showing it again.";

    // The owner approved the settings card of the link previews.
    public const string ShowReceivedLinkPreviews = "Show previews for links you receive";

    // Narrator reads this name for the ☰ button of the title bar, and its tooltip shows it.
    public const string ShowOrHideConversations = "Show or hide conversations";

    public const string ShowPinnedGrid            = "Show pinned chats as a grid";
    public const string ShowPinnedGridDescription = "Pinned chats show as large pictures at the top of your inbox. Pin a chat on your phone.";

    // The controller wrote the settings card of the microphone. The owner approves it.
    public const string Microphone            = "Microphone";
    public const string MicrophoneDescription = "Which microphone records voice messages.";
    public const string WindowsDefault        = "Windows default";

    public const string Reply              = "Reply";
    public const string Send               = "Send";
    public const string StartAConversation = "Start a conversation";
    public const string TypeNameOrNumber   = "Type a name or phone number";
    public const string SendToFormat       = "Send to {0}";

    // The owner wrote the Go button of Start Chat. It must stay verbatim.
    public const string StartChatGo = "Start Chat";

    // The controller wrote the failure of Start Chat for R11. The owner can edit it.
    public const string StartFailed = "Couldn't start this conversation. Try again.";

    // The controller wrote the copy of media in the chat in Plan 6. The owner approves it at the gate.
    public const string Open            = "Open";
    public const string SaveAs          = "Save as…";
    public const string Downloading     = "Downloading…";
    public const string PhotoLoadFailed = "Couldn't load this photo.";
    public const string FileOpenFailed  = "Couldn't open this file.";
    public const string PhotoPending    = "Waiting for the photo from your phone…";

    // The controller wrote the copy of the photo viewer and the download in Plan 7. The owner approves it at the gate.
    public const string Download         = "Download";
    public const string SavedToDownloads = "Saved to Downloads";
    public const string ShowInFolder     = "Show in folder";
    public const string OpenInPhotos     = "Open in Photos";

    // The controller wrote the copy of videos in Plan 7. The owner approves it at the gate.
    public const string PlayVideo        = "Play video";
    public const string OpenVideo        = "Open video";
    public const string OpenInDefaultApp = "Open in default app";
    public const string VideoPlayFailed  = "Couldn't play this video.";

    // The owner named Open with on 2026-11-01. The ellipsis matches Save as, because a dialog follows.
    public const string OpenWith = "Open with…";

    // The controller wrote the labels of reply quotes for media in Plan 7. The owner approves them at the gate.
    public const string QuoteImage = "Image";
    public const string QuoteVideo = "Video";
    public const string QuoteGif   = "GIF";

    // The controller wrote the accessible name of the jump from a reply quote to the quoted message, in Batch F. The
    // owner approves it.
    public const string GoToOriginalMessage = "Go to original message";

    // The controller wrote the copy of attachments in Plan 6. The owner approves it at the gate.
    public const string Attach           = "Attach";
    public const string AttachPhoto      = "Attach photo";
    public const string RemoveAttachment = "Remove attachment";
    public const string AttachSendFailed = "Couldn't send this attachment. Try again.";

    // The owner wrote the attachment limits. They must stay verbatim.
    public const string AttachTooBig      = "Your attachment is too big (100 MB max).";
    public const string AttachTotalTooBig = "Your attachment is too big (200 MB max).";

    // The controller wrote the copy of voice messages in Plan 6. The owner approves it at the gate.
    public const string RecordVoiceMessage = "Record voice message";
    public const string Done               = "Done";
    public const string CancelRecording    = "Cancel recording";
    public const string Play               = "Play";
    public const string Pause              = "Pause";
    public const string Resume             = "Resume";
    public const string RecordingFailed    = "Couldn't save this recording. Try again.";

    // The controller wrote the microphone dialog. The owner approves it.
    public const string MicrophoneTitle = "Use your microphone?";
    public const string MicrophoneText  = "Messages needs your microphone to record voice messages. Turn it on for Messages in Windows Settings, then try again.";
    public const string OpenSettings    = "Open settings";

    public const string StartupRefused = "You'll have to manually enable startup. Turn it on in Settings > Apps > Startup.";
}

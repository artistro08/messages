using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Sends from the open chat: the draft, the chips, the replies, the reactions and the retries.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>
    /// Sends the draft and the attachments. A bubble shows at once for each, and the draft and the chips clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any failure marks its bubble failed and throws nothing. The call does nothing when <see cref="CanSend"/> is
    /// false. A reply to an RCS message carries its ID, and the bubble shows the quote. A reply to SMS or MMS goes as
    /// plain text, as the Go connector sends it in <c>handlematrix.go</c>. The reply clears after the send.
    /// </para>
    /// <para>
    /// A recording in the preview chip goes first, as <c>Voice message.m4a</c> with <c>audio/mp4</c>. Its temp file
    /// goes at once. The attachments go before the text, 1 at a time in order. Each gets its own bubble, which shows
    /// its upload. Off RCS, the text goes on the last attachment as its caption. RCS refuses captions on media, so
    /// there the text goes after as its own message. A reply goes on the text, or on the first attachment with no text
    /// message. A failed attachment fails only its own bubble, which retries alone.
    /// </para>
    /// <para>
    /// The call reads the picked files only now, each into 1 array. A file that it can no longer read stays as its
    /// chip, with <see cref="ChatCopy.AttachSendFailed"/> over the chips. The chat frees the bytes of an attachment
    /// when its upload is done. Only a failure before then keeps them for the retry.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when the phone answered or the bubbles failed.</returns>
    public async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        string text                         = string.IsNullOrWhiteSpace(Draft) ? string.Empty : Draft;
        List<PendingAttachment> attachments = [.. Attachments];
        bool isRcs                          = _store.Find(ConversationId)?.IsRcs == true;
        string? replyTo                     = ReplyingTo is { IsRcs: true, Id.Length: > 0 } quoted ? quoted.Id : null;

        // The chips clear before the read, so a second Send cannot send the same files again. The saved draft also
        // goes, so the list row stops showing it.
        Draft = string.Empty;
        Attachments.Clear();
        AttachError = string.Empty;
        CancelReply();
        _store.SaveDraft(ConversationId, string.Empty);

        // A file that the read fails on goes back as its chip.
        List<(string Name, string Mime, byte[] Bytes, int Width, int Height)> ready = [];
        foreach (PendingAttachment attachment in attachments)
        {
            try
            {
                ready.Add((attachment.Name, attachment.Mime, await attachment.ReadAsync(), attachment.Width, attachment.Height));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _store.LogFailure("read attachment", ex);
                Attachments.Add(attachment);
                AttachError = ChatCopy.AttachSendFailed;
            }
        }

        // Take deletes the temp file of the recording.
        if (Voice.Take() is { } voice)
        {
            ready.Insert(0, (VoiceDraft.FileName, VoiceDraft.Mime, voice, 0, 0));
        }

        bool captionOnMedia = ready.Count > 0 && !isRcs && text.Length > 0;
        bool textApart      = text.Length > 0 && !captionOnMedia;

        // The bubbles show before the phone answers. The attachments show their own bytes.
        List<MessageBubbleViewModel> bubbles = [];
        for (int i = 0; i < ready.Count; i++)
        {
            (string name, string mime, byte[] bytes, int width, int height) = ready[i];

            string caption                     = captionOnMedia && i == ready.Count - 1 ? text : string.Empty;
            string? mediaReply                 = i == 0 && !textApart ? replyTo : null;
            MessageBubbleViewModel mediaBubble = AddPending(MediaMessage(name, mime, bytes, width, height, caption, mediaReply, isRcs));
            _outgoing[mediaBubble]             = new OutgoingMedia(bytes, name, mime);
            bubbles.Add(mediaBubble);
        }

        if (textApart)
        {
            bubbles.Add(AddPending(ChatMessage.Pending(GmClient.NewTmpId(), ConversationId, string.Empty, _time.GetUtcNow(), MessageKind.Text, text) with
            {
                ReplyToId = replyTo ?? string.Empty,
                IsRcs     = isRcs,
            }));
        }

        // The sends go 1 at a time, so the messages arrive in order.
        foreach (MessageBubbleViewModel bubble in bubbles)
        {
            await SendBubbleAsync(bubble);
        }
    }

    /// <summary>
    /// Adds a picked or pasted photo or file as a chip, unless <see cref="CanAttach"/> refuses its size.
    /// </summary>
    /// <remarks>
    /// The call does not read the file. <paramref name="open"/> opens it for the picture of the chip, and Send reads
    /// it. A refused file shows <see cref="AttachError"/> until the next pick, a removed chip or Send. The next pick
    /// calls <see cref="ClearAttachError"/>.
    /// </remarks>
    /// <param name="name">The file name the other side sees.</param>
    /// <param name="mime">The mime type.</param>
    /// <param name="size">The size of the file in bytes.</param>
    /// <param name="open">Opens the file for the read.</param>
    /// <param name="width">The width of a photo in pixels, or 0 when unknown.</param>
    /// <param name="height">The height of a photo in pixels, or 0 when unknown.</param>
    /// <param name="thumbnail">
    /// Opens the thumbnail of the file for the chip, such as the frame of a video, or null for none.
    /// </param>
    /// <returns>True when the chip was added.</returns>
    public bool Attach(string name, string mime, long size, Func<Task<Stream>> open, int width = 0, int height = 0, Func<Task<Stream>>? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(mime);
        ArgumentNullException.ThrowIfNull(open);

        if (!CanAttach(size))
        {
            return false;
        }

        Attachments.Add(new PendingAttachment(++_nextAttachmentId, name, mime, size, width, height, open, thumbnail));
        return true;
    }

    /// <summary>
    /// Adds bytes in memory as a chip. The overload that opens a file does the work.
    /// </summary>
    /// <param name="name">The file name the other side sees.</param>
    /// <param name="mime">The mime type.</param>
    /// <param name="bytes">The bytes of the file.</param>
    /// <param name="width">The width of a photo in pixels, or 0 when unknown.</param>
    /// <param name="height">The height of a photo in pixels, or 0 when unknown.</param>
    /// <returns>True when the chip was added.</returns>
    public bool Attach(string name, string mime, byte[] bytes, int width = 0, int height = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return Attach(name, mime, bytes.LongLength, () => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), width, height);
    }

    /// <summary>
    /// Tells whether the chat can attach a file of this size. When it cannot, <see cref="AttachError"/> says that the
    /// file is too big.
    /// </summary>
    /// <param name="size">The size of the file in bytes.</param>
    /// <returns>
    /// False when the file is over <see cref="ChatStore.MaxAttachmentBytes"/>, or when it takes the chips past
    /// <see cref="MaxTotalAttachmentBytes"/> together.
    /// </returns>
    public bool CanAttach(long size)
    {
        if (size > ChatStore.MaxAttachmentBytes)
        {
            AttachError = ChatCopy.AttachTooBig;
            return false;
        }

        if (Attachments.Sum(a => a.Size) + size > MaxTotalAttachmentBytes)
        {
            AttachError = ChatCopy.AttachTotalTooBig;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Hides the too-big note when a new pick starts.
    /// </summary>
    public void ClearAttachError() => AttachError = string.Empty;

    /// <summary>
    /// Shows <see cref="ChatCopy.RecordingFailed"/> over the chips when a recording that ended with Done failed. Thus
    /// no recording goes without a word.
    /// </summary>
    /// <remarks>The note goes as the too-big note goes, or when the next recording starts.</remarks>
    public void NoteRecordingFailed() => AttachError = ChatCopy.RecordingFailed;

    /// <summary>
    /// Removes a chip when the user clicks its remove button.
    /// </summary>
    /// <param name="attachment">The attachment.</param>
    public void RemoveAttachment(PendingAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        Attachments.Remove(attachment);
        AttachError = string.Empty;
    }

    /// <summary>
    /// Starts a reply to a bubble. The reply chip shows until the next send or <see cref="CancelReply"/>.
    /// </summary>
    /// <param name="bubble">The bubble to reply to.</param>
    public void StartReply(MessageBubbleViewModel bubble)
    {
        ArgumentNullException.ThrowIfNull(bubble);

        ReplyingTo = bubble;
    }

    /// <summary>
    /// Cancels the reply when the user clicks the close button of the reply chip.
    /// </summary>
    public void CancelReply() => ReplyingTo = null;

    /// <summary>
    /// Reacts to a bubble. The call adds the emoji, changes my other emoji to it, or removes it when it is mine.
    /// </summary>
    /// <remarks>
    /// The bubble changes when the echo from the phone arrives, not before. The call logs a failure by type and throws
    /// nothing.
    /// </remarks>
    /// <param name="bubble">The bubble reacted to.</param>
    /// <param name="emoji">The emoji.</param>
    /// <returns>A task that completes when the phone answered.</returns>
    public async Task ReactAsync(MessageBubbleViewModel bubble, string emoji)
    {
        ArgumentNullException.ThrowIfNull(bubble);
        ArgumentException.ThrowIfNullOrEmpty(emoji);

        // The phone did not report the message yet.
        if (bubble.Id.Length == 0)
        {
            return;
        }

        SendReactionRequest.Types.Action action = bubble.MyReaction switch
        {
            null                        => SendReactionRequest.Types.Action.Add,
            var mine when mine == emoji => SendReactionRequest.Types.Action.Remove,
            _                           => SendReactionRequest.Types.Action.Switch,
        };

        try
        {
            await _store.SendReactionAsync(ConversationId, bubble.Id, emoji, action, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.LogFailure("react", ex);
        }
    }

    /// <summary>
    /// Deletes the message of a bubble from the phone. The bubble goes when the phone says that it did.
    /// </summary>
    /// <remarks>
    /// Only the copy on this side goes. The call logs a failure or a refusal from the phone, and the bubble stays.
    /// </remarks>
    /// <param name="bubble">The bubble to delete.</param>
    /// <returns>True when the phone deleted it.</returns>
    public async Task<bool> DeleteAsync(MessageBubbleViewModel bubble)
    {
        ArgumentNullException.ThrowIfNull(bubble);

        // The phone did not report the message yet.
        if (bubble.Id.Length == 0)
        {
            return false;
        }

        try
        {
            if (!await _store.DeleteMessageAsync(ConversationId, bubble.Id, CancellationToken.None))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.LogFailure("delete", ex);
            return false;
        }

        Messages.Remove(bubble);
        RefreshBubbles();
        return true;
    }

    /// <summary>
    /// Sends a failed bubble again with a new temporary ID: its text, or its attachment with its caption.
    /// </summary>
    /// <remarks>
    /// An attachment sends the same upload again, with its media ID and key. It uploads again only when the first try
    /// failed before the end of its upload. A bubble from before the chat opened sends the media of its echo.
    /// </remarks>
    /// <param name="failed">The bubble that failed.</param>
    /// <returns>A task that completes when the phone answered or the bubble failed again.</returns>
    public async Task RetryAsync(MessageBubbleViewModel failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        if (!failed.IsFailed || !Messages.Contains(failed))
        {
            return;
        }

        // The old temporary ID leaves the lookup.
        _byTmpId.Remove(failed.TmpId);
        failed.MarkSending(GmClient.NewTmpId());
        Index(failed);
        RefreshAround(failed);
        await SendBubbleAsync(failed);
    }

    // Sends the attachment of a bubble with its caption, or its text. Each failure fails the bubble and does not
    // escape: the phone, a refusal, the network, a timeout, a decode or a file that is too big.
    private async Task SendBubbleAsync(MessageBubbleViewModel bubble)
    {
        try
        {
            if (OutgoingOf(bubble) is { } outgoing)
            {
                // The upload runs 1 time. A retry sends the same upload again.
                if (outgoing.Uploaded is null)
                {
                    var progress      = new PostedProgress(share => _post(() => bubble.SetUploadShare(share)));
                    outgoing.Uploaded = await _store.UploadAttachmentAsync(outgoing.Bytes, outgoing.Name, outgoing.Mime, progress, CancellationToken.None);
                    outgoing.Bytes    = [];
                }

                string caption      = bubble.Caption;
                string? mediaReply  = bubble.ReplyToId.Length > 0 ? bubble.ReplyToId : null;
                await _store.SendUploadedMediaAsync(ConversationId, outgoing.Uploaded, caption.Length > 0 ? caption : null, bubble.TmpId, mediaReply, CancellationToken.None);
                return;
            }

            string? replyTo = bubble.ReplyToId.Length > 0 ? bubble.ReplyToId : null;
            await _store.SendTextAsync(ConversationId, bubble.Text, bubble.TmpId, replyTo, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.LogFailure("send", ex);
            bubble.MarkFailed();
            RefreshAround(bubble);
        }
    }

    private async Task SendTypingAsync()
    {
        try
        {
            await _store.SendTypingAsync(ConversationId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.LogFailure("send typing", ex);
        }
    }
}

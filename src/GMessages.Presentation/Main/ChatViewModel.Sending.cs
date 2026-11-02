using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Sending from the open chat: the draft, the chips, replies, reactions and retries.
/// </summary>
/// <remarks>
/// Part of <see cref="ChatViewModel"/>; the other members are in ChatViewModel.cs.
/// </remarks>
public sealed partial class ChatViewModel
{

    // =========================================================================
    // SENDING
    // =========================================================================

    /// <summary>
    /// Sends the draft and the attachments: a bubble appears at once for each, and the draft and chips clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A send that fails for any reason marks its bubble failed instead of throwing. Does nothing when <see cref="CanSend"/> is false.
    /// A reply to an RCS message carries its ID and the bubble shows the quote; a reply to SMS or MMS goes as plain
    /// text, as the Go connector sends it (<c>handlematrix.go</c>). The reply clears once sent.
    /// </para>
    /// <para>
    /// A recording waiting in the preview chip goes first, as <c>Voice message.m4a</c> (<c>audio/mp4</c>), its temp file
    /// deleted. Attachments go first, one at a time in order, each its own bubble showing its upload. Off RCS the text rides on
    /// the last one as its caption; RCS refuses captions on media, so there the text follows as its own message. A reply
    /// rides on the text, or on the first attachment when there is no text message. A failed attachment fails only its
    /// own bubble, which retries on its own.
    /// </para>
    /// <para>
    /// The picked files are read only now, each into one array. A file that can no longer be read stays as its chip,
    /// with <see cref="ChatCopy.AttachSendFailed"/> over the chips. An attachment's bytes are let go once its upload is
    /// done; only a failure before that keeps them for the retry.
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

        // Cleared Before Reading, So A Second Send Can't Send The Same Files Again
        Draft = string.Empty;
        Attachments.Clear();
        AttachError = string.Empty;
        CancelReply();

        // Read The Picked Files Now; One That Can't Be Read Goes Back As Its Chip
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

        // The Recording Goes First, As An Attachment (Its Temp File Is Deleted Now)
        if (Voice.Take() is { } voice)
        {
            ready.Insert(0, (VoiceDraft.FileName, VoiceDraft.Mime, voice, 0, 0));
        }

        bool captionOnMedia = ready.Count > 0 && !isRcs && text.Length > 0;
        bool textApart      = text.Length > 0 && !captionOnMedia;

        // Optimistic Bubbles: The Attachments (Showing Their Own Bytes), Then The Text. A Reply With No Text Message
        // Rides On The First Attachment
        List<MessageBubbleViewModel> bubbles = [];
        for (int i = 0; i < ready.Count; i++)
        {
            var a                              = ready[i];
            string caption                     = captionOnMedia && i == ready.Count - 1 ? text : string.Empty;
            string? mediaReply                 = i == 0 && !textApart ? replyTo : null;
            MessageBubbleViewModel mediaBubble = AddPending(MediaMessage(a.Name, a.Mime, a.Bytes, a.Width, a.Height, caption, mediaReply, isRcs));
            _outgoing[mediaBubble]             = new OutgoingMedia(a.Bytes, a.Name, a.Mime);
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

        // One At A Time, So They Arrive In Order
        foreach (MessageBubbleViewModel bubble in bubbles)
        {
            await SendBubbleAsync(bubble);
        }
    }

    /// <summary>
    /// Adds a picked or pasted photo or file as a chip, unless <see cref="CanAttach"/> refuses its size.
    /// </summary>
    /// <remarks>
    /// The file is not read now: <paramref name="open"/> opens it for the chip's picture, and Send reads it. A refused
    /// file shows <see cref="AttachError"/> until the next pick (<see cref="ClearAttachError"/>), a removed chip or Send.
    /// </remarks>
    /// <param name="name">The file name the other side sees.</param>
    /// <param name="mime">The mime type.</param>
    /// <param name="size">The file's size in bytes.</param>
    /// <param name="open">Opens the file for reading.</param>
    /// <param name="width">A photo's width in pixels, or 0 when unknown.</param>
    /// <param name="height">A photo's height in pixels, or 0 when unknown.</param>
    /// <param name="thumbnail">Opens the file's thumbnail (a video's frame) for the chip, or null for none.</param>
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
    /// Adds bytes already in memory as a chip (see the overload that opens a file).
    /// </summary>
    /// <param name="name">The file name the other side sees.</param>
    /// <param name="mime">The mime type.</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="width">A photo's width in pixels, or 0 when unknown.</param>
    /// <param name="height">A photo's height in pixels, or 0 when unknown.</param>
    /// <returns>True when the chip was added.</returns>
    public bool Attach(string name, string mime, byte[] bytes, int width = 0, int height = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return Attach(name, mime, bytes.LongLength, () => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), width, height);
    }

    /// <summary>
    /// Tells whether a file of this size may be attached; when not, <see cref="AttachError"/> says it is too big.
    /// </summary>
    /// <param name="size">The file's size in bytes.</param>
    /// <returns>
    /// False when the file is over <see cref="ChatStore.MaxAttachmentBytes"/>, or would take the chips past
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
    /// Hides the too-big note (a new pick starts).
    /// </summary>
    public void ClearAttachError() => AttachError = string.Empty;

    /// <summary>
    /// Says over the chips that a recording ended with Done couldn't be finished (<see cref="ChatCopy.RecordingFailed"/>),
    /// so it is never lost without a word; the note goes like the too-big note, or when the next recording starts.
    /// </summary>
    public void NoteRecordingFailed() => AttachError = ChatCopy.RecordingFailed;

    /// <summary>
    /// Drops a chip (its remove button).
    /// </summary>
    /// <param name="attachment">The attachment.</param>
    public void RemoveAttachment(PendingAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        Attachments.Remove(attachment);
        AttachError = string.Empty;
    }

    /// <summary>
    /// Starts a reply to a bubble: the reply chip shows until the next send or <see cref="CancelReply"/>.
    /// </summary>
    /// <param name="bubble">The bubble to reply to.</param>
    public void StartReply(MessageBubbleViewModel bubble)
    {
        ArgumentNullException.ThrowIfNull(bubble);

        ReplyingTo = bubble;
    }

    /// <summary>
    /// Drops the reply being written (the reply chip's close button).
    /// </summary>
    public void CancelReply() => ReplyingTo = null;

    /// <summary>
    /// Reacts to a bubble: adds the emoji, switches my other emoji to it, or removes it when it is already mine.
    /// </summary>
    /// <remarks>The bubble changes when the phone's echo arrives, not before. A failure is logged by type and swallowed.</remarks>
    /// <param name="bubble">The bubble reacted to.</param>
    /// <param name="emoji">The emoji.</param>
    /// <returns>A task that completes when the phone answered.</returns>
    public async Task ReactAsync(MessageBubbleViewModel bubble, string emoji)
    {
        ArgumentNullException.ThrowIfNull(bubble);
        ArgumentException.ThrowIfNullOrEmpty(emoji);

        // Not Reported By The Phone Yet: Nothing To React To
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
    /// Sends a failed bubble again with a new temporary ID: its text, or its attachment with its caption.
    /// </summary>
    /// <remarks>
    /// An attachment sends the same upload again (its media ID and key), and uploads again only when the first try
    /// failed before its upload was done. A bubble from before the chat opened sends the media its echo carried.
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

        // A New Temporary ID: The Old One Leaves The Lookup
        _byTmpId.Remove(failed.TmpId);
        failed.MarkSending(GmClient.NewTmpId());
        Index(failed);
        RefreshAround(failed);
        await SendBubbleAsync(failed);
    }

    // Sends A Bubble's Attachment (Uploading It, With Its Caption), Or Its Text (As A Reply When It Quotes One); Any
    // Failure (Phone, Refusal, Network, Timeout, Decode, Too Big) Fails The Bubble Instead Of Escaping
    private async Task SendBubbleAsync(MessageBubbleViewModel bubble)
    {
        try
        {
            if (OutgoingOf(bubble) is { } outgoing)
            {
                // Upload Once; A Retry Sends The Same Upload Again
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

    // Tells The Other Side I'm Typing; A Failure Is Only Logged
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

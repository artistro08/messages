using System.ComponentModel;
using System.Globalization;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// One message bubble in the chat.
/// </summary>
/// <remarks>
/// Every change raises <see cref="PropertyChanged"/> once with an empty property name, which tells <c>x:Bind</c> to
/// re-read every property. Call everything on the UI thread.
/// </remarks>
public sealed class MessageBubbleViewModel : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    /// <summary>
    /// Creates the bubble from a message.
    /// </summary>
    /// <param name="m">The message.</param>
    public MessageBubbleViewModel(ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);

        Fill(m);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the message ID, or empty until the phone has reported the message.</summary>
    public string Id { get; private set; } = string.Empty;

    /// <summary>Gets the temporary ID the app made when sending, or empty.</summary>
    public string TmpId { get; private set; } = string.Empty;

    /// <summary>Gets the sender's participant ID.</summary>
    public string ParticipantId { get; private set; } = string.Empty;

    /// <summary>Gets the text, or the kind label ("Photo", "Video"...) for a message without text.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Gets whether the owner sent the message.</summary>
    public bool FromMe { get; private set; }

    /// <summary>Gets the sender's name when this is the first bubble of a received run in a group, else null.</summary>
    public string? SenderName { get; private set; }

    /// <summary>Gets the time of day the message was sent.</summary>
    public string Time { get; private set; } = string.Empty;

    /// <summary>Gets whether the message was not sent.</summary>
    public bool IsFailed { get; private set; }

    /// <summary>Gets whether the message is still being sent.</summary>
    public bool IsSending { get; private set; }

    /// <summary>
    /// Copies new message details into the bubble (an echo or a status change).
    /// </summary>
    /// <remarks>An empty ID or temporary ID in <paramref name="m"/> keeps the one the bubble already has.</remarks>
    /// <param name="m">The message.</param>
    public void Update(ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);

        Fill(m);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    internal void SetSenderName(string? name)
    {
        if (SenderName == name)
        {
            return;
        }

        SenderName = name;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    internal void MarkFailed()
    {
        IsFailed  = true;
        IsSending = false;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    internal void MarkSending(string tmpId)
    {
        TmpId     = tmpId;
        IsFailed  = false;
        IsSending = true;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    private void Fill(ChatMessage m)
    {
        Id            = m.Id.Length > 0 ? m.Id : Id;
        TmpId         = m.TmpId.Length > 0 ? m.TmpId : TmpId;
        ParticipantId = m.ParticipantId;
        Text          = m.Kind == MessageKind.Text ? m.Text : ChatFormat.KindLabel(m.Kind);
        FromMe        = m.FromMe;
        Time          = m.Time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        IsFailed      = m.IsFailed;
        IsSending     = m.Status == MessageStatusType.OutgoingSending;
    }
}

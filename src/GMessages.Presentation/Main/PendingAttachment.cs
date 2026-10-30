using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// A photo or file picked to send, shown as a chip above the message box until Send.
/// </summary>
/// <remarks>The name and bytes are never logged.</remarks>
/// <param name="Id">A number unique in the chat, for the chip's AutomationId.</param>
/// <param name="Name">The file name the other side sees.</param>
/// <param name="Mime">The mime type, such as <c>image/jpeg</c>.</param>
/// <param name="Bytes">The file's bytes.</param>
/// <param name="Width">A photo's width in pixels, or 0 when unknown.</param>
/// <param name="Height">A photo's height in pixels, or 0 when unknown.</param>
public sealed record PendingAttachment(int Id, string Name, string Mime, byte[] Bytes, int Width, int Height)
{
    /// <summary>Gets what the attachment sends as (Photo, Video, Audio or File), from its mime type.</summary>
    public MessageKind Kind => MediaPart.KindOf(MediaFormatsMap.FromMime(Mime));

    /// <summary>Gets whether the chip shows the photo itself rather than the name.</summary>
    public bool IsPhoto => Kind == MessageKind.Photo;
}

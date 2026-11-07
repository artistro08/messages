using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds a photo or file that the person picked or pasted to send. It shows as a chip above the message box until Send.
/// </summary>
/// <remarks>
/// The chip holds no file bytes. <paramref name="Open"/> opens the picked file, and <see cref="ReadAsync"/> reads it
/// only at Send. The log never holds the name or the bytes.
/// </remarks>
/// <param name="Id">A number that is unique in the chat, for the AutomationId of the chip.</param>
/// <param name="Name">The file name that the other side sees.</param>
/// <param name="Mime">The mime type, such as <c>image/jpeg</c>.</param>
/// <param name="Size">The size of the file in bytes when picked.</param>
/// <param name="Width">The width of a photo in pixels, or 0 when unknown.</param>
/// <param name="Height">The height of a photo in pixels, or 0 when unknown.</param>
/// <param name="Open">Opens the file for reading. The picture of the chip and Send read it from here.</param>
/// <param name="Thumbnail">
/// Opens a small picture that Windows made of the file, such as a video frame. It is null when the file has none.
/// </param>
public sealed record PendingAttachment(int Id, string Name, string Mime, long Size, int Width, int Height, Func<Task<Stream>> Open, Func<Task<Stream>>? Thumbnail = null)
{
    /// <summary>Gets the kind that the attachment sends as, from its mime type: Photo, Video, Audio or File.</summary>
    public MessageKind Kind => MediaPart.KindOf(MediaFormatsMap.FromMime(Mime));

    /// <summary>Gets whether the chip shows the photo itself and not the name.</summary>
    public bool IsPhoto => Kind == MessageKind.Photo;

    /// <summary>
    /// Gets whether the chip shows a picture and not its icon and name. The picture is the photo or the file thumbnail.
    /// </summary>
    public bool ShowsPicture => IsPhoto || Thumbnail is not null;

    /// <summary>
    /// Reads the whole file into 1 array of its length.
    /// </summary>
    /// <returns>The bytes of the file.</returns>
    /// <exception cref="AttachmentTooBigException">The file grew past <see cref="ChatStore.MaxAttachmentBytes"/> since it was picked.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public async Task<byte[]> ReadAsync()
    {
        await using Stream stream = await Open().ConfigureAwait(false);
        if (stream.Length > ChatStore.MaxAttachmentBytes)
        {
            throw new AttachmentTooBigException();
        }

        byte[] bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        return bytes;
    }
}

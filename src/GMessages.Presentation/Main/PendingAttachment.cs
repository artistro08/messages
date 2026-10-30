using GMessages.Presentation.Messaging;
using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// A photo or file picked to send, shown as a chip above the message box until Send.
/// </summary>
/// <remarks>
/// The chip holds no file bytes: <paramref name="Open"/> opens the picked file, and <see cref="ReadAsync"/> reads it
/// only at Send. The name and bytes are never logged.
/// </remarks>
/// <param name="Id">A number unique in the chat, for the chip's AutomationId.</param>
/// <param name="Name">The file name the other side sees.</param>
/// <param name="Mime">The mime type, such as <c>image/jpeg</c>.</param>
/// <param name="Size">The file's size in bytes when picked.</param>
/// <param name="Width">A photo's width in pixels, or 0 when unknown.</param>
/// <param name="Height">A photo's height in pixels, or 0 when unknown.</param>
/// <param name="Open">Opens the file for reading (the chip's picture and Send read it from here).</param>
public sealed record PendingAttachment(int Id, string Name, string Mime, long Size, int Width, int Height, Func<Task<Stream>> Open)
{
    /// <summary>Gets what the attachment sends as (Photo, Video, Audio or File), from its mime type.</summary>
    public MessageKind Kind => MediaPart.KindOf(MediaFormatsMap.FromMime(Mime));

    /// <summary>Gets whether the chip shows the photo itself rather than the name.</summary>
    public bool IsPhoto => Kind == MessageKind.Photo;

    /// <summary>
    /// Reads the whole file, straight into one array of its length.
    /// </summary>
    /// <returns>The file's bytes.</returns>
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

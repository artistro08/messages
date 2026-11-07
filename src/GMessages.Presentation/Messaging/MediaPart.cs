using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds the photo, video, voice message or file that a message carries.
/// </summary>
/// <remarks>
/// The record is a copy of the protocol <see cref="MediaContent"/>. The full file is <see cref="MediaId"/> with
/// <see cref="Key"/>, and the thumbnail is <see cref="ThumbnailId"/> with <see cref="ThumbnailKey"/>.
/// <see cref="InlineData"/> is a small file inside the message itself, not encrypted. The download order is that of
/// handlegmessages.go in the Go connector. The log never holds the keys, the bytes or the name.
/// </remarks>
/// <param name="MediaId">The media ID of the full file, or empty when only a thumbnail arrived so far.</param>
/// <param name="Key">The 32-byte decryption key of the full file, or empty.</param>
/// <param name="ThumbnailId">The media ID of the thumbnail, or empty.</param>
/// <param name="ThumbnailKey">The 32-byte decryption key of the thumbnail, or empty.</param>
/// <param name="InlineData">The file inside the message, not encrypted, or empty.</param>
/// <param name="Name">The file name that the sender gave, or empty.</param>
/// <param name="Size">The plain file size in bytes, or 0 when unknown.</param>
/// <param name="Width">The width of the image or video in pixels, or 0 when unknown.</param>
/// <param name="Height">The height of the image or video in pixels, or 0 when unknown.</param>
/// <param name="Kind">Photo, Video, Audio or File, from the format.</param>
/// <param name="Mime">The mime type, from the format through the table of LibGM.</param>
public sealed record MediaPart(
    string MediaId,
    byte[] Key,
    string ThumbnailId,
    byte[] ThumbnailKey,
    byte[] InlineData,
    string Name,
    long Size,
    int Width,
    int Height,
    MessageKind Kind,
    string Mime)
{
    // The mime type when neither the format nor the message names one.
    private const string FallbackMime = "application/octet-stream";

    /// <summary>
    /// Gets whether the media is still on its way, with no media ID, thumbnail ID or inline data.
    /// </summary>
    public bool IsPending => MediaId.Length == 0 && ThumbnailId.Length == 0 && InlineData.Length == 0;

    /// <summary>
    /// Gets the ID of the message part that holds the media, or empty. A request for the full-size image names it.
    /// </summary>
    public string ActionMessageId { get; init; } = string.Empty;

    /// <summary>
    /// Copies a protocol media part.
    /// </summary>
    /// <remarks>
    /// The mime type comes from the format through <see cref="MediaFormatsMap.ToMime"/>, as <c>FormatToMediaType</c>
    /// in LibGM does. For a format that the table does not know, the mime type of the message is next, then
    /// <c>application/octet-stream</c>.
    /// </remarks>
    /// <param name="c">The protocol media content.</param>
    /// <returns>The media part.</returns>
    public static MediaPart From(MediaContent c)
    {
        ArgumentNullException.ThrowIfNull(c);

        string mime = MediaFormatsMap.ToMime(c.Format) ?? (c.MimeType.Length > 0 ? c.MimeType : FallbackMime);

        return new MediaPart(
            c.MediaID,
            c.DecryptionKey.ToByteArray(),
            c.ThumbnailMediaID,
            c.ThumbnailDecryptionKey.ToByteArray(),
            c.MediaData.ToByteArray(),
            c.MediaName,
            c.Size,
            (int)Math.Clamp(c.Dimensions?.Width ?? 0, 0, int.MaxValue),
            (int)Math.Clamp(c.Dimensions?.Height ?? 0, 0, int.MaxValue),
            KindOf(c.Format),
            mime);
    }

    /// <summary>
    /// Gives the kind of message that a media format shows.
    /// </summary>
    /// <param name="format">The media format.</param>
    /// <returns>Photo for images, Video, Audio, else File.</returns>
    public static MessageKind KindOf(MediaFormats format)
    {
        string name = format.ToString();

        if (name.StartsWith("Image", StringComparison.Ordinal))
        {
            return MessageKind.Photo;
        }

        if (name.StartsWith("Video", StringComparison.Ordinal))
        {
            return MessageKind.Video;
        }

        return name.StartsWith("Audio", StringComparison.Ordinal) ? MessageKind.Audio : MessageKind.File;
    }
}

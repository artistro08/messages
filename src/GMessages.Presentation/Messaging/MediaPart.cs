using GMessages.Protocol;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// The photo, video, voice message or file a message carries.
/// </summary>
/// <remarks>
/// Copied from the protocol's <see cref="MediaContent"/>. The full file is <see cref="MediaId"/> with <see cref="Key"/>,
/// the thumbnail is <see cref="ThumbnailId"/> with <see cref="ThumbnailKey"/>, and <see cref="InlineData"/> is a small
/// file sent inside the message itself, not encrypted (the download order of the Go connector's handlegmessages.go).
/// Keys, bytes and the name are never logged.
/// </remarks>
/// <param name="MediaId">The full file's media ID, or empty when only a thumbnail arrived so far.</param>
/// <param name="Key">The full file's 32-byte decryption key, or empty.</param>
/// <param name="ThumbnailId">The thumbnail's media ID, or empty.</param>
/// <param name="ThumbnailKey">The thumbnail's 32-byte decryption key, or empty.</param>
/// <param name="InlineData">The file sent inside the message (not encrypted), or empty.</param>
/// <param name="Name">The file name the sender gave, or empty.</param>
/// <param name="Size">The plain file size in bytes, or 0 when unknown.</param>
/// <param name="Width">The image or video width in pixels, or 0 when unknown.</param>
/// <param name="Height">The image or video height in pixels, or 0 when unknown.</param>
/// <param name="Kind">Photo, Video, Audio or File, from the format.</param>
/// <param name="Mime">The mime type, from the format through LibGM's table.</param>
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
    // The Mime Type When Neither The Format Nor The Message Names One
    private const string FallbackMime = "application/octet-stream";

    /// <summary>Gets whether the media is still on its way: no media ID, no thumbnail ID and no inline data.</summary>
    public bool IsPending => MediaId.Length == 0 && ThumbnailId.Length == 0 && InlineData.Length == 0;

    /// <summary>
    /// Copies a protocol media part.
    /// </summary>
    /// <remarks>
    /// The mime type comes from the format through <see cref="MediaFormatsMap.ToMime"/>, as LibGM's
    /// <c>FormatToMediaType</c> does. A format the table doesn't know falls back to the message's own mime type, then
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
    /// Gives the kind of message a media format shows.
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

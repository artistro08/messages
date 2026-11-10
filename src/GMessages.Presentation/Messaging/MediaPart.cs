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
    /// The largest video that the store downloads only to make its still frame. A bigger video with a preview picture
    /// shows the preview.
    /// </summary>
    public const long MaxFrameVideoBytes = 100L * 1024 * 1024;

    // The inline data of a photo is its preview only when it is small and the photo is this many times bigger. The
    // preview of the phone is about 1 KB, 60 x 55 px. A real photo inline, as in an MMS, is far bigger. Its size can
    // also differ from its bytes, and it still shows its inline data.
    // ponytail: a size rule, not a proof. Read the image header if a real photo ever waits.
    private const int  MaxPhotoPreviewBytes = 16 * 1024;
    private const long MinPreviewShrink     = 4;

    /// <summary>
    /// Gets whether <see cref="InlineData"/> is only a small preview picture of a photo or a video, not its file.
    /// </summary>
    /// <remarks>
    /// The phone can send a message with a small JPEG or PNG in it before the file is ready, for example 60 x 55 px for
    /// a photo. A video file never starts as a JPEG or a PNG does. The size of the message can be 0, so the check reads
    /// the bytes. A photo is a preview when its inline data is at most 16 KB and <see cref="Size"/> is more than 4 times
    /// that. A photo or video that this device sent carries all its bytes inline, as long as its size.
    /// </remarks>
    public bool HasInlinePreview => InlineData.Length > 0 && Kind switch
    {
        MessageKind.Video => InlineData is [0xFF, 0xD8, 0xFF, ..] or [0x89, 0x50, 0x4E, 0x47, ..],
        MessageKind.Photo => InlineData.Length <= MaxPhotoPreviewBytes && InlineData.LongLength * MinPreviewShrink < Size,
        _                 => false,
    };

    /// <summary>
    /// Gets whether the part holds the whole file inline, as an attachment sent from this device does: its inline
    /// data is exactly <see cref="Size"/> long.
    /// </summary>
    /// <remarks>A received part with a preview, or with a size that is not its bytes, is not the whole file.</remarks>
    public bool IsWholeInline => InlineData.Length > 0 && InlineData.LongLength == Size;

    /// <summary>
    /// Tells whether this part, the whole file of an attachment sent from this device, keeps showing when the echo
    /// with the file comes. The bytes on show are then that file.
    /// </summary>
    /// <remarks>
    /// The echo names the file with a media ID or only a thumbnail. It has no bytes of its own, or only a small preview
    /// picture. An echo with only a preview and no file is not handled here: the bubble keeps the sent part for it.
    /// </remarks>
    /// <param name="echo">The new part that the phone sent for the same message.</param>
    /// <returns>True when the bytes of this part keep showing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="echo"/> is null.</exception>
    public bool KeepsShowingOver(MediaPart echo)
    {
        ArgumentNullException.ThrowIfNull(echo);

        return IsWholeInline
            && (echo.MediaId.Length > 0 || echo.ThumbnailId.Length > 0)
            && (echo.InlineData.Length == 0 || echo.HasInlinePreview);
    }

    /// <summary>
    /// Gets whether the photo or video has only its preview picture: the phone has not sent its file yet.
    /// </summary>
    public bool IsWaitingForFile => HasInlinePreview && MediaId.Length == 0 && ThumbnailId.Length == 0;

    /// <summary>
    /// How long after its send a part that the phone has not sent counts as not available here. The phone sent a 128 MB
    /// video in seconds; a 223 MB one never came.
    /// </summary>
    /// <remarks>ponytail: a time guess, the phone gives no "never" signal. Change it if a slow phone sends later.</remarks>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after this device first sees the part it can still come, at least. A message that synced late, while
    /// the app was closed, gets this time for its file, and a photo gets time to ask for its full size.
    /// </summary>
    public static readonly TimeSpan SeenGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gives when the part counts as not available here, because the phone never sends its file, or null when it is
    /// not waiting for a file.
    /// </summary>
    /// <remarks>
    /// Only a pending part and a part with only its preview wait. The time is <see cref="GiveUpAfter"/> after the send,
    /// but never before <see cref="SeenGrace"/> after this device first saw the part. The echo with the file makes a new
    /// part, which does not wait.
    /// </remarks>
    /// <param name="sentAt">When the message was sent.</param>
    /// <param name="firstSeen">When this device first saw the message.</param>
    /// <returns>The time, or null.</returns>
    public DateTimeOffset? UnavailableAt(DateTimeOffset sentAt, DateTimeOffset firstSeen)
    {
        if (!IsPending && !IsWaitingForFile)
        {
            return null;
        }

        DateTimeOffset bySend = sentAt + GiveUpAfter;
        DateTimeOffset bySeen = firstSeen + SeenGrace;
        return bySend > bySeen ? bySend : bySeen;
    }

    /// <summary>
    /// Gets whether the photo needs its full size from the phone: it has only a thumbnail or only a small preview.
    /// </summary>
    public bool NeedsFullSize => Kind == MessageKind.Photo && MediaId.Length == 0 && (ThumbnailId.Length > 0 || IsWaitingForFile);

    /// <summary>
    /// Gets whether the still frame of the video is its preview picture. The video has no thumbnail, and it has no file
    /// yet or is over <see cref="MaxFrameVideoBytes"/>, so the store does not download it for the frame.
    /// </summary>
    public bool FrameIsPreview => Kind == MessageKind.Video && HasInlinePreview && ThumbnailId.Length == 0 && (MediaId.Length == 0 || Size > MaxFrameVideoBytes);

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

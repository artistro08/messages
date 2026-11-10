using GMessages.Presentation.Messaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace GMessages.App.Media;

/// <summary>
/// Gets the still frame of a video for its bubble, its reply quote and the reply chip.
/// </summary>
/// <remarks>
/// <para>
/// The frame is the thumbnail of the phone when the phone sent one. Else the app makes a frame on this device, because
/// LibGM gives none for most videos. The owner asked for this in Plan 8.
/// </para>
/// <para>
/// <see cref="MediaComposition.GetThumbnailAsync"/> of Windows takes the frame 0.5 s into the video, or at the middle
/// of a shorter video. Thus the frame is not the black first frame of a fade-in. The frame has the shape of the video,
/// at most 720 px on its long side. <see cref="MediaClip"/> reads the decrypted file that the media store keeps.
/// </para>
/// <para>
/// The media store keeps the frame and runs the work off the UI thread. See <see cref="MediaStore.GetFrameAsync"/>. The
/// store empties the folder at sign-out, as it does the rest of the temp media. The log gets only the ID of the video.
/// </para>
/// </remarks>
internal static class VideoStill
{
    // The time of the frame in the video, and the longest side of the frame.
    private static readonly TimeSpan At = TimeSpan.FromSeconds(0.5);
    private const double LongSide       = 720;

    /// <summary>
    /// Gets the still frame of a video from the thumbnail of the phone, the kept file, or the video itself.
    /// </summary>
    /// <param name="media">The media store.</param>
    /// <param name="part">The media part of the video.</param>
    /// <returns>The image bytes of the frame.</returns>
    /// <exception cref="MediaPendingException">The part has no media ID, thumbnail or inline data yet.</exception>
    /// <exception cref="MessagingUnavailableException">The app is not connected.</exception>
    public static Task<byte[]> GetAsync(MediaStore media, MediaPart part) =>
        media.GetFrameAsync(part, (path, _) => MakeAsync(path), CancellationToken.None);

    private static async Task<byte[]> MakeAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        MediaClip clip   = await MediaClip.CreateFromFileAsync(file);
        var composition  = new MediaComposition();
        composition.Clips.Add(clip);

        VideoEncodingProperties shape = clip.GetVideoEncodingProperties();
        double scale                  = shape.Width == 0 || shape.Height == 0 ? 1 : Math.Min(1, LongSide / Math.Max(shape.Width, shape.Height));
        int width                     = shape.Width == 0 ? (int)LongSide : (int)Math.Round(shape.Width * scale);
        int height                    = shape.Height == 0 ? (int)LongSide : (int)Math.Round(shape.Height * scale);
        TimeSpan at                   = clip.OriginalDuration < At * 2 ? clip.OriginalDuration / 2 : At;

        using Windows.Graphics.Imaging.ImageStream image = await composition.GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame);
        using var copy = new MemoryStream();
        await image.AsStreamForRead().CopyToAsync(copy);
        return copy.Length > 0 ? copy.ToArray() : throw new InvalidDataException("No frame.");
    }
}

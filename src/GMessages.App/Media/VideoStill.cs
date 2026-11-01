using GMessages.Presentation.Messaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace GMessages.App.Media;

/// <summary>
/// A video's still frame for its bubble, its reply quote and the reply chip: the phone's thumbnail when it sent one,
/// else a frame made on this device (owner, Plan 8: LibGM gives none for most videos).
/// </summary>
/// <remarks>
/// <para>
/// The frame is taken half a second in (the middle of a shorter video), so it isn't a fade-in's black first frame, by
/// Windows' <see cref="MediaComposition.GetThumbnailAsync"/> at the video's own shape, at most 720 px on its long side.
/// <see cref="MediaClip"/> reads only files, so the video is written to <c>{temp}\Frames</c> for it and deleted after.
/// </para>
/// <para>
/// The media store keeps the frame (see <see cref="MediaStore.GetFrameAsync"/>), runs the work off the UI thread, and
/// empties the folder at sign-out like the rest of the temp media. Nothing about the video is logged but its ID.
/// </para>
/// </remarks>
internal static class VideoStill
{
    // Where The Frame Is Taken, And The Longest Side It Is Made At
    private static readonly TimeSpan At = TimeSpan.FromSeconds(0.5);
    private const double LongSide       = 720;

    /// <summary>
    /// Gets a video's still frame, from the phone's thumbnail, the kept file, or made from the video.
    /// </summary>
    /// <param name="media">The media store.</param>
    /// <param name="part">The video's media part.</param>
    /// <returns>The frame's image bytes.</returns>
    /// <exception cref="MediaPendingException">The part has no media ID, thumbnail or inline data yet.</exception>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    public static Task<byte[]> GetAsync(MediaStore media, MediaPart part) =>
        media.GetFrameAsync(part, (video, mime) => MakeAsync(media.TempFolder, video, mime), CancellationToken.None);

    // Writes The Video Out For MediaClip, Takes The Frame As An Image, Then Deletes The Video Again
    private static async Task<byte[]> MakeAsync(string tempFolder, byte[] video, string mime)
    {
        string folder = Path.Combine(tempFolder, "Frames");
        string path   = Path.Combine(folder, Guid.NewGuid().ToString("N") + MediaStore.ExtensionOf(mime));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(path, video);
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            MediaClip clip   = await MediaClip.CreateFromFileAsync(file);
            var composition  = new MediaComposition();
            composition.Clips.Add(clip);

            // The Video's Own Shape, Made No Bigger Than The Long Side
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
        finally
        {
            // The Clip May Still Hold It; The Folder Is Emptied At Sign-Out, Start And Quit Anyway
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

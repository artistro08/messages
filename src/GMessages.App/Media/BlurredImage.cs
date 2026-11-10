using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace GMessages.App.Media;

/// <summary>
/// Makes a strongly blurred picture from image bytes, for the first look of a photo or a video.
/// </summary>
/// <remarks>
/// A Composition blur needs Win2D, which this app does not carry. A small picture that only stretches looks pixelated.
/// Thus the picture decodes to <see cref="ShortSide"/> pixels on its short side, and 3 box blurs in each direction make
/// it soft. That comes close to a Gaussian blur. The short side sets the size, so a tall screenshot is not a few pixels
/// wide. The long side stops at <see cref="MaxAspect"/> times that, so a thin strip or a long screenshot stays small. The
/// picture keeps its shape, so the crop of the bubble shows the same part as the photo. The encoder then grows the
/// blurred picture, so its short side is <see cref="OutShortSide"/>, or less for a picture longer than
/// <see cref="MaxAspect"/> to 1. Without that, the window stretched a picture a few pixels wide by 30 times or more,
/// which showed the pixels as blocks.
/// </remarks>
internal static class BlurredImage
{
    // The short side of the small picture that the blur runs on, and of the grown picture, in pixels. Each long side
    // stops at MaxAspect times its short side.
    private const int ShortSide    = 24;
    private const int OutShortSide = 64;
    private const int MaxAspect    = 4;

    // The radius of each box blur, in pixels of the small picture.
    private const int Radius = 3;

    // The blurred picture of each image, while its bytes live. A bubble that scrolls back into view then blurs nothing.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], byte[]> Made = [];

    /// <summary>
    /// Makes the blurred picture of image bytes, shown at its own pixel size. Call it on the UI thread.
    /// </summary>
    /// <param name="image">The bytes of a photo or a still frame.</param>
    /// <returns>The blurred picture, or null when the bytes are not an image.</returns>
    public static async Task<ImageSource?> MakeImageAsync(byte[] image)
    {
        if (await MakeAsync(image) is not { } png)
        {
            return null;
        }

        using IRandomAccessStream stream = new MemoryStream(png, writable: false).AsRandomAccessStream();
        var bitmap                       = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    // Makes the blurred picture as PNG bytes, or null when the bytes are not an image. The bytes can go to any thread,
    // so the cache keeps them.
    private static async Task<byte[]?> MakeAsync(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (Made.TryGetValue(image, out byte[]? made))
        {
            return made;
        }

        try
        {
            using IRandomAccessStream stream = new MemoryStream(image, writable: false).AsRandomAccessStream();
            BitmapDecoder decoder            = await BitmapDecoder.CreateAsync(stream);
            double scale                     = Math.Min(1, Math.Min(
                ShortSide / (double)Math.Max(1, Math.Min(decoder.PixelWidth, decoder.PixelHeight)),
                ShortSide * MaxAspect / (double)Math.Max(1, Math.Max(decoder.PixelWidth, decoder.PixelHeight))));
            var transform                    = new BitmapTransform
            {
                ScaledWidth       = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight      = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            using SoftwareBitmap small = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            int width     = small.PixelWidth;
            int height    = small.PixelHeight;
            byte[] pixels = new byte[width * height * 4];
            small.CopyToBuffer(pixels.AsBuffer());

            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(pixels, width, height, horizontal: true);
                BoxBlur(pixels, width, height, horizontal: false);
            }

            using var output      = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
            // The grow keeps the shape. A thin strip can be 1 pixel on its short side here, so its long side sets the
            // grow instead.
            double grow                               = Math.Min(OutShortSide / (double)Math.Min(width, height), OutShortSide * MaxAspect / (double)Math.Max(width, height));
            encoder.BitmapTransform.ScaledWidth       = (uint)Math.Max(1, Math.Round(width * grow));
            encoder.BitmapTransform.ScaledHeight      = (uint)Math.Max(1, Math.Round(height * grow));
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            await encoder.FlushAsync();

            using var copy = new MemoryStream();
            output.Seek(0);
            await output.AsStreamForRead().CopyToAsync(copy);
            byte[] png = copy.ToArray();
            Made.AddOrUpdate(image, png);
            return png;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // Blurs each row or each column with a moving sum. A pixel past the edge repeats the edge pixel.
    private static void BoxBlur(byte[] pixels, int width, int height, bool horizontal)
    {
        int lines  = horizontal ? height : width;
        int length = horizontal ? width : height;
        int step   = horizontal ? 4 : width * 4;
        int span   = (Radius * 2) + 1;
        int[] line = new int[length * 4];

        for (int l = 0; l < lines; l++)
        {
            int start = horizontal ? l * width * 4 : l * 4;
            for (int i = 0; i < length; i++)
            {
                for (int c = 0; c < 4; c++)
                {
                    line[(i * 4) + c] = pixels[start + (i * step) + c];
                }
            }

            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -Radius; k <= Radius; k++)
                {
                    sum += line[(Math.Clamp(k, 0, length - 1) * 4) + c];
                }

                for (int i = 0; i < length; i++)
                {
                    pixels[start + (i * step) + c] = (byte)(sum / span);
                    sum += line[(Math.Min(i + Radius + 1, length - 1) * 4) + c] - line[(Math.Max(i - Radius, 0) * 4) + c];
                }
            }
        }
    }
}

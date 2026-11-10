using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GMessages.App.Media;

/// <summary>
/// Makes a strongly blurred picture from image bytes, for the first look of a photo or a video.
/// </summary>
/// <remarks>
/// A Composition blur needs Win2D, which this app does not carry. A small picture that only stretches looks pixelated.
/// Thus the picture decodes to <see cref="LongSide"/> pixels on its long side, and 3 box blurs in each direction make it
/// soft. That comes close to a Gaussian blur. The stretch then only smooths it more.
/// </remarks>
internal static class BlurredImage
{
    /// <summary>The long side of the blurred picture in pixels. Decode the result at this width or less.</summary>
    public const int LongSide = 32;

    // The radius of each box blur, in pixels of the small picture.
    private const int Radius = 3;

    // The blurred picture of each image, while its bytes live. A bubble that scrolls back into view then blurs nothing.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], byte[]> Made = [];

    /// <summary>
    /// Makes the blurred picture as PNG bytes.
    /// </summary>
    /// <param name="image">The bytes of a photo or a still frame.</param>
    /// <returns>The PNG of the blurred picture, or null when the bytes are not an image.</returns>
    public static async Task<byte[]?> MakeAsync(byte[] image)
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
            double scale                     = Math.Min(1, LongSide / (double)Math.Max(1, Math.Max(decoder.PixelWidth, decoder.PixelHeight)));
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

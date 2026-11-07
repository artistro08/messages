using System.Runtime.CompilerServices;
using GMessages.Presentation.Messaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows a round avatar: a photo, an initial on a colored circle, or 2 stacked circles for a group.
/// </summary>
/// <remarks>
/// A nonzero <see cref="SecondColor"/> draws the group look. A smaller back circle sits at the top right, and the front
/// circle shrinks to the bottom left. When <see cref="Image"/> is set, it covers the initial of the front circle.
/// <para>
/// <see cref="PhotoId"/> and <see cref="SecondPhotoId"/> load the photos of the participants from <see cref="Cache"/>
/// into the front and back circles. The avatar drops an answer that arrives after the ID changes, as in a recycled list
/// row. While the front circle shows a photo, the UI Automation item status of the avatar is "Photo". The UI tests read
/// this status.
/// </para>
/// </remarks>
public sealed partial class Avatar : UserControl
{
    /// <summary>Identifies the <see cref="Initial"/> property.</summary>
    public static readonly DependencyProperty InitialProperty =
        DependencyProperty.Register(nameof(Initial), typeof(string), typeof(Avatar), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Color"/> property.</summary>
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(nameof(Color), typeof(uint), typeof(Avatar), new PropertyMetadata(0u));

    /// <summary>Identifies the <see cref="Image"/> property.</summary>
    public static readonly DependencyProperty ImageProperty =
        DependencyProperty.Register(nameof(Image), typeof(ImageSource), typeof(Avatar), new PropertyMetadata(null, OnImageChanged));

    /// <summary>Identifies the <see cref="SecondInitial"/> property.</summary>
    public static readonly DependencyProperty SecondInitialProperty =
        DependencyProperty.Register(nameof(SecondInitial), typeof(string), typeof(Avatar), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="SecondColor"/> property.</summary>
    public static readonly DependencyProperty SecondColorProperty =
        DependencyProperty.Register(nameof(SecondColor), typeof(uint), typeof(Avatar), new PropertyMetadata(0u));

    /// <summary>Identifies the <see cref="PhotoId"/> property.</summary>
    public static readonly DependencyProperty PhotoIdProperty =
        DependencyProperty.Register(nameof(PhotoId), typeof(string), typeof(Avatar), new PropertyMetadata(string.Empty, OnPhotoIdChanged));

    /// <summary>Identifies the <see cref="SecondPhotoId"/> property.</summary>
    public static readonly DependencyProperty SecondPhotoIdProperty =
        DependencyProperty.Register(nameof(SecondPhotoId), typeof(string), typeof(Avatar), new PropertyMetadata(string.Empty, OnPhotoIdChanged));

    /// <summary>Identifies the <see cref="PhotoSize"/> property.</summary>
    public static readonly DependencyProperty PhotoSizeProperty =
        DependencyProperty.Register(nameof(PhotoSize), typeof(int), typeof(Avatar), new PropertyMetadata(PhotoDecodeWidth));

    // The avatar decodes photos at the 40 px size of the circle in logical pixels. Logical pixels include the display
    // scale.
    private const int PhotoDecodeWidth = 40;

    // Holds the decoded photos by size, then by bytes. The photo cache returns the same array for an ID. Thus a photo
    // that showed before shows again at once. Without this table, a chat switch shows the letter for a few frames while
    // the photo decodes again. An entry lives as long as the photo cache keeps its bytes. Only the UI thread uses it.
    private static readonly Dictionary<int, ConditionalWeakTable<byte[], ImageSource>> Decoded = [];

    // A single avatar fills the 40 px square. The 2 circles of a group overlap inside it.
    private const double FullSize  = 40;
    private const double GroupSize = 28;

    /// <summary>
    /// Creates the avatar.
    /// </summary>
    public Avatar() => InitializeComponent();

    /// <summary>Gets or sets the letter of the front circle.</summary>
    public string Initial
    {
        get => (string)GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    /// <summary>Gets or sets the color of the front circle as opaque ARGB.</summary>
    public uint Color
    {
        get => (uint)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Gets or sets the photo that covers the front circle, or null for the initial.</summary>
    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <summary>Gets or sets the letter of the back circle. Only a group has a back circle.</summary>
    public string SecondInitial
    {
        get => (string)GetValue(SecondInitialProperty);
        set => SetValue(SecondInitialProperty, value);
    }

    /// <summary>Gets or sets the color of the back circle as opaque ARGB. The value 0 means no back circle.</summary>
    public uint SecondColor
    {
        get => (uint)GetValue(SecondColorProperty);
        set => SetValue(SecondColorProperty, value);
    }

    /// <summary>Gets or sets the participant whose photo covers the front circle, or empty for none.</summary>
    public string PhotoId
    {
        get => (string)GetValue(PhotoIdProperty);
        set => SetValue(PhotoIdProperty, value);
    }

    /// <summary>Gets or sets the participant whose photo covers the back circle, or empty for none.</summary>
    public string SecondPhotoId
    {
        get => (string)GetValue(SecondPhotoIdProperty);
        set => SetValue(SecondPhotoIdProperty, value);
    }

    /// <summary>
    /// Gets or sets the decode width of photos in effective pixels. The default is 40, the size of the avatar.
    /// </summary>
    /// <remarks>
    /// A <c>Viewbox</c> that draws the avatar larger needs a larger value, so that its photo stays sharp. Set this
    /// property before the photo IDs. The avatar keeps the decoded photos for each size. Thus all avatars of the same
    /// size share them.
    /// </remarks>
    public int PhotoSize
    {
        get => (int)GetValue(PhotoSizeProperty);
        set => SetValue(PhotoSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets the photo source: the cache of the open main window, or null while that window is closed.
    /// </summary>
    /// <remarks>
    /// At most 1 main window is open, so a single shared reference is enough. The window clears it when it closes.
    /// </remarks>
    public static AvatarCache? Cache { get; set; }

    /// <summary>
    /// Decodes photo bytes into a small image.
    /// </summary>
    /// <param name="photo">The image bytes.</param>
    /// <param name="decodeWidth">The decode width in effective pixels, or 0 for the avatar size.</param>
    /// <returns>The image, or null when the bytes are not an image.</returns>
    public static async Task<ImageSource?> ToImageAsync(byte[] photo, int decodeWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(photo);

        try
        {
            using IRandomAccessStream stream = new MemoryStream(photo, writable: false).AsRandomAccessStream();
            var image                        = new BitmapImage { DecodePixelWidth = decodeWidth > 0 ? decodeWidth : PhotoDecodeWidth, DecodePixelType = DecodePixelType.Logical };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The bytes are not an image, so the circle keeps its initial.
            return null;
        }
    }

    /// <summary>
    /// Decodes image bytes only as wide as the image shows. The method reads the size of the picture first.
    /// </summary>
    /// <remarks>
    /// A single stream reads the bytes and makes no copy. Its header gives the size of the picture after its
    /// orientation. Then the same stream decodes at the width that <paramref name="decodeWidth"/> picks from that size.
    /// When the header does not read, the size is 0 x 0, and the decode still tries.
    /// </remarks>
    /// <param name="photo">The image bytes.</param>
    /// <param name="decodeWidth">Picks the decode width from the width and height of the picture in pixels. The size is
    /// 0 x 0 when it is unknown. A result of 0 decodes at the full width.</param>
    /// <param name="type">Tells whether the decode width is in logical pixels or in physical pixels.</param>
    /// <returns>The image, the width and height of the picture, and the decode width. The image is null when the bytes
    /// are not an image.</returns>
    public static async Task<(ImageSource? Image, int Width, int Height, int DecodeWidth)> ToFittedImageAsync(
        byte[] photo, Func<int, int, int> decodeWidth, DecodePixelType type = DecodePixelType.Logical)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(decodeWidth);

        (int width, int height, int decode) = (0, 0, 0);
        try
        {
            using IRandomAccessStream stream = new MemoryStream(photo, writable: false).AsRandomAccessStream();
            try
            {
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                (width, height)       = ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The size stays unknown. The decode below still tries.
            }

            stream.Seek(0);
            decode    = decodeWidth(width, height);
            var image = new BitmapImage { DecodePixelWidth = decode, DecodePixelType = type };
            await image.SetSourceAsync(stream);
            return (image, width, height, decode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, width, height, decode);
        }
    }

    /// <summary>
    /// Shows the back circle only when it has a color.
    /// </summary>
    /// <param name="color">The color of the back circle.</param>
    /// <returns>Visible for a nonzero color.</returns>
    public static Visibility ShownIfColor(uint color) => color != 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gives the size of the front circle. The circle has the full size alone and a smaller size beside a back circle.
    /// </summary>
    /// <param name="secondColor">The color of the back circle.</param>
    /// <returns>The size in effective pixels.</returns>
    public static double FrontSize(uint secondColor) => secondColor != 0 ? GroupSize : FullSize;

    /// <summary>
    /// Gives the font size of the front initial to match the circle.
    /// </summary>
    /// <param name="secondColor">The color of the back circle.</param>
    /// <returns>The font size.</returns>
    public static double InitialSize(uint secondColor) => secondColor != 0 ? 12 : 16;

    /// <summary>
    /// Shows the filled person icon in place of a letter for a person with no name. The initial is empty when the phone
    /// has only a number or a short code.
    /// </summary>
    /// <param name="initial">The letter of the circle.</param>
    /// <returns>Visible when there is no letter.</returns>
    public static Visibility PersonIfNoInitial(string initial) => string.IsNullOrEmpty(initial) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gives the size of the person icon in the front circle to match the circle.
    /// </summary>
    /// <param name="secondColor">The color of the back circle.</param>
    /// <returns>The icon size.</returns>
    public static double PersonSize(uint secondColor) => secondColor != 0 ? 14 : 20;

    /// <summary>
    /// Loads again each missing photo under <paramref name="root"/>, such as a photo that failed while the phone was
    /// away. The method does not change a photo that shows.
    /// </summary>
    /// <param name="root">The element that holds the avatars, such as the root grid of a window.</param>
    public static void ReloadMissing(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root is Avatar avatar)
        {
            if (avatar.Image is null && avatar.PhotoId.Length > 0)
            {
                _ = avatar.LoadPhotoAsync(front: true);
            }

            if (avatar.SecondPhoto.ImageSource is null && avatar.SecondPhotoId.Length > 0)
            {
                _ = avatar.LoadPhotoAsync(front: false);
            }

            return;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            ReloadMissing(VisualTreeHelper.GetChild(root, i));
        }
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    // The item status tells UI Automation that the front circle shows a photo.
    private static void OnImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        AutomationProperties.SetItemStatus(d, e.NewValue is null ? string.Empty : "Photo");

    private static void OnPhotoIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        _ = ((Avatar)d).LoadPhotoAsync(front: e.Property == PhotoIdProperty);

    // Shows a decoded photo at once. Otherwise the method clears the photo of the circle and decodes the new photo. It
    // shows the new photo only when the ID did not change during the load.
    private async Task LoadPhotoAsync(bool front)
    {
        string id = front ? PhotoId : SecondPhotoId;
        if (string.IsNullOrEmpty(id) || Cache is not { } cache)
        {
            SetPhoto(front, null);
            return;
        }

        int size = PhotoSize;
        if (!Decoded.TryGetValue(size, out ConditionalWeakTable<byte[], ImageSource>? decoded))
        {
            decoded       = [];
            Decoded[size] = decoded;
        }

        Task<byte[]?> lookup = cache.GetAsync(id, CancellationToken.None);
        if (lookup.IsCompletedSuccessfully && lookup.Result is { } known && decoded.TryGetValue(known, out ImageSource? shown))
        {
            SetPhoto(front, shown);
            return;
        }

        SetPhoto(front, null);
        byte[]? photo = await lookup;
        if (photo is null || id != (front ? PhotoId : SecondPhotoId))
        {
            return;
        }

        ImageSource? image = await ToImageAsync(photo, size);
        if (image is not null)
        {
            decoded.AddOrUpdate(photo, image);
        }

        if (id == (front ? PhotoId : SecondPhotoId))
        {
            SetPhoto(front, image);
        }
    }

    private void SetPhoto(bool front, ImageSource? image)
    {
        if (front)
        {
            Image = image;
            return;
        }

        SecondPhoto.ImageSource = image;
    }
}

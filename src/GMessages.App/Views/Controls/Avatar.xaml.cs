using System.Runtime.InteropServices.WindowsRuntime;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace GMessages.App.Views.Controls;

/// <summary>
/// A round avatar: a photo, or a colored circle with an initial, or two stacked circles for a group.
/// </summary>
/// <remarks>
/// A nonzero <see cref="SecondColor"/> draws the group look: a smaller back circle at the top right and the front
/// circle shrunk to the bottom left. <see cref="Image"/>, when set, covers the front circle's initial.
/// <para>
/// <see cref="PhotoId"/> and <see cref="SecondPhotoId"/> load the participants' photos from <see cref="Cache"/> into
/// the front and back circles; an answer that arrives after the ID changed (a recycled list row) is dropped. While the
/// front circle shows a photo, the avatar's UI Automation item status is "Photo" (for the UI tests).
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

    // Photos Are Decoded At Twice The 40 px Circle, Enough For 200% Scaling
    private const int PhotoDecodeWidth = 80;

    // Circle Sizes: One Avatar Fills The 40 px Square; A Group's Two Circles Overlap Inside It
    private const double FullSize  = 40;
    private const double GroupSize = 28;

    /// <summary>
    /// Creates the avatar.
    /// </summary>
    public Avatar() => InitializeComponent();

    /// <summary>Gets or sets the front circle's letter.</summary>
    public string Initial
    {
        get => (string)GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    /// <summary>Gets or sets the front circle's color as opaque ARGB.</summary>
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

    /// <summary>Gets or sets the back circle's letter (groups only).</summary>
    public string SecondInitial
    {
        get => (string)GetValue(SecondInitialProperty);
        set => SetValue(SecondInitialProperty, value);
    }

    /// <summary>Gets or sets the back circle's color as opaque ARGB; 0 means no back circle.</summary>
    public uint SecondColor
    {
        get => (uint)GetValue(SecondColorProperty);
        set => SetValue(SecondColorProperty, value);
    }

    /// <summary>Gets or sets the participant whose photo covers the front circle; empty for none.</summary>
    public string PhotoId
    {
        get => (string)GetValue(PhotoIdProperty);
        set => SetValue(PhotoIdProperty, value);
    }

    /// <summary>Gets or sets the participant whose photo covers the back circle; empty for none.</summary>
    public string SecondPhotoId
    {
        get => (string)GetValue(SecondPhotoIdProperty);
        set => SetValue(SecondPhotoIdProperty, value);
    }

    /// <summary>
    /// Gets or sets where photos come from: the open main window's cache, or null while it is closed.
    /// </summary>
    /// <remarks>One main window at most is open, so one shared reference does; the window clears it on close.</remarks>
    public static AvatarCache? Cache { get; set; }

    /// <summary>
    /// Turns photo bytes into an image, decoded small.
    /// </summary>
    /// <param name="photo">The image bytes.</param>
    /// <returns>The image, or null when the bytes are not an image.</returns>
    public static async Task<ImageSource?> ToImageAsync(byte[] photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(photo.AsBuffer());
            stream.Seek(0);

            var image = new BitmapImage { DecodePixelWidth = PhotoDecodeWidth, DecodePixelType = DecodePixelType.Logical };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not An Image: Keep The Initial
            return null;
        }
    }

    /// <summary>
    /// Shows the back circle only when it has a color.
    /// </summary>
    /// <param name="color">The back circle's color.</param>
    /// <returns>Visible for a nonzero color.</returns>
    public static Visibility ShownIfColor(uint color) => color != 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gives the front circle's size: full for one avatar, smaller when a back circle shares the square.
    /// </summary>
    /// <param name="secondColor">The back circle's color.</param>
    /// <returns>The size in effective pixels.</returns>
    public static double FrontSize(uint secondColor) => secondColor != 0 ? GroupSize : FullSize;

    /// <summary>
    /// Gives the front initial's font size to match the circle.
    /// </summary>
    /// <param name="secondColor">The back circle's color.</param>
    /// <returns>The font size.</returns>
    public static double InitialSize(uint secondColor) => secondColor != 0 ? 12 : 16;

    /// <summary>
    /// Shows the filled person icon in place of a letter for someone with no name (an empty initial: the phone only has
    /// their number or a short code).
    /// </summary>
    /// <param name="initial">The circle's letter.</param>
    /// <returns>Visible when there is no letter.</returns>
    public static Visibility PersonIfNoInitial(string initial) => string.IsNullOrEmpty(initial) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gives the front circle's person icon size to match the circle.
    /// </summary>
    /// <param name="secondColor">The back circle's color.</param>
    /// <returns>The icon size.</returns>
    public static double PersonSize(uint secondColor) => secondColor != 0 ? 14 : 20;

    /// <summary>
    /// Loads again every photo under <paramref name="root"/> that is still missing, such as one that failed while the
    /// phone was away. Photos already showing are left alone.
    /// </summary>
    /// <param name="root">Where to look for avatars (a window's root grid).</param>
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

    // =========================================================================
    // PHOTOS
    // =========================================================================

    // Says "Photo" To UI Automation While The Front Circle Shows One
    private static void OnImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        AutomationProperties.SetItemStatus(d, e.NewValue is null ? string.Empty : "Photo");

    private static void OnPhotoIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        _ = ((Avatar)d).LoadPhotoAsync(front: e.Property == PhotoIdProperty);

    // Clears The Circle's Photo, Then Shows The New One Unless The ID Changed Meanwhile
    private async Task LoadPhotoAsync(bool front)
    {
        string id = front ? PhotoId : SecondPhotoId;
        SetPhoto(front, null);
        if (string.IsNullOrEmpty(id) || Cache is not { } cache)
        {
            return;
        }

        byte[]? photo = await cache.GetAsync(id, CancellationToken.None);
        if (photo is null || id != (front ? PhotoId : SecondPhotoId))
        {
            return;
        }

        ImageSource? image = await ToImageAsync(photo);
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

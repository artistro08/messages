using System.Numerics;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The bubble's photo: loading, sizing and clipping it, a GIF's pause when out of sight, and opening the viewer.
/// </summary>
/// <remarks>
/// Part of <see cref="MessageBubble"/>; the other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{

    // =========================================================================
    // PHOTO
    // =========================================================================

    // Another Photo, Or The Same One With Its Full Size Now (The Phone's Edit Swaps The Media Part): Load It. The Same
    // Message Keeps Its Blurred Look While The Full Image Loads; Another One Starts Clear. The Echo Of A Photo Sent From
    // Here Keeps What Shows: Its Bytes Are The Photo, So They Go In The Cache Under Its New Media ID Instead Of A Download
    private void ShowPhoto()
    {
        MediaPart? part = Bubble is { HasPhoto: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _photoPart) && ReferenceEquals(Bubble, _photoBubble))
        {
            return;
        }

        // No Photo: The Part Unloads With Its Images (x:Load), And A GIF's Clip Visual With It; A Load Still Running Stops
        if (part is null)
        {
            _photoPart       = null;
            _photoBubble     = Bubble;
            _photoFailed     = false;
            _photoAnimated   = false;
            _photoFullVisual = null;
            NextPhotoLoad();
            WatchGif(null);
            return;
        }

        FindName(nameof(Photo));
        bool sameMessage = Bubble is not null && ReferenceEquals(Bubble, _photoBubble);
        if (sameMessage && KeepSentMedia(_photoPart, part))
        {
            // An Echo With Only A Thumbnail (An MMS Photo's) Keeps The Sent Bytes On Show: Loading It Showed The Tiny
            // Thumbnail Blurred, Waiting For A Full Size That May Never Come
            _photoPart = part.MediaId.Length > 0 ? part : _photoPart;
            SizePhoto();
            return;
        }

        _photoPart   = part;
        _photoBubble = Bubble;
        _photoFailed = false;
        int load     = NextPhotoLoad();

        // Another Message, Or None (A Bubble Without A Photo): Let Go Of The Old Images
        if (!sameMessage)
        {
            PhotoBlur.Background        = null;
            PhotoFull.Background        = null;
            PhotoFullImage.Source       = null;
            PhotoFull.OpacityTransition = null;
            PhotoFull.Opacity           = 0;
            _photoAnimated              = false;
            WatchGif(null);
            ClipPhotoFull();
        }

        ShowPhotoNote(null, string.Empty);
        SizePhoto();
        _ = LoadPhotoAsync(part, load, again: false);
    }

    // The Same Media Though The Bubble's Update Built A New Part (A Status Or Reaction Change): Nothing To Load Again.
    // Inline Bytes Count By Reference, So Two Pending Parts With One Name Still Differ
    private static bool SameMedia(MediaPart? a, MediaPart? b) =>
        a is null || b is null ? a is null && b is null
            : a.MediaId == b.MediaId && a.ThumbnailId == b.ThumbnailId && a.Name == b.Name
              && (ReferenceEquals(a.InlineData, b.InlineData) || (a.InlineData.Length == 0 && b.InlineData.Length == 0));

    // The Echo Of An Attachment Sent From Here: The Shown Part Held Its Bytes, And The New One Has The Media ID They
    // Uploaded As (The Bytes Go In The Cache Under That ID, So Nothing Downloads Again), Or Only A Thumbnail (The Bytes
    // Keep Showing); True When So
    private static bool KeepSentMedia(MediaPart? shown, MediaPart? echo)
    {
        if (shown is not { InlineData.Length: > 0 } local || echo is not { InlineData.Length: 0 } || echo is { MediaId.Length: 0, ThumbnailId.Length: 0 })
        {
            return false;
        }

        (Application.Current as App)?.Media?.Seed(echo.MediaId, local.InlineData);
        return true;
    }

    // Starts A Photo Load: Its Number, With An Older Load's Wait For The Full Size Cancelled
    private int NextPhotoLoad()
    {
        _cancelPhotoWait?.Invoke();
        _cancelPhotoWait = null;
        return ++_photoLoad;
    }

    // The Photo's Or Video's Size From Its Dimensions, Fitted To The Bubble (See MessageBubbleViewModel.PhotoSize). A
    // Photo's Width Is Its Least: A Wider Caption Or Quote Stretches It To The Bubble's Width, Cropped To Its Height
    // (Issue #30). A Video Keeps Its Size
    private void SizePhoto()
    {
        if (Bubble is not { FillsBubble: true, Media: { } media } bubble)
        {
            return;
        }

        (double width, double height) = MessageBubbleViewModel.PhotoSize(media.Width, media.Height, _photoMaxWidth);
        FrameworkElement? shown       = bubble.HasVideo ? Video : Photo;
        if (shown is null)
        {
            return;
        }

        if (bubble.HasVideo)
        {
            shown.Width = width;
        }
        else
        {
            shown.MinWidth = width;
        }

        shown.Height = height;
    }

    // Blurred First, Then Sharp. A Thumbnail-Only Photo Asks The Phone For Its Full Size Instead, Which Comes Back As A New
    // Media Part (ShowPhoto Then Loads Again); If It Never Comes, The Photo Offers To Try Again. Any Failure Keeps What
    // Shows And Says So; A Click Tries Again
    private async Task LoadPhotoAsync(MediaPart part, int load, bool again)
    {
        if (part.IsPending)
        {
            ShowPhotoNote(ChatCopy.PhotoPending, "Pending");
            return;
        }

        if (Bubble is not { } bubble || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        try
        {
            // Blurred: The Thumbnail (Or The Photo Itself When No Thumbnail Came), Decoded Tiny And Stretched
            if (PhotoBlur.Background is null)
            {
                byte[] small     = await media.GetAsync(part, thumbnail: true, CancellationToken.None);
                ImageSource blur = await Avatar.ToImageAsync(small, BlurDecodeWidth) ?? throw new InvalidDataException();
                if (load != _photoLoad)
                {
                    return;
                }

                PhotoBlur.Background = new ImageBrush { ImageSource = blur, Stretch = Stretch.UniformToFill };
                TestHooks.ShowState(Photo, "Blurred");
            }

            // Only The Thumbnail Came: Ask For The Full Size, And Offer To Try Again If It Doesn't Come
            if (part is { MediaId.Length: 0, ThumbnailId.Length: > 0 })
            {
                await bubble.RequestFullSizeAsync(media, again);
                if (load != _photoLoad)
                {
                    return;
                }

                using (var wait = new CancellationTokenSource())
                {
                    _cancelPhotoWait = wait.Cancel;
                    try
                    {
                        await Task.Delay(TestHooks.ShortWait("full-size-wait") ?? FullSizeWait, wait.Token);
                    }
                    finally
                    {
                        _cancelPhotoWait = load == _photoLoad ? null : _cancelPhotoWait;
                    }
                }

                if (load == _photoLoad)
                {
                    ShowPhotoFailed();
                }

                return;
            }

            // Sharp: Decoded At The Photo's Size. One Already In Memory (A Recycled Row Showing It Again) Shows At Once
            Task<byte[]> fullTask  = media.GetAsync(part, thumbnail: false, CancellationToken.None);
            bool atOnce            = fullTask.IsCompleted;
            byte[] full            = await fullTask;
            long decodeStart       = System.Diagnostics.Stopwatch.GetTimestamp();
            if (load != _photoLoad)
            {
                return;
            }

            // Its Size Read From The Same Stream It Decodes From, Then Decoded To Fill The Box
            double boxWidth  = Math.Max(Photo.ActualWidth, Photo.MinWidth);
            double boxHeight = Photo.Height;
            (ImageSource? fitted, int pixelWidth, int pixelHeight, int decodeWidth) =
                await Avatar.ToFittedImageAsync(full, (w, h) => MessageBubbleViewModel.FillDecodeWidth(w, h, boxWidth, boxHeight));
            ImageSource image = fitted ?? throw new InvalidDataException();
            long decodeMs     = (long)System.Diagnostics.Stopwatch.GetElapsedTime(decodeStart).TotalMilliseconds;
            AppLog.PhotoDecoded(App.ShellLog, bubble.Id, full.Length, part.Size, pixelWidth, pixelHeight, decodeWidth, decodeMs);
            if (load != _photoLoad)
            {
                return;
            }

            // A Moving GIF Plays In An Image (A Brush Shows Only Its First Frame; Test Runs Note It); A Still Photo Is The
            // Layer's Brush, Which The Border's Corners Clip With No Composition Clip
            BitmapImage? gif            = image.As<BitmapImage>() is { IsAnimatedBitmap: true } moving ? moving : null;
            bool animated               = gif is not null;
            _photoAnimated              = animated;
            WatchGif(gif);
            PhotoFullImage.Source       = animated ? image : null;
            PhotoFull.Background        = animated ? null : new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
            PhotoFull.OpacityTransition = atOnce || !Ui.AnimationsEnabled ? null : _photoFade;
            PhotoFull.Opacity           = 1;
            ClipPhotoFull(mayTake: true);
            TestHooks.ShowState(Photo, "Full");
            if (animated)
            {
                TestHooks.NoteState("animated-photo", bubble.Id);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (load != _photoLoad)
            {
                return;
            }

            AppLog.MediaFailed(App.ShellLog, "photo", bubble.Id, ex.GetType().Name);
            ShowPhotoFailed();
        }
    }

    // A GIF's Image Clipped To The Photo's Rounded Corners (An Image Isn't Clipped By Its Border's Corners); A Still
    // Photo's Brush Needs No Clip, So Any Left From A GIF This Container Showed Goes.
    // The Clip Goes On The Image, Never On PhotoFull: PhotoFull Fades In Through OpacityTransition, A XAML Implicit
    // Transition, And Windows Refuses The Composition Visual Of An Element That Uses One (E_ACCESSDENIED, Reproduced By
    // ManyPhotos_FastScroll_NoCrash: Every Failure Was A Faded-In GIF's PhotoFull, Loaded And Laid Out). In 0.6.2 That
    // Throw Escaped SizeChanged And x:Bind Callbacks Into XAML And Crashed The App (0xc000027b). The Visual Is Taken Once,
    // For A GIF, On Loaded Or When It Arrives; Size And Corner Changes Only Reshape The Clip
    private void ClipPhotoFull(bool mayTake = false)
    {
        if (PhotoFullImage is null || Bubble is not { } bubble || (_photoFullVisual is null && (!mayTake || !_photoAnimated || !PhotoFullImage.IsLoaded)))
        {
            return;
        }

        try
        {
            _photoFullVisual ??= ElementCompositionPreview.GetElementVisual(PhotoFullImage);
            Visual visual      = _photoFullVisual;

            // The Photo's Corners From The Bubble Itself, Not The Bound Ones, Which May Not Have Caught Up With A Change Yet.
            // Not Laid Out Yet: Its SizeChanged Clips It Later
            CornerRadius r = PhotoCorners(bubble.FromMe, bubble.IsFirstInGroup, bubble.IsLastInGroup, bubble.HasBodyText);
            if (!_photoAnimated || !ClipRounded(visual, PhotoFullImage, r))
            {
                visual.Clip = null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Square Corners Beat A Crash; The Next Size Change Tries Again
            AppLog.MediaFailed(App.ShellLog, "clip", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }
    }

    // Rounds A Visual To An Element's Size With The Given Corners; False When The Element Isn't Laid Out Yet (Nothing
    // Changed). A Corner Can't Be Rounder Than Half The Shorter Side, Or The Clip Is Refused (Which Crashed The App On
    // Opening A Chat)
    private static bool ClipRounded(Visual visual, FrameworkElement element, CornerRadius corners)
    {
        float width  = (float)element.ActualWidth;
        float height = (float)element.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        float most  = Math.Min(width, height) / 2;
        visual.Clip = visual.Compositor.CreateRectangleClip(
            0, 0, width, height,
            Corner(corners.TopLeft, most), Corner(corners.TopRight, most), Corner(corners.BottomRight, most), Corner(corners.BottomLeft, most));
        return true;

        static Vector2 Corner(double radius, float most) => new(Math.Min((float)radius, most));
    }

    // Follows Where The Moving GIF Shown Is (Null For None), So It Pauses Off Screen (A Realized Row Off Screen Isn't
    // Recycled At Once, And Its GIF Kept Playing)
    private void WatchGif(BitmapImage? gif)
    {
        if (ReferenceEquals(gif, _gif))
        {
            return;
        }

        if (_gif is null)
        {
            EffectiveViewportChanged += OnGifViewportChanged;
        }
        else if (gif is null)
        {
            EffectiveViewportChanged -= OnGifViewportChanged;
        }

        _gif       = gif;
        _gifPaused = false;
    }

    // Off Screen: The GIF Pauses; Back On Screen, One Paused Here Plays Again (One Windows Never Played, With Animation
    // Effects Off, Stays Still)
    private void OnGifViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (_gif is not { } gif)
        {
            return;
        }

        bool outOfSight = OutOfSight(args.EffectiveViewport);
        if (outOfSight && gif.IsPlaying)
        {
            gif.Stop();
            _gifPaused = true;
        }
        else if (!outOfSight && _gifPaused)
        {
            gif.Play();
            _gifPaused = false;
        }
    }

    // Whether None Of The Bubble Is In The Viewport Seen
    private bool OutOfSight(Windows.Foundation.Rect seen) =>
        seen.Width <= 0 || seen.Height <= 0 || seen.Bottom <= 0 || seen.Top >= ActualHeight || seen.Right <= 0 || seen.Left >= ActualWidth;

    private void ShowPhotoFailed()
    {
        _photoFailed = true;
        ShowPhotoNote(ChatCopy.PhotoLoadFailed, "Failed");
    }

    // The Note Over The Photo (Waiting, Or Failed), Or None
    private void ShowPhotoNote(string? text, string state)
    {
        PhotoNote.Text           = text ?? string.Empty;
        PhotoNoteBack.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        TestHooks.ShowState(Photo, state);
    }

    // After A Failure, Try Again (Asking The Phone Again For A Full Size That Never Came); While Waiting For The Phone,
    // Nothing; Otherwise Show It In The Window's Photo Viewer
    private void OnPhotoClick(object sender, RoutedEventArgs e)
    {
        if (_photoPart is not { IsPending: false } part || sender is not Control photo)
        {
            return;
        }

        if (_photoFailed)
        {
            _photoFailed = false;
            ShowPhotoNote(null, string.Empty);
            _ = LoadPhotoAsync(part, NextPhotoLoad(), again: true);
            return;
        }

        ShowInViewer(part, photo);
    }

    // The Photo Over The Whole Window, Named As The Bubble's Photo Is; Focus Goes Back To The Part It Opened From
    private void ShowInViewer(MediaPart part, Control from) =>
        (Application.Current as App)?.MainWindow?.ShowPhoto(part, Bubble?.Id ?? string.Empty, Bubble?.PhotoName ?? ChatCopy.Photo, from);
}

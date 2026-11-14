using System.Numerics;
using GMessages.App.Media;
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
/// Loads, sizes and clips the photo of the bubble, pauses a GIF out of sight, and opens the viewer.
/// </summary>
/// <remarks>
/// This file is a part of <see cref="MessageBubble"/>. The other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{
    // Loads another photo, or the same photo when its full size arrives. The edit of the phone then swaps the media
    // part. The same message keeps its blurred look while the full image loads. Another message starts clear. The echo
    // of a photo sent from here keeps what shows. Its bytes are the photo, so they go in the cache under its new media
    // ID, and nothing downloads.
    private void ShowPhoto()
    {
        MediaPart? part = Bubble is { HasPhoto: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _photoPart) && ReferenceEquals(Bubble, _photoBubble))
        {
            return;
        }

        // With no photo, x:Load unloads the part with its images and the clip visual of a GIF. A load that still runs
        // stops.
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
            // An echo with only a thumbnail, as for an MMS photo, keeps the sent bytes on show. A load of the echo
            // showed the tiny thumbnail blurred. It then waited for a full size that can never come.
            _photoPart = part.MediaId.Length > 0 ? part : _photoPart;
            SizePhoto();
            return;
        }

        _photoPart             = part;
        _photoBubble           = Bubble;
        _photoFailed           = false;
        _photoUnavailable      = false;
        Photo.IsHitTestVisible = true;
        int load               = NextPhotoLoad();

        // For another message, the bubble releases the old images.
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

        ShowPhotoNote(null, MediaNote.None);
        SizePhoto();
        _ = LoadPhotoAsync(part, load, again: false);
    }

    // Returns true when 2 parts hold the same media, so that nothing loads again. A status or reaction change makes a
    // new part with the same media. Inline bytes compare by reference, so 2 pending parts with 1 name still differ.
    private static bool SameMedia(MediaPart? a, MediaPart? b) =>
        a is null || b is null ? a is null && b is null
            : a.MediaId == b.MediaId && a.ThumbnailId == b.ThumbnailId && a.Name == b.Name
              && (ReferenceEquals(a.InlineData, b.InlineData) || (a.InlineData.Length == 0 && b.InlineData.Length == 0));

    // Returns true for the echo of an attachment sent from here, so the sent bytes keep showing. See
    // MediaPart.KeepsShowingOver. An echo with the media ID of the upload gets the bytes in the cache under that ID, so
    // nothing downloads again. The caller keeps its own part unless the echo has a media ID.
    private static bool KeepSentMedia(MediaPart? shown, MediaPart? echo)
    {
        if (shown is null || echo is null || !shown.KeepsShowingOver(echo))
        {
            return false;
        }

        (Application.Current as App)?.Media?.Seed(echo.MediaId, shown.InlineData);
        return true;
    }

    // Starts a photo load and gives its number. The method cancels the wait of an older load for the full size.
    private int NextPhotoLoad()
    {
        int load = ++_photoLoad;
        _cancelPhotoWait?.Invoke();
        _cancelPhotoWait = null;
        return load;
    }

    // Sizes the photo or the video from its dimensions, fitted to the bubble. See MessageBubbleViewModel.PhotoSize. The
    // width of a photo is its minimum. A wider caption or quote stretches the photo to the width of the bubble, cropped
    // to its height. See issue #30. A video keeps its size.
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

    // Shows the photo blurred first, then sharp. A thumbnail-only photo asks the phone for its full size instead. The
    // full size arrives as a new media part, and ShowPhoto then loads again. When it never comes, the photo offers
    // to try again. Any failure keeps what shows and says so, and a click tries again.
    private async Task LoadPhotoAsync(MediaPart part, int load, bool again)
    {
        if (part.IsPending)
        {
            ShowPhotoNote(ChatCopy.PhotoPending, MediaNote.Pending);
            WhenUnavailable(part, () => _photoPart, ShowPhotoUnavailable);
            return;
        }

        if (Bubble is not { } bubble || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        try
        {
            // The blurred look is the thumbnail, blurred and stretched. With no thumbnail, the photo itself takes its
            // place.
            if (PhotoBlur.Background is null)
            {
                byte[] small     = await media.GetAsync(part, thumbnail: true, CancellationToken.None);
                ImageSource blur = await BlurredImage.MakeImageAsync(small) ?? throw new InvalidDataException();
                if (load != _photoLoad)
                {
                    return;
                }

                PhotoBlur.Background = new ImageBrush { ImageSource = blur, Stretch = Stretch.UniformToFill };
                TestHooks.ShowState(Photo, "Blurred");
            }

            // Only the thumbnail or a small preview came, so the bubble asks for the full size. The echo with the file
            // loads the photo again. When it does not come, the photo offers to try again.
            if (part.NeedsFullSize)
            {
                // A photo with only a preview says that it waits, as a waiting video does. It still asks for its full
                // size. When the phone never sends it, it says that it is not available.
                if (part.IsWaitingForFile)
                {
                    ShowPhotoNote(ChatCopy.PhotoPending, MediaNote.Pending);
                    WhenUnavailable(part, () => _photoPart, ShowPhotoUnavailable);
                }

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

            // The sharp image decodes at the size of the photo. An image already in memory shows at once with no fade,
            // as on a recycled row that shows it again.
            Task<byte[]> fullTask  = media.GetAsync(part, thumbnail: false, CancellationToken.None);
            bool atOnce            = fullTask.IsCompleted;
            byte[] full            = await fullTask;
            long decodeStart       = System.Diagnostics.Stopwatch.GetTimestamp();
            if (load != _photoLoad)
            {
                return;
            }

            // The decoder reads the size from the stream that it decodes. The photo then decodes to fill the box, at
            // the box width, so it is not blurred.
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

            // A moving GIF plays in an Image, because a brush shows only its first frame. Test runs note a GIF. A still
            // photo is the brush of the layer. The corners of the Border clip it with no Composition clip.
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
            // A photo that is not available stopped its own wait for the full size. That is no failure.
            if (load != _photoLoad || _photoUnavailable)
            {
                return;
            }

            AppLog.MediaFailed(App.ShellLog, "photo", bubble.Id, ex.GetType().Name);
            ShowPhotoFailed();
        }
    }

    // Sizes the Image of a GIF to the photo box and clips it to the rounded corners of the photo. The corners of its
    // Border do not clip an Image. A still photo is a brush and needs no clip. Thus the method removes a clip that a
    // GIF of this container left.
    // The clip goes on the Image, never on PhotoFull. PhotoFull fades in through OpacityTransition, a XAML implicit
    // transition. Windows refuses the Composition visual of an element that uses one, with E_ACCESSDENIED.
    // ManyPhotos_FastScroll_NoCrash reproduced it: every failure was the PhotoFull of a faded-in GIF, loaded and laid
    // out. In 0.6.2 that throw escaped SizeChanged and x:Bind callbacks into XAML and crashed the app with 0xc000027b.
    // The method takes the visual once for a GIF, on Loaded or when the GIF arrives. Changes of size and corners only
    // reshape the clip. The Image and its clip take the size of the Canvas, which is the photo box. The laid-out size
    // of the Image can be older than the box.
    private void ClipPhotoFull(bool mayTake = false)
    {
        if (PhotoFullImage is null)
        {
            return;
        }

        // An Image with UniformToFill and no size asks for all the width of the row.
        double width  = PhotoFullCanvas.ActualWidth;
        double height = PhotoFullCanvas.ActualHeight;
        if (_photoAnimated && width > 0 && height > 0)
        {
            PhotoFullImage.Width  = width;
            PhotoFullImage.Height = height;
        }

        if (Bubble is not { } bubble || (_photoFullVisual is null && (!mayTake || !_photoAnimated || !PhotoFullImage.IsLoaded)))
        {
            return;
        }

        try
        {
            _photoFullVisual ??= ElementCompositionPreview.GetElementVisual(PhotoFullImage);
            Visual visual      = _photoFullVisual;

            // The corners come from the bubble itself, not from the bound corners. The bound corners can still show the
            // state before a change. A box that is not laid out yet gets its clip later from SizeChanged.
            CornerRadius r = PhotoCorners(bubble.FromMe, bubble.IsFirstInGroup, bubble.IsLastInGroup, bubble.HasBodyText, bubble.HasQuote);
            if (!_photoAnimated || !ClipRounded(visual, PhotoFullCanvas, r))
            {
                visual.Clip = null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Square corners are better than a crash. The next size change tries again.
            AppLog.MediaFailed(App.ShellLog, "clip", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }
    }

    // Rounds a visual to the size of an element with the given corners. Returns false and changes nothing when the
    // element is not laid out yet. A corner radius must not be more than half the shorter side. Windows refuses such a
    // clip, and that refusal crashed the app when a chat opened.
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

    // Follows the position of the moving GIF on show, so that it pauses off screen. Null means no GIF. The list does
    // not recycle a realized row off screen at once, and its GIF kept playing.
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

    // An animated GIF pauses off screen and plays again on screen. Only a GIF that this method paused plays again. With
    // animation effects off, Windows never plays a GIF, and the GIF stays still.
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

    // Returns true when no part of the bubble is in the effective viewport.
    private bool OutOfSight(Windows.Foundation.Rect seen) =>
        seen.Width <= 0 || seen.Height <= 0 || seen.Bottom <= 0 || seen.Top >= ActualHeight || seen.Right <= 0 || seen.Left >= ActualWidth;

    private void ShowPhotoFailed()
    {
        // A photo that is not available stays so. A retry cannot bring it.
        if (_photoUnavailable)
        {
            return;
        }

        _photoFailed = true;
        ShowPhotoNote(ChatCopy.PhotoLoadFailed, MediaNote.Failed);
    }

    // The phone never sends the photo, so it says so and takes no click. Its wait for the full size stops.
    private void ShowPhotoUnavailable()
    {
        _photoUnavailable      = true;
        _photoFailed           = false;
        Photo.IsHitTestVisible = false;
        _cancelPhotoWait?.Invoke();
        ShowPhotoNote(ChatCopy.PhotoUnavailable, MediaNote.Unavailable);
    }

    // Shows the note over the photo for a wait or a failure, or no note. The ring turns during the wait.
    private void ShowPhotoNote(string? text, MediaNote note)
    {
        bool busy                = note == MediaNote.Pending;
        PhotoNote.Text           = text ?? string.Empty;
        PhotoNoteBack.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        PhotoNoteRing.IsActive   = busy;
        PhotoNoteRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TestHooks.ShowState(Photo, StateOf(note));
    }

    // After a failure, a click tries again and asks the phone again for a missing full size. While the photo waits for
    // the phone, a click does nothing. Else the photo shows in the photo viewer of the window.
    private void OnPhotoClick(object sender, RoutedEventArgs e)
    {
        if (_photoPart is not { IsPending: false } part || sender is not Control photo)
        {
            return;
        }

        if (_photoFailed)
        {
            _photoFailed = false;
            ShowPhotoNote(null, MediaNote.None);
            _ = LoadPhotoAsync(part, NextPhotoLoad(), again: true);
            return;
        }

        // A photo that has only its preview has nothing sharp to show in the viewer yet.
        if (!part.IsWaitingForFile)
        {
            ShowInViewer(part, photo);
        }
    }

    // Shows the photo over the whole window with the name of the photo in the bubble. Focus returns to the part that it
    // opened from.
    private void ShowInViewer(MediaPart part, Control from) =>
        (Application.Current as App)?.MainWindow?.ShowPhoto(part, Bubble?.Id ?? string.Empty, Bubble?.PhotoName ?? ChatCopy.Photo, from);
}

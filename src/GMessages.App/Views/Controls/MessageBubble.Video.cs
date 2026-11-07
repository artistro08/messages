using GMessages.App.Media;
using GMessages.App.Windowing;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows the video of the bubble: its still frame, the player in place and the viewer.
/// </summary>
/// <remarks>
/// This file is a part of <see cref="MessageBubble"/>. The other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{
    /// <summary>
    /// Stops the video that plays in place in any bubble. A chat switch and the viewer call it.
    /// </summary>
    internal static void StopInlineVideo() => s_playingVideo?.ReleaseVideo();

    // A new video shows stopped on the plain fill with its play button until its frame loads. The frame is the
    // thumbnail of the phone or a frame that VideoStill makes. The echo of a video sent from here keeps what shows. Its
    // bytes go in the cache under the new media ID. Without a frame yet, the echo loads one.
    private void ShowVideo()
    {
        MediaPart? part = Bubble is { HasVideo: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _videoPart) && ReferenceEquals(Bubble, _videoBubble))
        {
            return;
        }

        if (part is not null && Bubble is not null && ReferenceEquals(Bubble, _videoBubble) && KeepSentMedia(_videoPart, part))
        {
            _videoPart = part;
            FindName(nameof(Video));
            if (VideoThumb.Background is null)
            {
                LoadVideoFrame(part);
            }

            return;
        }

        ReleaseVideo();
        _videoPart       = part;
        _videoBubble     = Bubble;
        _videoFrameImage = null;

        // With no video, x:Load unloads its part.
        if (part is null)
        {
            return;
        }

        FindName(nameof(Video));
        VideoThumb.Background = null;
        ShowVideoNote(null, string.Empty);
        SizePhoto();
        LoadVideoFrame(part);
    }

    // Loads the frame of the video in the background. A frame that does not load leaves the plain fill. For the tests,
    // the item status of the play button is Pending, then Frame or NoFrame.
    private async void LoadVideoFrame(MediaPart part)
    {
        TestHooks.ShowState(VideoPlay, "Pending");
        if (part.IsPending || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        // The frame decodes as wide as the video shows. The box has its size before this method runs.
        int frameWidth = (int)Math.Ceiling(Video is { Width: > 0 } box ? box.Width : MaxBubbleWidth);
        try
        {
            byte[] bytes      = await VideoStill.GetAsync(media, part);
            ImageSource image = await Avatar.ToImageAsync(bytes, frameWidth) ?? throw new InvalidDataException();
            if (ReferenceEquals(part, _videoPart))
            {
                VideoThumb.Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
                _videoFrameImage      = image;
                TestHooks.ShowState(VideoPlay, "Frame");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "thumbnail", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            if (ReferenceEquals(part, _videoPart))
            {
                TestHooks.ShowState(VideoPlay, "NoFrame");
            }
        }
    }

    // Play stops any other video, then gets this video from memory or a download and plays it here with sound.
    private void OnVideoPlayClick(object sender, RoutedEventArgs e) => PlayInline(TimeSpan.Zero, play: true);

    // Starts the inline player at a position, playing or paused there. On close, the viewer returns its position.
    // The method is async void as a click handler is. Thus a failure reaches the unhandled error log of the app.
    private async void PlayInline(TimeSpan start, bool play)
    {
        if (_videoPart is not { IsPending: false } part || _video is not null || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        if (!ReferenceEquals(s_playingVideo, this))
        {
            s_playingVideo?.ReleaseVideo();
        }

        s_playingVideo = this;
        int load       = ++_videoLoad;
        ShowVideoNote(ChatCopy.Downloading, "Downloading");
        try
        {
            byte[] bytes = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
            if (load != _videoLoad || !ReferenceEquals(s_playingVideo, this))
            {
                return;
            }

            string id = PartIdOf("Video.Player.", Bubble?.PartKey ?? string.Empty);
            _video         = new VideoPlayback(XamlHost.Dispatcher, bytes, part.Mime, start, id, Bubble?.VideoName ?? ChatCopy.Video, drawFrames: true);
            _video.Failed += OnVideoFailed;

            // The frames fit the box at the display scale and show as its rounded picture under the controls. See
            // ClipVideoPicture. The host takes the plain fill of the bubble. Thus a video with another shape than its
            // box plays on that fill, not on the still frame.
            double scale = XamlRoot?.RasterizationScale ?? 1;
            _video.SizeFrames((int)Math.Round(VideoHost.ActualWidth * scale), (int)Math.Round(VideoHost.ActualHeight * scale));
            VideoPicture.Source  = _video.Frames;
            VideoHost.Background = Video.Background;
            VideoHost.Children.Add(_video.Element);
            ClipVideoPicture();
            VideoPlay.Visibility   = Visibility.Collapsed;
            VideoExpand.Visibility = Visibility.Visible;
            ShowVideoNote(null, "Playing");
            EffectiveViewportChanged += OnVideoViewportChanged;
            if (play)
            {
                _video.Player.Play();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (load != _videoLoad)
            {
                return;
            }

            AppLog.MediaFailed(App.ShellLog, "video", Bubble?.Id ?? string.Empty, ex.GetType().Name);
            ReleaseVideo();
            ShowVideoNote(ChatCopy.VideoPlayFailed, "Failed");
        }
    }

    // The player cannot play the video, so the bubble shows the frame again with the failure.
    private void OnVideoFailed(object? sender, EventArgs e)
    {
        AppLog.MediaFailed(App.ShellLog, "video", Bubble?.Id ?? string.Empty, "MediaFailed");
        ReleaseVideo();
        ShowVideoNote(ChatCopy.VideoPlayFailed, "Failed");
    }

    // A click on the frame opens the viewer at the start.
    private void OnVideoFrameClick(object sender, RoutedEventArgs e) => OpenVideoInViewer(VideoFrame);

    // After a click on the expand button, the viewer continues from the position of the inline player.
    private void OnVideoExpandClick(object sender, RoutedEventArgs e) => OpenVideoInViewer(VideoExpand);

    // Stops the inline player and shows the video over the whole window, at its position and play state. From the
    // frame, the video plays from the start. From the inline player, the viewer returns its position and state on
    // close. The inline player then continues from there, as the owner asked in Plan 10. On close, focus returns to the
    // frame in the way that the clicked button had it. Thus a click brings no focus ring.
    private void OpenVideoInViewer(Control from)
    {
        if (_videoPart is not { IsPending: false } part)
        {
            return;
        }

        VideoPlayback? inline              = _video;
        TimeSpan start                     = inline?.Position ?? TimeSpan.Zero;
        bool play                          = inline?.IsPlaying ?? true;
        FocusState how                     = from.FocusState;
        MessageBubbleViewModel? bubble     = Bubble;
        Action<TimeSpan, bool>? handBack   = inline is null ? null : (position, playing) =>
        {
            if (ReferenceEquals(Bubble, bubble) && ReferenceEquals(_videoPart, part) && IsLoaded)
            {
                PlayInline(position, playing);
            }
        };

        ReleaseVideo();
        ShowVideoNote(null, string.Empty);
        (Application.Current as App)?.MainWindow?.ShowVideo(part, Bubble?.Id ?? string.Empty, Bubble?.VideoName ?? ChatCopy.Video, VideoFrame, how, start, play, _videoFrameImage, handBack);
    }

    // The video stops when it scrolls out of sight. The list does not recycle a realized row off screen at once.
    private void OnVideoViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (OutOfSight(args.EffectiveViewport))
        {
            ReleaseVideo();
        }
    }

    // Stops the inline player and releases it and its bytes. The frame and its play button show again.
    private void ReleaseVideo()
    {
        _videoLoad++;
        if (ReferenceEquals(s_playingVideo, this))
        {
            s_playingVideo = null;
        }

        if (_video is not { } video)
        {
            return;
        }

        _video                    = null;
        EffectiveViewportChanged -= OnVideoViewportChanged;
        video.Failed              -= OnVideoFailed;
        video.Dispose();

        // The bubble now shows a message without a video, so x:Load already removed its part.
        if (Video is null)
        {
            return;
        }

        VideoHost.Children.Remove(video.Element);
        VideoHost.Background   = null;
        VideoPicture.Source    = null;
        VideoPlay.Visibility   = Visibility.Visible;
        VideoExpand.Visibility = Visibility.Collapsed;
        TestHooks.ShowState(VideoFrame, "Stopped");
    }

    // Shows a note over the frame, such as Downloading or the failure, or no note. The state goes on the frame for the
    // tests.
    private void ShowVideoNote(string? text, string state)
    {
        VideoNote.Text           = text ?? string.Empty;
        VideoNoteBack.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        TestHooks.ShowState(VideoFrame, state);
    }

    // Rounds the picture of the video to the corners of the bubble. The corners of a Border do not clip an Image. The
    // clip follows the size and the corners. The image uses no implicit transitions, so Windows lets the
    // code take its visual.
    private void ClipVideoPicture()
    {
        if (VideoPicture is null)
        {
            return;
        }

        try
        {
            ClipRounded(ElementCompositionPreview.GetElementVisual(VideoPicture), VideoPicture, Video.CornerRadius);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "clip", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }
    }
}

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
/// The bubble's video: the first frame, playing in place, and opening the viewer.
/// </summary>
/// <remarks>
/// Part of <see cref="MessageBubble"/>; the other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{

    // =========================================================================
    // VIDEO
    // =========================================================================

    /// <summary>
    /// Stops the video playing in place, wherever it is (a chat switch, the viewer opening).
    /// </summary>
    internal static void StopInlineVideo() => s_playingVideo?.ReleaseVideo();

    // Another Video: Stopped, On The Plain Fill With Its Play Button Until Its Frame Loads (The Phone's Thumbnail, Else
    // One Made Here; see VideoStill). The Echo Of One Sent From Here Keeps What Shows, Its Bytes Seeded Under The New
    // Media ID; Without A Frame Yet, It Loads One
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

        // No Video: Its Part Unloads (x:Load)
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

    // The Video's Frame, In The Background; One That Won't Load Leaves The Plain Fill. The Play Button's Item Status Says
    // Which Shows, For The Tests: Pending, Then Frame Or NoFrame
    private async void LoadVideoFrame(MediaPart part)
    {
        TestHooks.ShowState(VideoPlay, "Pending");
        if (part.IsPending || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        // Decoded As Wide As The Video Shows (Sized Before This Runs)
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

    // Play: Downloads (Or Reads From Memory) The Video And Plays It Here, With Sound, Stopping Any Other One First
    private void OnVideoPlayClick(object sender, RoutedEventArgs e) => PlayInline(TimeSpan.Zero, play: true);

    // The Inline Player From A Position, Playing Or Paused There (The Viewer Hands Back Where It Got To On Close). Async
    // Void Like A Click Handler, So A Failure Reaches The App's Unhandled Error Log
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

            // The Frames Fit The Box At The Display's Scale, Shown As Its Picture (Rounded, See ClipVideoPicture) Under The
            // Controls; The Host Takes The Bubble's Plain Fill, So A Video Of Another Shape Than Its Box Plays On It, Not
            // On The Still Frame
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

    // The Player Couldn't Play It: Back To The Frame, Saying So
    private void OnVideoFailed(object? sender, EventArgs e)
    {
        AppLog.MediaFailed(App.ShellLog, "video", Bubble?.Id ?? string.Empty, "MediaFailed");
        ReleaseVideo();
        ShowVideoNote(ChatCopy.VideoPlayFailed, "Failed");
    }

    // A Click On The Frame Opens The Viewer From The Start
    private void OnVideoFrameClick(object sender, RoutedEventArgs e) => OpenVideoInViewer(VideoFrame);

    // Expand: The Viewer Carries On Where The Inline Player Got To
    private void OnVideoExpandClick(object sender, RoutedEventArgs e) => OpenVideoInViewer(VideoExpand);

    // Stops The Inline Player And Shows The Video Over The Whole Window, Where It Got To And Playing Or Paused As It Was
    // (From The Frame: From The Start, Playing). Opened From The Inline Player, The Viewer Hands Back Its Own Position And
    // State On Close, And The Inline Player Carries On From There (Owner, Plan 10). Focus Comes Back To The Frame On
    // Close, The Way The Clicked Button Had It (A Click Brings No Focus Ring Back)
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

    // Scrolled Out Of Sight: The Video Stops (A Realized Row Off Screen Isn't Recycled At Once)
    private void OnVideoViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (OutOfSight(args.EffectiveViewport))
        {
            ReleaseVideo();
        }
    }

    // Stops The Inline Player And Lets Go Of It And The Bytes, Back To The Frame And Its Play Button
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

        // The Bubble Moved On To A Message Without A Video: Its Part Is Already Gone (x:Load)
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

    // The Note Over The Frame (Downloading, Or Failed), Or None; Its State Goes On The Frame For The Tests
    private void ShowVideoNote(string? text, string state)
    {
        VideoNote.Text           = text ?? string.Empty;
        VideoNoteBack.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        TestHooks.ShowState(VideoFrame, state);
    }

    // The Playing Video's Picture Rounded To The Bubble's Corners (An Image Isn't Clipped By A Border's Corners), Reshaped
    // With Its Size And The Corners. The Image Uses No Implicit Transitions, So Windows Hands Out Its Visual
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

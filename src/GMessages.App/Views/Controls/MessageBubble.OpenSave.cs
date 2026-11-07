using GMessages.App.Media;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The bubble menu's Open, Open with, Save as and Download.
/// </summary>
/// <remarks>
/// Part of <see cref="MessageBubble"/>; the other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{

    // =========================================================================
    // OPEN AND SAVE
    // =========================================================================

    // Opens The File In Its Default App (See MediaActions.OpenAsync). True When Opened Or Offered For Saving
    private Task<bool> OpenMediaAsync(MediaPart part) => MediaActions.OpenAsync(XamlRoot, part, Bubble?.Id ?? string.Empty);

    /// <summary>
    /// The bubble menu's Open: as a click on the photo (the viewer), the video's frame, the voice message, or the file
    /// card (its default app).
    /// </summary>
    public void Open()
    {
        if (Bubble?.HasFileCard == true)
        {
            OnFileClick(this, new RoutedEventArgs());
        }
        else if (Bubble?.HasVideo == true)
        {
            OpenVideoInViewer(VideoFrame);
        }
        else if (Bubble?.HasVoice == true)
        {
            // The photo viewer cannot show audio, so Open plays or pauses a voice message as its play button does.
            RequestVoice(null);
        }
        else if (Bubble?.Media is { IsPending: false } part && Photo is not null)
        {
            ShowInViewer(part, Photo);
        }
    }

    /// <summary>
    /// The bubble menu's Open with: the photo, video, voice message or file with Windows' app picker.
    /// </summary>
    public void OpenWith()
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.OpenWithAsync(part, Bubble.Id);
        }
    }

    /// <summary>
    /// The bubble menu's Save as: the Windows Save dialog for the photo or file.
    /// </summary>
    public void SaveAs()
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.SaveAsAsync(XamlRoot, part, Bubble.Id);
        }
    }

    /// <summary>
    /// The bubble menu's Download: into the Downloads folder, with the window's confirmation.
    /// </summary>
    public void Download()
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.DownloadAsync(part, Bubble.Id);
        }
    }
}

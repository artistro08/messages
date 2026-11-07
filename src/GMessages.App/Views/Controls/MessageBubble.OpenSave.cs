using GMessages.App.Media;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Runs Open, Open with, Save as and Download from the bubble menu.
/// </summary>
/// <remarks>
/// This file is a part of <see cref="MessageBubble"/>. The other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{
    /// <summary>
    /// Runs Open from the bubble menu. A photo or a video opens in the viewer. A voice message opens in the default
    /// audio app. A file card opens its file.
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
            // The owner wants a voice message to open in the default audio app, as a file does.
            if (Bubble.Media is { IsPending: false } voice)
            {
                _ = MediaActions.OpenAsync(XamlRoot, voice, Bubble.Id);
            }
        }
        else if (Bubble?.Media is { IsPending: false } part && Photo is not null)
        {
            ShowInViewer(part, Photo);
        }
    }

    /// <summary>
    /// Runs Open with from the bubble menu. The app picker of Windows opens the photo, video, voice message or file.
    /// </summary>
    public void OpenWith()
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.OpenWithAsync(part, Bubble.Id);
        }
    }

    /// <summary>
    /// Runs Save as from the bubble menu with the Save dialog of Windows.
    /// </summary>
    public void SaveAs()
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.SaveAsAsync(XamlRoot, part, Bubble.Id);
        }
    }

    /// <summary>
    /// Runs Download from the bubble menu. The file goes to the Downloads folder, and the window confirms it.
    /// </summary>
    public void Download()
    {
        if (Bubble?.Media is { IsPending: false } part)
        {
            _ = MediaActions.DownloadAsync(part, Bubble.Id);
        }
    }
}

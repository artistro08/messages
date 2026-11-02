using System.Numerics;
using System.Runtime.InteropServices;
using GMessages.App.Media;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The bubble's file card: the name, size and thumbnail, and the click that downloads and opens it.
/// </summary>
/// <remarks>
/// Part of <see cref="MessageBubble"/>; the other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{

    // =========================================================================
    // FILE CARD
    // =========================================================================

    // Another File: Its Size, And Its Thumbnail When One Came
    private async void ShowFile()
    {
        MediaPart? part = Bubble is { HasFileCard: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _filePart) && ReferenceEquals(Bubble, _fileBubble))
        {
            return;
        }

        // The Echo Of A File Sent From Here: Keep The Card As It Is
        if (part is not null && Bubble is not null && ReferenceEquals(Bubble, _fileBubble) && KeepSentMedia(_filePart, part))
        {
            _filePart = part;
            return;
        }

        _filePart    = part;
        _fileBubble  = Bubble;
        _fileOpening = false;
        _fileFailed  = false;

        // No File: The Card Unloads (x:Load)
        if (part is null)
        {
            return;
        }

        FindName(nameof(FileCard));
        FileThumb.Background = null;
        ShowFileDetail();
        if (part is not { ThumbnailId.Length: > 0 } || (Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        // A Thumbnail That Won't Load Leaves The Icon
        try
        {
            byte[] bytes      = await media.GetAsync(part, thumbnail: true, CancellationToken.None);
            ImageSource image = await Avatar.ToImageAsync(bytes, FileThumbWidth) ?? throw new InvalidDataException();
            if (ReferenceEquals(part, _filePart))
            {
                FileThumb.Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "thumbnail", Bubble?.Id ?? string.Empty, ex.GetType().Name);
        }
    }

    // The Line Under The Name: Downloading, The Failure, Or The Size
    private void ShowFileDetail()
    {
        (FileDetail.Text, string state) = (_fileOpening, _fileFailed) switch
        {
            (true, _) => (ChatCopy.Downloading, "Downloading"),
            (_, true) => (ChatCopy.FileOpenFailed, "Failed"),
            _         => (SizeText(_filePart?.Size ?? 0), "Ready"),
        };
        TestHooks.ShowState(FileCard, state);
    }

    // Downloads, Then Opens In The Default App; Once At A Time
    private async void OnFileClick(object sender, RoutedEventArgs e)
    {
        if (_filePart is not { IsPending: false } part || _fileOpening)
        {
            return;
        }

        _fileOpening = true;
        _fileFailed  = false;
        ShowFileDetail();

        MessageBubbleViewModel? bubble = _fileBubble;
        bool opened                    = await OpenMediaAsync(part);
        if (!ReferenceEquals(bubble, _fileBubble))
        {
            return;
        }

        _fileOpening = false;
        _fileFailed  = !opened;
        ShowFileDetail();
    }
}

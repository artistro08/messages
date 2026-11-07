using GMessages.App.Media;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows the file card of the bubble with the name, the size and the thumbnail. A click downloads and opens the file.
/// </summary>
/// <remarks>
/// This file is a part of <see cref="MessageBubble"/>. The other members are in MessageBubble.xaml.cs.
/// </remarks>
public sealed partial class MessageBubble
{
    private async void ShowFile()
    {
        MediaPart? part = Bubble is { HasFileCard: true } bubble ? bubble.Media : null;
        if (SameMedia(part, _filePart) && ReferenceEquals(Bubble, _fileBubble))
        {
            return;
        }

        // The echo of a file sent from here keeps the card as it is.
        if (part is not null && Bubble is not null && ReferenceEquals(Bubble, _fileBubble) && KeepSentMedia(_filePart, part))
        {
            _filePart = part;
            return;
        }

        _filePart    = part;
        _fileBubble  = Bubble;
        _fileOpening = false;
        _fileFailed  = false;

        // With no file, x:Load unloads the card.
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

        // A thumbnail that does not load leaves the icon.
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

    // The file downloads, then opens in its default app. Only 1 open runs at a time.
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
        bool opened                    = await MediaActions.OpenAsync(XamlRoot, part, Bubble?.Id ?? string.Empty);
        if (!ReferenceEquals(bubble, _fileBubble))
        {
            return;
        }

        _fileOpening = false;
        _fileFailed  = !opened;
        ShowFileDetail();
    }
}

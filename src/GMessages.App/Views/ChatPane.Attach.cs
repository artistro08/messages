using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.App.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// Handles the attachments of the compose area: the file pickers, the chips and the paste from the clipboard.
/// </summary>
/// <remarks>
/// This part of <see cref="ChatPane"/> holds the attachment members. ChatPane.xaml.cs holds the other members.
/// </remarks>
public sealed partial class ChatPane
{
    // Adds the picked files as chips in order, from the test or from the picker over this window. The log names only
    // the error type of a failure.
    private async Task PickAttachmentsAsync(ChatViewModel chat, bool photos)
    {
        try
        {
            IReadOnlyList<StorageFile> files = TestHooks.PickFiles(photos) is { } paths
                ? await Task.WhenAll(paths.Select(p => StorageFile.GetFileFromPathAsync(p).AsTask()))
                : await PickWithDialogAsync(photos);
            if (files.Count == 0)
            {
                return;
            }

            chat.ClearAttachError();
            await AttachFilesAsync(chat, files);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "attach", chat.ConversationId, ex.GetType().Name);
        }
        finally
        {
            _picking = false;
        }

        DraftBox.Focus(FocusState.Programmatic);
    }

    // Opens the Windows file picker over this window for any files or for photos only. The user can pick several files.
    private async Task<IReadOnlyList<StorageFile>> PickWithDialogAsync(bool photos)
    {
        nint hwnd = XamlWindow.HwndFor(XamlRoot);
        if (hwnd == 0)
        {
            return [];
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = photos ? PickerLocationId.PicturesLibrary : PickerLocationId.DocumentsLibrary,
            ViewMode               = photos ? PickerViewMode.Thumbnail : PickerViewMode.List,
            CommitButtonText       = photos ? ChatCopy.AttachPhoto : ChatCopy.Attach,
        };
        foreach (string type in photos ? (IEnumerable<string>)PhotoMimes.Keys : AnyType)
        {
            picker.FileTypeFilter.Add(type);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickMultipleFilesAsync();
    }

    // Checks the size of each picked or pasted file, then adds the files as chips in order. The method reads only the
    // header of a photo now, for its shape. A video also gets its thumbnail when Windows has one. Send reads the files.
    private static async Task AttachFilesAsync(ChatViewModel chat, IReadOnlyList<StorageFile> files)
    {
        foreach (StorageFile file in files)
        {
            Windows.Storage.FileProperties.BasicProperties properties = await file.GetBasicPropertiesAsync();
            if (!chat.CanAttach((long)properties.Size))
            {
                continue;
            }

            string mime                   = PhotoMimes.GetValueOrDefault(file.FileType) ?? MediaMimes.GetValueOrDefault(file.FileType) ?? (file.ContentType.Length > 0 ? file.ContentType : "application/octet-stream");
            (int width, int height)       = mime.StartsWith("image/", StringComparison.Ordinal) ? await PhotoSizeAsync(file) : (0, 0);
            Func<Task<Stream>>? thumbnail = mime.StartsWith("video/", StringComparison.Ordinal) ? await VideoThumbnailAsync(file) : null;
            chat.Attach(file.Name, mime, (long)properties.Size, () => file.OpenStreamForReadAsync(), width, height, thumbnail);
        }

        AppLog.AttachmentsPicked(App.ShellLog, chat.ConversationId, files.Count, chat.Attachments.Count);
    }

    // Gets the frame of a video that Explorer shows, for its chip. Null when Windows has only an icon or nothing.
    private static async Task<Func<Task<Stream>>?> VideoThumbnailAsync(StorageFile file)
    {
        try
        {
            using Windows.Storage.FileProperties.StorageItemThumbnail? thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, ChipSide);
            if (thumbnail is null || thumbnail.Type != Windows.Storage.FileProperties.ThumbnailType.Image)
            {
                return null;
            }

            using var copy = new MemoryStream();
            await thumbnail.AsStreamForRead().CopyToAsync(copy);
            byte[] bytes = copy.ToArray();
            return () => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // Reads the shown size of a photo from the header of the file, without a full decode. The size follows the EXIF
    // orientation, so the bubble keeps its shape. The result is 0 x 0 when the header does not decode.
    private static async Task<(int Width, int Height)> PhotoSizeAsync(StorageFile file)
    {
        try
        {
            using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (0, 0);
        }
    }

    // Pastes the clipboard of the test when it set one, else the real clipboard. Under the test hooks, the pane takes
    // every paste.
    private async Task PasteFromTestHookAsync()
    {
        try
        {
            DataPackageView? view = await TestHooks.PasteContentAsync() ?? ClipboardView();
            if (view is not null)
            {
                Paste(view, owned: true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "paste", Chat?.ConversationId ?? string.Empty, ex.GetType().Name);
        }
    }

    // Gets the clipboard. Null when Windows refuses it because another app holds it.
    private static DataPackageView? ClipboardView()
    {
        try
        {
            return Clipboard.GetContent();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // True when the clipboard holds media: files from Explorer or the GIFs of the emoji panel. A picture with no text
    // beside it also counts, such as a screenshot or the Copy image of a browser. Text with pictures pastes as text,
    // such as a copied web page or spreadsheet. The pane takes a paste only when it carries media.
    private static bool CarriesMedia(DataPackageView view) =>
        view.Contains(StandardDataFormats.StorageItems)
        || (!view.Contains(StandardDataFormats.Text) && (view.Contains(StandardDataFormats.Bitmap) || PastedPictures.Any(p => view.Contains(p.Format))));

    private static bool IsPasteKey(VirtualKey key) =>
        (key == VirtualKey.V && UiHelpers.IsDown(VirtualKey.Control)) || (key == VirtualKey.Insert && UiHelpers.IsDown(VirtualKey.Shift));

    // The paste of the box itself: the Paste of its menu, or a paste that OnDraftKeyDown did not take. Media becomes
    // chips, and the box pastes text.
    private void OnDraftPaste(object sender, TextControlPasteEventArgs e)
    {
        // In a test run, the pane takes every paste at once, as in OnDraftKeyDown.
        if (TestHooks.BlockLaunches)
        {
            e.Handled = true;
            _ = PasteFromTestHookAsync();
            return;
        }

        if (ClipboardView() is not { } view)
        {
            return;
        }

        e.Handled = CarriesMedia(view);
        Paste(view, e.Handled);
    }

    // Pastes the clipboard. The log names only its formats, never its content. Media becomes chips. Under the test
    // hooks, text goes into the box as the box pastes it.
    private void Paste(DataPackageView view, bool owned)
    {
        if (Chat is not { } chat)
        {
            return;
        }

        bool media = CarriesMedia(view);
        AppLog.PasteFormats(App.ShellLog, chat.ConversationId, view.AvailableFormats, media);
        if (!owned)
        {
            return;
        }

        _ = media ? AttachPastedAsync(chat, view) : PasteTextAsync(view);
    }

    // In a test run, types the text of the hook over the selection.
    private async Task PasteTextAsync(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.Text))
        {
            DraftBox.SelectedText = await view.GetTextAsync();
            DraftBox.Select(DraftBox.SelectionStart + DraftBox.SelectionLength, 0);
        }
    }

    // Adds pasted media as chips, with the same limits as picked files. Files attach as picked files do. Else a picture
    // in its own format attaches byte for byte, so a GIF still moves on the phone. Else the bitmap attaches as a PNG.
    // The log names only the error type of a failure.
    private async Task AttachPastedAsync(ChatViewModel chat, DataPackageView view)
    {
        try
        {
            chat.ClearAttachError();
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await view.GetStorageItemsAsync();
                await AttachFilesAsync(chat, [.. items.Where(i => i.IsOfType(StorageItemTypes.File)).Select(i => i.As<StorageFile>())]);
            }
            else if ((await PastedPictureAsync(view) ?? await PastedWebGifAsync(view, chat.Previews)) is { } picture)
            {
                (int width, int height) = await PictureSizeAsync(picture.Bytes);
                chat.Attach(picture.Name, picture.Mime, picture.Bytes, width, height);
            }
            else if (view.Contains(StandardDataFormats.Bitmap))
            {
                (byte[] png, int width, int height) = await BitmapToPngAsync(await view.GetBitmapAsync());
                chat.Attach(PastedBitmapName, "image/png", png, width, height);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "paste", chat.ConversationId, ex.GetType().Name);
        }

        DraftBox.Focus(FocusState.Programmatic);
    }

    // Gets the first picture on the clipboard in its own format, or null. See PastedPictures.
    private static async Task<(string Name, string Mime, byte[] Bytes)?> PastedPictureAsync(DataPackageView view)
    {
        foreach ((string format, string mime, string name) in PastedPictures)
        {
            if (!view.Contains(format))
            {
                continue;
            }

            using IRandomAccessStream stream = (await view.GetDataAsync(format)).As<IRandomAccessStream>();
            using var copy                   = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(copy);
            return (name, mime, copy.ToArray());
        }

        return null;
    }

    // Gets the GIF that a pasted web picture links to, or null. A GIF of the emoji panel comes as HTML, an <img> of the
    // GIF on Tenor. A bitmap of its first frame comes beside it, with no GIF or file format. Thus a paste of the bitmap
    // sent a still PNG. The previewer fetches the picture as it fetches a link preview, from public hosts with no
    // cookies. The method keeps it only when it is a GIF within the attachment limit.
    private static async Task<(string Name, string Mime, byte[] Bytes)?> PastedWebGifAsync(DataPackageView view, LinkPreviewer? previews)
    {
        if (previews is null || !view.Contains(StandardDataFormats.Html))
        {
            return null;
        }

        string html = HtmlFormatHelper.GetStaticFragment(await view.GetHtmlFormatAsync());
        if (ImageSource().Match(html) is not { Success: true } match
            || !Uri.TryCreate(System.Net.WebUtility.HtmlDecode(match.Groups[1].Value), UriKind.Absolute, out Uri? url))
        {
            return null;
        }

        // The bytes must also start with "GIF8", whatever type the server named. The previewer never throws and logs
        // its own failures.
        byte[]? bytes = await previews.FetchImageAsync(url, "image/gif", ChatStore.MaxAttachmentBytes, CancellationToken.None);
        return bytes is [0x47, 0x49, 0x46, 0x38, ..] ? (PastedGifName, "image/gif", bytes) : null;
    }

    // The src of an <img> in pasted HTML.
    [System.Text.RegularExpressions.GeneratedRegex("""<img\b[^>]*?\bsrc\s*=\s*["']([^"']+)["']""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ImageSource();

    // Encodes a pasted bitmap as a PNG and returns it with its size.
    private static async Task<(byte[] Png, int Width, int Height)> BitmapToPngAsync(RandomAccessStreamReference bitmap)
    {
        using IRandomAccessStreamWithContentType source = await bitmap.OpenReadAsync();
        BitmapDecoder decoder                           = await BitmapDecoder.CreateAsync(source);
        using SoftwareBitmap pixels                     = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        using var output      = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(pixels);
        await encoder.FlushAsync();

        using var copy = new MemoryStream();
        output.Seek(0);
        await output.AsStreamForRead().CopyToAsync(copy);
        return (copy.ToArray(), pixels.PixelWidth, pixels.PixelHeight);
    }

    // Reads the size of a pasted picture from its header. The result is 0 x 0 when the header does not decode.
    private static async Task<(int Width, int Height)> PictureSizeAsync(byte[] bytes)
    {
        try
        {
            using IRandomAccessStream stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            BitmapDecoder decoder            = await BitmapDecoder.CreateAsync(stream);
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (0, 0);
        }
    }

    // The menu of the box pastes text itself. With only media on the clipboard, its Paste takes the media instead.
    private Action? PasteMediaFromMenu()
    {
        if (DraftBox.CanPasteClipboardContent || ClipboardView() is not { } view || !CarriesMedia(view))
        {
            return null;
        }

        return () =>
        {
            if (ClipboardView() is { } now)
            {
                Paste(now, owned: CarriesMedia(now));
            }
        };
    }

    // Decodes the picture of a photo chip small from the file. A file that Windows cannot read leaves the chip blank.
    private static async Task LoadChipImageAsync(BitmapImage image, Func<Task<Stream>> open)
    {
        try
        {
            await using Stream file = await open();
            using IRandomAccessStream stream = file.AsRandomAccessStream();
            await image.SetSourceAsync(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "chip", string.Empty, ex.GetType().Name);
        }
    }
}

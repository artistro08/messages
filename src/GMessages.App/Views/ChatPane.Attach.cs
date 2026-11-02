using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using WinRT;

namespace GMessages.App.Views;

/// <summary>
/// The compose area's attachments: the file pickers, the chips, and pasting from the clipboard.
/// </summary>
/// <remarks>
/// Part of <see cref="ChatPane"/>; the other members are in ChatPane.xaml.cs.
/// </remarks>
public sealed partial class ChatPane
{

    // =========================================================================
    // ATTACHING
    // =========================================================================

    // The Picked Files (The Test's, Or The Picker's Over This Window), Each Checked For Size, Then Added As Chips In
    // Order. Only A Photo's Header Is Read Now (For Its Shape); Send Reads The Files. A File That Can't Be Opened Is
    // Skipped And Logged By Its Error Type Only
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

    // The Windows File Picker Over This Window: Any Files, Or Photos Only; Several May Be Picked
    private async Task<IReadOnlyList<StorageFile>> PickWithDialogAsync(bool photos)
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } island)
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

        WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId));
        return await picker.PickMultipleFilesAsync();
    }

    // Files Picked Or Pasted, Each Checked For Size, Then Added As Chips In Order. Only A Photo's Header Is Read Now (For
    // Its Shape), And A Video's Thumbnail When Windows Has One; Send Reads The Files
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

    // A Video's Frame As Windows Shows It In Explorer, For Its Chip; Null When Windows Only Has An Icon For It (Or None)
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

    // A Photo's Size As Shown (Turned By Its EXIF Orientation), So Its Bubble Keeps Its Shape, Read From The File's
    // Header Without Decoding It; 0 x 0 When It Won't Decode
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

    // =========================================================================
    // PASTING
    // =========================================================================

    // The Test's Clipboard When It Set One, Else The Real One; Pasted As Owned (Under The Test Hooks Every Paste Is)
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

    // The Clipboard; Null When Windows Won't Hand It Over (Another App Holds It)
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

    // Media On The Clipboard: Files (Explorer, The Emoji Panel's GIFs), Or A Picture With No Text Beside It (A Screenshot,
    // A Browser's Copy Image). Text With Pictures (A Copied Web Page Or Spreadsheet) Pastes As Text
    private static bool CarriesMedia(DataPackageView view) =>
        view.Contains(StandardDataFormats.StorageItems)
        || (!view.Contains(StandardDataFormats.Text) && (view.Contains(StandardDataFormats.Bitmap) || PastedPictures.Any(p => view.Contains(p.Format))));

    // The Paste Is Ours When It Carries Media (Under The Test Hooks Every Paste Is Ours Before This Is Asked)
    private static bool OwnsPaste(DataPackageView view) => CarriesMedia(view);

    // Ctrl + V And Shift + Insert
    private static bool IsPasteKey(VirtualKey key) =>
        (key == VirtualKey.V && UiHelpers.IsDown(VirtualKey.Control)) || (key == VirtualKey.Insert && IsShiftDown());

    // The Box's Own Paste (Its Menu's Paste, Or Any Paste The Keys Above Didn't Take): Media Becomes Chips, Text Is Left To It
    private void OnDraftPaste(object sender, TextControlPasteEventArgs e)
    {
        // Test Run: Every Paste Is Ours At Once, As In OnDraftKeyDown
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

        e.Handled = OwnsPaste(view);
        Paste(view, e.Handled);
    }

    // Pastes What The Clipboard Holds, Logging Its Formats Only (Never Its Content): Media Becomes Chips; Under The Test
    // Hooks Text Goes Into The Box As The Box Would Paste It
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

    // Test Run: The Hook's Text, Typed Over The Selection
    private async Task PasteTextAsync(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.Text))
        {
            DraftBox.SelectedText = await view.GetTextAsync();
            DraftBox.Select(DraftBox.SelectionStart + DraftBox.SelectionLength, 0);
        }
    }

    // Pasted Media As Chips, Under The Same Limits As Picked Files: The Files Each As Picked; Else A Picture In Its Own
    // Format (Kept Byte For Byte, So A GIF Still Moves On The Phone); Else The Bitmap, Saved As A PNG. A Failure Is Logged
    // By Its Error Type Only
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

    // The First Picture On The Clipboard In Its Own Format (See PastedPictures), Or Null
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

    // The GIF A Pasted Web Picture Links To, Or Null. The Emoji Panel's GIFs Come As HTML (An <img> Of The GIF On Tenor) Beside
    // A Bitmap Of Its First Frame, With No GIF Or File Format, So Pasting The Bitmap Sent A Still PNG. The Picture Is Fetched As
    // A Link Preview Is (By The Previewer: Public Hosts Only, No Cookies) And Kept Only When It Is A GIF Within The
    // Attachment Limit
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

        // A GIF By Its Bytes Too ("GIF8"), Whatever The Server Said; The Previewer Never Throws And Logs Its Own Failures
        byte[]? bytes = await previews.FetchImageAsync(url, "image/gif", ChatStore.MaxAttachmentBytes, CancellationToken.None);
        return bytes is [0x47, 0x49, 0x46, 0x38, ..] ? (PastedGifName, "image/gif", bytes) : null;
    }

    // An <img>'s src In Pasted HTML
    [System.Text.RegularExpressions.GeneratedRegex("""<img\b[^>]*?\bsrc\s*=\s*["']([^"']+)["']""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ImageSource();

    // A Pasted Bitmap, Encoded As A PNG, With Its Size
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

    // A Pasted Picture's Size, Read From Its Header; 0 x 0 When It Won't Decode
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

    // The Box's Menu: Windows Shows Its Paste Only For Text, So With Media On The Clipboard Windows' Own Paste Command Is
    // Added (Its Name And Icon Come From Windows), And Taken Away Again When The Menu Closes
    private void OnDraftMenuOpening(object? sender, object e)
    {
        // The Menu Is Windows' Shared Text Menu: Only When It Opens On The Message Box
        if (DraftBox.ContextFlyout is not { } menu || menu.Target != DraftBox || DraftBox.CanPasteClipboardContent || ClipboardView() is not { } view || !CarriesMedia(view))
        {
            return;
        }

        var command = new StandardUICommand(StandardUICommandKind.Paste);
        command.ExecuteRequested += (_, _) =>
        {
            if (ClipboardView() is { } now)
            {
                Paste(now, owned: CarriesMedia(now));
            }
        };

        var paste = new AppBarButton { Command = command };
        AutomationProperties.SetAutomationId(paste, "Chat.PasteMedia");
        CommandBarFlyout bar = menu.As<CommandBarFlyout>();
        bar.PrimaryCommands.Add(paste);
        menu.Closed += OnClosed;

        void OnClosed(object? s, object a)
        {
            menu.Closed -= OnClosed;
            bar.PrimaryCommands.Remove(paste);
        }
    }

    // A Photo Chip's Picture, Decoded Small From The File; A File Windows Can't Read Leaves The Chip Blank
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

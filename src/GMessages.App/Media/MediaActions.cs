using GMessages.Presentation.Messaging;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace GMessages.App.Media;

/// <summary>
/// What a photo or file can do, shared by the bubble's menu and the photo viewer: open it in its default app, save a
/// copy where the person picks (Save as), or save it to their Downloads folder (Download).
/// </summary>
/// <remarks>
/// Every action reads the decrypted bytes from the app's <see cref="MediaStore"/>. Failures are logged by the message ID
/// and the error type only, never the file's name, path or type. A test run (see <see cref="TestHooks"/>) notes what
/// would open instead of opening it, answers the Save as dialog with its own path, and downloads to its data folder.
/// </remarks>
internal static class MediaActions
{
    /// <summary>
    /// Writes the file to the private temp folder and opens it in its default app.
    /// </summary>
    /// <remarks>
    /// A program, script or installer is never opened (see <see cref="MediaStore.CanOpen"/>): the Save as dialog shows
    /// instead, so the person decides.
    /// </remarks>
    /// <param name="root">The window's XAML root, for the Save as dialog.</param>
    /// <param name="part">The media part.</param>
    /// <param name="messageId">The message's ID, for the log.</param>
    /// <returns>True when opened, or offered for saving.</returns>
    public static async Task<bool> OpenAsync(XamlRoot? root, MediaPart part, string messageId)
    {
        if ((Application.Current as App)?.Media is not { } media)
        {
            return false;
        }

        if (!MediaStore.CanOpen(part.Name, part.Mime))
        {
            AppLog.MediaFailed(App.ShellLog, "open", messageId, "Blocked");
            await SaveAsAsync(root, part, messageId);
            return true;
        }

        try
        {
            string path = await media.SaveToTempAsync(part, CancellationToken.None);
            bool launch = !TestHooks.RecordLaunch(path);
            if (launch && !await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path)))
            {
                AppLog.MediaFailed(App.ShellLog, "open", messageId, "NoApp");
                return false;
            }

            AppLog.MediaOpened(App.ShellLog, messageId, launch);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "open", messageId, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// The Windows Save dialog over the window, named as the sender named the file (made safe); the picked file gets the
    /// bytes and the mark of the web.
    /// </summary>
    /// <remarks>
    /// The file type list is a named <see cref="List{T}"/>: the Native AOT build only makes WinRT wrappers for
    /// conversions it can see, and a collection expression hid this one, so the dialog's setup threw
    /// <see cref="InvalidCastException"/> and nothing showed (the owner's "Save as doesn't work").
    /// </remarks>
    /// <param name="root">The window's XAML root.</param>
    /// <param name="part">The media part.</param>
    /// <param name="messageId">The message's ID, for the log.</param>
    /// <returns>A task that ends when the file is written, or the dialog was cancelled.</returns>
    public static async Task SaveAsAsync(XamlRoot? root, MediaPart part, string messageId)
    {
        if ((Application.Current as App)?.Media is not { } media || root?.ContentIslandEnvironment is not { } island)
        {
            AppLog.MediaFailed(App.ShellLog, "save", messageId, "NoWindow");
            return;
        }

        try
        {
            string name      = MediaStore.SanitizeName(part.Name, part.Mime);
            string extension = Path.GetExtension(name);
            var picker       = new FileSavePicker
            {
                SuggestedFileName      = Path.GetFileNameWithoutExtension(name),
                SuggestedStartLocation = PickerLocationId.Downloads,
            };
            picker.FileTypeChoices.Add(ChatCopy.File, new List<string> { extension.Length > 1 ? extension : ".bin" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId));

            StorageFile? file = TestHooks.PickSaveFile() is { } picked
                ? (picked.Length > 0 ? await TestSaveFileAsync(picked) : null)
                : await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            byte[] bytes = await media.GetAsync(part, thumbnail: false, CancellationToken.None);
            await FileIO.WriteBytesAsync(file, bytes);
            if (file.Path.Length > 0)
            {
                media.MarkFromInternet(file.Path);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "save", messageId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Saves the file to the person's Downloads folder under a name no other file there has, with the mark of the web,
    /// then shows the window's "Saved to Downloads" confirmation with Show in folder.
    /// </summary>
    /// <param name="part">The media part.</param>
    /// <param name="messageId">The message's ID, for the log.</param>
    /// <returns>The saved file's full path, or null when it failed (logged).</returns>
    public static async Task<string?> DownloadAsync(MediaPart part, string messageId)
    {
        if ((Application.Current as App) is not { Media: { } media } app)
        {
            return null;
        }

        try
        {
            string folder = TestHooks.DownloadsFolder() ?? UserDataPaths.GetDefault().Downloads;
            string path   = await media.SaveToFolderAsync(part, folder, CancellationToken.None);
            app.MainWindow?.ShowDownloaded(path);
            return path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "download", messageId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Opens File Explorer on the file's folder with the file selected (a test run notes the folder instead).
    /// </summary>
    /// <param name="path">The file's full path.</param>
    /// <returns>A task that ends once Explorer was asked.</returns>
    public static async Task ShowInFolderAsync(string path)
    {
        string folder_path = Path.GetDirectoryName(path) ?? string.Empty;
        if (TestHooks.RecordLaunch(folder_path))
        {
            return;
        }

        try
        {
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folder_path);
            var options          = new FolderLauncherOptions();
            options.ItemsToSelect.Add(await StorageFile.GetFileFromPathAsync(path));
            await Launcher.LaunchFolderAsync(folder, options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "show", string.Empty, ex.GetType().Name);
        }
    }

    // The Test's Picked Path As The Dialog Hands It Back: An Empty File, Made
    private static async Task<StorageFile> TestSaveFileAsync(string path)
    {
        await File.WriteAllBytesAsync(path, []);
        return await StorageFile.GetFileFromPathAsync(path);
    }
}

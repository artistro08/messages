using GMessages.App.Windowing;
using GMessages.Presentation.Messaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Xaml;

namespace GMessages.App.Media;

/// <summary>
/// Runs the actions on a photo or a file for the bubble menu and the photo viewer: Open, Open with, Save as and
/// Download.
/// </summary>
/// <remarks>
/// <para>
/// Open opens the file in its default app. Open with opens it with an app that the person picks. Save as saves a copy
/// where the person picks. Download saves it to the Downloads folder.
/// </para>
/// <para>
/// Every action reads the decrypted bytes from the <see cref="MediaStore"/> of the app. For a failure, the log gets
/// only the message ID and the error type. It never gets the name, path or type of the file. A test run notes what
/// would open and does not open it. See <see cref="TestHooks"/>. A test run also answers the Save as dialog with its
/// own path and downloads to its data folder.
/// </para>
/// </remarks>
internal static class MediaActions
{
    /// <summary>
    /// Writes the file to the private temp folder and opens it in its default app.
    /// </summary>
    /// <remarks>
    /// The method never opens a program, a script or an installer. See <see cref="MediaStore.CanOpen"/>. The Save as
    /// dialog shows instead, so the person decides.
    /// </remarks>
    /// <param name="root">The XAML root of the window, for the Save as dialog.</param>
    /// <param name="part">The media part.</param>
    /// <param name="messageId">The ID of the message, for the log.</param>
    /// <returns>True when the file opened or the Save as dialog showed.</returns>
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
    /// Writes the file to the private temp folder with the mark of the web, as Open does. Then the app picker of
    /// Windows opens it, so the person picks the app.
    /// </summary>
    /// <remarks>
    /// The menu offers Open with for every attachment, also for a program or a script, as the owner asked. Open never
    /// opens those files. The picker and the launcher refuse to run a program themselves.
    /// </remarks>
    /// <param name="part">The media part.</param>
    /// <param name="messageId">The ID of the message, for the log.</param>
    /// <returns>A task that ends when the app has asked Windows.</returns>
    public static async Task OpenWithAsync(MediaPart part, string messageId)
    {
        if ((Application.Current as App)?.Media is not { } media)
        {
            return;
        }

        try
        {
            string path = await media.SaveToTempAsync(part, CancellationToken.None);
            bool launch = !TestHooks.RecordLaunch(path, picker: true);
            if (launch && !await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path), new LauncherOptions { DisplayApplicationPicker = true }))
            {
                AppLog.MediaFailed(App.ShellLog, "openwith", messageId, "NoApp");
                return;
            }

            AppLog.MediaOpened(App.ShellLog, messageId, launch);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "openwith", messageId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Shows the Save dialog of Windows over the window, with the safe form of the file name from the sender. The
    /// picked file gets the bytes and the mark of the web.
    /// </summary>
    /// <remarks>
    /// The file type list must be a named <see cref="List{T}"/>. The Native AOT build makes WinRT wrappers only for the
    /// conversions that it can see. A collection expression hid this conversion, so the setup of the dialog threw
    /// <see cref="InvalidCastException"/>. Then no dialog showed, and the owner reported that Save as did not work.
    /// </remarks>
    /// <param name="root">The XAML root of the window.</param>
    /// <param name="part">The media part.</param>
    /// <param name="messageId">The ID of the message, for the log.</param>
    /// <returns>A task that ends when the file is written or the person cancels the dialog.</returns>
    public static async Task SaveAsAsync(XamlRoot? root, MediaPart part, string messageId)
    {
        nint owner = XamlWindow.HwndFor(root);
        if ((Application.Current as App)?.Media is not { } media || owner == 0)
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
            var choices = new List<string> { extension.Length > 1 ? extension : ".bin" };
            picker.FileTypeChoices.Add(ChatCopy.File, choices);
            TestHooks.NoteState("save-as", picker.SuggestedFileName + choices[0]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);

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
    /// Saves the file to the Downloads folder with a unique name and the mark of the web. Then the window shows its
    /// "Saved to Downloads" confirmation with Show in folder.
    /// </summary>
    /// <param name="part">The media part.</param>
    /// <param name="messageId">The ID of the message, for the log.</param>
    /// <returns>The full path of the saved file, or null when the save failed. The log gets the failure.</returns>
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
    /// Opens File Explorer on the folder of the file, with the file selected. A test run notes the folder and opens
    /// nothing.
    /// </summary>
    /// <param name="path">The full path of the file.</param>
    /// <returns>A task that ends when the app has asked Explorer.</returns>
    public static async Task ShowInFolderAsync(string path)
    {
        string folderPath = Path.GetDirectoryName(path) ?? string.Empty;
        if (TestHooks.RecordLaunch(folderPath))
        {
            return;
        }

        try
        {
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            var options          = new FolderLauncherOptions();
            options.ItemsToSelect.Add(await StorageFile.GetFileFromPathAsync(path));
            await Launcher.LaunchFolderAsync(folder, options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.MediaFailed(App.ShellLog, "show", string.Empty, ex.GetType().Name);
        }
    }

    // Makes an empty file at the path that the test picked, as the dialog gives it back.
    private static async Task<StorageFile> TestSaveFileAsync(string path)
    {
        await File.WriteAllBytesAsync(path, []);
        return await StorageFile.GetFileFromPathAsync(path);
    }
}

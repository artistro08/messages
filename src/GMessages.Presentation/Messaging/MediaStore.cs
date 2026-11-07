using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GMessages.Protocol;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Downloads, decrypts and caches the photos, videos, voice messages and files of messages. It also writes them to a
/// private temp folder, so that other apps can open them.
/// </summary>
/// <remarks>
/// <para>
/// The store downloads a part in the order of handlegmessages.go in the Go connector. It tries the full file, then the
/// thumbnail, then the inline data. The inline data comes back as it is, with no download and no decryption. A request
/// for the thumbnail tries the thumbnail first, then the inline data, then the full file. A pending part throws
/// <see cref="MediaPendingException"/>.
/// </para>
/// <para>
/// Decrypted bytes stay in memory up to 64 MB in all, and the least recently used go first. The store gives back a
/// bigger file but does not keep it. Two requests for the same media ID share 1 download.
/// <see cref="RequestFullSizeAsync"/> asks the phone for the full-size image once per message. The phone then sends the
/// message again with the full media ID.
/// </para>
/// <para>
/// <b>Privacy.</b> The store decrypts media only on this device. Media stays only in memory or in the temp folder. In
/// the app, the temp folder is <c>ApplicationData.Current.TemporaryFolder\Media</c>. <see cref="Clear"/> empties both.
/// The app calls it at start, at quit, and at sign-out or unpair through <see cref="ChatStore.Reset"/>. The log never
/// holds bytes, keys, file names or mime types. It holds only counts and error type names. Any thread can call the
/// store.
/// </para>
/// </remarks>
public sealed partial class MediaStore : IDisposable
{
    private const long MemoryCapBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The default time that 1 download can take before it fails. <see cref="DownloadTimeout"/> can change it.
    /// </summary>
    /// <remarks>
    /// The time is long enough for a 100 MB file on a slow connection. It is less than the 30 minute limit of the media
    /// client.
    /// </remarks>
    // ponytail: a deadline for the whole download, not a stall check. Reset a timer per chunk if slow links need more.
    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(15);

    // Google answers with no bytes until the upload of the sender is complete. The store asks again after this time.
    private static readonly TimeSpan NotReadyRetry = TimeSpan.FromSeconds(3);

    /// <summary>The longest file name that the store writes to the temp folder, with the extension.</summary>
    public const int MaxNameLength = 100;

    private const string FallbackName = "attachment";

    // Windows refuses these device names as a file name, with or without an extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Only these types open in their default app. The person saves any other type to a folder that they pick.
    private static readonly HashSet<string> OpenableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif",
        ".mp4", ".mov", ".m4v", ".3gp", ".3g2", ".mkv", ".avi", ".webm",
        ".mpeg", ".mpg", ".3gpp",
        ".mp3", ".m4a", ".ogg", ".wav", ".aac", ".amr", ".flac", ".opus",
        ".pdf", ".txt", ".csv", ".rtf", ".vcf", ".ics",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt",
    };

    private readonly Func<IMessagingClient?> _client;
    private readonly string _tempFolder;
    private readonly ILogger _log;
    private readonly Lock _lock = new();

    // The memory cache by media ID. The dictionary order is least recently used first.
    private readonly OrderedDictionary<string, byte[]> _cache = [];
    private long _cachedBytes;

    private readonly Dictionary<string, Task<byte[]>> _inFlight = [];

    private readonly Dictionary<string, Task<byte[]>> _framesInFlight = [];

    // Each frame needs its whole video, so the store makes 1 frame at a time. Thus the videos of a chat download in
    // sequence.
    // ponytail: 1 at a time across all chats. Add a queue that drops scrolled-away videos if long chats feel slow.
    private readonly SemaphoreSlim _frameTurn = new(1, 1);

    // The IDs of the messages whose full-size image the store asked for.
    private readonly HashSet<string> _fullSizeAsked = [];

    // Clear increments the generation. The store does not cache a download that started before the clear.
    private int _generation;

    // Clear cancels and replaces this source. The cancel stops every download that started before the clear.
    private CancellationTokenSource _generationCancel = new();

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="client">Gets the current phone client, or null when the phone is not connected.</param>
    /// <param name="tempFolder">The private folder for the files that other apps open.</param>
    public MediaStore(Func<IMessagingClient?> client, string tempFolder, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(tempFolder);
        ArgumentNullException.ThrowIfNull(log);

        _client     = client;
        _tempFolder = tempFolder;
        _log        = log;
    }

    /// <summary>
    /// Gets the private temp folder, which also holds voice recordings. <see cref="Clear"/> empties it.
    /// </summary>
    public string TempFolder => _tempFolder;

    /// <summary>
    /// Gets or sets the time that 1 download can take. Each download reads it when it starts. After that time, the
    /// store stops the request, and the download fails with a <see cref="TimeoutException"/>. The default is
    /// <see cref="DefaultDownloadTimeout"/>. The UI test hooks make it shorter.
    /// </summary>
    public Func<TimeSpan> DownloadTimeout { get; set; } = static () => DefaultDownloadTimeout;

    // =========================================================================
    // MEDIA
    // =========================================================================

    /// <summary>
    /// Gets the decrypted bytes of a part, from memory or from a download.
    /// </summary>
    /// <param name="part">The media part.</param>
    /// <param name="thumbnail">True to prefer the thumbnail, which is the first blurred look of a photo.</param>
    /// <param name="ct">Cancels the wait. It does not cancel a download that another caller shares.</param>
    /// <returns>The decrypted bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="part"/> is null.</exception>
    /// <exception cref="MediaPendingException">The part has no media ID, thumbnail or inline data yet.</exception>
    /// <exception cref="MessagingUnavailableException">The phone is not connected.</exception>
    /// <exception cref="TimeoutException">The download took longer than <see cref="DownloadTimeout"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was canceled, or <see cref="Clear"/> stopped the download.
    /// </exception>
    public Task<byte[]> GetAsync(MediaPart part, bool thumbnail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);

        if (part.IsPending)
        {
            throw new MediaPendingException();
        }

        (string id, byte[] key) = Source(part, thumbnail);
        if (id.Length == 0)
        {
            return Task.FromResult(part.InlineData);
        }

        lock (_lock)
        {
            if (_cache.Remove(id, out byte[]? cached))
            {
                _cache.Add(id, cached);
                return Task.FromResult(cached);
            }

            if (!_inFlight.TryGetValue(id, out Task<byte[]>? download))
            {
                // The download runs on another thread. Thus it cannot leave _inFlight before this method adds it.
                IMessagingClient client = _client() ?? throw new MessagingUnavailableException();
                int generation          = _generation;
                CancellationToken clear = _generationCancel.Token;
                download                = Task.Run(() => DownloadAsync(client, id, key, generation, clear), CancellationToken.None);
                _inFlight[id]           = download;
            }

            return download.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Gets the still frame of a video. It is the thumbnail that the phone sent, else a frame that this device makes
    /// from the video and keeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The phone rarely sends a thumbnail for a video. LibGM passes on any <c>thumbnailMediaID</c> that it gets, but
    /// has no frame of its own. Without a thumbnail, the store gets the video from memory or a download, and
    /// <paramref name="extract"/> makes the frame. The store keeps the frame as a file in <c>{temp}\Frames</c>, named
    /// by a hash of the media ID. Thus the store does not get the video again for the frame. <see cref="Clear"/>
    /// empties the frames with the rest.
    /// </para>
    /// <para>
    /// The work runs on another thread, 1 frame at a time. Two callers for the same video share the work. A video
    /// that this device sent and that is not yet on the server has its bytes in the part. The store makes its frame
    /// from those bytes and does not keep it.
    /// </para>
    /// </remarks>
    /// <param name="part">The media part of the video.</param>
    /// <param name="extract">Makes a still image as PNG or JPEG bytes from the bytes and mime type of a video.</param>
    /// <param name="ct">Cancels the wait. It does not cancel work that another caller shares.</param>
    /// <returns>The image bytes of the frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="part"/> or <paramref name="extract"/> is null.</exception>
    /// <exception cref="MediaPendingException">The part has no media ID, thumbnail or inline data yet.</exception>
    /// <exception cref="MessagingUnavailableException">The phone is not connected.</exception>
    public Task<byte[]> GetFrameAsync(MediaPart part, Func<byte[], string, Task<byte[]>> extract, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(extract);

        if (part.ThumbnailId.Length > 0)
        {
            return GetAsync(part, thumbnail: true, ct);
        }

        // This device sent the video, and it is not yet on the server. The store does not keep this frame.
        if (part.MediaId.Length == 0)
        {
            if (part.InlineData.Length == 0)
            {
                throw new MediaPendingException();
            }

            return Task.Run(() => extract(part.InlineData, part.Mime), CancellationToken.None).WaitAsync(ct);
        }

        lock (_lock)
        {
            if (!_framesInFlight.TryGetValue(part.MediaId, out Task<byte[]>? making))
            {
                // The work runs on another thread. Thus it cannot leave _framesInFlight before this method adds it.
                int generation                = _generation;
                making                        = Task.Run(() => MakeFrameAsync(part, extract, generation), CancellationToken.None);
                _framesInFlight[part.MediaId] = making;
            }

            return making.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Keeps bytes that this device already has under a media ID, so a later request for that ID downloads nothing.
    /// </summary>
    /// <remarks>
    /// The app calls this when the echo of a sent attachment arrives. The phone gives the file a media ID, and the sent
    /// bytes are that file. The store does not change an ID that it already keeps. The memory cap of downloads applies.
    /// </remarks>
    /// <param name="mediaId">The media ID.</param>
    /// <param name="bytes">The decrypted bytes.</param>
    public void Seed(string mediaId, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (string.IsNullOrEmpty(mediaId) || bytes.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            Keep(mediaId, bytes);
        }
    }

    /// <summary>
    /// Writes the decrypted file of a part to the temp folder, so another app can open it.
    /// </summary>
    /// <remarks>
    /// Each file gets its own random folder, <c>{temp}\{random}\{name}</c>, so 2 files with the same name never clash.
    /// The name is the name of the sender, made safe by <see cref="SanitizeName"/>.
    /// </remarks>
    /// <param name="part">The media part.</param>
    /// <param name="ct">Cancels the wait and the write.</param>
    /// <returns>The full path of the written file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="part"/> is null.</exception>
    /// <exception cref="MediaPendingException">The part has no media ID, thumbnail or inline data yet.</exception>
    /// <exception cref="MessagingUnavailableException">The phone is not connected.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was canceled, or <see cref="Clear"/> ran before the file was written.
    /// </exception>
    public async Task<string> SaveToTempAsync(MediaPart part, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);

        // The generation comes first. A clear at sign-out during the download then means that nothing is written.
        int generation;
        lock (_lock)
        {
            generation = _generation;
        }

        byte[] bytes  = await GetAsync(part, thumbnail: false, ct).ConfigureAwait(false);
        string folder = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N"));
        string path   = Path.Combine(folder, SanitizeName(part.Name, part.Mime));

        lock (_lock)
        {
            if (generation != _generation)
            {
                throw new OperationCanceledException("The media was cleared while it downloaded.");
            }
        }

        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        MarkFromInternet(path);
        return path;
    }

    /// <summary>
    /// Writes the decrypted file of a part into a folder that the person keeps, such as Downloads. The name is one that
    /// no other file in the folder has.
    /// </summary>
    /// <remarks>
    /// The name is the name of the sender, made safe by <see cref="SanitizeName"/>. When that name is taken, the store
    /// tries <c>name (1).ext</c>, then <c>name (2).ext</c> and so on. Windows names a second copy the same way. The
    /// store creates the file with <see cref="FileMode.CreateNew"/>, so it never replaces a file that appears in the
    /// meantime. The file gets the mark of the web from <see cref="MarkFromInternet"/>.
    /// </remarks>
    /// <param name="part">The media part.</param>
    /// <param name="folder">The full path of the folder, which must exist.</param>
    /// <param name="ct">Cancels the wait and the write.</param>
    /// <returns>The full path of the written file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="part"/> or <paramref name="folder"/> is null.</exception>
    /// <exception cref="MediaPendingException">The part has no media ID, thumbnail or inline data yet.</exception>
    /// <exception cref="MessagingUnavailableException">The phone is not connected.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<string> SaveToFolderAsync(MediaPart part, string folder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(folder);

        byte[] bytes     = await GetAsync(part, thumbnail: false, ct).ConfigureAwait(false);
        string name      = SanitizeName(part.Name, part.Mime);
        string stem      = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);

        // CreateNew throws IOException on a taken name, and the loop tries the next number.
        for (int copy = 0; ; copy++)
        {
            string path = Path.Combine(folder, copy == 0 ? name : FormattableString.Invariant($"{stem} ({copy}){extension}"));
            if (File.Exists(path))
            {
                continue;
            }

            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another file took the name between the check and the create.
                continue;
            }

            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            MarkFromInternet(path);
            return path;
        }
    }

    /// <summary>
    /// Marks a written file as downloaded from the internet, as a browser marks a download. This mark is the mark of
    /// the web.
    /// </summary>
    /// <remarks>
    /// The method writes the <c>Zone.Identifier</c> stream with <c>ZoneId=3</c>. Windows then applies SmartScreen to
    /// the file, and Office applies Protected View and its internet warnings. Only this method writes the stream,
    /// because the name sanitizer refuses <c>:</c> in names. A drive that is not NTFS cannot hold the stream. On such a
    /// drive, the method logs the error type and returns.
    /// </remarks>
    /// <param name="path">The full path of the file.</param>
    public void MarkFromInternet(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            LogMarkFailed(ex.GetType().Name);
        }
    }

    /// <summary>
    /// Asks the phone for the full-size image of a message that has only a thumbnail, once per message.
    /// </summary>
    /// <remarks>
    /// The phone answers with the message again, with its full media ID, which replaces the media part of the bubble.
    /// The method asks nothing for a message that has its full file, has no thumbnail, or was asked for before. The
    /// store forgets a failed request, so the person can try again.
    /// </remarks>
    /// <param name="m">The message.</param>
    /// <param name="again">True to ask again when the photo never came and the person retries.</param>
    /// <returns>A task that completes when the phone accepts the request, or at once when nothing is asked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="m"/> is null.</exception>
    /// <exception cref="MessagingUnavailableException">The phone is not connected.</exception>
    public async Task RequestFullSizeAsync(ChatMessage m, bool again = false)
    {
        ArgumentNullException.ThrowIfNull(m);

        if (m.Media is not { MediaId.Length: 0, ThumbnailId.Length: > 0 } media || m.Id.Length == 0)
        {
            return;
        }

        IMessagingClient client;
        lock (_lock)
        {
            client = _client() ?? throw new MessagingUnavailableException();
            if (again)
            {
                _fullSizeAsked.Remove(m.Id);
            }

            if (!_fullSizeAsked.Add(m.Id))
            {
                return;
            }
        }

        try
        {
            await client.GetFullSizeImageAsync(m.Id, media.ActionMessageId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_lock)
            {
                _fullSizeAsked.Remove(m.Id);
            }

            LogFullSizeFailed(m.Id, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Releases the token source and the frame gate. It does not empty the temp folder: <see cref="Clear"/> does that.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _generationCancel.Dispose();
        }

        _frameTurn.Dispose();
    }

    /// <summary>
    /// Drops the decrypted bytes in memory when the window closes. It does not touch temp files or running downloads.
    /// </summary>
    /// <remarks>A download that still runs can add 1 item back after this call.</remarks>
    public void ClearMemory()
    {
        lock (_lock)
        {
            _cache.Clear();
            _cachedBytes = 0;
        }
    }

    /// <summary>
    /// Forgets every decrypted file: the memory cache, the running downloads, the full-size requests and the temp
    /// folder.
    /// </summary>
    /// <remarks>
    /// The method stops a running download. Its request stops, nothing is decrypted, and its callers get an
    /// <see cref="OperationCanceledException"/>. The method cannot delete a file that another app holds open. It leaves
    /// that file, counts it in the log, and the next clear tries again. Thus such a file stays after sign-out until the
    /// next start or quit.
    /// </remarks>
    public void Clear()
    {
        lock (_lock)
        {
            _generation++;
            _generationCancel.Cancel();
            _generationCancel.Dispose();
            _generationCancel = new CancellationTokenSource();
            _cache.Clear();
            _cachedBytes = 0;
            _inFlight.Clear();
            _framesInFlight.Clear();
            _fullSizeAsked.Clear();
        }

        if (!Directory.Exists(_tempFolder))
        {
            return;
        }

        int left = 0;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(_tempFolder))
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder is gone or cannot be read, so it has nothing to empty.
            LogTempUnreadable(ex.GetType().Name);
        }

        if (left > 0)
        {
            LogTempLeft(left);
        }
    }

    /// <summary>
    /// Makes the file name of a sender safe to write.
    /// </summary>
    /// <remarks>
    /// The method changes these characters to <c>_</c>: path separators, characters that Windows refuses, control
    /// characters, and format characters. A format character such as the right-to-left override can hide an
    /// extension. The method also changes each <c>..</c> to <c>__</c>, and removes dots and spaces at the start and the
    /// end. A Windows device name such as <c>CON</c>, <c>NUL</c> or <c>COM1</c> gets a <c>_</c> in front, with or
    /// without an extension. The method cuts the name to <see cref="MaxNameLength"/> and keeps its extension. An empty
    /// name becomes <c>attachment</c> with an extension from the mime type.
    /// </remarks>
    /// <param name="name">The file name of the sender.</param>
    /// <param name="mime">The mime type, for the extension of a name that has none.</param>
    /// <returns>A file name with no folder part.</returns>
    public static string SanitizeName(string name, string mime)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var built      = new StringBuilder(name ?? string.Empty);
        for (int i = 0; i < built.Length; i++)
        {
            if (char.IsControl(built[i]) || built[i] is '/' or '\\' || Array.IndexOf(invalid, built[i]) >= 0
                || CharUnicodeInfo.GetUnicodeCategory(built[i]) == UnicodeCategory.Format)
            {
                built[i] = '_';
            }
        }

        string safe = built.ToString().Replace("..", "__", StringComparison.Ordinal).Trim(' ', '.');
        if (safe.Length == 0)
        {
            safe = FallbackName + ExtensionOf(mime);
        }

        // Phones often send a name with no extension, such as "image" or an ID. The mime type gives the extension, so
        // the file opens and saves as its type.
        else if (Path.GetExtension(safe).Length <= 1)
        {
            safe += ExtensionOf(mime);
        }

        // Windows refuses a device name, whatever follows the first dot.
        int dot     = safe.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? safe : safe[..dot];
        if (ReservedNames.Contains(stem.TrimEnd(' ')))
        {
            safe = "_" + safe;
        }

        // The cut keeps an extension of up to 16 characters.
        if (safe.Length > MaxNameLength)
        {
            string extension = Path.GetExtension(safe);
            extension        = extension.Length is > 0 and <= 16 ? extension : string.Empty;
            safe             = safe[..(MaxNameLength - extension.Length)].TrimEnd(' ', '.') + extension;
        }

        return safe;
    }

    /// <summary>
    /// Tells whether a received file can open in its default app.
    /// </summary>
    /// <remarks>
    /// Only an allowlist of photo, video, audio, document and contact types opens. The person can only save any other
    /// type or send it to "Open with". The person then decides what to do with it. Other types include programs,
    /// scripts, web pages, SVG, themes, shortcuts and certificates. The check uses the name that
    /// <see cref="SanitizeName"/> gives the file, so dots or spaces at the end cannot hide the extension.
    /// </remarks>
    /// <param name="name">The file name of the sender.</param>
    /// <param name="mime">The mime type, for the extension of a name that has none.</param>
    /// <returns>True for an allowlisted type; false otherwise.</returns>
    public static bool CanOpen(string name, string mime) =>
        OpenableExtensions.Contains(Path.GetExtension(SanitizeName(name, mime)));

    // =========================================================================
    // HELPERS
    // =========================================================================

    // The ID and key to download, or an empty ID for the inline data.
    private static (string Id, byte[] Key) Source(MediaPart part, bool thumbnail)
    {
        if (thumbnail && part.ThumbnailId.Length > 0)
        {
            return (part.ThumbnailId, part.ThumbnailKey);
        }

        if (thumbnail && part.InlineData.Length > 0)
        {
            return (string.Empty, []);
        }

        if (part.MediaId.Length > 0)
        {
            return (part.MediaId, part.Key);
        }

        return part.ThumbnailId.Length > 0 ? (part.ThumbnailId, part.ThumbnailKey) : (string.Empty, []);
    }

    // Downloads 1 media ID for all callers that wait on it. The store keeps the bytes unless a clear ran in the
    // meantime. A clear stops the download. A stalled download fails after DownloadTimeout.
    private async Task<byte[]> DownloadAsync(IMessagingClient client, string id, byte[] key, int generation, CancellationToken clear)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(clear);
        try
        {
            limit.CancelAfter(DownloadTimeout());

            byte[] bytes;
            try
            {
                // Google answers with no bytes until the upload of the sender is complete, as for an arriving video.
                // The store asks again after a short time until the timeout, and never keeps the empty answer.
                while ((bytes = await client.DownloadMediaAsync(id, key, limit.Token).ConfigureAwait(false)).Length == 0)
                {
                    await Task.Delay(NotReadyRetry, limit.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!clear.IsCancellationRequested)
            {
                throw new TimeoutException("The media download took too long.");
            }

            lock (_lock)
            {
                if (generation == _generation)
                {
                    Keep(id, bytes);
                }
            }

            return bytes;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !clear.IsCancellationRequested)
        {
            // A clear at sign-out stops a download on purpose. The filter keeps that stop out of the log.
            LogDownloadFailed(id, ex.GetType().Name);
            throw;
        }
        finally
        {
            lock (_lock)
            {
                if (generation == _generation)
                {
                    _inFlight.Remove(id);
                }
            }
        }
    }

    // Gets the frame of a video from its file, else makes it from the video, 1 at a time. The store keeps the frame
    // unless a clear ran in the meantime. The write happens under the lock, so a clear never leaves a frame file.
    private async Task<byte[]> MakeFrameAsync(MediaPart part, Func<byte[], string, Task<byte[]>> extract, int generation)
    {
        string path = Path.Combine(_tempFolder, "Frames", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(part.MediaId)))[..32] + ".frame");
        try
        {
            if (File.Exists(path))
            {
                return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }

            await _frameTurn.WaitAsync().ConfigureAwait(false);
            try
            {
                byte[] video = await GetAsync(part, thumbnail: false, CancellationToken.None).ConfigureAwait(false);
                byte[] frame = await extract(video, part.Mime).ConfigureAwait(false);
                lock (_lock)
                {
                    if (generation == _generation)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, frame);
                    }
                }

                return frame;
            }
            finally
            {
                _frameTurn.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFrameFailed(part.MediaId, ex.GetType().Name);
            throw;
        }
        finally
        {
            lock (_lock)
            {
                if (generation == _generation)
                {
                    _framesInFlight.Remove(part.MediaId);
                }
            }
        }
    }

    // Adds to the memory cache and drops the least recently used past the cap. The cache does not keep a file over the
    // cap. When a seed and a download both add an ID, the first bytes stay. The caller holds the lock.
    private void Keep(string id, byte[] bytes)
    {
        if (bytes.LongLength > MemoryCapBytes || !_cache.TryAdd(id, bytes))
        {
            return;
        }

        _cachedBytes += bytes.LongLength;
        while (_cachedBytes > MemoryCapBytes)
        {
            _cachedBytes -= _cache.GetAt(0).Value.LongLength;
            _cache.RemoveAt(0);
        }
    }

    /// <summary>
    /// Gives the file extension for a mime type, such as <c>.jpg</c> for <c>image/jpeg</c> or <c>.mp4</c> for
    /// <c>video/mp4</c>.
    /// </summary>
    /// <remarks>
    /// A short table holds the types whose subtype is not their usual extension. Examples are <c>video/quicktime</c>
    /// as <c>.mov</c>, <c>audio/mpeg</c> as <c>.mp3</c> and <c>audio/mp4</c> as <c>.m4a</c>. Any other type uses a
    /// subtype of 1 to 5 letters or digits, such as <c>image/png</c> as <c>.png</c>. Else the type gives no extension.
    /// </remarks>
    /// <param name="mime">The mime type.</param>
    /// <returns>The extension with its dot, or empty when the type gives none.</returns>
    public static string ExtensionOf(string mime)
    {
        // ponytail: a short table plus the subtype. Add rows when the subtype of a type is not its extension.
        string type    = (mime ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        string subtype = type[(type.IndexOf('/', StringComparison.Ordinal) + 1)..];

        return type switch
        {
            "image/jpeg" or "image/jpg" or "image/pjpeg"    => ".jpg",
            "image/svg+xml"                                 => ".svg",
            "image/x-ms-bmp"                                => ".bmp",
            "audio/mp4" or "audio/x-m4a"                    => ".m4a",
            "audio/mpeg" or "audio/mp3"                     => ".mp3",
            "audio/x-wav" or "audio/wave"                   => ".wav",
            "video/quicktime"                               => ".mov",
            "video/3gpp" or "audio/3gpp"                    => ".3gp",
            "video/3gpp2" or "audio/3gpp2"                  => ".3g2",
            "video/x-matroska"                              => ".mkv",
            "video/x-msvideo"                               => ".avi",
            "text/plain"                                    => ".txt",
            "text/vcard" or "text/x-vcard"                  => ".vcf",
            "text/calendar"                                 => ".ics",
            "application/msword"                            => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"   => ".docx",
            "application/vnd.ms-excel"                      => ".xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"         => ".xlsx",
            "application/vnd.ms-powerpoint"                 => ".ppt",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            _                                               => subtype.Length is > 0 and <= 5 && subtype.All(char.IsAsciiLetterOrDigit) ? "." + subtype : string.Empty,
        };
    }

    // =========================================================================
    // LOGGING
    // =========================================================================

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not download media {MediaId} ({ErrorType})")]
    private partial void LogDownloadFailed(string mediaId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not make the frame of video {MediaId} ({ErrorType})")]
    private partial void LogFrameFailed(string mediaId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not ask for the full-size image of {MessageId} ({ErrorType})")]
    private partial void LogFullSizeFailed(string messageId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not mark a media file as from the internet ({ErrorType})")]
    private partial void LogMarkFailed(string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} media temp entries are still open in another app")]
    private partial void LogTempLeft(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not empty the media temp folder ({ErrorType})")]
    private partial void LogTempUnreadable(string errorType);
}

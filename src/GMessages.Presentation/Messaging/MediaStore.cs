using System.Text;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Downloads, decrypts and caches photos, videos, voice messages and files, and writes them to a private temp folder
/// for opening in other apps.
/// </summary>
/// <remarks>
/// <para>
/// A part downloads in the Go connector's order (handlegmessages.go): the full file, else the thumbnail, else the
/// inline data, which comes back as it is, with no download and no decrypting. A thumbnail asked for prefers the
/// thumbnail, then the inline data, then the full file. A pending part throws <see cref="MediaPendingException"/>.
/// </para>
/// <para>
/// Decrypted bytes stay in memory, newest used kept, up to 64 MB in all (a bigger file is handed back but not kept).
/// Asking for a media ID already on its way shares that download. <see cref="RequestFullSizeAsync"/> asks the phone
/// once per message for the full-size image; the phone then sends the message again with the full media ID.
/// </para>
/// <para>
/// <b>Privacy.</b> Media is decrypted only on this device, and lives only in memory or in the temp folder given (in the
/// app, <c>ApplicationData.Current.TemporaryFolder\Media</c>). <see cref="Clear"/> empties both: the app calls it at
/// start, on quit, and on sign-out or unpair (through <see cref="ChatStore.Reset"/>). Bytes, keys, file names and mime
/// types are never logged; only counts and error type names are. Safe to call from any thread.
/// </para>
/// </remarks>
#pragma warning disable CA1001 // Lives As Long As The App; Clear Cancels Its Token Source And Replaces It
public sealed partial class MediaStore
#pragma warning restore CA1001
{
    /// <summary>How many bytes of decrypted media stay in memory.</summary>
    public const long MemoryCapBytes = 64L * 1024 * 1024;

    /// <summary>How long one download may take before it fails, unless <see cref="DownloadTimeout"/> says otherwise.</summary>
    /// <remarks>Long enough for a 100 MB file on a slow connection, under the media client's 30 minute cap.</remarks>
    // ponytail: a whole-download deadline, not a stall check; reset a timer per chunk read if slow links need more
    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The longest file name written to the temp folder, extension included.</summary>
    public const int MaxNameLength = 100;

    // A Name With Nothing Usable Left
    private const string FallbackName = "attachment";

    // Windows Device Names, Refused As A File Name With Or Without An Extension
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Programs, Scripts, Installers And Shortcuts: A Received One Is Never Opened, Only Saved Where The Person Picks
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msc", ".dll", ".ocx", ".sys", ".drv",
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".vb", ".vbs", ".vbe",
        ".vbscript", ".js", ".jse", ".ws", ".wsf", ".wsc", ".wsh", ".hta", ".jar", ".jnlp", ".py", ".pyw", ".pyc", ".pl",
        ".rb", ".shs", ".sct", ".msh", ".msh1", ".msh2", ".mshxml",
        ".msi", ".msp", ".mst", ".msu", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".application",
        ".appref-ms", ".xbap", ".gadget", ".vsto",
        ".lnk", ".url", ".website", ".scf", ".inf", ".ins", ".isp", ".its", ".job", ".mmc", ".reg", ".rdp", ".chm", ".hlp",
        ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms", ".diagcab", ".xll", ".ade", ".adp",
        ".mde", ".iso", ".img", ".vhd", ".vhdx", ".cab",
    };

    private readonly Func<IMessagingClient?> _client;
    private readonly string _tempFolder;
    private readonly ILogger _log;
    private readonly Lock _lock = new();

    // Memory Cache By Media ID: The Dictionary Order Is Least Recently Used First
    private readonly OrderedDictionary<string, byte[]> _cache = [];
    private long _cachedBytes;

    // Downloads On Their Way, By Media ID
    private readonly Dictionary<string, Task<byte[]>> _inFlight = [];

    // Messages Whose Full-Size Image Was Asked For
    private readonly HashSet<string> _fullSizeAsked = [];

    // Bumped By Clear: A Download Started Before It Is Not Cached
    private int _generation;

    // Canceled And Replaced By Clear: Aborts Every Download Started Before It
    private CancellationTokenSource _generationCancel = new();

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="client">Gets the current phone client, or null when not connected.</param>
    /// <param name="tempFolder">The private folder files are written to for other apps; emptied by <see cref="Clear"/>.</param>
    /// <param name="log">The logger.</param>
    public MediaStore(Func<IMessagingClient?> client, string tempFolder, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(tempFolder);
        ArgumentNullException.ThrowIfNull(log);

        _client     = client;
        _tempFolder = tempFolder;
        _log        = log;
    }

    /// <summary>Gets the private temp folder (emptied by <see cref="Clear"/>), where voice recordings go too.</summary>
    public string TempFolder => _tempFolder;

    /// <summary>
    /// Gets or sets how long one download may take, read as each download starts. Past it the request is aborted and
    /// the download fails with a <see cref="TimeoutException"/>. Defaults to <see cref="DefaultDownloadTimeout"/>; the
    /// UI test hooks shorten it.
    /// </summary>
    public Func<TimeSpan> DownloadTimeout { get; set; } = static () => DefaultDownloadTimeout;

    // =========================================================================
    // MEDIA
    // =========================================================================

    /// <summary>
    /// Gets a part's decrypted bytes, from memory or downloaded.
    /// </summary>
    /// <param name="part">The media part.</param>
    /// <param name="thumbnail">True to prefer the thumbnail (a photo's first, blurred look).</param>
    /// <param name="ct">Cancels the wait (not a download another caller shares).</param>
    /// <returns>The decrypted bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="part"/> is null.</exception>
    /// <exception cref="MediaPendingException">The part has no media ID, thumbnail or inline data yet.</exception>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    /// <exception cref="TimeoutException">The download took longer than <see cref="DownloadTimeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled, or <see cref="Clear"/> aborted the download.</exception>
    public Task<byte[]> GetAsync(MediaPart part, bool thumbnail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);

        if (part.IsPending)
        {
            throw new MediaPendingException();
        }

        // Pick The Source; Inline Data Needs No Download
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
                // Started Off This Thread, So It Can't Finish (And Leave _inFlight) Before It Is Added
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
    /// Keeps bytes this device already has under a media ID, so asking for that ID later downloads nothing.
    /// </summary>
    /// <remarks>
    /// Used when a sent attachment's echo arrives: the phone gives the file a media ID, and the bytes that were sent are
    /// that file. An ID already kept is left as it is. Kept under the same memory cap as downloads.
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
            if (!_cache.ContainsKey(mediaId))
            {
                Keep(mediaId, bytes);
            }
        }
    }

    /// <summary>
    /// Writes a part's decrypted file to the temp folder, so another app can open it.
    /// </summary>
    /// <remarks>
    /// Each file gets its own random folder, <c>{temp}\{random}\{name}</c>, so two files with one name never clash.
    /// The name is the sender's, made safe by <see cref="SanitizeName"/>.
    /// </remarks>
    /// <param name="part">The media part.</param>
    /// <param name="ct">Cancels the wait and the write.</param>
    /// <returns>The full path of the written file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="part"/> is null.</exception>
    /// <exception cref="MediaPendingException">The part has no media ID, thumbnail or inline data yet.</exception>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled, or <see cref="Clear"/> ran before the file was written.</exception>
    public async Task<string> SaveToTempAsync(MediaPart part, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);

        // Note The Generation First: A Clear (Sign-Out) During The Download Means Nothing Is Written
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
    /// Marks a written file as downloaded from the internet (the mark of the web), as a browser download is.
    /// </summary>
    /// <remarks>
    /// Writes the <c>Zone.Identifier</c> stream with <c>ZoneId=3</c>, so Windows applies SmartScreen, Office's Protected
    /// View and its "downloaded from the internet" warnings to the file. Only this method writes the stream: the name
    /// sanitizer refuses <c>:</c> in names. A drive that can't hold the stream (not NTFS) logs the error type and moves on.
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
    /// The phone answers by sending the message again with its full media ID, which replaces the bubble's media part.
    /// A message that already has its full file, has no thumbnail, or was asked for before is left alone. A failed ask
    /// is forgotten, so it can be tried again.
    /// </remarks>
    /// <param name="m">The message.</param>
    /// <param name="again">True to ask again though asked before (the photo never came, and the person retries).</param>
    /// <returns>A task that completes when the phone accepted the request, or at once when nothing is asked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="m"/> is null.</exception>
    /// <exception cref="MessagingUnavailableException">Not connected.</exception>
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
    /// Forgets every decrypted file: the memory cache, the downloads on their way, the full-size asks, and the temp folder.
    /// </summary>
    /// <remarks>
    /// A file another app still holds open can't be deleted; it is left and counted in the log, and the next clear tries
    /// again. A download on its way is aborted: its request stops, nothing is decrypted, and its callers get an
    /// <see cref="OperationCanceledException"/>. So a file another app still holds open survives sign-out until the
    /// next start or quit.
    /// </remarks>
    public void Clear()
    {
        lock (_lock)
        {
            _generation++;
            _generationCancel.Cancel();
            _generationCancel = new CancellationTokenSource();
            _cache.Clear();
            _cachedBytes = 0;
            _inFlight.Clear();
            _fullSizeAsked.Clear();
        }

        // Empty The Temp Folder, Keeping Whatever Another App Still Holds Open
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
            // The Folder Itself Went Or Can't Be Read: Nothing Here To Empty
            LogTempUnreadable(ex.GetType().Name);
        }

        if (left > 0)
        {
            LogTempLeft(left);
        }
    }

    /// <summary>
    /// Makes a sender's file name safe to write.
    /// </summary>
    /// <remarks>
    /// Path separators, characters Windows refuses and control characters become <c>_</c>, as does any <c>..</c>.
    /// Leading and trailing dots and spaces go. A Windows device name (<c>CON</c>, <c>NUL</c>, <c>COM1</c>...), with or
    /// without an extension, gets a <c>_</c> in front. The name is cut to <see cref="MaxNameLength"/>, keeping its
    /// extension. An empty name becomes <c>attachment</c> with an extension from the mime type.
    /// </remarks>
    /// <param name="name">The sender's file name.</param>
    /// <param name="mime">The mime type, for the extension of an empty name.</param>
    /// <returns>A file name with no folder part.</returns>
    public static string SanitizeName(string name, string mime)
    {
        // Replace Separators, Refused And Control Characters, And Any Parent-Folder Step
        char[] invalid = Path.GetInvalidFileNameChars();
        var built      = new StringBuilder(name ?? string.Empty);
        for (int i = 0; i < built.Length; i++)
        {
            if (char.IsControl(built[i]) || built[i] is '/' or '\\' || Array.IndexOf(invalid, built[i]) >= 0)
            {
                built[i] = '_';
            }
        }

        string safe = built.ToString().Replace("..", "__", StringComparison.Ordinal).Trim(' ', '.');
        if (safe.Length == 0)
        {
            safe = FallbackName + ExtensionOf(mime);
        }

        // Device Names Are Refused Whatever Follows The First Dot
        int dot     = safe.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? safe : safe[..dot];
        if (ReservedNames.Contains(stem.TrimEnd(' ')))
        {
            safe = "_" + safe;
        }

        // Cut To Length, Keeping A Short Extension
        if (safe.Length > MaxNameLength)
        {
            string extension = Path.GetExtension(safe);
            extension        = extension.Length is > 0 and <= 16 ? extension : string.Empty;
            safe             = safe[..(MaxNameLength - extension.Length)].TrimEnd(' ', '.') + extension;
        }

        return safe;
    }

    /// <summary>
    /// Tells whether a received file may open in its default app.
    /// </summary>
    /// <remarks>
    /// Programs, scripts, installers and shortcuts (<c>.exe</c>, <c>.bat</c>, <c>.ps1</c>, <c>.js</c>, <c>.msi</c>,
    /// <c>.lnk</c>...) never open: the person can only save them, and decides what to do with them. The check is made on
    /// the name the file is written under (see <see cref="SanitizeName"/>), so trailing dots or spaces can't hide the
    /// extension.
    /// </remarks>
    /// <param name="name">The sender's file name.</param>
    /// <param name="mime">The mime type, for the extension of an empty name.</param>
    /// <returns>False for a program or script; true otherwise.</returns>
    public static bool CanOpen(string name, string mime) =>
        !ProgramExtensions.Contains(Path.GetExtension(SanitizeName(name, mime)));

    // =========================================================================
    // HELPERS
    // =========================================================================

    // The ID And Key To Download, Or An Empty ID For The Inline Data
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

    // Downloads One Media ID For Everyone Waiting On It, Then Keeps It Unless Clear Ran Meanwhile. Clear Aborts It; A
    // Stalled One Gives Up After DownloadTimeout
    private async Task<byte[]> DownloadAsync(IMessagingClient client, string id, byte[] key, int generation, CancellationToken clear)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(clear);
        try
        {
            limit.CancelAfter(DownloadTimeout());

            byte[] bytes;
            try
            {
                bytes = await client.DownloadMediaAsync(id, key, limit.Token).ConfigureAwait(false);
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
            // A Sign-Out Aborting It Isn't A Failure Worth Logging
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

    // Adds To The Memory Cache, Dropping The Least Recently Used Past The Cap; A File Over The Cap Is Not Kept
    private void Keep(string id, byte[] bytes)
    {
        if (bytes.LongLength > MemoryCapBytes)
        {
            return;
        }

        _cache.Add(id, bytes);
        _cachedBytes += bytes.LongLength;
        while (_cachedBytes > MemoryCapBytes)
        {
            _cachedBytes -= _cache.GetAt(0).Value.LongLength;
            _cache.RemoveAt(0);
        }
    }

    // ponytail: a short guess from the mime subtype (jpeg as jpg, audio/mp4 as m4a); a full table if other apps choke
    private static string ExtensionOf(string mime)
    {
        string type    = mime ?? string.Empty;
        string subtype = type[(type.IndexOf('/', StringComparison.Ordinal) + 1)..];

        return type switch
        {
            "image/jpeg" => ".jpg",
            "audio/mp4"  => ".m4a",
            _            => subtype.Length is > 0 and <= 5 && subtype.All(char.IsAsciiLetterOrDigit) ? "." + subtype : string.Empty,
        };
    }

    // =========================================================================
    // LOGGING
    // =========================================================================

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not download media {MediaId} ({ErrorType})")]
    private partial void LogDownloadFailed(string mediaId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not ask for the full-size image of {MessageId} ({ErrorType})")]
    private partial void LogFullSizeFailed(string messageId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not mark a media file as from the internet ({ErrorType})")]
    private partial void LogMarkFailed(string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} media temp entries are still open in another app")]
    private partial void LogTempLeft(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not empty the media temp folder ({ErrorType})")]
    private partial void LogTempUnreadable(string errorType);
}

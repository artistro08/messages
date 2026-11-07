using System.Security.Cryptography;
using System.Text.Json;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Proto;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Session;

/// <summary>
/// Saves and loads <see cref="AuthData"/> in a file that DPAPI encrypts for the current user.
/// </summary>
/// <remarks>
/// The storage is C#-only. Go keeps this JSON in the bridge database. A source-generated JSON context serializes the
/// data, and <see cref="ProtectedData"/> encrypts it. Thus only the same Windows user can read it. A save writes a
/// <c>.tmp</c> file and then replaces the real file, so a crash never leaves a half-written session. A load never
/// throws for a bad file. The store logs only the type of the reason, never the contents. The related Go type is
/// <c>AuthData</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// </remarks>
/// <param name="filePath">The path of the session file.</param>
/// <param name="logger">The logger for load failures, or null.</param>
public sealed partial class SessionStore(string filePath, ILogger? logger = null)
{
    /// <summary>
    /// Encrypts and writes the session.
    /// </summary>
    /// <remarks>The method creates the folder when necessary. It replaces an existing file.</remarks>
    /// <param name="data">The session to save.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(AuthData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // The method takes 1 token snapshot, so a refresh that runs now cannot tear it.
        TachyonToken tachyon = data.Tachyon;
        SessionFile file     = new()
        {
            AesKey           = data.RequestCrypto.AesKey,
            HmacKey          = data.RequestCrypto.HmacKey,
            RefreshD         = data.RefreshKey.D,
            RefreshX         = data.RefreshKey.X,
            RefreshY         = data.RefreshKey.Y,
            Browser          = data.Browser?.ToByteArray(),
            Mobile           = data.Mobile?.ToByteArray(),
            TachyonToken     = tachyon.Token,
            TachyonExpiry    = tachyon.Expiry,
            TachyonTtlMicros = tachyon.TtlMicros,
            WebEncryptionKey = data.WebEncryptionKey,
            SessionId        = data.SessionId,
            DestRegId        = data.DestRegId,
            DestPhoneId      = data.DestPhoneId,
            PairingId        = data.PairingId,
            Cookies          = data.Cookies.HasCookies ? new Dictionary<string, string>(data.Cookies.Snapshot()) : null,
        };

        // ponytail: the wipe is best effort. The SessionFile above still holds the same secrets.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(file, SessionJsonContext.Default.SessionFile);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // ponytail: the store expects 1 writer. GmClient lets only 1 save run at a time.
        string tmp = filePath + ".tmp";
        using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(encrypted);
            fs.Flush(true);
        }

        File.Move(tmp, filePath, overwrite: true);
    }

    /// <summary>
    /// Reads and decrypts the session.
    /// </summary>
    /// <remarks>
    /// The method returns null and does not throw when the file is missing or cut off. It also returns null when the
    /// file is not DPAPI data, is from a different user, is not valid JSON or has no required keys. The caller then
    /// treats the result as "not logged in".
    /// </remarks>
    /// <returns>The session, or null.</returns>
    public AuthData? Load()
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        // The finally block wipes the JSON as a best effort. The parsed session still holds the same secrets.
        byte[]? json = null;
        try
        {
            json          = ProtectedData.Unprotect(File.ReadAllBytes(filePath), null, DataProtectionScope.CurrentUser);
            SessionFile f = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionFile)
                            ?? throw new InvalidDataException("Empty session.");

            if (f.AesKey is null || f.HmacKey is null || f.RefreshD is null || f.RefreshX is null || f.RefreshY is null)
            {
                throw new InvalidDataException("Missing keys.");
            }

            if (f.AesKey.Length != 32 || f.HmacKey.Length != 32)
            {
                throw new InvalidDataException("Bad key length.");
            }

            AuthData data = new()
            {
                RequestCrypto    = new AesCtrHelper(f.AesKey, f.HmacKey),
                RefreshKey       = RefreshKey.FromJwk(f.RefreshD, f.RefreshX, f.RefreshY),
                Browser          = f.Browser is null ? null : Device.Parser.ParseFrom(f.Browser),
                Mobile           = f.Mobile is null ? null : Device.Parser.ParseFrom(f.Mobile),
                Tachyon          = new TachyonToken(f.TachyonToken is { Length: > 0 } token ? token : null, f.TachyonExpiry, f.TachyonTtlMicros),
                WebEncryptionKey = f.WebEncryptionKey,
                SessionId        = f.SessionId,
                DestRegId        = f.DestRegId,
                DestPhoneId      = f.DestPhoneId,
                PairingId        = f.PairingId,
            };
            if (f.Cookies is not null)
            {
                data.Cookies.Set(f.Cookies);
            }

            return data;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException or InvalidProtocolBufferException
                                       or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (logger is not null)
            {
                LogLoadFailed(logger, ex.GetType().Name);
            }

            return null;
        }
        finally
        {
            if (json is not null)
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
    }

    /// <summary>
    /// Deletes the session file and any leftover temp file.
    /// </summary>
    /// <remarks>The unpair uses it. A missing file is not an error.</remarks>
    /// <exception cref="IOException">The file is in use.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be deleted.</exception>
    public void Delete()
    {
        File.Delete(filePath + ".tmp");
        File.Delete(filePath);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session file could not be loaded ({Reason})")]
    private static partial void LogLoadFailed(ILogger logger, string reason);
}

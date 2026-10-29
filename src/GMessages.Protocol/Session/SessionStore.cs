using System.Security.Cryptography;
using System.Text.Json;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Proto;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Session;

/// <summary>
/// Saves and loads <see cref="AuthData"/> in a DPAPI-encrypted file (current user scope).
/// </summary>
/// <remarks>
/// Storage is C#-only; Go keeps this JSON in the bridge database. The data is serialized with a source-generated
/// JSON context and encrypted with <see cref="ProtectedData"/>, so only the same Windows user can read it. Saving
/// writes a <c>.tmp</c> file and then replaces the real one, so a crash never leaves a half-written session.
/// Loading never throws for a bad file. Only the reason type is logged, never the contents.
/// Related Go type: <c>AuthData</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// </remarks>
/// <param name="filePath">Where the session file lives.</param>
/// <param name="logger">Optional logger for load failures.</param>
public sealed partial class SessionStore(string filePath, ILogger? logger = null)
{
    /// <summary>
    /// Encrypts and writes the session.
    /// </summary>
    /// <remarks>Creates the folder if needed. Replaces any existing file.</remarks>
    /// <param name="data">The session to save.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(AuthData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Map To Stored Shape (One Token Snapshot, So A Refresh Running Now Can't Tear It)
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

        // Encrypt
        byte[] json      = JsonSerializer.SerializeToUtf8Bytes(file, SessionJsonContext.Default.SessionFile);
        byte[] encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);

        // Write Temp, Then Replace
        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmp = filePath + ".tmp";
        // ponytail: single writer assumed; GmClient serializes saves
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
    /// Returns null instead of throwing when the file is missing, cut off, not DPAPI data (or from another user),
    /// not valid JSON, or lacks required keys. The caller then treats it as "not logged in".
    /// </remarks>
    /// <returns>The session, or null.</returns>
    public AuthData? Load()
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            byte[] json   = ProtectedData.Unprotect(File.ReadAllBytes(filePath), null, DataProtectionScope.CurrentUser);
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
                // Sessions From Older Versions Saved __Secure-1PSIDTS, A Short-Lived Cookie Google Stops Accepting; Sending The Stale Copy Gets The Session Refused
                data.Cookies.Set(f.Cookies.Where(c => c.Key != "__Secure-1PSIDTS").ToDictionary(c => c.Key, c => c.Value));
            }

            return data;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException or InvalidProtocolBufferException
                                       or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Log The Reason Type Only, Never The Contents
            if (logger is not null)
            {
                LogLoadFailed(logger, ex.GetType().Name);
            }

            return null;
        }
    }

    /// <summary>
    /// Deletes the session file and any leftover temp file.
    /// </summary>
    /// <remarks>Used when unpairing. A missing file is not an error.</remarks>
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

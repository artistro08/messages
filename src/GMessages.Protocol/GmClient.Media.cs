using System.Globalization;
using System.Net;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using Google.Protobuf;

namespace GMessages.Protocol;

/// <summary>
/// Media: uploading an attachment, downloading one, and asking the phone for a full-size image.
/// </summary>
/// <remarks>
/// Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/media.go">media.go</see>
/// and <c>GetFullSizeImage</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">methods.go</see>.
/// Media goes to <see cref="GmEndpoints.UploadMedia"/> with LibGM's upload headers only: no api key and no cookies.
/// Every file is encrypted with <see cref="MediaCrypto"/> and a fresh key before it leaves, and decrypted only here.
/// Keys, media bytes and file names are never logged.
/// </remarks>
public sealed partial class GmClient
{
    // Upload Body Slices, So Progress Moves While A Big File Goes Out
    private const int UploadSliceSize = 64 * 1024;

    // =========================================================================
    // MEDIA
    // =========================================================================

    /// <summary>
    /// Encrypts and uploads an attachment, ready to send in a message.
    /// </summary>
    /// <remarks>
    /// Ported from <c>UploadMedia</c>, <c>StartUploadMedia</c> and <c>FinalizeUploadMedia</c>. The file is encrypted
    /// with a new key, then a resumable upload is started (its body is the base64 of a
    /// <see cref="StartMediaUploadRequest"/>) and finalized with the whole encrypted file. The start answer must carry
    /// <c>x-goog-upload-url</c> and an integer <c>x-goog-upload-chunk-granularity</c>. The finalize answer is an
    /// <see cref="UploadMediaResponse"/>, base64 or raw. The format comes from <see cref="MediaFormatsMap.FromMime"/>.
    /// Put the result in a <see cref="MessageInfo"/> of a <see cref="SendMessageRequest"/>.
    /// </remarks>
    /// <param name="plain">The file's bytes.</param>
    /// <param name="fileName">The file name the other side sees.</param>
    /// <param name="mime">The mime type, such as <c>image/jpeg</c>.</param>
    /// <param name="progress">Gets the share of the encrypted file sent so far, from 0 to 1; may be null.</param>
    /// <param name="ct">Cancels the upload.</param>
    /// <returns>The media content: format, media ID, name, plain size, decryption key and mime type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> or <paramref name="mime"/> is null.</exception>
    /// <exception cref="InvalidDataException">The start answer lacks the upload URL or granularity, or the finalize answer has no media ID.</exception>
    /// <exception cref="InvalidProtocolBufferException">The finalize answer is not an upload response.</exception>
    /// <exception cref="GmRequestException">The server refused the start or the finalize.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<MediaContent> UploadMediaAsync(ReadOnlyMemory<byte> plain, string fileName, string mime, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(mime);

        // Encrypt With A Fresh Key
        byte[] key       = MediaCrypto.GenerateKey();
        byte[] encrypted = MediaCrypto.Encrypt(plain.Span, key);
        string size      = encrypted.Length.ToString(CultureInfo.InvariantCulture);
        progress?.Report(0);

        // Start: Base64 Of The Start Request
        var start_request = new StartMediaUploadRequest { AttachmentType = 1, AuthData = MediaAuth(), Mobile = _auth.Mobile };
        var start_body    = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(start_request.ToByteArray())));
        GmMediaResponse started = await _http.SendMediaAsync(HttpMethod.Post, _endpoints.UploadMedia, UploadHeaders(size, "start", "", mime, "resumable"), start_body, ct).ConfigureAwait(false);

        if (!started.Headers.TryGetValue("x-goog-upload-url", out string? upload_url) || !Uri.TryCreate(upload_url, UriKind.Absolute, out Uri? upload_uri))
        {
            throw new InvalidDataException("The upload start answer has no upload URL.");
        }

        if (!started.Headers.TryGetValue("x-goog-upload-chunk-granularity", out string? granularity) || !int.TryParse(granularity, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidDataException("The upload start answer has no chunk granularity.");
        }

        // Finalize: The Whole Encrypted File
        var file_body             = new ProgressContent(encrypted, progress);
        GmMediaResponse finalized = await _http.SendMediaAsync(HttpMethod.Post, upload_uri, UploadHeaders(size, "upload, finalize", "0", mime, ""), file_body, ct).ConfigureAwait(false);
        UploadMediaResponse uploaded = UploadMediaResponse.Parser.ParseFrom(IsStandardBase64(finalized.Body) ? Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(finalized.Body)) : finalized.Body);
        if (uploaded.Media is not { MediaID.Length: > 0 } media)
        {
            throw new InvalidDataException("The upload answer has no media ID.");
        }

        return new MediaContent
        {
            Format        = MediaFormatsMap.FromMime(mime),
            MediaID       = media.MediaID,
            MediaName     = fileName,
            Size          = plain.Length,
            DecryptionKey = ByteString.CopyFrom(key),
            MimeType      = mime,
        };
    }

    /// <summary>
    /// Downloads and decrypts an attachment.
    /// </summary>
    /// <remarks>
    /// Ported from <c>DownloadMedia</c>: a GET whose <c>x-goog-download-metadata</c> header is the base64 of a
    /// <see cref="DownloadAttachmentRequest"/> (encrypted, with the account's auth data). Unlike Go, the status is
    /// checked. Use a message's <c>MediaID</c> and <c>DecryptionKey</c>, or its thumbnail pair.
    /// </remarks>
    /// <param name="mediaId">The media ID.</param>
    /// <param name="key">The 32-byte decryption key.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>The decrypted media.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mediaId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not 32 bytes (Go's <c>NewAESGCMHelper</c> refuses it too).</exception>
    /// <exception cref="GmRequestException">The server refused the download.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The file does not decrypt with the key.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<byte[]> DownloadMediaAsync(string mediaId, ReadOnlyMemory<byte> key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mediaId);
        if (key.Length != MediaCrypto.KeySize)
        {
            throw new ArgumentException($"Unsupported AES key length (got={key.Length} expected={MediaCrypto.KeySize}).", nameof(key));
        }

        // Metadata: Base64 Of The Download Request
        var metadata = new DownloadAttachmentRequest
        {
            Info     = new AttachmentInfo { AttachmentID = mediaId, Encrypted = true },
            AuthData = MediaAuth(),
        };
        string metadata_header = Convert.ToBase64String(metadata.ToByteArray());

        // Download And Decrypt
        GmMediaResponse res = await _http.SendMediaAsync(HttpMethod.Get, _endpoints.UploadMedia, DownloadHeaders(metadata_header), null, ct).ConfigureAwait(false);
        return MediaCrypto.Decrypt(res.Body, key.Span);
    }

    /// <summary>
    /// Asks the phone for the full-size version of an image that arrived as a thumbnail only.
    /// </summary>
    /// <remarks>
    /// Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">GetFullSizeImage</see>.
    /// Sent as user-initiated. The phone answers at once, then sends the message again with its full media ID, which
    /// arrives as an update of the same message.
    /// </remarks>
    /// <param name="messageId">The message ID.</param>
    /// <param name="actionMessageId">The ID of the message's media part (Go passes it through as given).</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task that completes when the phone accepted the request.</returns>
    /// <exception cref="InvalidDataException">The phone answered with another type.</exception>
    /// <exception cref="PhoneNotRespondingException">No answer within 60 seconds.</exception>
    /// <exception cref="ConnectionClosedException">Not connected, or the connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task GetFullSizeImageAsync(string messageId, string actionMessageId, CancellationToken ct)
    {
        var request = new GetFullSizeImageRequest { MessageID = messageId, ActionMessageID = actionMessageId };

        IncomingRpc answer = await Rpc.SendAsync(new SendParams(ActionType.GetFullSizeImage, request, UserInitiated: true), ct).ConfigureAwait(false);
        Typed<GetFullSizeImageResponse>(answer);
    }

    // =========================================================================
    // MEDIA HELPERS
    // =========================================================================

    // The Auth Block Of Media Requests, Built As RpcSession Builds It For Acks
    private AuthMessage MediaAuth() => new()
    {
        RequestID        = Guid.NewGuid().ToString(),
        TachyonAuthToken = ByteString.CopyFrom(_auth.TachyonAuthToken ?? []),
        Network          = _auth.AuthNetwork,
        ConfigVersion    = GmConstants.ConfigVersion,
    };

    // Port Of util.NewMediaUploadHeaders (Empty Values Are Left Out, As In Go)
    private static List<KeyValuePair<string, string>> UploadHeaders(string size, string command, string offset, string mime, string protocol)
    {
        List<KeyValuePair<string, string>> headers =
        [
            new("sec-ch-ua", GmConstants.SecUA),
            new("x-goog-upload-protocol", protocol),
            new("x-goog-upload-header-content-length", size),
            new("sec-ch-ua-mobile", GmConstants.SecUAMobile),
            new("user-agent", GmConstants.UserAgent),
            new("x-goog-upload-header-content-type", mime),
            new("content-type", "application/x-www-form-urlencoded;charset=UTF-8"),
            new("x-goog-upload-command", command),
            new("x-goog-upload-offset", offset),
            .. BrowserHeaders(),
        ];

        return [.. headers.Where(h => h.Value.Length > 0)];
    }

    // Port Of util.BuildUploadHeaders
    private static List<KeyValuePair<string, string>> DownloadHeaders(string metadata) =>
    [
        new("x-goog-download-metadata", metadata),
        new("sec-ch-ua", GmConstants.SecUA),
        new("sec-ch-ua-mobile", GmConstants.SecUAMobile),
        new("user-agent", GmConstants.UserAgent),
        .. BrowserHeaders(),
    ];

    // The Browser Tail Both Go Header Sets End With
    private static KeyValuePair<string, string>[] BrowserHeaders() =>
    [
        new("sec-ch-ua-platform", "\"" + GmConstants.UAPlatform + "\""),
        new("accept", "*/*"),
        new("origin", "https://messages.google.com"),
        new("sec-fetch-site", "cross-site"),
        new("sec-fetch-mode", "cors"),
        new("sec-fetch-dest", "empty"),
        new("referer", "https://messages.google.com/"),
        new("accept-encoding", "gzip, deflate, br"),
        new("accept-language", "en-US,en;q=0.9"),
    ];

    // Port Of isStandardBase64: Whole Groups Of Four, Base64 Characters Only
    private static bool IsStandardBase64(byte[] data) =>
        data.Length % 4 == 0 && data.All(c => c is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'+' or (byte)'/' or (byte)'=');

    // A Body Sent In Slices, Reporting The Share Sent After Each
    private sealed class ProgressContent(byte[] bytes, IProgress<double>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (int sent = 0; sent < bytes.Length;)
            {
                int length = Math.Min(UploadSliceSize, bytes.Length - sent);
                await stream.WriteAsync(bytes.AsMemory(sent, length), cancellationToken).ConfigureAwait(false);
                sent += length;
                progress?.Report((double)sent / bytes.Length);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }
}

using System.Globalization;
using System.Net;
using System.Text;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using Google.Protobuf;

namespace GMessages.Protocol;

/// <summary>
/// The media part of <see cref="GmClient"/>. It uploads and downloads attachments and asks the phone for a full-size image.
/// </summary>
/// <remarks>
/// Ported from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/media.go">media.go</see>
/// and <c>GetFullSizeImage</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">methods.go</see>.
/// Media goes to <see cref="GmEndpoints.UploadMedia"/> with only the LibGM upload headers. It has no API key and no
/// cookies. <see cref="MediaCrypto"/> encrypts each file with a new key before it goes out. Only this class decrypts
/// media. The client never logs keys, media bytes or file names.
/// </remarks>
public sealed partial class GmClient
{
    // The upload body goes in slices, so the progress moves while a big file goes out.
    private const int UploadSliceSize = 64 * 1024;

    /// <summary>
    /// Encrypts and uploads an attachment for a message.
    /// </summary>
    /// <remarks>
    /// Ported from <c>UploadMedia</c>, <c>StartUploadMedia</c> and <c>FinalizeUploadMedia</c>. The method encrypts the
    /// file with a new key and starts a resumable upload. The start body is the base64 of a
    /// <see cref="StartMediaUploadRequest"/>. The finalize request sends the full encrypted file. The start answer must
    /// have <c>x-goog-upload-url</c> and an integer <c>x-goog-upload-chunk-granularity</c>. The finalize answer is an
    /// <see cref="UploadMediaResponse"/>, as base64 or raw bytes. <see cref="MediaFormatsMap.FromMime"/> gives the
    /// format. Put the result in a <see cref="MessageInfo"/> of a <see cref="SendMessageRequest"/>.
    /// </remarks>
    /// <param name="plain">The bytes of the file.</param>
    /// <param name="fileName">The file name that the other side sees.</param>
    /// <param name="mime">The mime type, such as <c>image/jpeg</c>.</param>
    /// <param name="progress">Gets the part of the encrypted file sent, from 0 to 1. Can be null.</param>
    /// <param name="ct">Cancels the upload.</param>
    /// <returns>The media content: format, media ID, name, plain size, decryption key and mime type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> or <paramref name="mime"/> is null.</exception>
    /// <exception cref="InvalidDataException">The start answer lacks the upload URL or granularity, the finalize answer is base64 with bad padding or has no media ID, or an answer is over <see cref="GmHttpClient.MaxMediaBodyBytes"/>.</exception>
    /// <exception cref="InvalidProtocolBufferException">The finalize answer is not an upload response.</exception>
    /// <exception cref="GmRequestException">The start or the finalize did not answer HTTP 200.</exception>
    /// <exception cref="HttpRequestException">The request failed before any response arrived.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<MediaContent> UploadMediaAsync(ReadOnlyMemory<byte> plain, string fileName, string mime, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(mime);

        byte[] key       = MediaCrypto.GenerateKey();
        byte[] encrypted = MediaCrypto.Encrypt(plain.Span, key);
        string size      = encrypted.Length.ToString(CultureInfo.InvariantCulture);
        progress?.Report(0);

        var startRequest  = new StartMediaUploadRequest { AttachmentType = 1, AuthData = MediaAuth(), Mobile = _auth.Mobile };
        var startBody     = new ByteArrayContent(Encoding.ASCII.GetBytes(Convert.ToBase64String(startRequest.ToByteArray())));
        GmMediaResponse started = await _http.SendMediaAsync(HttpMethod.Post, _endpoints.UploadMedia, UploadHeaders(size, "start", "", mime, "resumable"), startBody, requireOk: true, ct).ConfigureAwait(false);

        if (!started.Headers.TryGetValue("x-goog-upload-url", out string? uploadUrl) || !Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? uploadUri))
        {
            throw new InvalidDataException("The upload start answer has no upload URL.");
        }

        if (!started.Headers.TryGetValue("x-goog-upload-chunk-granularity", out string? granularity) || !int.TryParse(granularity, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidDataException("The upload start answer has no chunk granularity.");
        }

        var fileBody              = new ProgressContent(encrypted, progress);
        GmMediaResponse finalized = await _http.SendMediaAsync(HttpMethod.Post, uploadUri, UploadHeaders(size, "upload, finalize", "0", mime, ""), fileBody, requireOk: true, ct).ConfigureAwait(false);
        UploadMediaResponse uploaded = UploadMediaResponse.Parser.ParseFrom(DecodeUploadAnswer(finalized.Body));
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
    /// Ported from <c>DownloadMedia</c>. The method sends a GET with an <c>x-goog-download-metadata</c> header. The
    /// header is the base64 of an encrypted <see cref="DownloadAttachmentRequest"/> with the auth data of the account.
    /// Unlike Go, the method checks the status. Use the <c>MediaID</c> and <c>DecryptionKey</c> of a message, or its
    /// thumbnail pair.
    /// </remarks>
    /// <param name="mediaId">The media ID.</param>
    /// <param name="key">The 32-byte decryption key.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>The decrypted media.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mediaId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not 32 bytes. Go's <c>NewAESGCMHelper</c> also refuses it.</exception>
    /// <exception cref="GmRequestException">The server refused the download.</exception>
    /// <exception cref="InvalidDataException">The download is over <see cref="GmHttpClient.MaxMediaBodyBytes"/>.</exception>
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

        var metadata = new DownloadAttachmentRequest
        {
            Info     = new AttachmentInfo { AttachmentID = mediaId, Encrypted = true },
            AuthData = MediaAuth(),
        };
        string metadataHeader = Convert.ToBase64String(metadata.ToByteArray());

        GmMediaResponse res = await _http.SendMediaAsync(HttpMethod.Get, _endpoints.UploadMedia, DownloadHeaders(metadataHeader), null, requireOk: false, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return MediaCrypto.Decrypt(res.Body, key.Span);
    }

    /// <summary>
    /// Asks the phone for the full-size version of an image that arrived as a thumbnail only.
    /// </summary>
    /// <remarks>
    /// Ported from <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/methods.go">GetFullSizeImage</see>.
    /// The request is user-initiated. The phone answers at once. Then it sends the message again with its full media
    /// ID, as an update of the same message.
    /// </remarks>
    /// <param name="messageId">The message ID.</param>
    /// <param name="actionMessageId">The ID of the media part of the message. Go sends it as it is.</param>
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

    // The auth block of media requests is the same as the one that RpcSession builds for acks.
    private AuthMessage MediaAuth() => new()
    {
        RequestID        = Guid.NewGuid().ToString(),
        TachyonAuthToken = ByteString.CopyFrom(_auth.TachyonAuthToken ?? []),
        Network          = _auth.AuthNetwork,
        ConfigVersion    = GmConstants.ConfigVersion,
    };

    // Port of util.NewMediaUploadHeaders. As in Go, the method leaves out empty values.
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

    // Port of util.BuildUploadHeaders.
    private static List<KeyValuePair<string, string>> DownloadHeaders(string metadata) =>
    [
        new("x-goog-download-metadata", metadata),
        new("sec-ch-ua", GmConstants.SecUA),
        new("sec-ch-ua-mobile", GmConstants.SecUAMobile),
        new("user-agent", GmConstants.UserAgent),
        .. BrowserHeaders(),
    ];

    // Both Go header sets end with these browser headers.
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

    // The finalize answer is base64 when it looks like base64, otherwise raw bytes. Bad padding fails cleanly.
    private static byte[] DecodeUploadAnswer(byte[] body)
    {
        if (!IsStandardBase64(body))
        {
            return body;
        }

        try
        {
            return Convert.FromBase64String(Encoding.ASCII.GetString(body));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The upload answer is not valid base64.", ex);
        }
    }

    // Port of isStandardBase64: full groups of 4, with only base64 characters.
    private static bool IsStandardBase64(byte[] data) =>
        data.Length % 4 == 0 && data.All(c => c is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'+' or (byte)'/' or (byte)'=');

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

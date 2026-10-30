using System.Net;
using System.Text;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Session;
using GMessages.Protocol.Tests.Fakes;
using GMessages.Protocol.Tests.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace GMessages.Protocol.Tests;

/// <summary>
/// Checks the media requests on <see cref="GmClient"/>: upload start and finalize, and download.
/// </summary>
/// <remarks>
/// C#-only. The expected headers and bodies were read from <c>StartUploadMedia</c>, <c>FinalizeUploadMedia</c> and
/// <c>DownloadMedia</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/media.go">media.go</see>
/// and <c>NewMediaUploadHeaders</c> and <c>BuildUploadHeaders</c> in util/func.go. The account has cookies, so a
/// missing cookie header proves media requests never carry them.
/// </remarks>
[TestClass]
public class GmClientMediaTests
{
    private const string UploadUrl = "https://upload.example/session/1";

    private static readonly string[] NeverSent = ["x-goog-api-key", "x-user-agent", "cookie", "authorization"];

    private sealed class Rig : IAsyncDisposable
    {
        public required GmClient Client { get; init; }

        public required FakeHttpHandler Handler { get; init; }

        public required string SessionPath { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            File.Delete(SessionPath);
        }
    }

    private static Rig CreateRig(Func<int, RecordedRequest, HttpResponseMessage> respond)
    {
        AuthData auth = AuthDataGoldenTests.BuildGolden();
        var handler   = new FakeHttpHandler { Respond = respond };
        string path   = Path.Combine(Path.GetTempPath(), "gmclient-media-test-" + Guid.NewGuid().ToString("N") + ".bin");
        var client    = GmClient.Create(auth, new SessionStore(path), GmEndpoints.Production, TimeProvider.System, NullLoggerFactory.Instance, handler);
        return new Rig { Client = client, Handler = handler, SessionPath = path };
    }

    // The Start Answer: Upload URL And Granularity, Unless Left Out
    private static HttpResponseMessage StartAnswer(bool withGranularity = true)
    {
        HttpResponseMessage res = FakeHttpHandler.Response(HttpStatusCode.OK);
        res.Headers.TryAddWithoutValidation("x-goog-upload-url", UploadUrl);
        if (withGranularity)
        {
            res.Headers.TryAddWithoutValidation("x-goog-upload-chunk-granularity", "262144");
        }

        return res;
    }

    private static void AssertNoCredentials(RecordedRequest request)
    {
        foreach (string name in NeverSent)
        {
            Assert.IsFalse(request.Headers.ContainsKey(name), $"{name} was sent to {request.Url}");
        }
    }

    /// <summary>
    /// Upload starts with LibGM's headers and a base64 start request, finalizes the encrypted file at the given URL
    /// with a base64 answer, and never sends the api key, x-user-agent or cookies.
    /// </summary>
    [TestMethod]
    public async Task Upload_SendsLibGmHeaders_NoCookies()
    {
        byte[] plain  = Encoding.UTF8.GetBytes("a photo, pretend");
        var answer    = new UploadMediaResponse { Media = new UploadedMedia { MediaID = "media-1", MediaNumber = 7 } };
        byte[] base64 = Encoding.ASCII.GetBytes(Convert.ToBase64String(answer.ToByteArray()));
        await using Rig rig = CreateRig((i, _) => i == 0 ? StartAnswer() : FakeHttpHandler.Response(HttpStatusCode.OK, "text/plain", base64));
        var progress = new List<double>();

        MediaContent media = await rig.Client.UploadMediaAsync(plain, "photo.png", "image/png", new SyncProgress(progress.Add), CancellationToken.None);

        // Start
        RecordedRequest start = rig.Handler.Requests[0];
        RecordedRequest final = rig.Handler.Requests[1];
        string size           = final.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(HttpMethod.Post, start.Method);
        Assert.AreEqual("https://instantmessaging-pa.googleapis.com/upload", start.Url);
        Assert.AreEqual("resumable", start.Headers["x-goog-upload-protocol"]);
        Assert.AreEqual("start", start.Headers["x-goog-upload-command"]);
        Assert.AreEqual(size, start.Headers["x-goog-upload-header-content-length"]);
        Assert.AreEqual("image/png", start.Headers["x-goog-upload-header-content-type"]);
        Assert.AreEqual("application/x-www-form-urlencoded;charset=UTF-8", start.Headers["content-type"]);
        Assert.AreEqual(GmConstants.UserAgent, start.Headers["user-agent"]);
        Assert.AreEqual("https://messages.google.com", start.Headers["origin"]);
        Assert.IsFalse(start.Headers.ContainsKey("x-goog-upload-offset"));
        var start_request = StartMediaUploadRequest.Parser.ParseFrom(Convert.FromBase64String(Encoding.ASCII.GetString(start.Body)));
        Assert.AreEqual(1, start_request.AttachmentType);
        Assert.AreEqual("fake-tachyon", start_request.AuthData.TachyonAuthToken.ToStringUtf8());
        Assert.AreEqual(GmConstants.ConfigVersion, start_request.AuthData.ConfigVersion);
        Assert.AreEqual("golden-src", start_request.Mobile.SourceID);
        AssertNoCredentials(start);

        // Finalize
        Assert.AreEqual(UploadUrl, final.Url);
        Assert.AreEqual("upload, finalize", final.Headers["x-goog-upload-command"]);
        Assert.AreEqual("0", final.Headers["x-goog-upload-offset"]);
        Assert.IsFalse(final.Headers.ContainsKey("x-goog-upload-protocol"));
        Assert.AreEqual(size, final.Headers["x-goog-upload-header-content-length"]);
        AssertNoCredentials(final);

        // Result: The Key Opens What Was Uploaded
        Assert.AreEqual("media-1", media.MediaID);
        Assert.AreEqual(MediaFormats.ImagePng, media.Format);
        Assert.AreEqual("photo.png", media.MediaName);
        Assert.AreEqual(plain.Length, media.Size);
        Assert.AreEqual("image/png", media.MimeType);
        CollectionAssert.AreEqual(plain, MediaCrypto.Decrypt(final.Body, media.DecryptionKey.Span));
        Assert.AreEqual(1.0, progress[^1]);
    }

    /// <summary>
    /// A raw (not base64) finalize answer is read too, as Go does.
    /// </summary>
    [TestMethod]
    public async Task Upload_RawAnswer_IsRead()
    {
        var answer = new UploadMediaResponse { Media = new UploadedMedia { MediaID = "media-raw!" } };
        await using Rig rig = CreateRig((i, _) => i == 0 ? StartAnswer() : FakeHttpHandler.Response(HttpStatusCode.OK, "application/x-protobuf", answer.ToByteArray()));

        MediaContent media = await rig.Client.UploadMediaAsync(new byte[10], "f.bin", "application/x-unknown", null, CancellationToken.None);

        Assert.AreEqual("media-raw!", media.MediaID);
        Assert.AreEqual(MediaFormats.AppUnspecified, media.Format);
    }

    /// <summary>
    /// A start answer without the chunk granularity fails the upload before anything is finalized.
    /// </summary>
    [TestMethod]
    public async Task Upload_MissingGranularity_Throws()
    {
        await using Rig rig = CreateRig((_, _) => StartAnswer(withGranularity: false));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => rig.Client.UploadMediaAsync(new byte[10], "f.png", "image/png", null, CancellationToken.None));
        Assert.HasCount(1, rig.Handler.Requests);
    }

    /// <summary>
    /// Upload start needs exactly HTTP 200, as Go's does: another 2xx fails before anything is finalized.
    /// </summary>
    /// <param name="status">The start answer's status.</param>
    [TestMethod]
    [DataRow(201)]
    [DataRow(204)]
    public async Task Upload_StartNot200_Throws(int status)
    {
        await using Rig rig = CreateRig((_, _) =>
        {
            HttpResponseMessage res = StartAnswer();
            res.StatusCode          = (HttpStatusCode)status;
            return res;
        });

        GmRequestException refused = await Assert.ThrowsExactlyAsync<GmRequestException>(() => rig.Client.UploadMediaAsync(new byte[10], "f.png", "image/png", null, CancellationToken.None));
        Assert.AreEqual(status, refused.StatusCode);
        Assert.HasCount(1, rig.Handler.Requests);
    }

    /// <summary>
    /// A finalize answer that looks like base64 but has bad padding fails as invalid data, not a format error.
    /// </summary>
    [TestMethod]
    public async Task Upload_BadBase64Padding_ThrowsInvalidData()
    {
        await using Rig rig = CreateRig((i, _) => i == 0 ? StartAnswer() : FakeHttpHandler.Response(HttpStatusCode.OK, "text/plain", "A==="u8.ToArray()));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => rig.Client.UploadMediaAsync(new byte[10], "f.png", "image/png", null, CancellationToken.None));
    }

    /// <summary>
    /// A download whose declared length is over the cap fails before the body is read.
    /// </summary>
    [TestMethod]
    public async Task Download_DeclaredOverCap_Throws()
    {
        await using Rig rig = CreateRig((_, _) =>
        {
            HttpResponseMessage res          = FakeHttpHandler.Response(HttpStatusCode.OK, "application/octet-stream", new byte[16]);
            res.Content.Headers.ContentLength = GmHttpClient.MaxMediaBodyBytes + 1;
            return res;
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => rig.Client.DownloadMediaAsync("media-1", MediaCrypto.GenerateKey(), CancellationToken.None));
    }

    /// <summary>
    /// A streamed download with no declared length fails once it crosses the cap.
    /// </summary>
    [TestMethod]
    public async Task Download_StreamedOverCap_Throws()
    {
        await using Rig rig = CreateRig((_, _) =>
        {
            HttpResponseMessage res = FakeHttpHandler.Response(HttpStatusCode.OK);
            res.Content             = new StreamContent(new ZeroStream(GmHttpClient.MaxMediaBodyBytes + 1));
            return res;
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => rig.Client.DownloadMediaAsync("media-1", MediaCrypto.GenerateKey(), CancellationToken.None));
    }

    /// <summary>
    /// A refused download throws with its status instead of decrypting an error page.
    /// </summary>
    [TestMethod]
    public async Task Download_Non200_Throws()
    {
        await using Rig rig = CreateRig((_, _) => FakeHttpHandler.Response(HttpStatusCode.NotFound, "text/plain", "gone"u8.ToArray()));

        GmRequestException refused = await Assert.ThrowsExactlyAsync<GmRequestException>(() => rig.Client.DownloadMediaAsync("media-1", MediaCrypto.GenerateKey(), CancellationToken.None));
        Assert.AreEqual(404, refused.StatusCode);
    }

    /// <summary>
    /// A download sends the metadata header (the attachment ID, encrypted, with auth data) and LibGM's headers only,
    /// and returns the decrypted body.
    /// </summary>
    [TestMethod]
    public async Task Download_DecryptsBody()
    {
        byte[] key   = MediaCrypto.GenerateKey();
        byte[] plain = new byte[100000];
        Random.Shared.NextBytes(plain);
        byte[] encrypted = MediaCrypto.Encrypt(plain, key);
        await using Rig rig = CreateRig((_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/octet-stream", encrypted));

        byte[] result = await rig.Client.DownloadMediaAsync("media-9", key, CancellationToken.None);

        CollectionAssert.AreEqual(plain, result);
        RecordedRequest get = rig.Handler.Requests[0];
        Assert.AreEqual(HttpMethod.Get, get.Method);
        Assert.AreEqual("https://instantmessaging-pa.googleapis.com/upload", get.Url);
        Assert.AreEqual("*/*", get.Headers["accept"]);
        var metadata = DownloadAttachmentRequest.Parser.ParseFrom(Convert.FromBase64String(get.Headers["x-goog-download-metadata"]));
        Assert.AreEqual("media-9", metadata.Info.AttachmentID);
        Assert.IsTrue(metadata.Info.Encrypted);
        Assert.AreEqual("fake-tachyon", metadata.AuthData.TachyonAuthToken.ToStringUtf8());
        AssertNoCredentials(get);
    }

    /// <summary>
    /// The mime table falls back to the part before the slash, and the reverse table is fixed where Go's is random.
    /// </summary>
    [TestMethod]
    public void MediaFormatsMap_FollowsLibGm()
    {
        Assert.AreEqual(MediaFormats.ImageJpeg, MediaFormatsMap.FromMime("image/jpeg"));
        Assert.AreEqual(MediaFormats.ImageUnspecified, MediaFormatsMap.FromMime("image/heic"));
        Assert.AreEqual(MediaFormats.AppTxt, MediaFormatsMap.FromMime("text/markdown"));
        Assert.AreEqual(MediaFormats.UnspecifiedType, MediaFormatsMap.FromMime("model/gltf"));

        Assert.AreEqual("audio/mp4", MediaFormatsMap.ToMime(MediaFormats.AudioMp4));
        Assert.AreEqual("image/bmp", MediaFormatsMap.ToMime(MediaFormats.ImageXMsBmp));
        Assert.AreEqual("text/plain", MediaFormatsMap.ToMime(MediaFormats.AppTxt));
        Assert.AreEqual("text/calendar", MediaFormatsMap.ToMime(MediaFormats.CalApplicationIcs));
        Assert.IsNull(MediaFormatsMap.ToMime(MediaFormats.ImageUnspecified));
    }

    // Progress<T> Posts To The Thread Pool; This One Records In Order, On The Spot
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    // A Non-Seekable Stream Of Zeros With No Declared Length
    private sealed class ZeroStream(long length) : Stream
    {
        private long _left = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, read);
            _left -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

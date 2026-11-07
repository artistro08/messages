using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using GMessages.Protocol.Http;
using GMessages.Protocol.PbLite;
using GMessages.Protocol.Proto;
using Google.Protobuf;

namespace GMessages.App.UITests.FakeGoogle;

/// <summary>
/// A fake Google Messages server on 127.0.0.1, so the real client can sign in, pair and stay connected with no
/// real Google and no real phone.
/// </summary>
/// <remarks>
/// The server serves the paths of <see cref="GmEndpoints"/> when all 3 bases are <see cref="BaseUrl"/>.
/// Protocol answers are PBLite (<c>application/json+protobuf</c>), as Google sends them.
/// <c>/web/authentication</c> is a simulated login page. It sets no cookies until its "Sign in" button
/// (<c>id="signin"</c>) posts to <c>/web/authentication/login</c>.
/// <para>
/// The long poll answers at once with <c>[[</c> and a startup ack. The ack counts the catch-up messages of the phone,
/// which come first. Pushed payloads follow, with a heartbeat every 10 seconds.
/// The long poll stays open until the client leaves or <see cref="KillLongPoll"/> ends it.
/// <c>SendMessage</c> requests go to <see cref="Phone"/>, and its answers come back on the long poll.
/// </para>
/// <para>
/// <c>/upload</c> serves media as Google does. A <c>start</c> POST answers with an upload URL and a granularity.
/// An <c>upload, finalize</c> POST to that URL stores the encrypted file in <see cref="FakePhone.Chats"/>
/// and answers with a base64 <c>UploadMediaResponse</c>. A GET answers with the stored bytes of the attachment ID in
/// <c>x-goog-download-metadata</c>, or with 404 when no bytes exist.
/// A finalize to a session that never started, or at an offset other than 0, gets 400.
/// Any <c>/upload</c> request that carries <c>cookie</c> or <c>x-goog-api-key</c> also gets 400.
/// </para>
/// The server serves each request on its own task. It binds 127.0.0.1 only and never contacts anything.
/// </remarks>
public sealed class FakeGoogleServer : IAsyncDisposable
{
    /// <summary>The interval between heartbeats on an idle long poll.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the eight cookies the login sets, by name, with fake values.
    /// </summary>
    public static FrozenDictionary<string, string> SignInCookies { get; } = new Dictionary<string, string>
    {
        ["SAPISID"]          = "fake-sapisid",
        ["SID"]              = "fake-sid",
        ["HSID"]             = "fake-hsid",
        ["SSID"]             = "fake-ssid",
        ["APISID"]           = "fake-apisid",
        ["OSID"]             = "fake-osid",
        ["__Secure-1PSIDTS"] = "fake-1psidts",
        ["__Secure-1PSID"]   = "fake-1psid",
    }.ToFrozenDictionary();

    private const string PbLiteMime = "application/json+protobuf";

    // Chromium refuses these ports (net/base/port_util.cc). WebView2 can never load a page on them.
    private static readonly FrozenSet<int> ChromiumUnsafePorts = new[] { 6000, 6566, 6665, 6666, 6667, 6668, 6669, 6697, 10080 }.ToFrozenSet();

    private const string SignInPage = """
        <!doctype html>
        <html>
            <head>
                <title>Sign in</title>
            </head>
            <body>
                <form method="post" action="/web/authentication/login">
                    <button id="signin" type="submit">
                        Sign in
                    </button>
                </form>
            </body>
        </html>
        """;

    private const string SignedInPage = """
        <!doctype html>
        <html>
            <head>
                <title>Signed in</title>
            </head>
            <body>
                <p>
                    Signed in
                </p>
            </body>
        </html>
        """;

    /// <summary>
    /// Gets or sets the GIF served at <c>/web-gif.gif</c>, as Tenor serves the GIFs of the emoji panel.
    /// Until a test sets it, the GIF is 1 x 1 pixel.
    /// </summary>
    public byte[] WebGif { get; set; } = Convert.FromHexString("4749463839610100010080000000FFFFFF00000021F90400000000002C00000000010001000002024401003B");

    /// <summary>The <c>og:title</c> of the page at <c>/preview-page</c>, which a link preview card shows.</summary>
    public const string PreviewPageTitle = "Fake Preview Page";

    private const string PreviewPage = $"""
        <!doctype html>
        <html>
            <head>
                <title>Fallback Title</title>
                <meta property="og:title" content="{PreviewPageTitle}">
                <meta property="og:site_name" content="Fake Site">
            </head>
            <body>
                <p>
                    Preview
                </p>
            </body>
        </html>
        """;

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<string> _paths = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private readonly ConcurrentDictionary<string, byte> _uploadSessions = new();
    private readonly Lock _lock = new();
    private readonly List<byte[]> _pending = [];
    private readonly List<byte[]> _catchUp = [];
    private readonly string _configDeviceId = Guid.NewGuid().ToString();
    private readonly string _phoneRegId     = Guid.NewGuid().ToString();
    private Channel<byte[]>? _stream;
    private int _killStatus;
    private volatile bool _rejectCookies;
    private volatile bool _slowStart;
    private volatile bool _failMediaDownloads;
    private readonly ConcurrentQueue<string> _downloads = new();
    private long _downloadDelayTicks;

    /// <summary>
    /// Starts the server on a free port of 127.0.0.1.
    /// </summary>
    /// <exception cref="HttpListenerException">No free port binds after 5 tries.</exception>
    public FakeGoogleServer()
    {
        Phone = new FakePhone(
            m => Push(new LongPollingPayload { Data = m }),
            m =>
            {
                lock (_lock)
                {
                    _catchUp.Add(PbLiteSerializer.Serialize(new LongPollingPayload { Data = m }));
                }
            });

        // Bind port 0, release it, then listen on it. Retry when another process takes the port in between.
        for (int attempt = 1; ; attempt++)
        {
            int port      = FreePort();
            var listener  = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                BaseUrl   = new Uri($"http://127.0.0.1:{port}/");
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }

        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>
    /// Gets the base address of the server, for example <c>http://127.0.0.1:50123/</c>.
    /// </summary>
    public Uri BaseUrl { get; }

    /// <summary>
    /// Gets the fake phone the account is paired with.
    /// </summary>
    public FakePhone Phone { get; }

    /// <summary>
    /// Gets or sets whether the sign-in refuses the cookies with a 401.
    /// </summary>
    public bool RejectCookies
    {
        get => _rejectCookies;
        set => _rejectCookies = value;
    }

    /// <summary>
    /// Gets or sets whether the start-up of the app is slow. The server issues tokens with 30 minutes left, so every
    /// start refreshes first, and the server answers each refresh 3 s late.
    /// </summary>
    /// <remarks>
    /// Start-up never fetches <c>/web/config</c>, because only pairing does. The token refresh is the request that a
    /// start waits for. Set this property before pairing, so the saved session already needs a refresh.
    /// </remarks>
    public bool SlowStart
    {
        get => _slowStart;
        set => _slowStart = value;
    }

    /// <summary>
    /// Gets or sets whether media downloads get a 500 answer, as a failing download.
    /// </summary>
    public bool FailMediaDownloads
    {
        get => _failMediaDownloads;
        set => _failMediaDownloads = value;
    }

    /// <summary>
    /// Gets or sets how long the finalize of an upload waits before it answers, so a test can see the upload bar.
    /// </summary>
    public TimeSpan UploadDelay { get; set; }

    /// <summary>
    /// Gets or sets how long a media download waits before it answers, as a stalled download. Zero answers at once.
    /// </summary>
    public TimeSpan MediaDownloadDelay
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _downloadDelayTicks));
        set => Interlocked.Exchange(ref _downloadDelayTicks, value.Ticks);
    }

    /// <summary>
    /// Gets the attachment ID of every media download so far, in arrival order.
    /// </summary>
    public IReadOnlyList<string> DownloadedMediaIds => [.. _downloads];

    /// <summary>
    /// Gets every request path so far, in arrival order.
    /// </summary>
    public IReadOnlyList<string> RequestedPaths => [.. _paths];

    /// <summary>
    /// Gets every unexpected server error so far: failed accepts and requests that got a 500 answer.
    /// </summary>
    public IReadOnlyList<Exception> Errors => [.. _errors];

    /// <summary>
    /// Queues a payload for the open long poll, or for the next one when none is open.
    /// </summary>
    /// <param name="payload">The payload.</param>
    public void Push(LongPollingPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        byte[] bytes = PbLiteSerializer.Serialize(payload);
        lock (_lock)
        {
            // ponytail: a payload written to a stream that the client just dropped is lost, as with real Google.
            // This is fine for tests.
            if (_stream is null || !_stream.Writer.TryWrite(bytes))
            {
                _pending.Add(bytes);
            }
        }
    }

    /// <summary>
    /// Ends the open long poll. Later long polls get <paramref name="status"/> instead of a stream.
    /// </summary>
    /// <param name="status">
    /// The HTTP status for later long polls, such as 401.
    /// A value of 0 only ends the stream and lets the client reconnect.
    /// </param>
    public void KillLongPoll(int status)
    {
        lock (_lock)
        {
            _killStatus = status;
            _stream?.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Stops the server, ends every open request and waits for the request tasks to finish.
    /// </summary>
    /// <returns>A task that completes when the server has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync();
        _listener.Close();
        await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Task.WhenAll(_handlers.Keys).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    // =========================================================================
    // ROUTING
    // =========================================================================

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                // The server is stopping or disposed.
                return;
            }
            catch (Exception ex)
            {
                // One bad accept must not kill the server. Record the error so a test can see it, and keep listening.
                _errors.Enqueue(ex);
                continue;
            }

            Task handler = Task.Run(() => HandleAsync(context));
            _handlers[handler] = 0;
            _ = handler.ContinueWith(t => _handlers.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    // A failure answers 500 with the error, so a test sees the error instead of a hang.
    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        string path                 = request.Url!.AbsolutePath;
        _paths.Enqueue(path);

        try
        {
            switch (path)
            {
                case "/web/authentication" when request.HttpMethod == "GET":
                    await RespondAsync(context.Response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(SignInPage));
                    break;

                case "/web/authentication/login" when request.HttpMethod == "POST":
                    foreach ((string name, string value) in SignInCookies)
                    {
                        // A __Secure- cookie must carry Secure, or browsers drop it.
                        string secure = name.StartsWith("__Secure-", StringComparison.Ordinal) ? "; Secure" : "";
                        context.Response.Headers.Add("Set-Cookie", $"{name}={value}; Path=/{secure}");
                    }

                    await RespondAsync(context.Response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(SignedInPage));
                    break;

                case "/preview-page" when request.HttpMethod == "GET":
                    await RespondAsync(context.Response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PreviewPage));
                    break;

                case "/web-gif.gif" when request.HttpMethod == "GET":
                    await RespondAsync(context.Response, 200, "image/gif", WebGif);
                    break;

                case "/web/config":
                    await RespondAsync(context.Response, 200, PbLiteMime, PbLiteSerializer.Serialize(Config()));
                    break;

                case not null when path.EndsWith("Registration/SignInGaia", StringComparison.Ordinal):
                    await (RejectCookies
                        ? RespondAsync(context.Response, 401, PbLiteMime, "[]"u8.ToArray())
                        : RespondAsync(context.Response, 200, PbLiteMime, PbLiteSerializer.Serialize(SignIn())));
                    break;

                case not null when path.EndsWith("Registration/RegisterRefresh", StringComparison.Ordinal):
                    if (SlowStart)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token);
                    }

                    await RespondAsync(context.Response, 200, PbLiteMime, PbLiteSerializer.Serialize(new RegisterRefreshResponse { TokenData = Token() }));
                    break;

                case not null when path.EndsWith("Messaging/ReceiveMessages", StringComparison.Ordinal):
                    await LongPollAsync(context.Response);
                    break;

                case not null when path.EndsWith("Messaging/SendMessage", StringComparison.Ordinal):
                    Phone.Handle(PbLiteSerializer.Deserialize<OutgoingRPCMessage>(await ReadBodyAsync(request)));
                    await RespondAsync(context.Response, 200, PbLiteMime, "[]"u8.ToArray());
                    break;

                // Media requests never carry cookies or the API key.
                case not null when path.StartsWith("/upload", StringComparison.Ordinal) && (request.Headers["cookie"] is not null || request.Headers["x-goog-api-key"] is not null):
                    await RespondAsync(context.Response, 400, "text/plain", "Media requests carry no credentials"u8.ToArray());
                    break;

                // The next 3 cases are the media upload start, the upload finalize and the download.
                case "/upload" when request.HttpMethod == "POST" && request.Headers["x-goog-upload-command"] == "start":
                    string session = Guid.NewGuid().ToString("N");
                    _uploadSessions[session] = 0;
                    context.Response.Headers.Add("x-goog-upload-url", new Uri(BaseUrl, "/upload/session/" + session).AbsoluteUri);
                    context.Response.Headers.Add("x-goog-upload-chunk-granularity", "262144");
                    context.Response.Headers.Add("x-goog-upload-status", "active");
                    await RespondAsync(context.Response, 200, "text/plain", []);
                    break;

                case not null when path.StartsWith("/upload/session/", StringComparison.Ordinal) && request.HttpMethod == "POST" && request.Headers["x-goog-upload-command"] == "upload, finalize":
                    if (!_uploadSessions.TryRemove(path["/upload/session/".Length..], out _) || request.Headers["x-goog-upload-offset"] != "0")
                    {
                        await RespondAsync(context.Response, 400, "text/plain", "Unknown upload session or offset"u8.ToArray());
                        break;
                    }

                    string mediaId = Phone.Chats.StoreUpload(await ReadBodyAsync(request));
                    await Task.Delay(UploadDelay, _stop.Token);
                    var uploaded    = new UploadMediaResponse { Media = new UploadedMedia { MediaID = mediaId, MediaNumber = 1 } };
                    await RespondAsync(context.Response, 200, "text/plain", Encoding.ASCII.GetBytes(Convert.ToBase64String(uploaded.ToByteArray())));
                    break;

                case "/upload" when request.HttpMethod == "GET":
                    var download  = DownloadAttachmentRequest.Parser.ParseFrom(Convert.FromBase64String(request.Headers["x-goog-download-metadata"] ?? ""));
                    byte[]? media = Phone.Chats.MediaBytes(download.Info?.AttachmentID ?? "");
                    _downloads.Enqueue(download.Info?.AttachmentID ?? "");
                    if (MediaDownloadDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(MediaDownloadDelay, _stop.Token);
                    }

                    await (_failMediaDownloads
                        ? RespondAsync(context.Response, 500, "text/plain", "Download failed"u8.ToArray())
                        : media is null
                        ? RespondAsync(context.Response, 404, "text/plain", "No such media"u8.ToArray())
                        : RespondAsync(context.Response, 200, "application/octet-stream", media));
                    break;

                case not null when path.EndsWith("Messaging/AckMessages", StringComparison.Ordinal):
                    await RespondAsync(context.Response, 200, PbLiteMime, "[]"u8.ToArray());
                    break;

                default:
                    await RespondAsync(context.Response, 404, "text/plain", "Not found"u8.ToArray());
                    break;
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            _errors.Enqueue(ex);
            try
            {
                await RespondAsync(context.Response, 500, "text/plain", Encoding.UTF8.GetBytes(ex.ToString()));
            }
            catch (Exception)
            {
                // The client is gone, or the answer already started.
            }
        }
        catch (Exception)
        {
            // The server is stopping.
        }
    }

    // =========================================================================
    // LONG POLL
    // =========================================================================

    // The answer is "[[" and a startup ack at once. Payloads follow as they come, with a heartbeat when idle.
    // A killed long poll ends with "]]".
    private async Task LongPollAsync(HttpListenerResponse response)
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        int kill;
        int catchUpCount = 0;
        lock (_lock)
        {
            kill = _killStatus;
            if (kill <= 0)
            {
                // The newest long poll replaces the old one. It gets the unread payloads of the old one first, then everything
                // queued while no long poll was open.
                if (_stream is { } old)
                {
                    old.Writer.TryComplete();
                    while (old.Reader.TryRead(out byte[]? unread))
                    {
                        channel.Writer.TryWrite(unread);
                    }
                }

                _stream = channel;

                // Catch-up messages wait for the poll of a paired client and go first. The startup ack counts them.
                if (Phone.RequestCrypto is not null)
                {
                    foreach (byte[] catchUp in _catchUp)
                    {
                        channel.Writer.TryWrite(catchUp);
                    }

                    catchUpCount = _catchUp.Count;
                    _catchUp.Clear();
                }

                foreach (byte[] queued in _pending)
                {
                    channel.Writer.TryWrite(queued);
                }

                _pending.Clear();
            }
        }

        if (kill > 0)
        {
            await RespondAsync(response, kill, PbLiteMime, "[]"u8.ToArray());
            return;
        }

        response.StatusCode  = 200;
        response.ContentType = PbLiteMime;
        response.SendChunked = true;
        Stream body          = response.OutputStream;
        try
        {
            await WriteAsync(body, "[["u8.ToArray(), PbLiteSerializer.Serialize(new LongPollingPayload { Ack = new StartAckMessage { Count = catchUpCount } }));
            byte[] heartbeat = PbLiteSerializer.Serialize(new LongPollingPayload { Heartbeat = new EmptyArr() });
            while (true)
            {
                // Canceling WaitToReadAsync consumes no payload, so the heartbeat timeout loses nothing.
                bool more;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    idle.CancelAfter(HeartbeatInterval);
                    try
                    {
                        more = await channel.Reader.WaitToReadAsync(idle.Token);
                    }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                    {
                        await WriteAsync(body, ","u8.ToArray(), heartbeat);
                        continue;
                    }
                }

                if (!more)
                {
                    await WriteAsync(body, "]]"u8.ToArray());
                    break;
                }

                while (channel.Reader.TryRead(out byte[]? payload))
                {
                    await WriteAsync(body, ","u8.ToArray(), payload);
                }
            }
        }
        catch (Exception)
        {
            // The client left, or the server is stopping.
        }
        finally
        {
            lock (_lock)
            {
                if (_stream == channel)
                {
                    _stream = null;
                }

                // Unsent payloads go to the live long poll, or wait for the next one.
                channel.Writer.TryComplete();
                while (channel.Reader.TryRead(out byte[]? left))
                {
                    if (_stream is null || !_stream.Writer.TryWrite(left))
                    {
                        _pending.Add(left);
                    }
                }
            }

            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // The client is already gone.
            }
        }
    }

    private static async Task WriteAsync(Stream body, params byte[][] parts)
    {
        foreach (byte[] part in parts)
        {
            await body.WriteAsync(part);
        }

        await body.FlushAsync();
    }

    // =========================================================================
    // ANSWERS
    // =========================================================================

    // The device ID in the web config becomes the session ID of the client.
    private Config Config() => new()
    {
        ClientVersion = $"{GmConstants.ConfigVersion.Year:D4}{GmConstants.ConfigVersion.Month:D2}{GmConstants.ConfigVersion.Day:D2}",
        DeviceInfo    = new Config.Types.DeviceInfo { DeviceID = _configDeviceId },
    };

    // The sign-in answer holds a token, the browser device and one phone seen just now.
    private SignInGaiaResponse SignIn()
    {
        var deviceData = new SignInGaiaResponse.Types.DeviceData
        {
            DeviceWrapper = new SignInGaiaResponse.Types.DeviceData.Types.DeviceWrapper
            {
                Device = new Device { UserID = 1, SourceID = "Fake-Browser-Source", Network = GmConstants.GoogleNetwork },
            },
        };
        deviceData.UnknownItems2.Add(new RPCGaiaData.Types.UnknownContainer.Types.Item2.Types.Item1 { DestOrSourceUUID = _phoneRegId, UnknownInt4 = 1, UnknownBigInt7 = 777 });
        deviceData.UnknownItems3.Add(new RPCGaiaData.Types.UnknownContainer.Types.Item4
        {
            DestOrSourceUUID             = _phoneRegId,
            UnknownTimestampMicroseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            Item8                        = new RPCGaiaData.Types.UnknownContainer.Types.Item4.Types.Item8 { UnknownTimestamp = 5551 },
        });

        return new SignInGaiaResponse { TokenData = Token(), DeviceData = deviceData };
    }

    // The token is valid for 24 hours. For a slow start it is valid for 30 minutes, which is inside the 1 hour
    // refresh window of the client.
    private TokenData Token() => new()
    {
        TachyonAuthToken = ByteString.CopyFromUtf8("fake-tachyon-token"),
        TTL              = (long)(SlowStart ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(24)).TotalMicroseconds,
    };

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static async Task RespondAsync(HttpListenerResponse response, int status, string contentType, byte[] body)
    {
        response.StatusCode      = status;
        response.ContentType     = contentType;
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        using var buffer = new MemoryStream();
        await request.InputStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // Port 0 picks a free port. The loop skips the ports that Chromium refuses.
    private static int FreePort()
    {
        while (true)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            if (!ChromiumUnsafePorts.Contains(port))
            {
                return port;
            }
        }
    }
}

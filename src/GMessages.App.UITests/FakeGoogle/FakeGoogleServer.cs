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
/// Serves the paths of <see cref="GmEndpoints"/> when all three bases are <see cref="BaseUrl"/>. Protocol answers are
/// PBLite (<c>application/json+protobuf</c>), as Google sends them. <c>/web/authentication</c> is a simulated login
/// page: it sets no cookies until its "Sign in" button (<c>id="signin"</c>) posts to <c>/web/authentication/login</c>.
/// The long poll answers <c>[[</c> and a startup ack (counting the phone's catch-up messages, which come first) at once, then pushed payloads, with a heartbeat every 10 seconds,
/// and stays open until the client leaves or <see cref="KillLongPoll"/>. <c>SendMessage</c> requests go to
/// <see cref="Phone"/>, whose answers come back on the long poll. <c>/RotateCookies</c> (with the accounts base set to
/// <see cref="BaseUrl"/> too) sets a fresh <c>__Secure-1PSIDTS</c> on every call. Each request is served on its own task. Binds
/// 127.0.0.1 only and never contacts anything.
/// </remarks>
public sealed class FakeGoogleServer : IAsyncDisposable
{
    /// <summary>How often an idle long poll gets a heartbeat.</summary>
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

    // Ports Chromium Refuses (net/base/port_util.cc); WebView2 Could Never Load A Page On Them
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

    /// <summary>The <c>og:title</c> of the page at <c>/preview-page</c>, which a link preview card shows.</summary>
    public const string PreviewPageTitle = "Fake Preview Page";

    // A Linked Page For The Link Preview Card
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
    private readonly Lock _lock = new();
    private readonly List<byte[]> _pending = [];
    private readonly List<byte[]> _catchUp = [];
    private readonly string _configDeviceId = Guid.NewGuid().ToString();
    private readonly string _phoneRegId     = Guid.NewGuid().ToString();
    private Channel<byte[]>? _stream;
    private int _killStatus;
    private volatile bool _rejectCookies;
    private volatile bool _slowStart;
    private int _rotations;

    /// <summary>
    /// Starts the server on a free port of 127.0.0.1.
    /// </summary>
    /// <exception cref="HttpListenerException">No free port could be bound after several tries.</exception>
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

        // Free Port: Bind Port 0, Release It, Listen; Retry If Someone Took It Meanwhile
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
    /// Gets the server's base address, for example <c>http://127.0.0.1:50123/</c>.
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
    /// Gets or sets whether the app's start-up is slow: tokens are handed out with 30 minutes left, so every start
    /// refreshes first, and each refresh is answered 3 s late.
    /// </summary>
    /// <remarks>
    /// Start-up never fetches <c>/web/config</c> (only pairing does); the token refresh is the request a start waits
    /// on. Set it before pairing, so the saved session already needs a refresh.
    /// </remarks>
    public bool SlowStart
    {
        get => _slowStart;
        set => _slowStart = value;
    }

    /// <summary>
    /// Gets every request path so far, in arrival order.
    /// </summary>
    public IReadOnlyList<string> RequestedPaths => [.. _paths];

    /// <summary>
    /// Gets every unexpected server error so far: failed accepts and requests answered with 500.
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
            // ponytail: a payload written to a stream the client just dropped is lost, as with real Google; fine for tests
            if (_stream is null || !_stream.Writer.TryWrite(bytes))
            {
                _pending.Add(bytes);
            }
        }
    }

    /// <summary>
    /// Ends the open long poll; later long polls get <paramref name="status"/> instead of a stream.
    /// </summary>
    /// <param name="status">The HTTP status for later long polls, such as 401; 0 only ends the stream and lets the client reconnect.</param>
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
    /// <returns>A task that completes when everything has stopped.</returns>
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
                // Stopping Or Disposed
                return;
            }
            catch (Exception ex)
            {
                // One Bad Accept Must Not Kill The Server; Keep It Visible And Keep Listening
                _errors.Enqueue(ex);
                continue;
            }

            Task handler = Task.Run(() => HandleAsync(context));
            _handlers[handler] = 0;
            _ = handler.ContinueWith(t => _handlers.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    // One Request; A Failure Answers 500 With The Error, So A Test Sees It Instead Of Hanging
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
                        // A __Secure- Cookie Must Carry Secure Or Browsers Drop It
                        string secure = name.StartsWith("__Secure-", StringComparison.Ordinal) ? "; Secure" : "";
                        context.Response.Headers.Add("Set-Cookie", $"{name}={value}; Path=/{secure}");
                    }

                    await RespondAsync(context.Response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(SignedInPage));
                    break;

                case "/RotateCookies" when request.HttpMethod == "POST":
                    // Google's Cookie Rotation: A Fresh __Secure-1PSIDTS For All Of google.com Each Time
                    int rotation = Interlocked.Increment(ref _rotations);
                    context.Response.Headers.Add("Set-Cookie", $"__Secure-1PSIDTS=fake-1psidts-rotated-{rotation}; Domain=.google.com; Max-Age=31536000; Path=/; Secure; HttpOnly");
                    await RespondAsync(context.Response, 200, "application/json", "[]"u8.ToArray());
                    break;

                case "/preview-page" when request.HttpMethod == "GET":
                    await RespondAsync(context.Response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PreviewPage));
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
                // The Client Is Gone Or The Answer Was Already Started
            }
        }
        catch (Exception)
        {
            // Stopping
        }
    }

    // =========================================================================
    // LONG POLL
    // =========================================================================

    // "[[" Plus A Startup Ack At Once, Then Payloads As They Come, A Heartbeat When Idle, "]]" When Killed
    private async Task LongPollAsync(HttpListenerResponse response)
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        int kill;
        int catch_up_count = 0;
        lock (_lock)
        {
            kill = _killStatus;
            if (kill <= 0)
            {
                // The Newest Long Poll Takes Over: The Old One's Unread Payloads, Then Anything Queued While None Was Open
                if (_stream is { } old)
                {
                    old.Writer.TryComplete();
                    while (old.Reader.TryRead(out byte[]? unread))
                    {
                        channel.Writer.TryWrite(unread);
                    }
                }

                _stream = channel;

                // Catch-Up Messages Wait For A Paired Client's Poll, Go First, And Are Counted By The Startup Ack
                if (Phone.RequestCrypto is not null)
                {
                    foreach (byte[] catch_up in _catchUp)
                    {
                        channel.Writer.TryWrite(catch_up);
                    }

                    catch_up_count = _catchUp.Count;
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
            await WriteAsync(body, "[["u8.ToArray(), PbLiteSerializer.Serialize(new LongPollingPayload { Ack = new StartAckMessage { Count = catch_up_count } }));
            byte[] heartbeat = PbLiteSerializer.Serialize(new LongPollingPayload { Heartbeat = new EmptyArr() });
            while (true)
            {
                // Wait For A Payload; Canceling WaitToReadAsync Consumes Nothing
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
            // The Client Left Or The Server Is Stopping
        }
        finally
        {
            lock (_lock)
            {
                if (_stream == channel)
                {
                    _stream = null;
                }

                // Unsent Payloads Go To The Live Long Poll, Or Wait For The Next One
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
                // Already Gone
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

    // Web Config: The Device ID Becomes The Client's Session ID
    private Config Config() => new()
    {
        ClientVersion = $"{GmConstants.ConfigVersion.Year:D4}{GmConstants.ConfigVersion.Month:D2}{GmConstants.ConfigVersion.Day:D2}",
        DeviceInfo    = new Config.Types.DeviceInfo { DeviceID = _configDeviceId },
    };

    // Sign-In: A Token, The Browser Device, And One Phone Seen Just Now
    private SignInGaiaResponse SignIn()
    {
        var device_data = new SignInGaiaResponse.Types.DeviceData
        {
            DeviceWrapper = new SignInGaiaResponse.Types.DeviceData.Types.DeviceWrapper
            {
                Device = new Device { UserID = 1, SourceID = "Fake-Browser-Source", Network = GmConstants.GoogleNetwork },
            },
        };
        device_data.UnknownItems2.Add(new RPCGaiaData.Types.UnknownContainer.Types.Item2.Types.Item1 { DestOrSourceUUID = _phoneRegId, UnknownInt4 = 1, UnknownBigInt7 = 777 });
        device_data.UnknownItems3.Add(new RPCGaiaData.Types.UnknownContainer.Types.Item4
        {
            DestOrSourceUUID             = _phoneRegId,
            UnknownTimestampMicroseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            Item8                        = new RPCGaiaData.Types.UnknownContainer.Types.Item4.Types.Item8 { UnknownTimestamp = 5551 },
        });

        return new SignInGaiaResponse { TokenData = Token(), DeviceData = device_data };
    }

    // A Token Valid For 24 Hours, Or 30 Minutes (Inside The Client's 1 Hour Refresh Window) For A Slow Start
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

    // Port 0 Picks A Free Port; Chromium-Unsafe Ones Are Skipped
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

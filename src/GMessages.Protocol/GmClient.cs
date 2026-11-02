using System.Net;
using System.Text;
using GMessages.Protocol.Auth;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol;

/// <summary>
/// The single entry point for the app: pairs with the phone, connects, disconnects, unpairs, and keeps the saved
/// session up to date.
/// </summary>
/// <remarks>
/// Ported from <c>Connect</c>, <c>Reconnect</c>, <c>Disconnect</c>, <c>IsConnected</c>, <c>IsLoggedIn</c>,
/// <c>checkLoggedIn</c>, <c>startLongPolling</c> and <c>postConnect</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>,
/// <c>Unpair</c> in pair.go and <c>DoGaiaPairing</c> in pair_google.go. Connect refreshes the token first so bad
/// credentials show up right away, then starts the long poll; once it opens, the client waits 2 seconds (longer while
/// the startup skip count is above zero), flushes acks, waits 1 more second and asks the phone for updates
/// (<c>GET_UPDATES</c> with a new session ID). Go then checks whether Google Messages is the default SMS app; that
/// check is not ported, because nothing here uses its answer.
/// <para>
/// Connect, disconnect, pairing and unpairing run one at a time. The session is saved after every token refresh and
/// every cookie change: right away when the last save started more than 5 seconds ago, otherwise once at the end of
/// that 5 second window. Saves are paused while pairing, so a half-finished pairing never reaches the disk. A failed
/// save raises <see cref="SessionSaveFailed"/> so the app can warn the user. After
/// <see cref="UnpairAsync"/> or <see cref="DisposeAsync"/> the client is finished. Tokens, keys and cookie values are
/// never logged.
/// </para>
/// </remarks>
public sealed partial class GmClient : IMessagingClient, IAsyncDisposable
{
    /// <summary>The shortest time between two automatic session saves.</summary>
    public static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(5);

    // Post-Connect Waits (Go: postConnect's time.Sleep Calls)
    private static readonly TimeSpan PostConnectDelay  = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SkipCountDelay    = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BeforeUpdatesWait = TimeSpan.FromSeconds(1);

    private readonly AuthData _auth;
    private readonly SessionStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _longPollClient;
    private readonly HttpMessageHandler? _ownedHandler;
    private readonly TokenRefresher _refresher;
    private readonly UpdateDispatcher _dispatcher;
    private readonly LongPollLoop _loop;
    private readonly GaiaSignIn _signIn;
    private readonly GaiaPairing _pairing;
    private readonly GmHttpClient _http;
    private readonly GmEndpoints _endpoints;

    // One Connect, Disconnect, Pair Or Unpair At A Time
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _pairCts;
    private int _finished;
    private int _disposed;

    // Long Poll Run (Changed Under _gate; _runLock Also Guards _postConnect, Set From The Loop)
    private readonly Lock _runLock = new();
    private CancellationTokenSource? _runCts;
    private Task _run         = Task.CompletedTask;
    private Task _postConnect = Task.CompletedTask;

    // Session Saves: _saveLock Guards The Timer And Flags, _writeLock Keeps Writes (And The Delete) One At A Time
    private readonly Lock _saveLock  = new();
    private readonly Lock _writeLock = new();
    private ITimer? _saveTimer;
    private DateTimeOffset _lastSaveStart = DateTimeOffset.MinValue;
    private bool _savesStopped;
    private bool _savesSuspended;

    private GmClient(AuthData auth, SessionStore store, GmEndpoints endpoints, TimeProvider time, ILoggerFactory logs, HttpMessageHandler handler, bool ownsHandler)
    {
        _auth           = auth;
        _store          = store;
        _time           = time;
        _log            = logs.CreateLogger<GmClient>();
        _ownedHandler   = ownsHandler ? handler : null;
        _httpClient     = new HttpClient(handler, disposeHandler: false);
        _longPollClient = new HttpClient(handler, disposeHandler: false) { Timeout = GmHttpClient.LongPollTimeout };

        // Wire The Pieces
        var http    = new GmHttpClient(_httpClient, _longPollClient, auth.Cookies, time, logs.CreateLogger<GmHttpClient>());
        Rpc         = new RpcSession(http, auth, endpoints, time, logs.CreateLogger<RpcSession>());
        _refresher  = new TokenRefresher(http, auth, endpoints, time, logs.CreateLogger<TokenRefresher>());
        _dispatcher = new UpdateDispatcher(Rpc, logs.CreateLogger<UpdateDispatcher>());
        _loop       = new LongPollLoop(http, Rpc, auth, endpoints, _refresher, _dispatcher, time, logs.CreateLogger<LongPollLoop>());
        _signIn     = new GaiaSignIn(http, auth, endpoints, time, logs.CreateLogger<GaiaSignIn>());
        _pairing    = new GaiaPairing(_signIn, Rpc, _loop, auth, time, logs.CreateLogger<GaiaPairing>());
        _http       = http;
        _endpoints  = endpoints;

        // Requests Waiting For An Answer Fail Fast While No Long Poll Runs (Pairing's Own Poll Counts)
        Rpc.IsListening = () => _loop.IsRunning;

        // Events And Saves
        _loop.EventRaised      += Raise;
        _refresher.EventRaised += OnRefresherEvent;
        auth.Cookies.Changed   += OnCookiesChanged;
    }

    /// <summary>
    /// Raised for every event: messages, conversations, listen state, phone liveness, token refreshes and pairing.
    /// </summary>
    /// <remarks>
    /// Go's event handler. Handlers run on background threads and must not block for long. A handler that throws is
    /// logged and does not stop the client. Handlers must not block synchronously on GmClient methods: some events,
    /// such as <see cref="PairSuccessful"/>, are raised while the client's lifecycle lock is held.
    /// </remarks>
    public event Action<object>? EventRaised;

    /// <summary>
    /// Gets the RPC session, for sending requests to the phone.
    /// </summary>
    /// <remarks>Its session ID is empty until the first connect sends <c>GET_UPDATES</c>, as in Go.</remarks>
    public RpcSession Rpc { get; }

    /// <summary>
    /// Gets whether the account is paired and has its cookies.
    /// </summary>
    /// <remarks>Ported from <c>IsLoggedIn</c>: a browser device is set and <see cref="AuthData.HasCookies"/> holds.</remarks>
    public bool IsLoggedIn => _auth.Browser is not null && _auth.HasCookies;

    /// <summary>
    /// Gets whether the long poll is open right now.
    /// </summary>
    /// <remarks>Ported from <c>IsConnected</c>; briefly false while the long poll reconnects, as in Go.</remarks>
    public bool IsConnected => _loop.IsConnected;

    /// <summary>Runs inside each session write, before the file is written (test hook).</summary>
    internal Action? OnWritingForTests { get; set; }

    /// <summary>
    /// Creates a client for an account.
    /// </summary>
    /// <remarks>
    /// Without a <paramref name="handler"/>, a <see cref="SocketsHttpHandler"/> is created with cookies off (the
    /// account's <see cref="CookieJar"/> owns them) and gzip, deflate and Brotli decompression on (Go's transport asks
    /// for compression by itself). Two clients share it: a normal one, and one with a 30 minute timeout for the long
    /// poll. A given handler is used as is and not disposed.
    /// </remarks>
    /// <param name="auth">The account; a fresh <see cref="AuthData.CreateNew"/> with cookies set for pairing.</param>
    /// <param name="store">Where the session is saved.</param>
    /// <param name="endpoints">The server addresses.</param>
    /// <param name="time">The clock for every wait and the save window.</param>
    /// <param name="logs">Creates the loggers.</param>
    /// <param name="handler">The HTTP handler to send through, for tests; null for the real network.</param>
    /// <returns>The client; dispose it when done.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static GmClient Create(AuthData auth, SessionStore store, GmEndpoints endpoints, TimeProvider time, ILoggerFactory logs, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logs);

        if (handler is not null)
        {
            return new GmClient(auth, store, endpoints, time, logs, handler, ownsHandler: false);
        }

        var socketHandler = new SocketsHttpHandler
        {
            UseCookies             = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };

        return new GmClient(auth, store, endpoints, time, logs, socketHandler, ownsHandler: true);
    }

    // =========================================================================
    // CONNECTION
    // =========================================================================

    /// <summary>
    /// Refreshes the token if needed and starts listening for events.
    /// </summary>
    /// <remarks>
    /// Ported from <c>Connect</c>. The refresh happens here, not only in the long poll, so bad credentials fail the
    /// call. A refresh error that can be retried (network, 5xx) is logged and left to the long poll. Returns once the
    /// long poll is started; it opens and sends <c>GET_UPDATES</c> in the background. A call while the long poll is
    /// already running makes it reconnect right away (dropping any backoff wait), the way Go's <c>Connect</c> retries
    /// at once; there is still only one long poll. Call it when the network comes back or the PC wakes up.
    /// </remarks>
    /// <param name="ct">Cancels the wait for another operation and the token refresh. The long poll runs until <see cref="DisconnectAsync"/>.</param>
    /// <returns>A task that completes when the long poll is started.</returns>
    /// <exception cref="ObjectDisposedException">The client was unpaired or disposed.</exception>
    /// <exception cref="InvalidOperationException">Not paired: no token or no browser device.</exception>
    /// <exception cref="SessionExpiredException">The server refused the token refresh for good; pair again.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task ConnectAsync(CancellationToken ct)
    {
        ThrowIfFinished();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfFinished();
            ThrowIfNotLoggedIn();
            // Already Running: Reconnect Now Instead Of Waiting Out A Backoff (The Loop Refreshes The Token Itself)
            if (IsRunning)
            {
                _loop.RequestReconnect();
                return;
            }

            // Refresh The Token (Its Event Goes Out Here; No Long Poll Is Relaying Refresher Events Now)
            _refresher.EventRaised += Raise;
            try
            {
                await _refresher.RefreshIfNeededAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && TokenRefresher.IsFatal(ex))
            {
                throw new SessionExpiredException("Failed to refresh auth token.", ex);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogTransientRefreshError(_log, ex);
            }
            finally
            {
                _refresher.EventRaised -= Raise;
            }

            await StartLongPollingAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops listening and fails every request still waiting for the phone.
    /// </summary>
    /// <remarks>
    /// Ported from <c>Disconnect</c>: closes the long poll and fails the waiters, since their answers could only
    /// arrive over the long poll. Go also closes idle HTTP connections; .NET has no such call short of disposing the
    /// handler, and idle connections close by themselves after a while. Safe to call when not connected. A running
    /// pairing is canceled first, so its <see cref="PairAsync"/> fails at once, as in Go (the bridge cancels a login
    /// this way).
    /// </remarks>
    /// <returns>A task that completes when the long poll has ended.</returns>
    public async Task DisconnectAsync()
    {
        CancelPairing();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    /// <summary>
    /// Cancels a running pairing, disconnects, writes a pending session save, and releases the HTTP clients.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once. Afterwards the client is finished. Errors from the stopping long poll (such as
    /// an aborted socket) are logged, not thrown, and the pending save and the release run even if disconnecting
    /// fails, so a rotated cookie or refreshed token is never lost on exit.
    /// </remarks>
    /// <returns>A task that completes when everything is released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _finished, 1);

        CancelPairing();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseAfterDisconnect();
            _gate.Release();
        }
    }

    // Writes A Pending Save, Waits Out Any Write In Progress, Then Unsubscribes And Releases The HTTP Clients
    private void ReleaseAfterDisconnect()
    {
        try
        {
            bool pending;
            lock (_saveLock)
            {
                pending = _saveTimer is not null;
            }

            if (pending)
            {
                SaveNow();
            }

            StopSaves();
            lock (_writeLock)
            {
                // Nothing; Entering Waits For An In-Flight Timer Save
            }
        }
        finally
        {
            _auth.Cookies.Changed  -= OnCookiesChanged;
            _refresher.EventRaised -= OnRefresherEvent;
            _loop.EventRaised      -= Raise;
            _httpClient.Dispose();
            _longPollClient.Dispose();
            _ownedHandler?.Dispose();
        }
    }

    private void ThrowIfFinished() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _finished) != 0, this);

    // Port Of checkLoggedIn
    private void ThrowIfNotLoggedIn()
    {
        if (_auth.TachyonAuthToken is null)
        {
            throw new InvalidOperationException("No auth token.");
        }

        if (_auth.Browser is null)
        {
            throw new InvalidOperationException("Not logged in.");
        }
    }

    // =========================================================================
    // EVENTS AND LOGGING
    // =========================================================================

    private void Raise(object evt)
    {
        try
        {
            EventRaised?.Invoke(evt);
        }
        catch (Exception ex)
        {
            ProtocolLog.LogHandlerFailed(_log, ex, evt.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient error refreshing auth token on connect, will retry in long polling loop")]
    private static partial void LogTransientRefreshError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Long polling ended with an error")]
    private static partial void LogLongPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to reconnect")]
    private static partial void LogReconnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skip count is non-zero in postConnect ({SkipCount}), waiting longer")]
    private static partial void LogSkipCountWaiting(ILogger logger, int skipCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skip count is still non-zero ({SkipCount})")]
    private static partial void LogSkipCountStillSet(ILogger logger, int skipCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to set active session")]
    private static partial void LogSetActiveSessionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sent set active session/get updates request")]
    private static partial void LogSetActiveSessionSent(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Post-connect steps failed")]
    private static partial void LogPostConnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paired with phone {PhoneIdHash}")]
    private static partial void LogPaired(ILogger logger, string phoneIdHash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pairing long poll ended with an error")]
    private static partial void LogPairingLongPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Google account never finished pairing, nothing to unpair on the server")]
    private static partial void LogNothingToUnpair(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account has no cookies; QR-code unpair is not supported, removing the local session only")]
    private static partial void LogBugleUnpairSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save the session")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to delete the saved session")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Long polling ended with an error while stopping")]
    private static partial void LogStopError(ILogger logger, Exception exception);
}

/// <summary>
/// Thrown when the server refused the token refresh for good, so the user must pair again.
/// </summary>
/// <remarks>
/// Stands in for the error Go's <c>Connect</c> returns when <c>isFatalRefreshError</c> holds (HTTP 401, 403 or 404,
/// or Google's "invalid credentials" and "entity not found" errors), in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// The server's error is the inner exception.
/// </remarks>
public sealed class SessionExpiredException : Exception
{
    /// <summary>
    /// Creates the exception with the default message.
    /// </summary>
    public SessionExpiredException()
        : base("The session has expired; pair again.")
    {
    }

    /// <summary>
    /// Creates the exception with a custom message.
    /// </summary>
    /// <param name="message">The message.</param>
    public SessionExpiredException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with a custom message and cause.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SessionExpiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

using System.Net;
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
/// The single entry point for the app. It pairs with the phone, connects, disconnects, unpairs and keeps the saved
/// session up to date.
/// </summary>
/// <remarks>
/// Ported from <c>Connect</c>, <c>Reconnect</c>, <c>Disconnect</c>, <c>IsConnected</c>, <c>IsLoggedIn</c>,
/// <c>checkLoggedIn</c>, <c>startLongPolling</c> and <c>postConnect</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>,
/// <c>Unpair</c> in pair.go and <c>DoGaiaPairing</c> in pair_google.go. Connect refreshes the token first, so bad
/// credentials fail at once. Then it starts the long poll. When the long poll opens, the client waits 2 seconds.
/// It waits longer while the startup skip count is above 0. Then it flushes the acks, waits 1 more second and sends
/// <c>GET_UPDATES</c> with a new session ID. Go then checks if Google Messages is the default SMS app. This port
/// does not do that check, because nothing uses its answer.
/// <para>
/// Connect, disconnect, pairing and unpairing run one at a time. The client saves the session after each token
/// refresh and each cookie change. The save runs at once when the last save started 5 seconds ago or more.
/// Otherwise it runs 1 time at the end of that 5 second window. Saves stop during pairing, so a half-finished
/// pairing never gets to the disk. A failed save raises <see cref="SessionSaveFailed"/>, so the app can warn the
/// user. After <see cref="UnpairAsync"/> or <see cref="DisposeAsync"/>, the client is finished. The client never
/// logs tokens, keys or cookie values.
/// </para>
/// </remarks>
public sealed partial class GmClient : IMessagingClient, IAsyncDisposable
{
    /// <summary>The shortest time between two automatic session saves.</summary>
    public static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(5);

    // These waits port the time.Sleep calls in Go's postConnect.
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

    // _gate lets only 1 connect, disconnect, pairing or unpairing run at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _pairCts;
    private int _finished;
    private int _disposed;

    // The run changes only under _gate. _runLock also guards _postConnect, because the loop sets it.
    private readonly Lock _runLock = new();
    private CancellationTokenSource? _runCts;
    private Task _run         = Task.CompletedTask;
    private Task _postConnect = Task.CompletedTask;

    // _saveLock guards the save timer and the flags. _writeLock lets only 1 write or delete run at a time.
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

        var http    = new GmHttpClient(_httpClient, _longPollClient, auth.Cookies, time, logs.CreateLogger<GmHttpClient>());
        Rpc         = new RpcSession(http, auth, endpoints, time, logs.CreateLogger<RpcSession>());
        _refresher  = new TokenRefresher(http, auth, endpoints, time, logs.CreateLogger<TokenRefresher>());
        _dispatcher = new UpdateDispatcher(Rpc, logs.CreateLogger<UpdateDispatcher>());
        _loop       = new LongPollLoop(http, Rpc, auth, endpoints, _refresher, _dispatcher, time, logs.CreateLogger<LongPollLoop>());
        _signIn     = new GaiaSignIn(http, auth, endpoints, time, logs.CreateLogger<GaiaSignIn>());
        _pairing    = new GaiaPairing(_signIn, Rpc, _loop, auth, time, logs.CreateLogger<GaiaPairing>());
        _http       = http;
        _endpoints  = endpoints;

        // A request that waits for an answer fails at once when no long poll runs. The pairing long poll counts.
        Rpc.IsListening = () => _loop.IsRunning;

        _loop.EventRaised      += Raise;
        _refresher.EventRaised += OnRefresherEvent;
        auth.Cookies.Changed   += OnCookiesChanged;
    }

    /// <summary>
    /// Occurs for each event: messages, conversations, listen state, phone liveness, token refreshes and pairing.
    /// </summary>
    /// <remarks>
    /// Go's event handler. Handlers run on background threads and must not block for a long time. The client logs a
    /// handler that throws and continues. Handlers must not block synchronously on GmClient methods. The client
    /// raises some events, such as <see cref="PairSuccessful"/>, while it holds its lifecycle lock.
    /// </remarks>
    public event Action<object>? EventRaised;

    /// <summary>
    /// Gets the RPC session that sends requests to the phone.
    /// </summary>
    /// <remarks>Its session ID is empty until the first connect sends <c>GET_UPDATES</c>, as in Go.</remarks>
    public RpcSession Rpc { get; }

    /// <summary>
    /// Gets whether the account is paired and has its cookies.
    /// </summary>
    /// <remarks>Ported from <c>IsLoggedIn</c>.</remarks>
    public bool IsLoggedIn => _auth.Browser is not null && _auth.HasCookies;

    /// <summary>
    /// Gets whether the long poll is open now.
    /// </summary>
    /// <remarks>Ported from <c>IsConnected</c>. As in Go, the value is false for a short time while the long poll reconnects.</remarks>
    public bool IsConnected => _loop.IsConnected;

    /// <summary>Gets or sets a test hook that runs in each session write, before the file write.</summary>
    internal Action? OnWritingForTests { get; set; }

    /// <summary>
    /// Creates a client for an account.
    /// </summary>
    /// <remarks>
    /// Without a <paramref name="handler"/>, the client creates a <see cref="SocketsHttpHandler"/> with cookies off,
    /// because the <see cref="CookieJar"/> of the account owns them. The handler decompresses gzip, deflate and Brotli,
    /// because Go's transport asks for compression by itself. 2 HTTP clients share the handler. The second client has
    /// a 30 minute timeout for the long poll. The client uses a given handler as it is and does not dispose it.
    /// </remarks>
    /// <param name="auth">The account. For pairing, use a new <see cref="AuthData.CreateNew"/> with cookies set.</param>
    /// <param name="store">The store that saves the session.</param>
    /// <param name="endpoints">The server addresses.</param>
    /// <param name="time">The clock for each wait and the save window.</param>
    /// <param name="logs">The factory that creates the loggers.</param>
    /// <param name="handler">The HTTP handler for tests, or null for the real network.</param>
    /// <returns>The client. Dispose it after use.</returns>
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
    /// Ported from <c>Connect</c>. The refresh runs here and in the long poll, so bad credentials fail this call. The
    /// client logs a refresh error that it can retry, such as a network error or a 5xx status. The long poll then
    /// retries it. The call returns when the long poll starts. The long poll opens and sends <c>GET_UPDATES</c> in the
    /// background. A call while the long poll runs makes it reconnect at once and skip its backoff wait. Go's
    /// <c>Connect</c> also retries at once. Only 1 long poll runs. Call this method when the network comes back or the
    /// PC wakes up.
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

            // The running loop refreshes the token itself.
            if (IsRunning)
            {
                _loop.RequestReconnect();
                return;
            }

            // No long poll relays the refresher events now, so this method raises them.
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
    /// Ported from <c>Disconnect</c>. The waiters fail, because their answers can only come over the long poll. Go
    /// also closes idle HTTP connections. .NET has no such call except to dispose the handler. Idle connections close
    /// by themselves after some time. A call when not connected is safe. The method first cancels a running pairing,
    /// so its <see cref="PairAsync"/> fails at once, as in Go. The bridge cancels a login in this way.
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
    /// Cancels a running pairing, disconnects, writes a pending session save and releases the HTTP clients.
    /// </summary>
    /// <remarks>
    /// More than 1 call is safe. After the call, the client is finished. The method logs the errors of the stopping
    /// long poll, such as an aborted socket, and does not throw them. The pending save and the release run also when
    /// the disconnect fails. Thus the app never loses a rotated cookie or a refreshed token on exit.
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
                // The lock waits for a timer save that is in progress.
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

    // Port of checkLoggedIn.
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

    private void Raise(object evt) => ProtocolLog.Raise(_log, EventRaised, evt);

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

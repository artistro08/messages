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
/// that 5 second window. Saves are paused while pairing, so a half-finished pairing never reaches the disk. After
/// <see cref="UnpairAsync"/> or <see cref="DisposeAsync"/> the client is finished. Tokens, keys and cookie values are
/// never logged.
/// </para>
/// </remarks>
public sealed partial class GmClient : IAsyncDisposable
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
    private int _saveCount;

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

    /// <summary>Gets how many session writes have finished (test hook).</summary>
    internal int SaveCount => Volatile.Read(ref _saveCount);

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

        var socket_handler = new SocketsHttpHandler
        {
            UseCookies             = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        return new GmClient(auth, store, endpoints, time, logs, socket_handler, ownsHandler: true);
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
    // PAIRING
    // =========================================================================

    /// <summary>
    /// Pairs with the phone of the signed-in Google account, then connects.
    /// </summary>
    /// <remarks>
    /// Ported from <c>DoGaiaPairing</c>, with the config fetch the bridge's Google login does first (it sets the
    /// session ID that the sign-in sends). <paramref name="showEmoji"/> is called with the emoji the user must tap on
    /// the phone; the call then waits for the tap. Session saves are paused throughout. Once paired, the pairing long
    /// poll is stopped and awaited, the session is saved, <see cref="PairSuccessful"/> is raised and the client
    /// reconnects logged in (Go's <c>Reconnect</c>, without a token refresh). On any failure the half-set pairing
    /// data is cleared (<see cref="AuthData.ClearPairing"/>) and nothing is saved. Pairing a connected client throws:
    /// disconnect first.
    /// </remarks>
    /// <param name="showEmoji">Shows the emoji to the user.</param>
    /// <param name="ct">Cancels the pairing.</param>
    /// <returns>A task that completes when the phone confirmed and the long poll is restarted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="showEmoji"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The client was unpaired or disposed.</exception>
    /// <exception cref="InvalidOperationException">The client is connected, or the pairing long poll stopped before connecting.</exception>
    /// <exception cref="PairingException">No cookies, no phone, a timeout, or the user declined or chose wrong.</exception>
    /// <exception cref="InvalidDataException">The server or phone sent something invalid.</exception>
    /// <exception cref="GmRequestException">The server refused a request.</exception>
    /// <exception cref="ConnectionClosedException">The connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled, or the client was disconnected or disposed.</exception>
    public async Task PairAsync(Action<string> showEmoji, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(showEmoji);

        ThrowIfFinished();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfFinished();
            if (IsRunning)
            {
                throw new InvalidOperationException("Disconnect before pairing.");
            }

            // Pair With Saves Paused; Disconnect And Dispose Cancel It
            string phone_id;
            using var pair_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Volatile.Write(ref _pairCts, pair_cts);
            if (Volatile.Read(ref _finished) != 0)
            {
                // Dispose Ran Before _pairCts Was Visible
                await pair_cts.CancelAsync().ConfigureAwait(false);
            }

            SetSavesSuspended(true);
            try
            {
                phone_id = await PairCoreAsync(showEmoji, pair_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                _auth.ClearPairing();
                throw;
            }
            finally
            {
                Volatile.Write(ref _pairCts, null);
                SetSavesSuspended(false);
            }

            // Save, Announce, Reconnect
            LogPaired(_log, phone_id);
            SaveNow();
            Raise(new PairSuccessful(phone_id));
            await ReconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tells the phone to forget this browser, disconnects, clears the pairing and deletes the saved session.
    /// </summary>
    /// <remarks>
    /// Ported from <c>Unpair</c>: a Google account that never finished pairing has nothing to revoke; otherwise an
    /// <c>UNPAIR_GAIA_PAIRING</c> is sent. Go's QR-code (Bugle) unpair is not ported, since this app only pairs
    /// through Google accounts; that case is logged and skipped. Unlike Go, the local session is removed even when
    /// the request fails (the error is still thrown), so the user is never stuck signed in. Afterwards the client is
    /// finished: create a new one to pair again.
    /// </remarks>
    /// <param name="ct">Cancels the wait for another operation and the request.</param>
    /// <returns>A task that completes when the session file is gone.</returns>
    /// <exception cref="ObjectDisposedException">The client was already unpaired or disposed.</exception>
    /// <exception cref="GmRequestException">The server refused the request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task UnpairAsync(CancellationToken ct)
    {
        ThrowIfFinished();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfFinished();
            try
            {
                if (_auth.IsGoogleAccount && _auth.PairingId == Guid.Empty)
                {
                    LogNothingToUnpair(_log);
                }
                else if (_auth.HasCookies)
                {
                    await _pairing.UnpairAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    LogBugleUnpairSkipped(_log);
                }
            }
            finally
            {
                Volatile.Write(ref _finished, 1);
                await DisconnectCoreAsync().ConfigureAwait(false);
                StopSaves();
                lock (_writeLock)
                {
                    _store.Delete();
                }

                _auth.ClearPairing();
            }
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
    /// <remarks>Safe to call more than once. Afterwards the client is finished.</remarks>
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

            // Write A Pending Save, Then Wait Out Any Write In Progress
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

            _auth.Cookies.Changed  -= OnCookiesChanged;
            _refresher.EventRaised -= OnRefresherEvent;
            _loop.EventRaised      -= Raise;
            _httpClient.Dispose();
            _longPollClient.Dispose();
            _ownedHandler?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    // =========================================================================
    // LONG POLL
    // =========================================================================

    private bool IsRunning
    {
        get
        {
            lock (_runLock)
            {
                return _runCts is not null && !_run.IsCompleted;
            }
        }
    }

    // Cancels A Running Pairing So It Fails At Once And Gives Up The Gate
    private void CancelPairing()
    {
        try
        {
            Volatile.Read(ref _pairCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It Just Ended
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

    // Port Of Disconnect; Caller Holds _gate
    private async Task DisconnectCoreAsync()
    {
        await StopLongPollingAsync().ConfigureAwait(false);
        Rpc.FailAllWaiters();
    }

    // Port Of startLongPolling: The Loop Runs The Pinger And Ack Timer; postConnect Runs Once It Opens; Caller Holds _gate
    private async Task StartLongPollingAsync()
    {
        await StopLongPollingAsync().ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        lock (_runLock)
        {
            _runCts = cts;
            _run    = RunLongPollAsync(cts.Token);
        }
    }

    private async Task RunLongPollAsync(CancellationToken ct)
    {
        try
        {
            await _loop.RunAsync(true, () => TrackPostConnect(PostConnectAsync(ct)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogLongPollFailed(_log, ex);
        }
    }

    // Keeps Every postConnect Run So Stopping Can Wait For It
    private void TrackPostConnect(Task postConnect)
    {
        lock (_runLock)
        {
            _postConnect = _postConnect.IsCompleted ? postConnect : Task.WhenAll(_postConnect, postConnect);
        }
    }

    // Port Of closeLongPolling Plus Waiting For The Run And postConnect To End; Caller Holds _gate
    private async Task StopLongPollingAsync()
    {
        CancellationTokenSource? cts;
        Task run;
        lock (_runLock)
        {
            cts     = _runCts;
            run     = _run;
            _runCts = null;
            _run    = Task.CompletedTask;
        }

        if (cts is null)
        {
            return;
        }

        _loop.Stop();
        await cts.CancelAsync().ConfigureAwait(false);
        await run.ConfigureAwait(false);

        Task post_connect;
        lock (_runLock)
        {
            post_connect = _postConnect;
            _postConnect = Task.CompletedTask;
        }

        await post_connect.ConfigureAwait(false);
        cts.Dispose();
    }

    // Port Of Reconnect: Close, Check, Start (No Token Refresh); Caller Holds _gate
    private async Task ReconnectAsync()
    {
        await StopLongPollingAsync().ConfigureAwait(false);
        try
        {
            ThrowIfNotLoggedIn();
        }
        catch (InvalidOperationException ex)
        {
            LogReconnectFailed(_log, ex);
            Raise(new ListenFatalError(new InvalidOperationException("Failed to reconnect.", ex)));
            return;
        }

        await StartLongPollingAsync().ConfigureAwait(false);
    }

    // Port Of postConnect (Without The Bugle Default Check); Never Throws
    private async Task PostConnectAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(PostConnectDelay, _time, ct).ConfigureAwait(false);

            // Wait Up To 3 s More For The Startup Skip Count To Drain
            if (_dispatcher.SkipCount > 0)
            {
                LogSkipCountWaiting(_log, _dispatcher.SkipCount);
                for (int i = 0; i < 3 && _dispatcher.SkipCount > 0; i++)
                {
                    await Task.Delay(SkipCountDelay, _time, ct).ConfigureAwait(false);
                }

                if (_dispatcher.SkipCount > 0)
                {
                    LogSkipCountStillSet(_log, _dispatcher.SkipCount);
                }

                Raise(new HackySetActiveMayFail());
            }

            // Acks, Then GET_UPDATES
            await Rpc.FlushAcksAsync(ct).ConfigureAwait(false);
            await Task.Delay(BeforeUpdatesWait, _time, ct).ConfigureAwait(false);
            try
            {
                await _loop.SetActiveSessionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogSetActiveSessionFailed(_log, ex);
                Raise(new PingFailed(new InvalidOperationException("Failed to set active session.", ex), 0));
                return;
            }

            LogSetActiveSessionSent(_log);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disconnected Before It Finished
        }
        catch (Exception ex)
        {
            LogPostConnectFailed(_log, ex);
        }
    }

    // Config, Start, Emoji, Finish; The Pairing Long Poll Always Ends Here
    private async Task<string> PairCoreAsync(Action<string> showEmoji, CancellationToken ct)
    {
        await _signIn.FetchConfigAsync(ct).ConfigureAwait(false);
        (string emoji, PairingSession session) = await _pairing.StartAsync(ct).ConfigureAwait(false);
        try
        {
            showEmoji(emoji);
            return await _pairing.FinishAsync(session, ct).ConfigureAwait(false);
        }
        finally
        {
            await StopPairingLongPollAsync().ConfigureAwait(false);
        }
    }

    // Stops And Awaits The Pairing Long Poll; Its Late Error Is Logged So It Can't Hide The Pairing Result
    private async Task StopPairingLongPollAsync()
    {
        try
        {
            await _pairing.StopLongPollAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPairingLongPollFailed(_log, ex);
        }
    }

    // =========================================================================
    // SESSION SAVES
    // =========================================================================

    private void OnCookiesChanged(object? sender, EventArgs e) => RequestSave();

    private void OnRefresherEvent(object evt)
    {
        if (evt is AuthTokenRefreshed)
        {
            RequestSave();
        }
    }

    // Saves Now When The Last Save Started 5 s Ago Or More; Otherwise Once At The End Of That Window
    private void RequestSave()
    {
        lock (_saveLock)
        {
            if (_savesStopped || _savesSuspended || _saveTimer is not null)
            {
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();
            TimeSpan wait      = _lastSaveStart + SaveDebounce - now;
            if (wait > TimeSpan.Zero)
            {
                _saveTimer = _time.CreateTimer(_ => SaveFromTimer(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }

            _lastSaveStart = now;
        }

        WriteSession();
    }

    private void SaveFromTimer()
    {
        lock (_saveLock)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            if (_savesStopped || _savesSuspended)
            {
                return;
            }

            _lastSaveStart = _time.GetUtcNow();
        }

        WriteSession();
    }

    // Saves Right Away, Closing Any Open Window (After Pairing, And The Flush On Dispose)
    private void SaveNow()
    {
        lock (_saveLock)
        {
            _saveTimer?.Dispose();
            _saveTimer     = null;
            _lastSaveStart = _time.GetUtcNow();
        }

        WriteSession();
    }

    // One Writer At A Time; Any Failure Is Logged, Never Thrown (It May Run On A Timer Thread)
    private void WriteSession()
    {
        lock (_writeLock)
        {
            // An Immediate Save That Raced The Pairing Pause Must Not Write Mid-Pairing
            if (Volatile.Read(ref _savesStopped) || Volatile.Read(ref _savesSuspended))
            {
                return;
            }

            try
            {
                OnWritingForTests?.Invoke();
                _store.Save(_auth);
                Interlocked.Increment(ref _saveCount);
            }
            catch (Exception ex)
            {
                LogSaveFailed(_log, ex);
            }
        }
    }

    // Pausing Also Drops An Open Window, So Nothing Saved Before Pairing Is Written Mid-Pairing
    private void SetSavesSuspended(bool suspended)
    {
        lock (_saveLock)
        {
            _savesSuspended = suspended;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }

    private void StopSaves()
    {
        lock (_saveLock)
        {
            _savesStopped = true;
            _saveTimer?.Dispose();
            _saveTimer = null;
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
            LogHandlerFailed(_log, ex, evt.GetType().Name);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Paired with phone {PhoneId}")]
    private static partial void LogPaired(ILogger logger, string phoneId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pairing long poll ended with an error")]
    private static partial void LogPairingLongPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Google account never finished pairing, nothing to unpair on the server")]
    private static partial void LogNothingToUnpair(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account has no cookies; QR-code unpair is not supported, removing the local session only")]
    private static partial void LogBugleUnpairSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save the session")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "An event subscriber threw for {EventType}")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string eventType);
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

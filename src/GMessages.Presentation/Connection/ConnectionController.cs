using GMessages.Protocol;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Session;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Connection;

/// <summary>
/// Owns the protocol <see cref="GmClient"/> for the app's lifetime: starts it, pairs, unpairs, reconnects on resume
/// or network-back, and reacts when Google signs the user out.
/// </summary>
/// <remarks>
/// <para>
/// <b>UI thread.</b> Every event (<see cref="StateChanged"/>, <see cref="ClientEvent"/>) is raised through the
/// <c>post</c> callback, which the app wires to <c>DispatcherQueue.TryEnqueue</c>. <see cref="State"/> itself changes
/// right away on whatever thread made the change, so a handler always reads the newest state; the event only says
/// "look again".
/// </para>
/// <para>
/// <b>Serialization.</b> Start, pair, unpair, resume/network-back, sign-out and dispose run one at a time behind one
/// async gate (<see cref="SemaphoreSlim"/>), so two of them never touch the client at once. The small state fields
/// (<see cref="State"/> and the current client) sit behind a plain lock that is only held for the field writes:
/// never across an await, a <see cref="GmClient"/> call or <c>post</c>. That lets the client's event thread move
/// between Connected and Reconnecting without waiting on the gate.
/// </para>
/// <para>
/// <b>Sign-out.</b> <see cref="ListenFatalError"/> or <see cref="GaiaLoggedOut"/> hands the work to the thread pool
/// (<see cref="Task.Run(Func{Task})"/>): the client raises some events under its own lifecycle lock, and its
/// <see cref="GmClient.DisposeAsync"/> waits for the long poll that is raising the event, so disposing inline could
/// deadlock. The pool task takes the gate, checks the client is still the current one (a stale or repeated event is
/// ignored), disposes it (a finished client can't be reused), deletes the dead session file (after dispose, which may
/// flush a save) and moves to <see cref="ConnectionState.SignedOut"/>.
/// </para>
/// <para>
/// <b>Pairing cancel.</b> A running pairing's cancel source lives behind the state lock, so
/// <see cref="DisposeAsync"/> and <see cref="UnpairAsync"/> cancel it before waiting on the gate instead of waiting
/// out the emoji tap.
/// </para>
/// </remarks>
public sealed partial class ConnectionController : IAsyncDisposable
{
    /// <summary>The text shown in the pairing window after Google ends the session.</summary>
    public const string SignedOutText = "You were signed out. Sign in again to keep getting texts.";

    private readonly AppPaths _paths;
    private readonly GmEndpoints _endpoints;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly Action<Action> _post;
    private readonly HttpMessageHandler? _handler;

    // One Operation At A Time (Start, Pair, Unpair, Notify, Sign-Out, Dispose)
    private readonly SemaphoreSlim _gate = new(1, 1);

    // State Fields; Held Only For The Writes, Never Across An Await, A Client Call Or Post
    private readonly Lock _stateLock = new();
    private ConnectionState _state = ConnectionState.NotPaired;
    private GmClient? _client;
    private bool _listenTrouble;
    private bool _disposed;

    // Running Pairing's Cancel Source; Dispose And Unpair Cancel It Before Waiting On The Gate
    private CancellationTokenSource? _pairCts;
    private bool _closing;

    /// <summary>
    /// Creates the controller. Nothing runs until <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="paths">Where the session file lives.</param>
    /// <param name="endpoints">The server addresses.</param>
    /// <param name="time">The clock handed to every client.</param>
    /// <param name="logs">Creates the loggers.</param>
    /// <param name="post">Runs an action on the UI thread; tests pass an inline runner or a queue.</param>
    /// <param name="handler">The HTTP handler for tests; null for the real network.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ConnectionController(AppPaths paths, GmEndpoints endpoints, TimeProvider time, ILoggerFactory logs, Action<Action> post, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(post);

        _paths     = paths;
        _endpoints = endpoints;
        _time      = time;
        _logs      = logs;
        _log       = logs.CreateLogger<ConnectionController>();
        _post      = post;
        _handler   = handler;
    }

    /// <summary>
    /// Raised (through <c>post</c>) after <see cref="State"/> changes.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised (through <c>post</c>) for every protocol event of the current client: messages, conversations, listen
    /// state, pairing, save failures.
    /// </summary>
    /// <remarks>The argument is one of the records in <c>GMessages.Protocol.Events</c>; switch on its type.</remarks>
    public event EventHandler<object>? ClientEvent;

    /// <summary>
    /// Gets where the app stands with the phone.
    /// </summary>
    public ConnectionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Gets the plain-text reason shown in the pairing window, or null when not signed out.
    /// </summary>
    public string? SignedOutReason => State == ConnectionState.SignedOut ? SignedOutText : null;

    /// <summary>
    /// Gets the paired client, or null when not paired (including while a pairing is still running).
    /// </summary>
    public GmClient? Client
    {
        get
        {
            lock (_stateLock)
            {
                return _state is ConnectionState.NotPaired or ConnectionState.SignedOut ? null : _client;
            }
        }
    }

    /// <summary>Gets the client in use, including one that is still pairing (test hook).</summary>
    internal GmClient? CurrentClient
    {
        get
        {
            lock (_stateLock)
            {
                return _client;
            }
        }
    }

    // =========================================================================
    // START, PAIR, UNPAIR
    // =========================================================================

    /// <summary>
    /// Loads the saved session and connects.
    /// </summary>
    /// <remarks>
    /// No session (or a bad file, or one that isn't logged in) leaves <see cref="ConnectionState.NotPaired"/>.
    /// Otherwise the state goes to Connecting, then Connected once the long poll is started. A refresh the server
    /// refuses for good, or any other connect error (such as a session with no token), means the session can't be
    /// used: the client is dropped, the session file deleted, and the state is
    /// <see cref="ConnectionState.SignedOut"/>. A second call does nothing.
    /// </remarks>
    /// <returns>A task that completes when the state is settled.</returns>
    /// <exception cref="ObjectDisposedException">The controller was disposed.</exception>
    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentClient is not null)
            {
                return;
            }

            // Load The Session
            AuthData? auth = new SessionStore(_paths.SessionFile, _log).Load();
            if (auth is null)
            {
                SetState(ConnectionState.NotPaired);
                return;
            }

            GmClient client = CreateClient(auth);
            if (!client.IsLoggedIn)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                SetState(ConnectionState.NotPaired);
                return;
            }

            // Connect
            SetCurrent(client);
            SetState(ConnectionState.Connecting);
            try
            {
                await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogConnectFailed(_log, ex);
                await DropAndSignOutAsync(client).ConfigureAwait(false);
                return;
            }

            // A Temporary Error May Already Have Moved Us To Reconnecting; Leave That Alone
            SetStateIf(client, ConnectionState.Connected, ConnectionState.Connecting);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Pairs with the phone of the Google account signed in through <paramref name="cookies"/>.
    /// </summary>
    /// <remarks>
    /// Builds fresh <see cref="AuthData"/> with the cookies and a fresh client, then pairs. On success the session is
    /// saved and the state is Connected. On failure that client is disposed, the state stays where it was
    /// (NotPaired or SignedOut) and the error is rethrown for the pairing window to show.
    /// </remarks>
    /// <param name="cookies">The Google sign-in cookies.</param>
    /// <param name="showEmoji">Shows the emoji the user must tap on the phone.</param>
    /// <param name="ct">Cancels the pairing.</param>
    /// <returns>A task that completes when paired and connected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cookies"/> or <paramref name="showEmoji"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The controller was disposed.</exception>
    /// <exception cref="InvalidOperationException">Already paired: unpair first.</exception>
    /// <exception cref="Protocol.Pairing.PairingException">No phone, a timeout, or the user declined or chose wrong.</exception>
    /// <exception cref="GmRequestException">The server refused a request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task PairAsync(IReadOnlyDictionary<string, string> cookies, Action<string> showEmoji, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        ArgumentNullException.ThrowIfNull(showEmoji);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentClient is not null)
            {
                throw new InvalidOperationException("Already paired. Unpair first.");
            }

            // Fresh Account And Client
            AuthData auth = AuthData.CreateNew();
            auth.Cookies.Set(cookies);
            GmClient client = CreateClient(auth);
            SetCurrent(client);

            // Cancel Source Dispose And Unpair Can Reach Without The Gate (Canceled At Once If Dispose Already Asked)
            using var pair_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bool closing;
            lock (_stateLock)
            {
                _pairCts = pair_cts;
                closing  = _closing;
            }

            if (closing)
            {
                await pair_cts.CancelAsync().ConfigureAwait(false);
            }

            // Pair; On Failure Drop This Client And Keep The State
            try
            {
                await client.PairAsync(showEmoji, pair_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                SetCurrent(null);
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                lock (_stateLock)
                {
                    _pairCts = null;
                }
            }

            // The Post-Pair Reconnect May Already Have Hit A Temporary Error
            bool trouble;
            lock (_stateLock)
            {
                trouble = _listenTrouble;
            }

            SetState(trouble ? ConnectionState.Reconnecting : ConnectionState.Connected);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Cancels A Running Pairing So Dispose And Unpair Don't Wait Out The Emoji Tap
    private void CancelPairing(bool closing)
    {
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            _closing = _closing || closing;
            cts      = _pairCts;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The Pairing Just Ended On Its Own
        }
    }

    /// <summary>
    /// Unpairs from the phone, drops the client and deletes the saved session.
    /// </summary>
    /// <remarks>
    /// Ends in <see cref="ConnectionState.NotPaired"/> even when the server refuses the request; that error is then
    /// rethrown. With no client (signed out), only the session file is deleted. A running pairing is canceled first.
    /// </remarks>
    /// <returns>A task that completes when the session file is gone.</returns>
    /// <exception cref="ObjectDisposedException">The controller was disposed.</exception>
    /// <exception cref="GmRequestException">The server refused the unpair request.</exception>
    public async Task UnpairAsync()
    {
        CancelPairing(closing: false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            GmClient? client = CurrentClient;
            SetCurrent(null);
            try
            {
                if (client is null)
                {
                    new SessionStore(_paths.SessionFile, _log).Delete();
                    return;
                }

                try
                {
                    await client.UnpairAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                SetState(ConnectionState.NotPaired);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // =========================================================================
    // RESUME AND NETWORK
    // =========================================================================

    /// <summary>
    /// Tells the controller the PC woke up; reconnects right away when paired.
    /// </summary>
    /// <returns>A task that completes when the reconnect is requested.</returns>
    public Task NotifyResumedAsync() => ReconnectAsync();

    /// <summary>
    /// Tells the controller the network came back; reconnects right away when paired.
    /// </summary>
    /// <returns>A task that completes when the reconnect is requested.</returns>
    public Task NotifyNetworkAvailableAsync() => ReconnectAsync();

    // Running Client: Reconnect Now (Plan 1 R24). Stopped Client: Connect Again, Which May Find The Session Expired
    private async Task ReconnectAsync()
    {
        if (State is ConnectionState.NotPaired or ConnectionState.SignedOut)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            GmClient? client = Client;
            if (_disposed || client is null)
            {
                return;
            }

            // Any Error Here Is Not Transient (ConnectAsync Swallows Those): The Session Can't Be Used
            try
            {
                await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogConnectFailed(_log, ex);
                await DropAndSignOutAsync(client).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // =========================================================================
    // CLIENT EVENTS
    // =========================================================================

    private GmClient CreateClient(AuthData auth)
    {
        var store  = new SessionStore(_paths.SessionFile, _log);
        var client = GmClient.Create(auth, store, _endpoints, _time, _logs, _handler);
        client.EventRaised += e => OnClientEvent(client, e);
        return client;
    }

    // Runs On The Client's Event Thread: Never Block, Never Call The Client (Internal So Tests Can Feed Rare Events)
    internal void OnClientEvent(GmClient client, object e)
    {
        if (CurrentClient != client)
        {
            return;
        }

        switch (e)
        {
            // Google Ended The Session: Dispose Off This Thread, Then Sign Out
            case ListenFatalError or GaiaLoggedOut:
                LogSignedOut(_log, e.GetType().Name);
                _ = Task.Run(() => SignOutAsync(client));
                break;

            // Listen State
            case ListenTemporaryError:
                SetListenTrouble(client, true);
                SetStateIf(client, ConnectionState.Reconnecting, ConnectionState.Connecting, ConnectionState.Connected);
                break;
            case ListenRecovered:
                SetListenTrouble(client, false);
                SetStateIf(client, ConnectionState.Connected, ConnectionState.Reconnecting);
                break;

            // Save Failed: Warn Here, The App Shows It Through ClientEvent
            case SessionSaveFailed failed:
                LogSaveFailed(_log, failed.ErrorType);
                break;
        }

        _post(() => ClientEvent?.Invoke(this, e));
    }

    private async Task SignOutAsync(GmClient client)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Stale Or Repeated Event: That Client Is Already Gone
                if (CurrentClient != client)
                {
                    return;
                }

                await DropAndSignOutAsync(client).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            // Fire-And-Forget Task: Log Instead Of Leaving An Unobserved Exception
            LogSignOutFailed(_log, ex);
        }
    }

    // Caller Holds The Gate. The Session Is Dead For Good, So Its File Goes Too (After Dispose, Which May Flush A Save)
    private async Task DropAndSignOutAsync(GmClient client)
    {
        SetCurrent(null);
        await client.DisposeAsync().ConfigureAwait(false);
        try
        {
            new SessionStore(_paths.SessionFile, _log).Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(_log, ex.GetType().Name);
        }

        SetState(ConnectionState.SignedOut);
    }

    // =========================================================================
    // STATE
    // =========================================================================

    private void SetCurrent(GmClient? client)
    {
        lock (_stateLock)
        {
            _client        = client;
            _listenTrouble = false;
        }
    }

    // Remembers Whether The Current Client's Long Poll Is Between A Temporary Error And Its Recovery
    private void SetListenTrouble(GmClient client, bool trouble)
    {
        lock (_stateLock)
        {
            if (_client == client)
            {
                _listenTrouble = trouble;
            }
        }
    }

    private void SetState(ConnectionState next)
    {
        lock (_stateLock)
        {
            if (_state == next)
            {
                return;
            }

            _state = next;
        }

        RaiseStateChanged();
    }

    // Moves Only When The Client Is Still Current And The State Is One Of The Allowed Ones
    private void SetStateIf(GmClient client, ConnectionState next, params ConnectionState[] from)
    {
        lock (_stateLock)
        {
            if (_client != client || _state == next || !from.Contains(_state))
            {
                return;
            }

            _state = next;
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged() => _post(() => StateChanged?.Invoke(this, EventArgs.Empty));

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    /// <summary>
    /// Disposes the client. Later calls other than dispose throw or do nothing.
    /// </summary>
    /// <remarks>
    /// A running pairing is canceled first, so quitting during the emoji wait doesn't hang. No state change is raised:
    /// the app is shutting down. Safe to call more than once.
    /// </remarks>
    /// <returns>A task that completes when the client is released.</returns>
    public async ValueTask DisposeAsync()
    {
        CancelPairing(closing: true);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed       = true;
            GmClient? client = CurrentClient;
            SetCurrent(null);
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // =========================================================================
    // LOGGING (EVENT TYPE NAMES ONLY, NEVER TOKENS OR COOKIES)
    // =========================================================================

    [LoggerMessage(Level = LogLevel.Warning, Message = "Signed out by the server ({EventType})")]
    private static partial void LogSignedOut(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session could not be saved ({ErrorType})")]
    private static partial void LogSaveFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect failed; the session can't be used")]
    private static partial void LogConnectFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead session file could not be deleted ({ErrorType})")]
    private static partial void LogDeleteFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sign-out cleanup failed")]
    private static partial void LogSignOutFailed(ILogger logger, Exception ex);
}

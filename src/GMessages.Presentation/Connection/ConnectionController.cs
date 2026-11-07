using GMessages.Protocol;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Session;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Connection;

/// <summary>
/// Owns the protocol <see cref="GmClient"/> for the lifetime of the app. The controller starts the client, pairs,
/// unpairs and reconnects on resume or network return, and handles a sign-out by Google.
/// </summary>
/// <remarks>
/// <para>
/// <b>UI thread.</b> The controller raises <see cref="StateChanged"/> and <see cref="ClientEvent"/> through the
/// <c>post</c> callback. The app wires that callback to <c>DispatcherQueue.TryEnqueue</c>.
/// <see cref="State"/> changes at once on the thread that makes the change, so a handler always reads the newest
/// state. The event only tells the handler to read the state again.
/// </para>
/// <para>
/// <b>Serialization.</b> Start, pair, unpair, reconnect, sign-out and dispose run one at a time behind one async gate,
/// a <see cref="SemaphoreSlim"/>. Thus the operations never use the client at the same time. A plain lock guards the
/// small state fields: <see cref="State"/> and the current client. The controller holds that lock only for the field
/// writes. It never holds the lock across an await, a <see cref="GmClient"/> call or <c>post</c>. Thus the event
/// thread of the client can move between Connected and Reconnecting without a wait on the gate.
/// </para>
/// <para>
/// <b>Sign-out.</b> <see cref="ListenFatalError"/> or <see cref="GaiaLoggedOut"/> sends the work to the thread pool
/// through <see cref="Task.Run(Func{Task})"/>. The client raises some events under its own lifecycle lock.
/// <see cref="GmClient.DisposeAsync"/> waits for the long poll that raises the event, so an inline dispose can
/// deadlock. The pool task takes the gate and ignores a stale or repeated event. For the current client, the task
/// disposes the client, because a finished client cannot be used again. The task then deletes the dead session file
/// and the avatar folder, and moves to <see cref="ConnectionState.SignedOut"/>. The file delete comes after the
/// dispose, because the dispose can flush a save.
/// </para>
/// <para>
/// <b>Pairing cancel.</b> The state lock guards the cancel source of a running pairing. Thus
/// <see cref="DisposeAsync"/> and <see cref="UnpairAsync"/> cancel the pairing before they wait on the gate, and do
/// not wait for the emoji tap.
/// </para>
/// </remarks>
public sealed partial class ConnectionController : IAsyncDisposable
{
    /// <summary>The text that the pairing window shows after Google ends the session.</summary>
    public const string SignedOutText = "You were signed out. Sign in again to keep getting texts.";

    // Another process, such as an antivirus at boot, can hold the session file open. The load waits up to 3 times.
    private const int LockRetries = 3;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(1);

    private readonly AppPaths _paths;
    private readonly GmEndpoints _endpoints;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly Action<Action> _post;
    private readonly HttpMessageHandler? _handler;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Lock _stateLock = new();
    private ConnectionState _state = ConnectionState.NotPaired;
    private GmClient? _client;
    private bool _listenTrouble;
    private bool _disposed;

    private CancellationTokenSource? _pairCts;
    private bool _closing;

    /// <summary>
    /// Creates the controller. No work starts before <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="paths">The paths of the session file and the avatar folder.</param>
    /// <param name="endpoints">The server addresses.</param>
    /// <param name="time">The clock for every client.</param>
    /// <param name="logs">Creates the loggers.</param>
    /// <param name="post">Runs an action on the UI thread. Tests give an inline runner or a queue.</param>
    /// <param name="handler">The HTTP handler for tests, or null for the real network.</param>
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
    /// Occurs after <see cref="State"/> changes. The controller raises it through <c>post</c>.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Occurs for every protocol event of the current client. The controller raises it through <c>post</c>.
    /// </summary>
    /// <remarks>
    /// The events include messages, conversations, the listen state, pairing and save failures. The argument is a
    /// record from <c>GMessages.Protocol.Events</c>. Use a switch on its type.
    /// </remarks>
    public event EventHandler<object>? ClientEvent;

    /// <summary>
    /// Gets the connection state of the app with the phone.
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
    /// Gets the reason that the pairing window shows, or null when the state is not SignedOut.
    /// </summary>
    public string? SignedOutReason => State == ConnectionState.SignedOut ? SignedOutText : null;

    /// <summary>
    /// Gets the paired client, or null when no phone is paired. The value is also null while a pairing runs.
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

    /// <summary>Gets the client in use, also while it pairs. Tests use this property.</summary>
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
    /// With no session, a bad file or a session that is not logged in, the state stays
    /// <see cref="ConnectionState.NotPaired"/>. Otherwise the state goes to Connecting, then to Connected when the long
    /// poll starts. The server can refuse a refresh permanently, or another connect error can occur. A session with no
    /// token is an example. Then the controller drops the client, deletes the session file and moves to
    /// <see cref="ConnectionState.SignedOut"/>. Another process can hold the session file open. The load then waits up
    /// to 3 times, 1 s apart, so a short lock does not read as no session. The controller never deletes a locked file.
    /// A call while a client exists does nothing.
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

            for (int retry = 0; retry < LockRetries && IsLocked(_paths.SessionFile); retry++)
            {
                LogSessionLocked(_log, retry + 1);
                await Task.Delay(LockRetryDelay, _time).ConfigureAwait(false);
            }

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

            // A temporary error during the connect can move the state to Reconnecting. That state stays.
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
    /// The method builds new <see cref="AuthData"/> with the cookies and a new client, then pairs. On success, the
    /// client saves the session and the state is Connected. A temporary error after the pairing gives Reconnecting. On
    /// failure, the method disposes that client and rethrows the error for the pairing window. The state stays
    /// NotPaired or SignedOut. An unpair that cancels the pairing moves the state to NotPaired. The method then throws
    /// <see cref="OperationCanceledException"/>, whatever error the client gave.
    /// </remarks>
    /// <param name="cookies">The Google sign-in cookies.</param>
    /// <param name="showEmoji">Shows the emoji that the user must tap on the phone.</param>
    /// <param name="ct">Cancels the pairing.</param>
    /// <returns>A task that completes when the phone is paired and connected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cookies"/> or <paramref name="showEmoji"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The controller was disposed.</exception>
    /// <exception cref="InvalidOperationException">Already paired: unpair first.</exception>
    /// <exception cref="Protocol.Pairing.PairingException">No phone, a timeout, or the user declined or chose wrong.</exception>
    /// <exception cref="GmRequestException">The server refused a request.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was canceled, or an unpair canceled the pairing.
    /// </exception>
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

            AuthData auth = AuthData.CreateNew();
            auth.Cookies.Set(cookies);
            GmClient client = CreateClient(auth);
            SetCurrent(client);

            // Dispose and unpair reach this source without the gate. A dispose that comes first cancels the source at
            // once.
            using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bool closing;
            lock (_stateLock)
            {
                _pairCts = pairCts;
                closing  = _closing;
            }

            if (closing)
            {
                await pairCts.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await client.PairAsync(showEmoji, pairCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetCurrent(null);
                await client.DisposeAsync().ConfigureAwait(false);

                // The pairing window reads the state to tell an unpair from a failure. After the cancel, the socket can
                // throw any error. Thus each error of an unpair becomes a cancellation.
                if (pairCts.IsCancellationRequested && !ct.IsCancellationRequested && !IsClosing())
                {
                    SetState(ConnectionState.NotPaired);
                    throw new OperationCanceledException("An unpair canceled the pairing.", ex, pairCts.Token);
                }

                throw;
            }
            finally
            {
                lock (_stateLock)
                {
                    _pairCts = null;
                }
            }

            // The reconnect after the pairing can already have a temporary error.
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

    private bool IsClosing()
    {
        lock (_stateLock)
        {
            return _closing;
        }
    }

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
            // The pairing ends by itself at the same time.
        }
    }

    /// <summary>
    /// Unpairs from the phone, drops the client and deletes the saved session and the avatar folder.
    /// </summary>
    /// <remarks>
    /// The method first cancels a running pairing. The state ends in <see cref="ConnectionState.NotPaired"/>, also when
    /// the server refuses the request. The method then rethrows that error. With no client, as after a sign-out, the
    /// method deletes only the session file and the avatar folder.
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
                DeleteAvatars();
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
    /// Reconnects at once when a phone is paired. The app calls this method when the PC wakes or the network returns.
    /// </summary>
    /// <remarks>
    /// A running client reconnects at once (Plan 1 R24). A stopped client connects again and can find that the session
    /// expired.
    /// </remarks>
    /// <returns>A task that completes when the reconnect is requested.</returns>
    public async Task ReconnectAsync()
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

            // ConnectAsync handles a temporary error itself. Any error here means that the session is not usable.
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

    // The event thread of the client runs this method, so it must never block or call the client. Tests call it with
    // rare events.
    internal void OnClientEvent(GmClient client, object e)
    {
        if (CurrentClient != client)
        {
            return;
        }

        switch (e)
        {
            // Google ended the session. The dispose must not run on this thread.
            case ListenFatalError or GaiaLoggedOut:
                LogSignedOut(_log, e.GetType().Name);
                _ = Task.Run(() => SignOutAsync(client));
                break;

            case ListenTemporaryError:
                SetListenTrouble(client, true);
                SetStateIf(client, ConnectionState.Reconnecting, ConnectionState.Connecting, ConnectionState.Connected);
                break;
            case ListenRecovered:
                SetListenTrouble(client, false);
                SetStateIf(client, ConnectionState.Connected, ConnectionState.Reconnecting);
                break;

            // The app shows the save failure through ClientEvent.
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
                // After a stale or repeated event, the client is already gone.
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
            // No caller waits for this task. Thus the method logs the exception, which is otherwise unobserved.
            LogSignOutFailed(_log, ex);
        }
    }

    // The caller must hold the gate. The session is not usable again, so the session file goes too. The delete comes
    // after the dispose, because the dispose can flush a save.
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

        DeleteAvatars();
        SetState(ConnectionState.SignedOut);
    }

    // ponytail: the probe runs just before the load. A lock that starts between the probe and the load still reads as
    // no session. A load in SessionStore that reports an IOException can close that gap.
    private static bool IsLocked(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // For a missing file or a file that the app cannot read, the load decides.
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    // The photos of the contacts go with the session. The method logs a locked file and does not throw.
    private void DeleteAvatars()
    {
        try
        {
            if (Directory.Exists(_paths.AvatarsFolder))
            {
                Directory.Delete(_paths.AvatarsFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogAvatarsDeleteFailed(_log, ex.GetType().Name);
        }
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

    // Records whether the long poll of the current client is between a temporary error and its recovery.
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
    /// Disposes the client. After this call, the other members throw or do nothing.
    /// </summary>
    /// <remarks>
    /// The method first cancels a running pairing, so a quit during the emoji wait does not hang. The method raises no
    /// state change, because the app is closing. The app can call the method more than once.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Avatar folder could not be deleted ({ErrorType})")]
    private static partial void LogAvatarsDeleteFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session file is in use; trying again ({Attempt})")]
    private static partial void LogSessionLocked(ILogger logger, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sign-out cleanup failed")]
    private static partial void LogSignOutFailed(ILogger logger, Exception ex);
}

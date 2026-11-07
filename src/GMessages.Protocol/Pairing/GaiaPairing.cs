using System.Globalization;
using GMessages.Protocol.Crypto;
using GMessages.Protocol.Http;
using GMessages.Protocol.LongPoll;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace GMessages.Protocol.Pairing;

/// <summary>
/// Pairs with a Google account. It signs in and runs the UKEY2 handshake with the phone. It stores the keys after the
/// user taps the right emoji.
/// </summary>
/// <remarks>
/// Ported from <c>StartGaiaPairing</c>, <c>FinishGaiaPairing</c>, <c>cancelGaiaPairing</c>,
/// <c>sendGaiaPairingMessage</c> and <c>UnpairGaia</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go">pair_google.go</see>.
/// The answers of the phone arrive as data events on a long poll that is not logged in. Thus
/// <see cref="StartAsync"/> starts a long poll and waits for its connect before it sends the client init. That long
/// poll continues after a successful start, so that the finish answer can arrive. After the pairing, the caller stops
/// it with <see cref="StopLongPollAsync"/>. <c>Reconnect</c> in Go does the same. As in Go, the dispatcher only logs
/// the Gaia events on the long poll. Pairing needs nothing from them. The log never gets the keys, the device IDs or
/// the emoji.
/// </remarks>
/// <param name="signIn">Signs in and lists the phones of the account.</param>
/// <param name="rpc">Sends the pairing messages.</param>
/// <param name="longPoll">The long poll that carries the answers.</param>
/// <param name="auth">The account to pair.</param>
/// <param name="time">The clock for the init timeout and the start time.</param>
/// <param name="log">The logger.</param>
public sealed partial class GaiaPairing(GaiaSignIn signIn, RpcSession rpc, LongPollLoop longPoll, AuthData auth, TimeProvider time, ILogger log)
{
    /// <summary>The time in seconds that the phone has to answer the client init. Ported from <c>GaiaInitTimeout</c>.</summary>
    public const int GaiaInitTimeoutSeconds = 20;

    // The wording of the owner replaces "user cancelled pairing on phone" from Go. Keep it verbatim.
    internal const string CanceledOnPhoneMessage = "Pairing on phone was cancelled";

    // A verbatim copy of the text in sendGaiaPairingMessage.
    private const string PrivateApiConfirmation = "This is an undocumented API. Use or access of undocumented Google APIs without express authorization is prohibited per the Google API Terms of Service (https://developers.google.com/terms).";

    // 300 seconds in microseconds, as (300 * time.Second).Microseconds() in Go.
    private const long PairingTtlMicros = 300_000_000;

    /// <summary>The longest wait for the finish answer: the 300 second TTL of the pairing messages. This limit is C#-only.</summary>
    public static readonly TimeSpan PairingTtl = TimeSpan.FromMicroseconds(PairingTtlMicros);

    // The longest wait for a best-effort cancel request. This limit is C#-only, because Go has no limit.
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    // The long poll of a successful StartAsync, until StopLongPollAsync takes it.
    private Task? _longPoll;

    /// <summary>
    /// Signs in, picks the phone, starts the long poll and sends the client init. Returns the emoji to show.
    /// </summary>
    /// <remarks>
    /// Ported from <c>StartGaiaPairing</c>. The method sorts several phones by last-seen time, newest first. When the
    /// account has an earlier pairing, <see cref="AuthData.DestPhoneId"/> is set, and the method keeps only that phone.
    /// The chosen phone becomes <see cref="AuthData.DestRegId"/>. The method sends a cancel request when the phone does
    /// not answer within 20 seconds. A bad answer also causes a cancel request. Unlike Go, the method stops the long
    /// poll when the start fails, because nothing else stops it. Go leaves that to the context of the caller. Also
    /// unlike Go, a long poll that ends before its connect fails the start. Go waits forever then.
    /// </remarks>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>The emoji that the user must tap on the phone, and the session for <see cref="FinishAsync"/>.</returns>
    /// <exception cref="PairingException">No cookies, no phones, the paired phone is gone, or the init timed out.</exception>
    /// <exception cref="InvalidDataException">The phone's ID or answer is invalid.</exception>
    /// <exception cref="InvalidOperationException">The long poll stopped before connecting, or its token refresh failed fatally.</exception>
    /// <exception cref="GmRequestException">The server refused a request, including the long poll's credentials (401/403).</exception>
    /// <exception cref="ConnectionClosedException">The connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<(string Emoji, PairingSession Session)> StartAsync(CancellationToken ct)
    {
        if (!auth.HasCookies)
        {
            throw new PairingException(PairingFailure.NoCookies, "gaia pairing requires cookies");
        }

        List<PrimaryDevice> devices = [.. await signIn.GetPrimaryDevicesAsync(ct).ConfigureAwait(false)];
        if (devices.Count == 0)
        {
            throw new PairingException(PairingFailure.NoDevicesFound, "no devices found for gaia pairing");
        }

        if (devices.Count > 1)
        {
            devices = [.. devices.OrderByDescending(d => d.LastSeen)];
            LogMultipleDevices(log, devices.Count);
        }

        if (auth.DestPhoneId != 0)
        {
            devices.RemoveAll(d => d.UnknownTs != auth.DestPhoneId);
            if (devices.Count == 0)
            {
                throw new PairingException(PairingFailure.PhoneNotFound, "previously paired phone not found for gaia pairing");
            }
        }

        PrimaryDevice dest = devices[0];
        if (!Guid.TryParse(dest.RegId, out Guid destRegId))
        {
            throw new InvalidDataException("failed to parse destination UUID");
        }

        auth.DestRegId = destRegId;

        var connected    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task longPollRun = longPoll.RunAsync(false, () => connected.TrySetResult(), CancellationToken.None);
        try
        {
            if (await Task.WhenAny(connected.Task, longPollRun).WaitAsync(ct).ConfigureAwait(false) != connected.Task)
            {
                // Throw the error of the long poll, such as a 401, before the generic error.
                await longPollRun.ConfigureAwait(false);
                throw new InvalidOperationException("Long polling stopped before connecting.");
            }

            (string Emoji, PairingSession Session) started = await HandshakeAsync(dest, devices.Count, ct).ConfigureAwait(false);
            _longPoll = longPollRun;
            return started;
        }
        catch
        {
            // Wait for the end of the long poll. Its own error must not hide this error.
            longPoll.Stop();
            await longPollRun.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
    }

    /// <summary>
    /// Sends the client finished message, waits for the user to tap an emoji, and stores the new keys.
    /// </summary>
    /// <remarks>
    /// Ported from <c>FinishGaiaPairing</c>. On success, the method sets the request keys,
    /// <see cref="AuthData.PairingId"/> and <see cref="AuthData.DestPhoneId"/>. When <paramref name="ct"/> is
    /// canceled, the method sends a cancel request. Go sends that request with the canceled context, so it never goes
    /// out. This port sends it without the canceled token, as Go intends. Go waits as long as its context allows. This
    /// port also ends the wait in 2 C#-only cases. First, the long poll of <see cref="StartAsync"/> ends, so the answer
    /// cannot arrive. The method then throws the error of the long poll, or a <see cref="PairingException"/> when there
    /// is none. Second, the 300 second TTL of the finish message ends, and the server drops the request. The method
    /// then throws a <see cref="PairingException"/> with <see cref="PairingFailure.Timeout"/>. Both cases also send
    /// the cancel request.
    /// </remarks>
    /// <param name="session">The session from <see cref="StartAsync"/>.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>
    /// The phone ID: the source ID of the phone, a slash, and its timestamp. When the timestamp is 0, the unknown int
    /// replaces it.
    /// </returns>
    /// <exception cref="PairingException">The phone reported an error, such as the wrong emoji; no answer came within 300 seconds; or the long poll stopped while waiting.</exception>
    /// <exception cref="InvalidDataException">The answer or the key derivation version is invalid.</exception>
    /// <exception cref="GmRequestException">The server refused the request, including the long poll's credentials (401/403) while waiting.</exception>
    /// <exception cref="InvalidOperationException">The long poll's token refresh failed fatally while waiting.</exception>
    /// <exception cref="ConnectionClosedException">The connection closed while waiting.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<string> FinishAsync(PairingSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        GaiaPairingResponseContainer response;
        using (var ttl = new CancellationTokenSource(PairingTtl, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ttl.Token))
        {
            // Wait for the answer, unless the long poll that carries it ends first.
            Task<GaiaPairingResponseContainer> send = SendGaiaPairingMessageAsync(session, ActionType.CreateGaiaPairingClientFinished, session.FinishPayload, linked.Token);
            Task? poll                              = Volatile.Read(ref _longPoll);
            if (poll is not null && await Task.WhenAny(send, poll).ConfigureAwait(false) == poll)
            {
                await linked.CancelAsync().ConfigureAwait(false);
                await ((Task)send).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                _ = send.Exception;
                LogLongPollEndedWhileWaiting(log);
                await TryCancelAsync(session).ConfigureAwait(false);
                await poll.ConfigureAwait(false);
                throw new PairingException(PairingFailure.Unknown, "long polling stopped while waiting for the phone");
            }

            try
            {
                response = await send.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                LogCancelAfterCanceled(log);
                await TryCancelAsync(session).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (ttl.IsCancellationRequested)
            {
                await TryCancelAsync(session).ConfigureAwait(false);
                throw new PairingException(PairingFailure.Timeout, "pairing timed out (no answer within 300 seconds)");
            }
        }

        if (response.FinishErrorType != 0)
        {
            throw MapFinishError(response);
        }

        (byte[] aesKey, byte[] hmacKey) = session.DeriveRequestKeys();
        auth.RequestCrypto = new AesCtrHelper(aesKey, hmacKey);
        auth.PairingId     = session.Id;
        auth.DestPhoneId   = session.DestRegDevice.UnknownTs;

        ulong phoneNumber = session.DestRegDevice.UnknownTs != 0 ? session.DestRegDevice.UnknownTs : session.DestRegDevice.UnknownInt;
        return $"{auth.Mobile?.SourceID}/{phoneNumber}";
    }

    /// <summary>
    /// Stops the long poll of a successful <see cref="StartAsync"/> and waits for its end.
    /// </summary>
    /// <remarks>
    /// Go's <c>DoGaiaPairing</c> leaves this to <c>Reconnect</c>, which closes the long poll and does not wait. Here
    /// the wait lets the caller see a late error of that long poll. The error is not lost. The method does nothing when
    /// no pairing long poll runs.
    /// </remarks>
    /// <returns>A task that completes when the long poll ended.</returns>
    /// <exception cref="GmRequestException">The long poll had ended on refused credentials (401/403).</exception>
    /// <exception cref="InvalidOperationException">The long poll had ended on a fatal token refresh error.</exception>
    public async Task StopLongPollAsync()
    {
        Task? running = Interlocked.Exchange(ref _longPoll, null);
        if (running is null)
        {
            return;
        }

        longPoll.Stop();
        await running.ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the phone to forget this browser.
    /// </summary>
    /// <remarks>Ported from <c>UnpairGaia</c>: an encrypted <c>UNPAIR_GAIA_PAIRING</c> with the pairing ID.</remarks>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the server accepted the request.</returns>
    /// <exception cref="GmRequestException">The server refused the request.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public Task UnpairAsync(CancellationToken ct) =>
        rpc.SendNoResponseAsync(new SendParams(ActionType.UnpairGaiaPairing, new RevokeGaiaPairingRequest { PairingAttemptID = auth.PairingId.ToString() }), ct);

    /// <summary>
    /// Maps a finish answer with an error to the error of Go.
    /// </summary>
    /// <remarks>
    /// Ported from the error switch of <c>FinishGaiaPairing</c>. The message is the error text of Go. The 2
    /// phone-cancel cases are different: they use the wording of the owner in <see cref="CanceledOnPhoneMessage"/>.
    /// </remarks>
    /// <param name="response">The finish answer, with a non-zero error type.</param>
    /// <returns>The exception to throw.</returns>
    internal static PairingException MapFinishError(GaiaPairingResponseContainer response)
    {
        int type = response.FinishErrorType;
        return response.FinishErrorCode switch
        {
            GaiaPairingErrorCode.WrongVerificationCodeSelected => new PairingException(PairingFailure.IncorrectEmoji, "user chose incorrect emoji on phone"),
            GaiaPairingErrorCode.UserCanceledVerification      => new PairingException(PairingFailure.Canceled, CanceledOnPhoneMessage),
            GaiaPairingErrorCode.RequestOutOfDate
                or GaiaPairingErrorCode.RequestNotReceivedQuickly
                or GaiaPairingErrorCode.VerificationTimedOut   => new PairingException(PairingFailure.Timeout, $"pairing timed out (code: {type}/{(int)response.FinishErrorCode})"),
            GaiaPairingErrorCode.UserDeniedVerificationNotMe   => new PairingException(PairingFailure.NotMe, CanceledOnPhoneMessage + " (user chose 'this is not me' option)"),
            _                                                  => new PairingException(PairingFailure.Unknown, $"unknown error pairing: {type}/{ErrorCodeName(response.FinishErrorCode)}"),
        };
    }

    /// <summary>
    /// Sends 1 pairing message and waits for the answer of the phone.
    /// </summary>
    /// <remarks>
    /// Ported from <c>sendGaiaPairingMessage</c>. The message is not encrypted and has a 300 second TTL. The client
    /// finished message goes as a Bugle message with the API confirmation text. The other messages go as Gaia 2 and
    /// propose version 1 for the verification code and for the key derivation.
    /// </remarks>
    /// <param name="session">The session.</param>
    /// <param name="action">The pairing action.</param>
    /// <param name="data">The UKEY2 message.</param>
    /// <param name="ct">Cancels the send and the wait.</param>
    /// <returns>The answer of the phone.</returns>
    internal async Task<GaiaPairingResponseContainer> SendGaiaPairingMessageAsync(PairingSession session, ActionType action, byte[] data, CancellationToken ct)
    {
        var container = new GaiaPairingRequestContainer
        {
            PairingAttemptID = session.Id.ToString(),
            BrowserDetails   = GmConstants.BrowserDetails,
            StartTimestamp   = session.Start.ToUnixTimeMilliseconds(),
            Data             = ByteString.CopyFrom(data),
        };
        MessageType messageType = MessageType.Gaia2;
        if (action == ActionType.CreateGaiaPairingClientFinished)
        {
            messageType                      = MessageType.BugleMessage;
            container.PrivateAPIConfirmation = PrivateApiConfirmation;
        }
        else
        {
            container.ProposedVerificationCodeVersion = 1;
            container.ProposedKeyDerivationVersion    = 1;
        }

        Task<IncomingRpc> answer = await rpc.SendNoWaitAsync(new SendParams(action, container, CustomTtlMicros: PairingTtlMicros, DontEncrypt: true, MessageType: messageType), ct).ConfigureAwait(false);
        IncomingRpc response     = await answer.ConfigureAwait(false);
        try
        {
            return GaiaPairingResponseContainer.Parser.ParseFrom(response.Message!.UnencryptedData);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException("Failed to decode the pairing answer.", ex);
        }
    }

    /// <summary>
    /// Tells the phone to drop this pairing attempt.
    /// </summary>
    /// <remarks>Ported from <c>cancelGaiaPairing</c>: not encrypted, Gaia 2, a 300 second TTL and the session ID as request ID.</remarks>
    /// <param name="session">The session.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the server accepted the request.</returns>
    internal Task CancelAsync(PairingSession session, CancellationToken ct) =>
        rpc.SendNoResponseAsync(new SendParams(ActionType.CancelGaiaPairing, RequestId: session.Id.ToString(), CustomTtlMicros: PairingTtlMicros, DontEncrypt: true, MessageType: MessageType.Gaia2), ct);

    // Sends the client init, checks the server init and picks the emoji. A timeout or a bad answer cancels the attempt.
    private async Task<(string Emoji, PairingSession Session)> HandshakeAsync(PrimaryDevice dest, int deviceCount, CancellationToken ct)
    {
        PairingSession session = PairingSession.Create(dest, time);
        (byte[] clientInit, _) = session.PreparePayloads();

        GaiaPairingResponseContainer serverInit;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(GaiaInitTimeoutSeconds), time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
        {
            try
            {
                serverInit = await SendGaiaPairingMessageAsync(session, ActionType.CreateGaiaPairingClientInit, clientInit, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Read the timer before the cancel request. That request can take 10 seconds, and the timer can end meanwhile.
                // As in Go, only a canceled wait is a timeout.
                // A refusal stays a refusal when the timer ends before this catch.
                bool timedOut = ex is OperationCanceledException && timeout.IsCancellationRequested && !ct.IsCancellationRequested;
                await TryCancelAsync(session).ConfigureAwait(false);
                if (timedOut)
                {
                    throw new PairingException(PairingFailure.InitTimeout, deviceCount > 1 ? "client init timed out (had multiple primary-looking devices)" : "client init timed out");
                }

                throw;
            }
        }

        LogServerInit(log, serverInit.ConfirmedKeyDerivationVersion, serverInit.ConfirmedVerificationCodeVersion);
        try
        {
            string emoji = session.ProcessServerInit(serverInit);
            return (emoji, session);
        }
        catch (InvalidDataException)
        {
            await TryCancelAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    // As in Go, the method only logs a failed cancel. The wait is 10 seconds at most, so a bad network cannot stop the user.
    private async Task TryCancelAsync(PairingSession session)
    {
        try
        {
            using var limit = new CancellationTokenSource(CancelTimeout, time);
            await CancelAsync(session, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCancelFailed(log, ex);
        }
    }

    // Gives the String() of Go for the error code: the name, or the number for an unknown value.
    private static string ErrorCodeName(GaiaPairingErrorCode code) =>
        AuthenticationReflection.Descriptor.EnumTypes.First(e => e.Name == nameof(GaiaPairingErrorCode)).FindValueByNumber((int)code)?.Name
        ?? ((int)code).ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Found {Count} primary-looking devices for gaia pairing")]
    private static partial void LogMultipleDevices(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received server init: key derivation version {KeyDerivationVersion}, verification code version {VerificationCodeVersion}")]
    private static partial void LogServerInit(ILogger logger, int keyDerivationVersion, int verificationCodeVersion);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending gaia pairing cancel after the wait was canceled")]
    private static partial void LogCancelAfterCanceled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Long polling stopped while waiting for the pairing answer")]
    private static partial void LogLongPollEndedWhileWaiting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send gaia pairing cancel request")]
    private static partial void LogCancelFailed(ILogger logger, Exception exception);
}

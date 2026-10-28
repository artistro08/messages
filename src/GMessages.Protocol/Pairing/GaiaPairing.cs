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
/// Pairs with a Google account: signs in, runs the UKEY2 handshake with the phone, and stores the keys once the user
/// taps the right emoji.
/// </summary>
/// <remarks>
/// Ported from <c>StartGaiaPairing</c>, <c>FinishGaiaPairing</c>, <c>cancelGaiaPairing</c>,
/// <c>sendGaiaPairingMessage</c> and <c>UnpairGaia</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go">pair_google.go</see>.
/// The phone's answers arrive as data events on a long poll that is not logged in, so <see cref="StartAsync"/> starts
/// one and waits for it to connect before sending the client init. That long poll keeps running after a successful
/// start so the finish answer can arrive; the caller stops it with <see cref="StopLongPollAsync"/> (Go's
/// <c>Reconnect</c> does the same) once pairing is over. Gaia events on the long poll are only logged by the
/// dispatcher, as in Go; pairing needs nothing from them. Keys are never logged; device IDs and the emoji may be.
/// </remarks>
/// <param name="signIn">Signs in and lists the account's phones.</param>
/// <param name="rpc">Sends the pairing messages.</param>
/// <param name="longPoll">The long poll the answers arrive on.</param>
/// <param name="auth">The account being paired.</param>
/// <param name="time">The clock for the init timeout and the start time.</param>
/// <param name="log">The logger.</param>
public sealed partial class GaiaPairing(GaiaSignIn signIn, RpcSession rpc, LongPollLoop longPoll, AuthData auth, TimeProvider time, ILogger log)
{
    /// <summary>How long the phone has to answer the client init, in seconds (Go: <c>GaiaInitTimeout</c>).</summary>
    public const int GaiaInitTimeoutSeconds = 20;

    // Copied Verbatim From sendGaiaPairingMessage
    private const string PrivateApiConfirmation = "This is an undocumented API. Use or access of undocumented Google APIs without express authorization is prohibited per the Google API Terms of Service (https://developers.google.com/terms).";

    // 300 s In Microseconds (Go: (300 * time.Second).Microseconds())
    private const long PairingTtlMicros = 300_000_000;

    /// <summary>The longest wait for the finish answer: the pairing messages' 300 second TTL (C#-only ceiling).</summary>
    public static readonly TimeSpan PairingTtl = TimeSpan.FromMicroseconds(PairingTtlMicros);

    // The Long Poll Started By A Successful StartAsync, Until StopLongPollAsync Takes It
    private Task? _longPoll;

    // Longest Wait For A Best-Effort Cancel Request (C#-Only; Go Has No Limit)
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets which phone to use when there are several, as an index into the newest-first list.
    /// </summary>
    /// <remarks>
    /// Go's <c>GaiaHackyDeviceSwitcher</c>: a manual escape hatch; 0 picks the newest phone. Values past the end wrap
    /// around. A negative value is refused here, since it would pick no phone at all.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int HackyDeviceSwitcher
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    // =========================================================================
    // PAIRING
    // =========================================================================

    /// <summary>
    /// Signs in, picks the phone, starts the long poll and sends the client init; returns the emoji to show.
    /// </summary>
    /// <remarks>
    /// Ported from <c>StartGaiaPairing</c>. Several phones are sorted newest first by last-seen time. When the account
    /// was paired before (<see cref="AuthData.DestPhoneId"/> set), only that phone is kept. The chosen phone becomes
    /// <see cref="AuthData.DestRegId"/>. If the phone does not answer within 20 seconds, or its answer is invalid, a
    /// cancel request is sent. Unlike Go, the long poll is stopped when the start fails, because nothing else would
    /// stop it (Go leaves that to the caller's context), and a long poll that ends before connecting fails the start
    /// instead of waiting forever.
    /// </remarks>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>The emoji the user must tap on the phone, and the session to pass to <see cref="FinishAsync"/>.</returns>
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

        // Find The Phones
        List<PrimaryDevice> devices = [.. await signIn.GetPrimaryDevicesAsync(ct).ConfigureAwait(false)];
        if (devices.Count == 0)
        {
            throw new PairingException(PairingFailure.NoDevicesFound, "no devices found for gaia pairing");
        }

        if (devices.Count > 1)
        {
            devices = [.. devices.OrderByDescending(d => d.LastSeen)];
            LogMultipleDevices(log, devices.Count, HackyDeviceSwitcher);
        }

        // Keep Only The Previously Paired Phone
        if (auth.DestPhoneId != 0)
        {
            devices.RemoveAll(d => d.UnknownTs != auth.DestPhoneId);
            if (devices.Count == 0)
            {
                throw new PairingException(PairingFailure.PhoneNotFound, "previously paired phone not found for gaia pairing");
            }
        }

        PrimaryDevice dest = devices[HackyDeviceSwitcher % devices.Count];
        LogDevicePicked(log, dest.RegId, dest.UnknownInt, dest.UnknownTs, dest.LastSeen);
        if (!Guid.TryParse(dest.RegId, out Guid dest_reg_id))
        {
            throw new InvalidDataException("failed to parse destination UUID");
        }

        auth.DestRegId = dest_reg_id;

        // Start The Long Poll And Wait For It To Connect
        var connected  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task long_poll = longPoll.RunAsync(false, () => connected.TrySetResult(), CancellationToken.None);
        try
        {
            if (await Task.WhenAny(connected.Task, long_poll).WaitAsync(ct).ConfigureAwait(false) != connected.Task)
            {
                // Surface The Long Poll's Own Error (Such As A 401) Before The Generic One
                await long_poll.ConfigureAwait(false);
                throw new InvalidOperationException("Long polling stopped before connecting.");
            }

            (string Emoji, PairingSession Session) started = await HandshakeAsync(dest, devices.Count, ct).ConfigureAwait(false);
            _longPoll = long_poll;
            return started;
        }
        catch
        {
            // Wait For The Long Poll To End; Its Own Error Must Not Hide This One
            longPoll.Stop();
            await long_poll.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
    }

    /// <summary>
    /// Sends the client finished message, waits for the user to tap an emoji, and stores the new keys.
    /// </summary>
    /// <remarks>
    /// Ported from <c>FinishGaiaPairing</c>. On success the request keys, <see cref="AuthData.PairingId"/> and
    /// <see cref="AuthData.DestPhoneId"/> are set. If <paramref name="ct"/> is canceled, a cancel request is sent. Go
    /// sends it with the already-canceled context, so it never leaves; this port sends it without one, which is what
    /// Go meant. Unlike Go, which waits as long as its context allows, the wait has two more ends, both C#-only: the
    /// long poll from <see cref="StartAsync"/> ending (the answer could never arrive; its error is thrown), and the
    /// 300 second TTL of the finish message passing (the server drops the request then; a
    /// <see cref="PairingFailure.Timeout"/>). Both also send the cancel request.
    /// </remarks>
    /// <param name="session">The session from <see cref="StartAsync"/>.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The phone ID: the phone's source ID, a slash, and its timestamp (or its unknown int when 0).</returns>
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
            // Wait For The Answer, Unless The Long Poll It Arrives On Ends First
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

        // Store The Keys
        (byte[] aes_key, byte[] hmac_key) = session.DeriveRequestKeys();
        auth.RequestCrypto = new AesCtrHelper(aes_key, hmac_key);
        auth.PairingId     = session.Id;
        auth.DestPhoneId   = session.DestRegDevice.UnknownTs;

        ulong phone_number = session.DestRegDevice.UnknownTs != 0 ? session.DestRegDevice.UnknownTs : session.DestRegDevice.UnknownInt;
        return $"{auth.Mobile?.SourceID}/{phone_number}";
    }

    /// <summary>
    /// Stops the long poll a successful <see cref="StartAsync"/> left running, and waits for it to end.
    /// </summary>
    /// <remarks>
    /// Go's <c>DoGaiaPairing</c> leaves this to <c>Reconnect</c>, which closes the long poll without waiting. Waiting
    /// here means a late error from that long poll is observed by the caller instead of being lost. Does nothing when
    /// no pairing long poll is running.
    /// </remarks>
    /// <returns>A task that completes when the long poll has ended.</returns>
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

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Maps a finish answer with an error to Go's error.
    /// </summary>
    /// <remarks>Ported from the error switch of <c>FinishGaiaPairing</c>; the message is Go's error text.</remarks>
    /// <param name="response">The finish answer, with a non-zero error type.</param>
    /// <returns>The exception to throw.</returns>
    internal static PairingException MapFinishError(GaiaPairingResponseContainer response)
    {
        int type = response.FinishErrorType;
        return response.FinishErrorCode switch
        {
            GaiaPairingErrorCode.WrongVerificationCodeSelected => new PairingException(PairingFailure.IncorrectEmoji, "user chose incorrect emoji on phone"),
            GaiaPairingErrorCode.UserCanceledVerification      => new PairingException(PairingFailure.Canceled, "user cancelled pairing on phone"),
            GaiaPairingErrorCode.RequestOutOfDate
                or GaiaPairingErrorCode.RequestNotReceivedQuickly
                or GaiaPairingErrorCode.VerificationTimedOut   => new PairingException(PairingFailure.Timeout, $"pairing timed out (code: {type}/{(int)response.FinishErrorCode})"),
            GaiaPairingErrorCode.UserDeniedVerificationNotMe   => new PairingException(PairingFailure.NotMe, "user cancelled pairing on phone (user chose 'this is not me' option)"),
            _                                                  => new PairingException(PairingFailure.Unknown, $"unknown error pairing: {type}/{ErrorCodeName(response.FinishErrorCode)}"),
        };
    }

    /// <summary>
    /// Sends one pairing message and waits for the phone's answer.
    /// </summary>
    /// <remarks>
    /// Ported from <c>sendGaiaPairingMessage</c>. The message is unencrypted with a 300 s TTL. The client finished
    /// message goes as a bugle message with the API confirmation text; the others go as Gaia 2 and propose version 1
    /// for both the verification code and the key derivation.
    /// </remarks>
    /// <param name="session">The session.</param>
    /// <param name="action">The pairing action.</param>
    /// <param name="data">The UKEY2 message.</param>
    /// <param name="ct">Cancels the send and the wait.</param>
    /// <returns>The phone's answer.</returns>
    internal async Task<GaiaPairingResponseContainer> SendGaiaPairingMessageAsync(PairingSession session, ActionType action, byte[] data, CancellationToken ct)
    {
        var container = new GaiaPairingRequestContainer
        {
            PairingAttemptID = session.Id.ToString(),
            BrowserDetails   = GmConstants.BrowserDetails,
            StartTimestamp   = session.Start.ToUnixTimeMilliseconds(),
            Data             = ByteString.CopyFrom(data),
        };
        MessageType message_type = MessageType.Gaia2;
        if (action == ActionType.CreateGaiaPairingClientFinished)
        {
            message_type                     = MessageType.BugleMessage;
            container.PrivateAPIConfirmation = PrivateApiConfirmation;
        }
        else
        {
            container.ProposedVerificationCodeVersion = 1;
            container.ProposedKeyDerivationVersion    = 1;
        }

        Task<IncomingRpc> answer = await rpc.SendAsyncNoWait(new SendParams(action, container, CustomTtlMicros: PairingTtlMicros, DontEncrypt: true, MessageType: message_type), ct).ConfigureAwait(false);
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
    /// <remarks>Ported from <c>cancelGaiaPairing</c>: unencrypted, Gaia 2, 300 s TTL, the session ID as request ID.</remarks>
    /// <param name="session">The session.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>A task that completes when the server accepted the request.</returns>
    internal Task CancelAsync(PairingSession session, CancellationToken ct) =>
        rpc.SendNoResponseAsync(new SendParams(ActionType.CancelGaiaPairing, RequestId: session.Id.ToString(), CustomTtlMicros: PairingTtlMicros, DontEncrypt: true, MessageType: MessageType.Gaia2), ct);

    // Client Init, Server Init, Emoji; Cancels The Attempt On Timeout Or A Bad Answer
    private async Task<(string Emoji, PairingSession Session)> HandshakeAsync(PrimaryDevice dest, int deviceCount, CancellationToken ct)
    {
        PairingSession session = PairingSession.Create(dest, time);
        (byte[] client_init, _) = session.PreparePayloads();

        // Send The Client Init With A 20 s Timeout
        GaiaPairingResponseContainer server_init;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(GaiaInitTimeoutSeconds), time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
        {
            try
            {
                server_init = await SendGaiaPairingMessageAsync(session, ActionType.CreateGaiaPairingClientInit, client_init, linked.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await TryCancelAsync(session).ConfigureAwait(false);
                if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new PairingException(PairingFailure.InitTimeout, deviceCount > 1 ? "client init timed out (had multiple primary-looking devices)" : "client init timed out");
                }

                throw;
            }
        }

        // Check The Answer And Pick The Emoji
        LogServerInit(log, server_init.ConfirmedKeyDerivationVersion, server_init.ConfirmedVerificationCodeVersion);
        try
        {
            string emoji = session.ProcessServerInit(server_init);
            LogEmoji(log, emoji);
            return (emoji, session);
        }
        catch (InvalidDataException)
        {
            await TryCancelAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    // A Failed Cancel Is Only Logged, As In Go; 10 s At Most, So A Bad Network Can't Stall The User
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

    // Go's String() Of The Error Code: The Name, Or The Number When Unknown
    private static string ErrorCodeName(GaiaPairingErrorCode code) =>
        AuthenticationReflection.Descriptor.EnumTypes.First(e => e.Name == nameof(GaiaPairingErrorCode)).FindValueByNumber((int)code)?.Name
        ?? ((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Found {Count} primary-looking devices for gaia pairing, hacky device switcher {Switcher}")]
    private static partial void LogMultipleDevices(ILogger logger, int count, int switcher);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Found UUID to use for gaia pairing: {RegId}, unknown int {UnknownInt}, unknown ts {UnknownTs}, last seen {LastSeen}")]
    private static partial void LogDevicePicked(ILogger logger, string regId, ulong unknownInt, ulong unknownTs, DateTimeOffset lastSeen);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received server init: key derivation version {KeyDerivationVersion}, verification code version {VerificationCodeVersion}")]
    private static partial void LogServerInit(ILogger logger, int keyDerivationVersion, int verificationCodeVersion);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pairing emoji is {Emoji}")]
    private static partial void LogEmoji(ILogger logger, string emoji);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending gaia pairing cancel after the wait was canceled")]
    private static partial void LogCancelAfterCanceled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Long polling stopped while waiting for the pairing answer")]
    private static partial void LogLongPollEndedWhileWaiting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send gaia pairing cancel request")]
    private static partial void LogCancelFailed(ILogger logger, Exception exception);
}

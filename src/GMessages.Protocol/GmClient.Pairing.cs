using System.Security.Cryptography;
using System.Text;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;

namespace GMessages.Protocol;

/// <summary>
/// The pairing part of <see cref="GmClient"/>. It shows the emoji for the phone to confirm, finishes the pairing and unpairs.
/// </summary>
public sealed partial class GmClient
{
    /// <summary>
    /// Pairs with the phone of the signed-in Google account, then connects.
    /// </summary>
    /// <remarks>
    /// Ported from <c>DoGaiaPairing</c>. First it fetches the config, as the Google login of the bridge does. The
    /// config sets the session ID that the sign-in sends. The method calls <paramref name="showEmoji"/> with the emoji
    /// that the user must tap on the phone, then waits for the tap. Session saves stop during the pairing. After the
    /// pairing, the method stops the pairing long poll and waits for it. Then it saves the session, raises
    /// <see cref="PairSuccessful"/> and reconnects logged in. That is Go's <c>Reconnect</c>, without a token refresh.
    /// On a failure, <see cref="AuthData.ClearPairing"/> clears the half-set pairing data and nothing is saved. A
    /// connected client throws. Disconnect first.
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

            // Disconnect and dispose cancel the pairing through _pairCts.
            string phoneId;
            using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Volatile.Write(ref _pairCts, pairCts);
            if (Volatile.Read(ref _finished) != 0)
            {
                // Dispose ran before _pairCts was visible.
                await pairCts.CancelAsync().ConfigureAwait(false);
            }

            SetSavesSuspended(true);
            try
            {
                phoneId = await PairCoreAsync(showEmoji, pairCts.Token).ConfigureAwait(false);
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

            LogPaired(_log, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phoneId)))[..8]);
            SaveNow();
            Raise(new PairSuccessful(phoneId));
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
    /// Ported from <c>Unpair</c>. A Google account that did not finish the pairing has nothing to revoke. Otherwise
    /// the method sends <c>UNPAIR_GAIA_PAIRING</c>. This port does not include Go's QR-code unpair for Bugle, because
    /// the app pairs only through Google accounts. The method logs and skips that case. Unlike Go, the method removes
    /// the local session also when the request fails. It still throws the error. Thus the user never stays signed in
    /// by mistake. After the call, the client is finished. Create a new client to pair again.
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
                try
                {
                    await DisconnectCoreAsync().ConfigureAwait(false);
                }
                finally
                {
                    // Always remove the local session. A failed delete must not hide the error of the server.
                    StopSaves();
                    try
                    {
                        lock (_writeLock)
                        {
                            _store.Delete();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDeleteFailed(_log, ex);
                    }

                    _auth.ClearPairing();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // A canceled pairing fails at once and releases _gate.
    private void CancelPairing()
    {
        try
        {
            Volatile.Read(ref _pairCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pairing ended just before the cancel.
        }
    }

    // The pairing long poll always ends in this method.
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

    // The method logs a late error of the pairing long poll, so that error cannot hide the pairing result.
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
}

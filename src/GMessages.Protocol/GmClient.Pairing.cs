using System.Security.Cryptography;
using System.Text;
using GMessages.Protocol.Events;
using GMessages.Protocol.Http;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.Rpc;
using GMessages.Protocol.Session;

namespace GMessages.Protocol;

/// <summary>
/// Pairing: showing the emoji for the phone to confirm, finishing the pairing, and unpairing.
/// </summary>
/// <remarks>
/// Part of <see cref="GmClient"/>; the other members are in GmClient.cs.
/// </remarks>
public sealed partial class GmClient
{

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
            string phoneId;
            using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Volatile.Write(ref _pairCts, pairCts);
            if (Volatile.Read(ref _finished) != 0)
            {
                // Dispose Ran Before _pairCts Was Visible
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

            // Save, Announce, Reconnect
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
                try
                {
                    await DisconnectCoreAsync().ConfigureAwait(false);
                }
                finally
                {
                    // Always Remove The Local Session; A Failed Delete Must Not Hide The Server's Error
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
}

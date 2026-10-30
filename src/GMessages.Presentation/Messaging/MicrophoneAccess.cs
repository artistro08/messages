namespace GMessages.Presentation.Messaging;

/// <summary>
/// Whether Windows lets the app use the microphone, as its access check reports it.
/// </summary>
public enum MicrophoneAccessStatus
{
    /// <summary>Access is on.</summary>
    Allowed,

    /// <summary>The user hasn't decided yet, so Windows can ask.</summary>
    Undecided,

    /// <summary>Access is off: refused by the user, off for the whole PC, or not declared by the app.</summary>
    Refused,
}

/// <summary>
/// Decides whether a voice recording can start, from the microphone access checks around Windows' consent prompt.
/// </summary>
/// <remarks>
/// The app checks access first. Anything but allowed asks Windows, through <c>ActivateAudioInterfaceAsync</c>, which
/// shows the consent prompt when Windows still lets the user decide. The check alone can't be trusted: the owner's
/// 0.5.5 log read "DeniedByUser" while Windows' consent store already said "Prompt" again. Windows' answer to the
/// activation decides; anything that can't record shows the settings dialog.
/// </remarks>
public static class MicrophoneAccess
{
    /// <summary>
    /// Whether the app should ask Windows for the microphone, which shows its consent prompt when Windows allows one.
    /// </summary>
    /// <param name="before">The access check before asking.</param>
    /// <returns><see langword="true"/> unless access is already on.</returns>
    public static bool ShouldAsk(MicrophoneAccessStatus before) => before != MicrophoneAccessStatus.Allowed;

    /// <summary>
    /// Whether recording can start, or the settings dialog shows instead.
    /// </summary>
    /// <remarks>
    /// Allowed before records at once. Otherwise Windows' answer decides: the activation succeeding (<c>S_OK</c>) records,
    /// and so does a second check that says allowed, since either means Windows gave the microphone.
    /// </remarks>
    /// <param name="before">The access check before asking.</param>
    /// <param name="activateResult">The activation's HRESULT (<c>S_OK</c> given, <c>E_ACCESSDENIED</c> refused); ignored unless asked.</param>
    /// <param name="after">The access check after asking; ignored unless asked.</param>
    /// <returns><see langword="true"/> to record, <see langword="false"/> to show the settings dialog.</returns>
    public static bool CanRecord(MicrophoneAccessStatus before, int activateResult, MicrophoneAccessStatus after)
    {
        if (!ShouldAsk(before))
        {
            return true;
        }

        return activateResult == 0 || after == MicrophoneAccessStatus.Allowed;
    }
}

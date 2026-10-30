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
/// The app checks access first. Only an undecided status asks Windows, through <c>ActivateAudioInterfaceAsync</c>,
/// which shows the consent prompt; then it checks again. Anything that can't record shows the settings dialog.
/// </remarks>
public static class MicrophoneAccess
{
    /// <summary>
    /// Whether the app should ask Windows for the microphone, which shows its consent prompt.
    /// </summary>
    /// <param name="before">The access check before asking.</param>
    /// <returns><see langword="true"/> only when the user hasn't decided yet.</returns>
    public static bool ShouldAsk(MicrophoneAccessStatus before) => before == MicrophoneAccessStatus.Undecided;

    /// <summary>
    /// Whether recording can start, or the settings dialog shows instead.
    /// </summary>
    /// <remarks>
    /// Allowed before records at once, and refused before never asks. After the prompt, the second check decides: allowed
    /// records, and refused doesn't. A second check that still says undecided records only when the activation
    /// succeeded (<c>S_OK</c>), since Windows gave the microphone even if the check hasn't caught up.
    /// </remarks>
    /// <param name="before">The access check before asking.</param>
    /// <param name="activateResult">The activation's HRESULT (<c>S_OK</c> given, <c>E_ACCESSDENIED</c> refused); ignored unless asked.</param>
    /// <param name="after">The access check after asking; ignored unless asked.</param>
    /// <returns><see langword="true"/> to record, <see langword="false"/> to show the settings dialog.</returns>
    public static bool CanRecord(MicrophoneAccessStatus before, int activateResult, MicrophoneAccessStatus after)
    {
        if (!ShouldAsk(before))
        {
            return before == MicrophoneAccessStatus.Allowed;
        }

        return after == MicrophoneAccessStatus.Allowed
            || (after == MicrophoneAccessStatus.Undecided && activateResult == 0);
    }
}

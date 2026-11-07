namespace GMessages.Presentation.Messaging;

/// <summary>
/// Tells whether Windows lets the app use the microphone, as the access check of Windows reports it.
/// </summary>
public enum MicrophoneAccessStatus
{
    /// <summary>Access is on.</summary>
    Allowed,

    /// <summary>The user did not decide yet, so Windows can ask.</summary>
    Undecided,

    /// <summary>Access is off: the user refused it, it is off for the PC, or the app did not declare it.</summary>
    Refused,
}

/// <summary>
/// Decides whether a voice recording can start, from the microphone access checks around the consent prompt of Windows.
/// </summary>
/// <remarks>
/// The app checks access first. Any result other than allowed asks Windows through
/// <c>ActivateAudioInterfaceAsync</c>. Windows then shows the consent prompt when it still lets the user decide. The
/// check alone is not reliable. The 0.5.5 log of the owner read "DeniedByUser" while the consent store of Windows
/// already said "Prompt" again. The answer of Windows to the activation decides. Any result that cannot record shows
/// the settings dialog.
/// </remarks>
public static class MicrophoneAccess
{
    /// <summary>
    /// Tells whether the app must ask Windows for the microphone. Windows then shows its consent prompt when it allows
    /// one.
    /// </summary>
    /// <param name="before">The access check before the request.</param>
    /// <returns><see langword="true"/> unless access is already on.</returns>
    public static bool ShouldAsk(MicrophoneAccessStatus before) => before != MicrophoneAccessStatus.Allowed;

    /// <summary>
    /// Tells whether the recording can start, or the settings dialog shows instead.
    /// </summary>
    /// <remarks>
    /// When access was allowed before, the recording starts at once. Else the answer of Windows decides. A successful
    /// activation with <c>S_OK</c> records. A second check that says allowed also records, because either result means
    /// that Windows gave the microphone.
    /// </remarks>
    /// <param name="before">The access check before the request.</param>
    /// <param name="activateResult">
    /// The HRESULT of the activation: <c>S_OK</c> when given, <c>E_ACCESSDENIED</c> when refused. The method ignores it
    /// when it did not ask.
    /// </param>
    /// <param name="after">The access check after the request. The method ignores it when it did not ask.</param>
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

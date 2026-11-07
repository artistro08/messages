using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class MicrophoneAccessTests
{
    // These are the HRESULT values of ActivateAudioInterfaceAsync.
    private const int Ok           = 0;
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// Any status except Allowed asks Windows. The check can differ from the consent store of Windows.
    /// The 0.5.5 log of the owner shows a check that said refused while the store said prompt.
    /// </summary>
    [TestMethod]
    [DataRow(MicrophoneAccessStatus.Allowed, false)]
    [DataRow(MicrophoneAccessStatus.Undecided, true)]
    [DataRow(MicrophoneAccessStatus.Refused, true)]
    public void ShouldAsk_UnlessAllowed(MicrophoneAccessStatus before, bool expected)
    {
        Assert.AreEqual(expected, MicrophoneAccess.ShouldAsk(before));
    }

    /// <summary>
    /// The answer of Windows decides. A successful activation, or a second check that is allowed, records.
    /// A refused activation with a second check that is not allowed shows the settings dialog.
    /// </summary>
    [TestMethod]
    [DataRow(MicrophoneAccessStatus.Allowed, AccessDenied, MicrophoneAccessStatus.Refused, true)]
    [DataRow(MicrophoneAccessStatus.Refused, Ok, MicrophoneAccessStatus.Refused, true)]
    [DataRow(MicrophoneAccessStatus.Refused, AccessDenied, MicrophoneAccessStatus.Refused, false)]
    [DataRow(MicrophoneAccessStatus.Refused, AccessDenied, MicrophoneAccessStatus.Allowed, true)]
    [DataRow(MicrophoneAccessStatus.Undecided, Ok, MicrophoneAccessStatus.Allowed, true)]
    [DataRow(MicrophoneAccessStatus.Undecided, AccessDenied, MicrophoneAccessStatus.Allowed, true)]
    [DataRow(MicrophoneAccessStatus.Undecided, Ok, MicrophoneAccessStatus.Undecided, true)]
    [DataRow(MicrophoneAccessStatus.Undecided, AccessDenied, MicrophoneAccessStatus.Undecided, false)]
    [DataRow(MicrophoneAccessStatus.Undecided, AccessDenied, MicrophoneAccessStatus.Refused, false)]
    public void CanRecord_SecondCheckDecides(MicrophoneAccessStatus before, int activateResult, MicrophoneAccessStatus after, bool expected)
    {
        Assert.AreEqual(expected, MicrophoneAccess.CanRecord(before, activateResult, after));
    }
}

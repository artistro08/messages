using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class MicrophoneAccessTests
{
    // HRESULTs From ActivateAudioInterfaceAsync
    private const int Ok           = 0;
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// Anything but allowed asks Windows, since the check can lag Windows' consent store (owner's 0.5.5 log: the check
    /// said refused while the store said prompt).
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
    /// Windows' answer decides: a successful activation or an allowed second check records; a refused activation with a
    /// second check that isn't allowed shows the settings dialog.
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

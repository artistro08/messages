using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class MicrophoneAccessTests
{
    // HRESULTs From ActivateAudioInterfaceAsync
    private const int Ok           = 0;
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// Only an undecided status asks Windows; allowed records at once, and refused or off shows the settings dialog.
    /// </summary>
    [TestMethod]
    [DataRow(MicrophoneAccessStatus.Allowed, false)]
    [DataRow(MicrophoneAccessStatus.Undecided, true)]
    [DataRow(MicrophoneAccessStatus.Refused, false)]
    public void ShouldAsk_OnlyWhenUndecided(MicrophoneAccessStatus before, bool expected)
    {
        Assert.AreEqual(expected, MicrophoneAccess.ShouldAsk(before));
    }

    /// <summary>
    /// After the prompt, the second check decides; a still-undecided check records only when the activation succeeded,
    /// and a refused check never records.
    /// </summary>
    [TestMethod]
    [DataRow(MicrophoneAccessStatus.Allowed, Ok, MicrophoneAccessStatus.Allowed, true)]
    [DataRow(MicrophoneAccessStatus.Refused, Ok, MicrophoneAccessStatus.Refused, false)]
    [DataRow(MicrophoneAccessStatus.Undecided, Ok, MicrophoneAccessStatus.Allowed, true)]
    [DataRow(MicrophoneAccessStatus.Undecided, AccessDenied, MicrophoneAccessStatus.Allowed, true)]
    [DataRow(MicrophoneAccessStatus.Undecided, Ok, MicrophoneAccessStatus.Undecided, true)]
    [DataRow(MicrophoneAccessStatus.Undecided, AccessDenied, MicrophoneAccessStatus.Undecided, false)]
    [DataRow(MicrophoneAccessStatus.Undecided, AccessDenied, MicrophoneAccessStatus.Refused, false)]
    [DataRow(MicrophoneAccessStatus.Undecided, Ok, MicrophoneAccessStatus.Refused, false)]
    public void CanRecord_SecondCheckDecides(MicrophoneAccessStatus before, int activateResult, MicrophoneAccessStatus after, bool expected)
    {
        Assert.AreEqual(expected, MicrophoneAccess.CanRecord(before, activateResult, after));
    }
}

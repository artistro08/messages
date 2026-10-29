using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// One person in a conversation.
/// </summary>
/// <param name="Id">The participant ID.</param>
/// <param name="FullName">The full name, or empty.</param>
/// <param name="FirstName">The first name, or empty.</param>
/// <param name="AvatarHexColor">The avatar color as hex, or empty.</param>
/// <param name="IsMe">True for the phone's owner.</param>
/// <param name="FormattedNumber">The phone number as shown, or null.</param>
public sealed record ParticipantInfo(string Id, string FullName, string FirstName, string AvatarHexColor, bool IsMe, string? FormattedNumber)
{
    /// <summary>Gets the raw phone number the phone names this participant by in typing events, or empty.</summary>
    public string Number { get; init; } = string.Empty;

    /// <summary>
    /// Copies the fields the app needs out of a protocol participant.
    /// </summary>
    /// <param name="p">The protocol participant.</param>
    /// <returns>The slim record.</returns>
    public static ParticipantInfo From(Participant p)
    {
        ArgumentNullException.ThrowIfNull(p);

        return new ParticipantInfo(
            p.ID?.ParticipantID ?? string.Empty,
            p.FullName,
            p.FirstName,
            p.AvatarHexColor,
            p.IsMe,
            string.IsNullOrEmpty(p.FormattedNumber) ? null : p.FormattedNumber)
        {
            Number = p.ID?.Number ?? string.Empty,
        };
    }
}

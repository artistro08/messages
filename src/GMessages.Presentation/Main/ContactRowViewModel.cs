using System.Globalization;
using System.Text;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// One person (or one typed number) in the Start chat list and its chips.
/// </summary>
public sealed class ContactRowViewModel
{
    // Gray Used When The Phone Gives No Avatar Color
    private const uint FallbackColor = 0xFF5F6368;

    private static readonly CompositeFormat SendToFormat = CompositeFormat.Parse(ChatCopy.SendToFormat);

    /// <summary>
    /// Creates a row for a typed phone number or email: "Send to {query}".
    /// </summary>
    /// <param name="query">What the user typed.</param>
    public ContactRowViewModel(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        Name        = string.Format(CultureInfo.CurrentCulture, SendToFormat, query);
        Number      = query.Trim();
        Digits      = DigitsOf(Number);
        Initial     = "#";
        AvatarColor = FallbackColor;
        IsSendTo    = true;
    }

    /// <summary>
    /// Creates a row for one of the phone's contacts.
    /// </summary>
    /// <param name="contact">The contact.</param>
    public ContactRowViewModel(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        ContactId   = contact.ContactID;
        Name        = contact.Name;
        Number      = contact.Number?.Number ?? string.Empty;
        Digits      = DigitsOf(Number);
        Initial     = ChatFormat.Initial(contact.Name);
        AvatarColor = ChatFormat.ColorFromHex(contact.AvatarHexColor, FallbackColor);
        Subtitle    = string.IsNullOrEmpty(contact.Number?.FormattedNumber) ? Number : contact.Number.FormattedNumber;
    }

    /// <summary>Gets the contact ID, or empty for a typed number.</summary>
    public string ContactId { get; } = string.Empty;

    /// <summary>Gets the name, or "Send to {query}" for a typed number.</summary>
    public string Name { get; }

    /// <summary>Gets the number (or email) the chat is started with.</summary>
    public string Number { get; }

    /// <summary>Gets the number as shown under the name.</summary>
    public string Subtitle { get; } = string.Empty;

    /// <summary>Gets only the digits of <see cref="Number"/>, for matching.</summary>
    public string Digits { get; }

    /// <summary>Gets the avatar letter.</summary>
    public string Initial { get; }

    /// <summary>Gets the avatar color as opaque ARGB.</summary>
    public uint AvatarColor { get; }

    /// <summary>Gets whether this is the "Send to" row for a typed number.</summary>
    public bool IsSendTo { get; }

    /// <summary>
    /// Gives the name.
    /// </summary>
    /// <remarks>A XAML list item reads its item's <c>ToString()</c> as its UI Automation name, so screen readers hear this.</remarks>
    /// <returns>The name.</returns>
    public override string ToString() => Name;

    internal static string DigitsOf(string text) => new([.. text.Where(char.IsAsciiDigit)]);
}

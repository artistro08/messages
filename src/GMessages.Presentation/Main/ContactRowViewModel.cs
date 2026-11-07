using System.Globalization;
using System.Text;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.Presentation.Main;

/// <summary>
/// Shows 1 person or 1 typed number in the Start chat list and its chips.
/// </summary>
public sealed class ContactRowViewModel
{
    private static readonly CompositeFormat SendToFormat = CompositeFormat.Parse(ChatCopy.SendToFormat);

    /// <summary>
    /// Creates the "Send to {query}" row for a typed phone number or email address.
    /// </summary>
    /// <param name="query">What the user typed.</param>
    public ContactRowViewModel(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        Name        = string.Format(CultureInfo.CurrentCulture, SendToFormat, query);
        Number      = query.Trim();
        Digits      = DigitsOf(Number);
        Initial     = string.Empty;
        AvatarColor = AvatarParts.FallbackColor;
        IsSendTo    = true;
    }

    /// <summary>
    /// Creates a row for one of the phone's contacts.
    /// </summary>
    /// <param name="contact">The contact.</param>
    public ContactRowViewModel(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        Name        = contact.Name;
        Number      = contact.Number?.Number ?? string.Empty;
        Digits      = DigitsOf(Number);
        Initial     = ChatFormat.Initial(contact.Name);
        AvatarColor = ChatFormat.ColorFromHex(contact.AvatarHexColor, AvatarParts.FallbackColor);
        Subtitle    = string.IsNullOrEmpty(contact.Number?.FormattedNumber) ? Number : contact.Number.FormattedNumber;
    }

    /// <summary>Gets the name, or "Send to {query}" for a typed number.</summary>
    public string Name { get; }

    /// <summary>Gets the number or email address that starts the chat.</summary>
    public string Number { get; }

    /// <summary>Gets the number that shows under the name.</summary>
    public string Subtitle { get; } = string.Empty;

    /// <summary>Gets only the digits of <see cref="Number"/>, for matching.</summary>
    public string Digits { get; }

    /// <summary>
    /// Gets the avatar letter, or empty for the person icon. A typed or unsaved number has no letter.
    /// </summary>
    public string Initial { get; }

    /// <summary>Gets the avatar color as opaque ARGB.</summary>
    public uint AvatarColor { get; }

    /// <summary>Gets whether this is the "Send to" row for a typed number.</summary>
    public bool IsSendTo { get; }

    /// <summary>
    /// Gives the name.
    /// </summary>
    /// <remarks>
    /// A XAML list item reads the <c>ToString()</c> of its item as its UI Automation name. Screen readers then read
    /// this name.
    /// </remarks>
    /// <returns>The name.</returns>
    public override string ToString() => Name;

    internal static string DigitsOf(string text) => new([.. text.Where(char.IsAsciiDigit)]);
}

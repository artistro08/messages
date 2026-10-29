using System.Text;
using System.Xml;
using System.Xml.Linq;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Notifications;

/// <summary>
/// Builds the toast XML.
/// </summary>
public static class NotificationXml
{
    /// <summary>
    /// Builds the toast: title, body, optional avatar, reply box, Snooze and Dismiss.
    /// </summary>
    /// <remarks>
    /// Every value goes in through <see cref="XElement"/>/<see cref="XAttribute"/>, so markup in a message is escaped.
    /// Characters XML can't hold at all (control characters, lone surrogates) are dropped from the title and body.
    /// </remarks>
    /// <param name="n">The toast content.</param>
    /// <param name="avatarFileUri">A file URI for the avatar picture, or null for none.</param>
    /// <param name="sendIconUri">A file URI for the send button icon.</param>
    /// <param name="parts">Which buttons to include; every one by default. With none, the toast has no actions at all.</param>
    /// <returns>The toast XML.</returns>
    public static string Build(NotificationContent n, string? avatarFileUri, string sendIconUri, ToastParts parts = ToastParts.All)
    {
        ArgumentNullException.ThrowIfNull(n);

        string id = Uri.EscapeDataString(n.Group);

        // Visual
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", XmlSafe(n.Title)), new XElement("text", XmlSafe(n.Body)));

        if (avatarFileUri is not null)
        {
            binding.Add(new XElement("image", new XAttribute("placement", "appLogoOverride"), new XAttribute("hint-crop", "circle"), new XAttribute("src", avatarFileUri)));
        }

        // Actions: Windows' Quick Reply (A Text Input With A Button Tied To It By hint-inputId, Its Icon From imageUri),
        // The App's Own Snooze (Its Length Is A Setting, Which Windows' System Snooze Can't Take Without A Picker) And
        // Windows' Dismiss. Every <input> Before Any <action>, As The Toast Schema Requires (Else Windows Shows "New
        // notification")
        string about = $"conversation={id}&message={Uri.EscapeDataString(n.Tag)}";
        var actions  = new XElement("actions");

        if (parts.HasFlag(ToastParts.Reply))
        {
            actions.Add(
                new XElement("input", new XAttribute("id", "reply"), new XAttribute("type", "text"), new XAttribute("placeHolderContent", ChatCopy.Reply)),
                new XElement("action",
                    new XAttribute("content", string.Empty), new XAttribute("hint-inputId", "reply"), new XAttribute("activationType", "foreground"),
                    new XAttribute("arguments", $"action=reply&{about}"), new XAttribute("imageUri", sendIconUri),
                    new XAttribute("hint-toolTip", ChatCopy.Send)));
        }

        if (parts.HasFlag(ToastParts.Snooze))
        {
            actions.Add(new XElement("action", new XAttribute("activationType", "foreground"), new XAttribute("arguments", $"action=snooze&{about}"), new XAttribute("content", ChatCopy.Snooze)));
        }

        if (parts.HasFlag(ToastParts.Dismiss))
        {
            actions.Add(new XElement("action", new XAttribute("activationType", "system"), new XAttribute("arguments", "dismiss"), new XAttribute("content", string.Empty)));
        }

        // No Buttons: No <actions> Element At All, Since An Empty One Isn't A Valid Toast
        var toast = new XElement("toast",
            new XAttribute("launch", $"action=open&conversation={id}"),
            new XElement("visual", binding),
            actions.HasElements ? actions : null);

        return toast.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Builds the toast that says a reply from a toast wasn't sent. Clicking it opens the conversation.
    /// </summary>
    /// <param name="conversationId">The conversation the reply was for.</param>
    /// <param name="title">The conversation name.</param>
    /// <returns>The toast XML.</returns>
    public static string BuildReplyFailed(string conversationId, string title)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(title);

        var toast = new XElement("toast",
            new XAttribute("launch", $"action=open&conversation={Uri.EscapeDataString(conversationId)}"),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", XmlSafe(title)), new XElement("text", ChatCopy.ReplyNotSent))));

        return toast.ToString(SaveOptions.DisableFormatting);
    }

    // Drops Characters XML Can't Hold, Keeping Valid Surrogate Pairs (Emoji)
    private static string XmlSafe(string s)
    {
        var kept = new StringBuilder(s.Length);

        for (int i = 0; i < s.Length; i++)
        {
            if (XmlConvert.IsXmlChar(s[i]))
            {
                kept.Append(s[i]);
            }
            else if (i + 1 < s.Length && XmlConvert.IsXmlSurrogatePair(s[i + 1], s[i]))
            {
                kept.Append(s, i, 2);
                i++;
            }
        }

        return kept.ToString();
    }
}

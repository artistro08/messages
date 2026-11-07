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
    /// Builds a message toast with the title, the body, an optional avatar, a reply box, Snooze and Dismiss.
    /// </summary>
    /// <remarks>
    /// Every value goes in through <see cref="XElement"/> or <see cref="XAttribute"/>, so the XML escapes markup in a
    /// message. The method removes the characters that XML cannot hold from the title and the body. Control characters
    /// and lone surrogates are examples.
    /// </remarks>
    /// <param name="n">The toast content.</param>
    /// <param name="avatarFileUri">A file URI for the avatar picture, or null for none.</param>
    /// <param name="sendIconUri">A file URI for the send button icon.</param>
    /// <returns>The toast XML.</returns>
    public static string Build(NotificationContent n, string? avatarFileUri, string sendIconUri)
    {
        ArgumentNullException.ThrowIfNull(n);

        string id = Uri.EscapeDataString(n.Group);

        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", XmlSafe(n.Title)), new XElement("text", XmlSafe(n.Body)));

        if (avatarFileUri is not null)
        {
            binding.Add(new XElement("image", new XAttribute("placement", "appLogoOverride"), new XAttribute("hint-crop", "circle"), new XAttribute("src", avatarFileUri)));
        }

        // The attribution text is the small line that Windows shows under the body.
        if (n.CodeCopied)
        {
            binding.Add(new XElement("text", new XAttribute("placement", "attribution"), ChatCopy.CodeCopied));
        }

        // The reply button belongs to the text input through hint-inputId and gets its icon from imageUri.
        // The app has its own Snooze, because the system Snooze cannot use the length setting without a picker.
        // The toast schema requires every <input> before the first <action>. Else Windows shows "New notification".
        string about = $"conversation={id}&message={Uri.EscapeDataString(n.Tag)}";
        var actions  = new XElement("actions",
            new XElement("input", new XAttribute("id", "reply"), new XAttribute("type", "text"), new XAttribute("placeHolderContent", ChatCopy.Reply)),
            new XElement("action",
                new XAttribute("content", string.Empty), new XAttribute("hint-inputId", "reply"), new XAttribute("activationType", "foreground"),
                new XAttribute("arguments", $"action=reply&{about}"), new XAttribute("imageUri", sendIconUri),
                new XAttribute("hint-toolTip", ChatCopy.Send)),
            new XElement("action", new XAttribute("activationType", "foreground"), new XAttribute("arguments", $"action=snooze&{about}"), new XAttribute("content", ChatCopy.Snooze)),
            new XElement("action", new XAttribute("activationType", "system"), new XAttribute("arguments", "dismiss"), new XAttribute("content", string.Empty)));

        var toast = new XElement("toast",
            new XAttribute("launch", $"action=open&conversation={id}"),
            new XElement("visual", binding),
            actions);

        return toast.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Builds the toast that says the app did not send a reply from a toast. A click on it opens the conversation.
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

    // Removes the characters that XML cannot hold. A valid surrogate pair, such as an emoji, stays.
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

using System.Xml.Linq;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Notifications;

/// <summary>
/// Builds the toast XML.
/// </summary>
public static class NotificationXml
{
    /// <summary>
    /// Builds the toast: title, body, optional avatar, reply box, snooze picker and dismiss.
    /// </summary>
    /// <remarks>Every value goes in through <see cref="XElement"/>/<see cref="XAttribute"/>, so markup in a message is escaped.</remarks>
    /// <param name="n">The toast content.</param>
    /// <param name="avatarFileUri">A file URI for the avatar picture, or null for none.</param>
    /// <param name="sendIconUri">A file URI for the send button icon.</param>
    /// <returns>The toast XML.</returns>
    public static string Build(NotificationContent n, string? avatarFileUri, string sendIconUri)
    {
        ArgumentNullException.ThrowIfNull(n);

        string id = Uri.EscapeDataString(n.Group);

        // Visual
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", n.Title), new XElement("text", n.Body));

        if (avatarFileUri is not null)
        {
            binding.Add(new XElement("image", new XAttribute("placement", "appLogoOverride"), new XAttribute("hint-crop", "circle"), new XAttribute("src", avatarFileUri)));
        }

        // Actions
        var snooze = new XElement("input",
            new XAttribute("id", "snoozeTime"), new XAttribute("type", "selection"), new XAttribute("defaultInput", "15"),
            Choice("5", ChatCopy.Minutes5), Choice("15", ChatCopy.Minutes15), Choice("60", ChatCopy.Hour1), Choice("240", ChatCopy.Hours4));

        var actions = new XElement("actions",
            new XElement("input", new XAttribute("id", "reply"), new XAttribute("type", "text"), new XAttribute("placeHolderContent", ChatCopy.Reply)),
            new XElement("action",
                new XAttribute("content", string.Empty), new XAttribute("hint-inputId", "reply"), new XAttribute("activationType", "foreground"),
                new XAttribute("arguments", $"action=reply&conversation={id}&message={Uri.EscapeDataString(n.Tag)}"), new XAttribute("imageUri", sendIconUri)),
            snooze,
            new XElement("action", new XAttribute("activationType", "system"), new XAttribute("arguments", "snooze"), new XAttribute("hint-inputId", "snoozeTime"), new XAttribute("content", string.Empty)),
            new XElement("action", new XAttribute("activationType", "system"), new XAttribute("arguments", "dismiss"), new XAttribute("content", string.Empty)));

        var toast = new XElement("toast",
            new XAttribute("launch", $"action=open&conversation={id}"),
            new XElement("visual", binding),
            actions);

        return toast.ToString(SaveOptions.DisableFormatting);
    }

    // One Snooze Choice
    private static XElement Choice(string id, string content) => new("selection", new XAttribute("id", id), new XAttribute("content", content));
}

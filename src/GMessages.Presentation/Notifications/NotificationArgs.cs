namespace GMessages.Presentation.Notifications;

/// <summary>
/// Holds the arguments that a toast click or a toast button sends to the app.
/// </summary>
/// <param name="Action">The action name, such as <c>open</c> or <c>reply</c>.</param>
/// <param name="ConversationId">The conversation of the toast.</param>
/// <param name="MessageId">The message of the toast, or null.</param>
public sealed record NotificationArgs(string Action, string ConversationId, string? MessageId)
{
    /// <summary>
    /// Reads a list of <c>key=value</c> pairs that <c>&amp;</c> separates. The values are URL-escaped.
    /// </summary>
    /// <param name="arguments">The launch arguments or the button arguments.</param>
    /// <returns>The arguments, or null when the action or the conversation is missing.</returns>
    public static NotificationArgs? Parse(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return null;
        }

        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string part in arguments.Split('&'))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);

            if (eq > 0)
            {
                pairs[part[..eq]] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        if (!pairs.TryGetValue("action", out string? action) || !pairs.TryGetValue("conversation", out string? conversation) || action.Length == 0 || conversation.Length == 0)
        {
            return null;
        }

        return new NotificationArgs(action, conversation, pairs.GetValueOrDefault("message"));
    }
}

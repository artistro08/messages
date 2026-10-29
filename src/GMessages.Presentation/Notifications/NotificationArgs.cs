namespace GMessages.Presentation.Notifications;

/// <summary>
/// What a toast click or button sends back to the app.
/// </summary>
/// <param name="Action">The action name (open, reply...).</param>
/// <param name="ConversationId">The conversation it is about.</param>
/// <param name="MessageId">The message it is about, or null.</param>
public sealed record NotificationArgs(string Action, string ConversationId, string? MessageId)
{
    /// <summary>
    /// Reads an <c>&amp;</c>-separated <c>key=value</c> list. Values are URL-escaped.
    /// </summary>
    /// <param name="arguments">The launch or button arguments.</param>
    /// <returns>The args, or null when action or conversation is missing.</returns>
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

namespace GMessages.Presentation.Notifications;

/// <summary>
/// Which buttons a message toast carries. A diagnostic switch: the probe that finds what keeps toasts on screen.
/// </summary>
[Flags]
public enum ToastParts
{
    /// <summary>No buttons.</summary>
    None = 0,

    /// <summary>The quick reply box and its send button.</summary>
    Reply = 1,

    /// <summary>The app's Snooze button.</summary>
    Snooze = 2,

    /// <summary>Windows' Dismiss button.</summary>
    Dismiss = 4,

    /// <summary>Every button: the toast as shipped.</summary>
    All = Reply | Snooze | Dismiss,
}

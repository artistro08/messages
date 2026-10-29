using GMessages.Presentation;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;
using GMessages.Presentation.Settings;
using GMessages.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;

namespace GMessages.App;

/// <summary>
/// Shows a Windows notification for each new incoming message, and handles the reply box and clicks on them.
/// </summary>
/// <remarks>
/// <para>
/// Built on the Windows App SDK's <see cref="AppNotificationManager"/>. Dismiss is Windows' own system button; the
/// reply, Snooze and the body click come back here. A reply is sent through the running app without opening the
/// window, then the conversation is marked read; a reply the phone doesn't take shows a second toast saying so. A body
/// click raises <see cref="OpenConversationRequested"/>.
/// </para>
/// <para>
/// Snooze is the app's own button, because its length is a setting (<see cref="AppSettings.SnoozeMinutes"/>) and
/// Windows' system snooze only takes a length from a picker on the toast. Windows takes the toast away on the click;
/// the app shows the same toast again once the length has passed, unless its chat was opened meanwhile. To show it
/// again it keeps the latest toasts' payloads, and the snoozed ones apart from them, in memory only (never on disk;
/// see <see cref="ToastMemory"/>).
/// </para>
/// <para>
/// Each toast's tag is the message ID and its group the conversation ID, so opening a chat clears its toasts with
/// <see cref="RemoveConversation"/>. A toast that can't be built or shown is logged (the error type only) and
/// dropped, so one bad message never takes the tray down. Message text and names are never logged.
/// </para>
/// </remarks>
/// <param name="store">Sends replies and marks them read; names a conversation for the failure toast.</param>
/// <param name="settings">The notification settings.</param>
/// <param name="avatars">Writes the sender's photo to disk for the toast; cleared after each use.</param>
/// <param name="mainView">The open main window's state, or null when it is closed.</param>
/// <param name="dispatcher">The UI thread, where clicks are handled.</param>
/// <param name="logger">The app log.</param>
/// <seealso href="https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart"/>
internal sealed class NotificationService(ChatStore store, AppSettings settings, AvatarCache avatars, Func<MainViewModel?> mainView, DispatcherQueue dispatcher, ILogger logger) : IDisposable
{
    // The Quick Reply Button's Icon: Packaged, Windows Picks The Send.scale-*.png That Fits The Display Scale; Unpackaged
    // (Debug), Only A File Path Works, So It Is The 100% One
    private static readonly string SendIconUri = AppIdentity.IsPackaged
        ? "ms-appx:///Assets/Send.png"
        : new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Send.scale-100.png")).AbsoluteUri;

    // How Many Recent Toasts Can Still Be Snoozed (Their Payloads Kept In Memory)
    private const int SnoozableToasts = 20;

    private readonly AppNotificationManager _manager = AppNotificationManager.Default;
    private readonly ToastMemory _toasts = new(SnoozableToasts);
    private bool _registered;

    /// <summary>
    /// Asks the App to show the main window on a conversation (a toast body click). Carries the conversation ID.
    /// </summary>
    public event EventHandler<string>? OpenConversationRequested;

    /// <summary>
    /// Starts receiving toast clicks and replies.
    /// </summary>
    /// <remarks>
    /// The handler goes on before <see cref="AppNotificationManager.Register()"/>, as the Windows App SDK requires, so
    /// clicks come to this process instead of starting a new one.
    /// </remarks>
    public void Register()
    {
        _manager.NotificationInvoked += OnNotificationInvoked;
        store.ConversationChanged    += OnConversationChanged;
        try
        {
            _manager.Register();
            _registered = true;
            AppLog.NotificationsRegistered(logger);
        }
#pragma warning disable CA1031 // No notifications is better than no tray app; the failure is logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.NotificationFailed(logger, "register", ex.GetType().Name, ex.HResult);
        }
    }

    /// <summary>
    /// Shows a toast for an arrived message when <see cref="NotificationPolicy"/> says it should notify.
    /// </summary>
    /// <param name="e">The arrived message.</param>
    /// <returns>A task that completes once the toast was shown or skipped. It never throws.</returns>
    public async Task ShowAsync(MessageArrivedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        MainViewModel? view = mainView();
        if (!NotificationPolicy.ShouldNotify(e, settings.NotificationsEnabled, view?.SelectedConversationId, view?.IsWindowFocused == true, settings.NotifyForViewedChat))
        {
            return;
        }

        try
        {
            // Content And The Sender's Photo
            var content    = NotificationContent.Compose(e.Message, e.Conversation!);
            string? avatar = content.AvatarParticipantId is { Length: > 0 } participant ? await avatars.GetFileUriAsync(participant, CancellationToken.None) : null;
            avatars.Clear();

            string xml = NotificationXml.Build(content, avatar, SendIconUri);
            _toasts.Remember(new ShownToast(content.Tag, content.Group, xml));
            Show(xml, content.Tag, content.Group);
        }
#pragma warning disable CA1031 // A message that can't become a toast must not take the tray down; the failure is logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.NotificationFailed(logger, "show", ex.GetType().Name, ex.HResult);
        }
    }

    /// <summary>
    /// Removes every toast of a conversation (it was opened, so they're read).
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    public void RemoveConversation(string conversationId)
    {
        // A Snoozed Toast Of This Chat Stays Away Too
        _toasts.RemoveConversation(conversationId);
        _ = RunAsync("remove", () => _manager.RemoveByGroupAsync(conversationId).AsTask());
    }

    /// <summary>
    /// Removes every toast this app shows. UI test runs call it on quit, so toasts never leak between tests.
    /// </summary>
    /// <returns>A task that completes once they're gone. It never throws.</returns>
    public Task RemoveAllAsync()
    {
        _toasts.Clear();
        return RunAsync("remove all", () => _manager.RemoveAllAsync().AsTask());
    }

    /// <summary>
    /// Handles a toast click or button: <c>reply</c> sends the reply box's text, <c>snooze</c> shows the toast again
    /// after the snooze length, <c>open</c> raises <see cref="OpenConversationRequested"/>. Anything else is ignored.
    /// </summary>
    /// <remarks>Called on the UI thread, also by the App for a click that started it.</remarks>
    /// <param name="args">The click's arguments and the reply box's text.</param>
    /// <returns>A task that completes once handled. It never throws.</returns>
    public Task HandleAsync(AppNotificationActivatedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        NotificationArgs? parsed = NotificationArgs.Parse(args.Argument);
        AppLog.NotificationInvoked(logger, parsed?.Action ?? "unknown");
        switch (parsed?.Action)
        {
            case "reply":
                return ReplyAsync(parsed, args.UserInput.TryGetValue("reply", out string? text) ? text : null);

            case "snooze":
                return SnoozeAsync(parsed);

            case "open":
                OpenConversationRequested?.Invoke(this, parsed.ConversationId);
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the toast that says a reply wasn't sent, titled with the conversation's name.
    /// </summary>
    /// <param name="conversationId">The conversation the reply was for.</param>
    public void ShowReplyFailed(string conversationId)
    {
        try
        {
            string title = store.Find(conversationId)?.Name ?? AppInfo.DisplayName;
            Show(NotificationXml.BuildReplyFailed(conversationId, title), "reply-failed", conversationId);
        }
#pragma warning disable CA1031 // The tray must survive a toast that can't be shown; the failure is logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.NotificationFailed(logger, "show reply failed", ex.GetType().Name, ex.HResult);
        }
    }

    /// <summary>
    /// Stops receiving clicks. Later clicks start the app again.
    /// </summary>
    public void Dispose()
    {
        _manager.NotificationInvoked -= OnNotificationInvoked;
        store.ConversationChanged    -= OnConversationChanged;
        if (_registered)
        {
            _manager.Unregister();
            _registered = false;
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // A Conversation Read Anywhere (Here, A Toast Reply Or The Phone) Takes Its Toasts With It; See NotificationPolicy.ShouldDismissOnRead
    private void OnConversationChanged(object? sender, ConversationInfo conversation) => dispatcher.TryEnqueue(() =>
    {
        MainViewModel? view = mainView();
        if (NotificationPolicy.ShouldDismissOnRead(conversation, view?.SelectedConversationId, view?.IsWindowFocused == true, settings.NotifyForViewedChat))
        {
            RemoveConversation(conversation.Id);
        }
    });

    // Raised On A Background Thread
    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        dispatcher.TryEnqueue(() => _ = HandleAsync(args));

    // Sends The Reply, Then Marks The Conversation Read; Only A Send That Failed Gets The Failure Toast
    private async Task ReplyAsync(NotificationArgs args, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await store.SendTextAsync(args.ConversationId, text, GmClient.NewTmpId(), CancellationToken.None);
        }
#pragma warning disable CA1031 // Any send failure (not connected, phone refused, timeout) gets the same toast
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.NotificationFailed(logger, "reply", ex.GetType().Name, ex.HResult);
            ShowReplyFailed(args.ConversationId);
            return;
        }

        if (args.MessageId is null)
        {
            return;
        }

        await RunAsync("mark read", () => store.MarkReadAsync(args.ConversationId, args.MessageId, CancellationToken.None));
    }

    // Shows The Toast Again Once The Snooze Length Has Passed, Unless Its Chat Was Opened (Or The Toasts Cleared) Meanwhile
    // ponytail: the wait is an in-memory timer, so a snooze pending when the app quits is lost; a scheduled toast
    // (ScheduledToastNotification) would survive a quit if that ever matters
    private async Task SnoozeAsync(NotificationArgs args)
    {
        if (_toasts.Snooze(args.MessageId, args.ConversationId) is not { } toast)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMinutes(settings.SnoozeMinutes));
        if (_toasts.Wake(toast))
        {
            Show(toast.Xml, toast.Tag, toast.Group);
        }
    }

    private void Show(string xml, string tag, string group) => _manager.Show(new AppNotification(xml) { Tag = tag, Group = group });

    // Runs A Call And Logs Its Failure, So No Task Goes Unobserved
    private async Task RunAsync(string action, Func<Task> call)
    {
        try
        {
            await call();
        }
#pragma warning disable CA1031 // The tray must survive it; the failure is logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AppLog.NotificationFailed(logger, action, ex.GetType().Name, ex.HResult);
        }
    }
}

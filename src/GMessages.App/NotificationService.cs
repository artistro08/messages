using GMessages.Presentation;
using GMessages.Presentation.Main;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;
using GMessages.Presentation.Settings;
using GMessages.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Windows.System;

namespace GMessages.App;

/// <summary>
/// Shows a toast for each new incoming message, and handles the reply box and the clicks on the toasts.
/// </summary>
/// <remarks>
/// <para>
/// The service uses the <see cref="AppNotificationManager"/> of the Windows App SDK. Dismiss is the system button of
/// Windows. The reply, Snooze and the body click come back to this service. The running app sends a reply and does
/// not open the window, then marks the conversation as read. When the phone does not take a reply, a second toast
/// tells the person. A body click raises <see cref="OpenConversationRequested"/>.
/// </para>
/// <para>
/// Snooze is a button of the app, because its length is a setting (<see cref="AppSettings.SnoozeMinutes"/>). The
/// system snooze of Windows takes its length only from a picker on the toast. Windows removes the toast on the click.
/// The app shows the same toast again after the length, unless the person opened its chat during the wait. For that,
/// the app keeps the payloads of the latest and the snoozed toasts in memory only, never on disk. See
/// <see cref="ToastMemory"/>.
/// </para>
/// <para>
/// The tag of each toast is the message ID, and its group is the conversation ID. Thus a chat that opens clears its
/// toasts through <see cref="RemoveConversation"/>. The app logs the error type of a toast that it cannot build or
/// show, then drops the toast. Thus a bad message never stops the tray. The log never holds message text or names.
/// </para>
/// </remarks>
/// <param name="store">Sends replies and marks them as read. It also names the conversation of a failure toast.</param>
/// <param name="settings">The notification settings.</param>
/// <param name="avatars">Writes the photo of the sender to disk for the toast. The service clears it after use.</param>
/// <param name="mainView">Returns the state of the open main window, or null when the window is closed.</param>
/// <param name="dispatcher">The dispatcher of the UI thread, where the service handles the clicks.</param>
/// <param name="logger">The app log.</param>
/// <seealso href="https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart"/>
internal sealed class NotificationService(ChatStore store, AppSettings settings, AvatarCache avatars, Func<MainViewModel?> mainView, DispatcherQueue dispatcher, ILogger logger) : IDisposable
{
    // The icon of the quick reply button. Packaged, Windows picks the Send.scale-*.png that fits the display scale.
    // Unpackaged, as in Debug, only a file path works, so the app uses the 100% file.
    private static readonly string SendIconUri = AppIdentity.IsPackaged
        ? "ms-appx:///Assets/Send.png"
        : new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Send.scale-100.png")).AbsoluteUri;

    // The number of recent toasts that the person can still snooze. The app keeps their payloads in memory.
    private const int SnoozableToasts = 20;

    private readonly AppNotificationManager _manager = AppNotificationManager.Default;
    private readonly ToastMemory _toasts = new(SnoozableToasts);
    private bool _registered;

    /// <summary>
    /// Asks the app to show the main window on a conversation after a toast body click. The argument is the
    /// conversation ID.
    /// </summary>
    public event EventHandler<string>? OpenConversationRequested;

    /// <summary>
    /// Starts to receive toast clicks and replies.
    /// </summary>
    /// <remarks>
    /// The handler goes on before <see cref="AppNotificationManager.Register()"/>, as the Windows App SDK requires.
    /// Thus clicks come to this process, and Windows starts no new process.
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
        catch (Exception ex)
        {
            // A tray app with no notifications is better than no tray app. The log holds the failure.
            AppLog.NotificationFailed(logger, "register", ex.GetType().Name, ex.HResult);
        }
    }

    /// <summary>
    /// Shows a toast for a new message when <see cref="NotificationPolicy"/> allows it, once per message. A later
    /// update of a message with a toast shows nothing (see <see cref="ToastMemory.FirstShow"/>).
    /// </summary>
    /// <param name="e">The new message.</param>
    /// <returns>A task that completes when the toast shows or the service skips it. It never throws.</returns>
    public async Task ShowAsync(MessageArrivedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        MainViewModel? view = mainView();
        if (!NotificationPolicy.ShouldNotify(e, settings.NotificationsEnabled, view?.SelectedConversationId, view?.IsWindowFocused == true, settings.NotifyForViewedChat))
        {
            return;
        }

        // A message gets a single toast. The service claims the toast before any await. Thus a later update, such as a
        // status change or a reaction, never shows the banner again. That also holds during the show.
        if (!_toasts.FirstShow(e.Message.Id))
        {
            return;
        }

        try
        {
            // The app copies the code of each message with a toast (CopyCode), so the toast can tell the person.
            var content    = NotificationContent.Compose(e.Message, e.Conversation!) with { CodeCopied = settings.CopyCodes && OneTimeCode.Find(e.Message.Text) is not null };
            string? avatar = content.AvatarParticipantId is { Length: > 0 } participant ? await avatars.GetFileUriAsync(participant, CancellationToken.None) : null;
            avatars.Clear();

            string xml = NotificationXml.Build(content, avatar, SendIconUri);
            _toasts.Remember(new ShownToast(content.Tag, content.Group, xml));
            Show(xml, content.Tag, content.Group);
            AppLog.ToastShown(logger, content.Tag, content.Group);
        }
        catch (Exception ex)
        {
            // A message that cannot become a toast must not stop the tray. The log holds the failure.
            AppLog.NotificationFailed(logger, "show", ex.GetType().Name, ex.HResult);
        }
    }

    /// <summary>
    /// Removes every toast of a conversation and cancels its waiting snoozes. The app calls it when the person reads or
    /// mutes the conversation, or when the conversation leaves the main list.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    public void RemoveConversation(string conversationId)
    {
        // A snoozed toast of this chat does not come back.
        _toasts.RemoveConversation(conversationId);
        _ = RunAsync("remove", () => _manager.RemoveByGroupAsync(conversationId).AsTask());
    }

    /// <summary>
    /// Removes every toast of this app. A sign-out calls it, and UI test runs call it on quit, so no toast leaks
    /// between tests.
    /// </summary>
    /// <returns>A task that completes when the toasts are gone. It never throws.</returns>
    public Task RemoveAllAsync()
    {
        _toasts.Clear();
        return RunAsync("remove all", () => _manager.RemoveAllAsync().AsTask());
    }

    /// <summary>
    /// Handles a toast click or button.
    /// </summary>
    /// <remarks>
    /// <c>reply</c> sends the text of the reply box, and <c>snooze</c> shows the toast again after the snooze length.
    /// <c>open</c> raises <see cref="OpenConversationRequested"/>. Other actions do nothing. The UI thread calls this
    /// method. The app also calls it for a click that started the app.
    /// </remarks>
    /// <param name="args">The arguments of the click and the text of the reply box.</param>
    /// <returns>A task that completes when the service handled the click. It never throws.</returns>
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
    /// Shows the toast that tells the person that a reply did not go out. The title is the name of the conversation.
    /// </summary>
    /// <param name="conversationId">The conversation of the reply.</param>
    public void ShowReplyFailed(string conversationId)
    {
        try
        {
            string title = store.Find(conversationId)?.Name ?? AppInfo.DisplayName;
            Show(NotificationXml.BuildReplyFailed(conversationId, title), "reply-failed", conversationId);
        }
        catch (Exception ex)
        {
            // The tray must survive a toast that cannot show. The log holds the failure.
            AppLog.NotificationFailed(logger, "show reply failed", ex.GetType().Name, ex.HResult);
        }
    }

    /// <summary>
    /// Stops the receipt of clicks. A later click starts the app again.
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

    // A conversation that the person reads, mutes or moves out of the main list loses its toasts and waiting snoozes.
    // The change can come from this app, a toast reply or the phone. See NotificationPolicy.ShouldDismissOnLeave and
    // ShouldDismissOnRead.
    private void OnConversationChanged(object? sender, ConversationInfo conversation) => dispatcher.TryEnqueue(() =>
    {
        MainViewModel? view = mainView();
        if (NotificationPolicy.ShouldDismissOnLeave(conversation)
            || NotificationPolicy.ShouldDismissOnRead(conversation, view?.SelectedConversationId, view?.IsWindowFocused == true, settings.NotifyForViewedChat))
        {
            RemoveConversation(conversation.Id);
        }
    });

    // Windows raises the event on a background thread.
    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        dispatcher.TryEnqueue(() => _ = HandleAsync(args));

    // Sends the reply, then marks the conversation as read. Only a failed send gets the failure toast.
    private async Task ReplyAsync(NotificationArgs args, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await store.SendTextAsync(args.ConversationId, text, GmClient.NewTmpId(), null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Each send failure gets the same toast: no connection, a refusal of the phone or a timeout.
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

    // Shows the toast again after the snooze length. The toast does not come back when the person opens its chat or the
    // toasts clear during the wait.
    // ponytail: the wait is a timer in memory, so a quit loses a pending snooze. A ScheduledToastNotification can
    // survive a quit, if that ever matters.
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
            AppLog.ToastShown(logger, toast.Tag, toast.Group);
        }
    }

    private void Show(string xml, string tag, string group) => _manager.Show(new AppNotification(xml) { Tag = tag, Group = group });

    // Runs a call and logs its failure, so no task goes unobserved.
    private async Task RunAsync(string action, Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception ex)
        {
            // The tray must survive the failure. The log holds it.
            AppLog.NotificationFailed(logger, action, ex.GetType().Name, ex.HResult);
        }
    }
}

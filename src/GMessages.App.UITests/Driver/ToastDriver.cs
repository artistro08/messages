using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace GMessages.App.UITests.Driver;

/// <summary>
/// Finds the app's Windows notifications (toasts) through UI Automation, reads them, types replies and clicks them.
/// </summary>
/// <remarks>
/// Windows shows each toast as a top-level window named <c>New notification</c>, owned by the shell, on the desktop
/// root. Toasts of other apps can be on screen too, so a toast counts only when its texts include the expected title.
/// A failed wait lists the texts of every toast (<see cref="DescribeAll"/>). This shows a missing toast or a wrong one.
/// The content of a new toast slides in, and Windows drops a reply or click made during the slide.
/// <see cref="Reply"/> and <see cref="ClickBody"/> first wait for the part they use to stop moving.
/// </remarks>
public sealed class ToastDriver
{
    /// <summary>The name Windows gives each toast window.</summary>
    public const string ToastWindowName = "New notification";

    private readonly UIA3Automation _automation;

    /// <summary>
    /// Creates a driver that searches with <paramref name="automation"/>.
    /// </summary>
    /// <param name="automation">The UI Automation client.</param>
    public ToastDriver(UIA3Automation automation) => _automation = automation;

    /// <summary>
    /// Waits for a toast whose texts include every one of <paramref name="texts"/>.
    /// </summary>
    /// <param name="timeout">How long to look.</param>
    /// <param name="texts">The texts the toast must show, such as its title and body.</param>
    /// <returns>The toast window.</returns>
    /// <exception cref="AssertFailedException">No such toast shows.</exception>
    public AutomationElement WaitFor(TimeSpan timeout, params string[] texts)
    {
        AutomationElement? toast = null;
        Wait.Until(() => (toast = TryFind(texts)) is not null, timeout, () => $"No toast showed with [{string.Join(" | ", texts)}]. Toasts on screen:{Environment.NewLine}{DescribeAll()}");
        return toast!;
    }

    /// <summary>
    /// Finds a toast whose texts include every one of <paramref name="texts"/>, without waiting.
    /// </summary>
    /// <param name="texts">The texts the toast must show.</param>
    /// <returns>The toast window, or null when no toast matches.</returns>
    public AutomationElement? TryFind(params string[] texts) => Toasts().FirstOrDefault(t => texts.All(Texts(t).Contains));

    /// <summary>
    /// Lists every toast on screen, one per line, as the texts of the toast in order.
    /// </summary>
    /// <returns>The lines, or <c>(none)</c> when no toast shows.</returns>
    public string DescribeAll()
    {
        string[] lines = [.. Toasts().Select(t => "[" + string.Join(" | ", Texts(t)) + "]")];
        return lines.Length > 0 ? string.Join(Environment.NewLine, lines) : "(none)";
    }

    /// <summary>
    /// Checks that no toast with <paramref name="text"/> appears during the whole of <paramref name="period"/>.
    /// </summary>
    /// <param name="text">A text the toast would show, such as its title.</param>
    /// <param name="period">How long to watch.</param>
    /// <exception cref="AssertFailedException">A matching toast shows.</exception>
    public void AssertNone(string text, TimeSpan period)
    {
        DateTime until = DateTime.UtcNow + period;
        while (DateTime.UtcNow < until)
        {
            if (TryFind(text) is { } toast)
            {
                Assert.Fail($"A toast showed: [{string.Join(" | ", Texts(toast))}].");
            }

            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Reads the texts of a toast, in order.
    /// </summary>
    /// <param name="toast">The toast window.</param>
    /// <returns>The texts.</returns>
    public static IReadOnlyList<string> Texts(AutomationElement toast)
    {
        ArgumentNullException.ThrowIfNull(toast);

        var texts = new List<string>();
        AppDriver.Search(toast, e =>
        {
            if (e.ControlType == ControlType.Text && AppDriver.SafeName(e) is { Length: > 0 } name)
            {
                texts.Add(name);
            }

            return false;
        });
        return texts;
    }

    /// <summary>
    /// Types into the reply box of a toast and presses the send button.
    /// </summary>
    /// <param name="toast">The toast window.</param>
    /// <param name="text">The reply.</param>
    public static void Reply(AutomationElement toast, string text)
    {
        ArgumentNullException.ThrowIfNull(toast);

        AutomationElement box = AppDriver.Search(toast, e => e.ControlType == ControlType.Edit)
            ?? throw new AssertFailedException($"The toast has no reply box. {Describe(toast)}");
        WaitUntilStill(box);
        box.Patterns.Value.Pattern.SetValue(text);

        // The quick reply button next to the box has no name in UI Automation. Only its AutomationId identifies it.
        AutomationElement send = AppDriver.Search(toast, e => e.ControlType == ControlType.Button && e.IsEnabled && e.AutomationId == SendButtonId)
            ?? throw new AssertFailedException($"The toast has no send button ('{SendButtonId}'). {Describe(toast)}");
        send.Patterns.Invoke.Pattern.Invoke();
    }

    /// <summary>
    /// Clicks the body of a toast, not one of its buttons.
    /// </summary>
    /// <param name="toast">The toast window.</param>
    public static void ClickBody(AutomationElement toast)
    {
        ArgumentNullException.ThrowIfNull(toast);

        // Click the title. The center of the toast can land on the reply box.
        AutomationElement title = AppDriver.Search(toast, e => e.ControlType == ControlType.Text && e.AutomationId == "Title")
            ?? throw new AssertFailedException($"The toast has no title. {Describe(toast)}");
        WaitUntilStill(title);
        title.Click();
    }

    /// <summary>
    /// Presses the Dismiss button of a toast to clear its banner.
    /// </summary>
    /// <remarks>
    /// Windows shows one banner at a time and queues the next banner behind it. On an unattended machine a banner never
    /// leaves by itself (see <c>ManualNotificationTests</c>). A test that waits for a second toast dismisses the first.
    /// </remarks>
    /// <param name="toast">The toast window.</param>
    public static void Dismiss(AutomationElement toast)
    {
        ArgumentNullException.ThrowIfNull(toast);

        AutomationElement dismiss = AppDriver.Search(toast, e => e.ControlType == ControlType.Button && AppDriver.SafeName(e) == "Dismiss")
            ?? throw new AssertFailedException($"The toast has no Dismiss button. {Describe(toast)}");
        WaitUntilStill(dismiss);
        dismiss.Patterns.Invoke.Pattern.Invoke();
    }

    /// <summary>
    /// Lists the elements of a toast (type, AutomationId, name) for a failure message.
    /// </summary>
    /// <param name="toast">The toast window.</param>
    /// <returns>The description.</returns>
    public static string Describe(AutomationElement toast)
    {
        ArgumentNullException.ThrowIfNull(toast);

        var parts = new List<string>();
        AppDriver.Search(toast, e =>
        {
            parts.Add($"{e.ControlType}#{e.Properties.AutomationId.ValueOrDefault}'{AppDriver.SafeName(e)}'[{e.Properties.ClassName.ValueOrDefault}]{(e.Patterns.Invoke.IsSupported ? "+Invoke" : "")}");
            return false;
        });
        return "Toast elements: " + string.Join(", ", parts);
    }

    // AutomationId of the send button that Windows puts next to the reply box.
    private const string SendButtonId = "BuddyButton";

    // A part must keep its bounds this long to count as done sliding in. The slide takes about 100 ms.
    private static readonly TimeSpan StillTime = TimeSpan.FromMilliseconds(500);

    // The entrance slide is over when the part keeps the same bounds for StillTime.
    private static void WaitUntilStill(AutomationElement part)
    {
        System.Drawing.Rectangle bounds = part.BoundingRectangle;
        DateTime stillSince             = DateTime.UtcNow;
        Wait.Until(() =>
        {
            System.Drawing.Rectangle now = part.BoundingRectangle;
            if (now != bounds)
            {
                bounds      = now;
                stillSince  = DateTime.UtcNow;
            }

            return DateTime.UtcNow - stillSince >= StillTime;
        }, AppDriver.DefaultTimeout, () => $"The toast never stopped moving; last at {bounds}.");
    }

    private AutomationElement[] Toasts()
    {
        try
        {
            return _automation.GetDesktop().FindAllChildren(cf => cf.ByName(ToastWindowName));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException or TimeoutException)
        {
            return [];
        }
    }
}

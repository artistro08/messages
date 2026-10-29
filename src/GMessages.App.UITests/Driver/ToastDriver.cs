using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace GMessages.App.UITests.Driver;

/// <summary>
/// Finds the app's Windows notifications (toasts) through UI Automation, reads them, types replies and clicks them.
/// </summary>
/// <remarks>
/// Windows shows each toast as a top-level window named <c>New notification</c>, owned by the shell, on the desktop
/// root. Toasts of other apps may be on screen too, so a toast only counts when its texts include the expected title;
/// nothing is read out of the others.
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
    /// Says whether Windows notifications are turned off for every app (Settings &gt; System &gt; Notifications).
    /// </summary>
    /// <returns>True when no toast can show.</returns>
    public static bool ToastsDisabled()
    {
        using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
        return settings?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") is int enabled && enabled == 0;
    }

    /// <summary>
    /// Waits for a toast whose texts include every one of <paramref name="texts"/>.
    /// </summary>
    /// <param name="timeout">How long to look.</param>
    /// <param name="texts">The texts it must show, such as its title and body.</param>
    /// <returns>The toast window.</returns>
    /// <exception cref="AssertFailedException">No such toast showed.</exception>
    public AutomationElement WaitFor(TimeSpan timeout, params string[] texts)
    {
        AutomationElement? toast = null;
        Wait.Until(() => (toast = TryFind(texts)) is not null, timeout, () => $"No toast showed with [{string.Join(" | ", texts)}].");
        return toast!;
    }

    /// <summary>
    /// Finds a toast whose texts include every one of <paramref name="texts"/>, without waiting.
    /// </summary>
    /// <param name="texts">The texts it must show.</param>
    /// <returns>The toast window, or null.</returns>
    public AutomationElement? TryFind(params string[] texts) => Toasts().FirstOrDefault(t => texts.All(Texts(t).Contains));

    /// <summary>
    /// Says that no toast showing <paramref name="text"/> appears for the whole of <paramref name="period"/>.
    /// </summary>
    /// <param name="text">A text the toast would show, such as its title.</param>
    /// <param name="period">How long to watch.</param>
    /// <exception cref="AssertFailedException">One showed.</exception>
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
    /// Reads a toast's texts, in order.
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
    /// Types into a toast's reply box and presses its send button.
    /// </summary>
    /// <param name="toast">The toast window.</param>
    /// <param name="text">The reply.</param>
    public static void Reply(AutomationElement toast, string text)
    {
        ArgumentNullException.ThrowIfNull(toast);

        AutomationElement box = AppDriver.Search(toast, e => e.ControlType == ControlType.Edit)
            ?? throw new AssertFailedException($"The toast has no reply box. {Describe(toast)}");
        box.Patterns.Value.Pattern.SetValue(text);

        AutomationElement send = AppDriver.Search(toast, e => e.ControlType == ControlType.Button && e.IsEnabled && AppDriver.SafeName(e) == SendButtonName)
            ?? throw new AssertFailedException($"The toast has no '{SendButtonName}' button. {Describe(toast)}");
        send.Patterns.Invoke.Pattern.Invoke();
    }

    /// <summary>
    /// Clicks a toast's body (not one of its buttons).
    /// </summary>
    /// <param name="toast">The toast window.</param>
    public static void ClickBody(AutomationElement toast)
    {
        ArgumentNullException.ThrowIfNull(toast);

        toast.Click();
    }

    /// <summary>
    /// Lists a toast's elements (type, AutomationId, name) for a failure message.
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

    // The Send Button Is The Reply Box's Button; Its Name Comes From Windows
    private const string SendButtonName = "Send";

    // Every Toast Window On The Desktop Root
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

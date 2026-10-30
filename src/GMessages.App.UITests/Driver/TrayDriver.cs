using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using GMessages.Presentation;

namespace GMessages.App.UITests.Driver;

/// <summary>
/// Finds the app's notification-area icon through UI Automation and clicks it like a person would.
/// </summary>
/// <remarks>
/// Windows 11 shows tray icons as buttons named after their tooltip (<see cref="AppInfo.DisplayName"/>) with the
/// AutomationId <c>NotifyItemIcon</c>. A pinned icon sits in the taskbar (<c>Shell_TrayWnd</c>); a hidden one sits
/// in the overflow window (<c>TopLevelWindowForOverflowXamlIsland</c>), which only exists while "Show Hidden Icons"
/// has it open. The search looks in the taskbar first, then opens the overflow and looks there. A right-click must be
/// a real mouse right-click, since the shell only sends <c>WM_CONTEXTMENU</c> for real input.
/// </remarks>
public sealed class TrayDriver
{
    private const string TaskbarClass   = "Shell_TrayWnd";
    private const string OverflowClass  = "TopLevelWindowForOverflowXamlIsland";
    private const string IconId         = "NotifyItemIcon";
    private const string ChevronName    = "Show Hidden Icons";

    private readonly UIA3Automation _automation;

    /// <summary>
    /// Creates a driver that searches with <paramref name="automation"/>.
    /// </summary>
    /// <param name="automation">The UI Automation client.</param>
    public TrayDriver(UIA3Automation automation) => _automation = automation;

    /// <summary>
    /// Waits for the app's tray icon, opening the overflow when it isn't pinned to the taskbar.
    /// </summary>
    /// <param name="timeout">How long to look.</param>
    /// <returns>The icon's button.</returns>
    /// <exception cref="AssertFailedException">The icon wasn't found in the taskbar or the overflow.</exception>
    public AutomationElement FindIcon(TimeSpan timeout)
    {
        AutomationElement? icon = null;
        string seen             = "";
        Wait.Until(() => (icon = TryFindIcon(out seen)) is not null, timeout,
            () => $"No tray icon named '{AppInfo.DisplayName}'. Looked in the taskbar ({TaskbarClass}) and the overflow ({OverflowClass}, opened with '{ChevronName}'). Icons seen: [{seen}].");
        return icon!;
    }

    /// <summary>
    /// Waits until the app's tray icon is gone from both the taskbar and the overflow.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <exception cref="AssertFailedException">The icon was still there.</exception>
    public void WaitForNoIcon(TimeSpan timeout)
    {
        Wait.Until(() => TryFindIcon(out _) is null, timeout, () => $"The '{AppInfo.DisplayName}' tray icon is still there.");
        CloseOverflow();
    }

    /// <summary>
    /// Left-clicks the icon (Invoke when the shell offers it, else a real mouse click).
    /// </summary>
    public void LeftClick()
    {
        AutomationElement icon = FindIcon(AppDriver.DefaultTimeout);
        if (icon.Patterns.Invoke.PatternOrDefault is { } invoke)
        {
            invoke.Invoke();
        }
        else
        {
            Mouse.Click(icon.GetClickablePoint(), MouseButton.Left);
        }

        CloseOverflow();
    }

    /// <summary>
    /// Right-clicks the icon with the real mouse, which opens the app's tray menu.
    /// </summary>
    /// <remarks>
    /// Another app's notification can sit over the icon (with the taskbar at the top, toasts show over the overflow), and
    /// a reminder stays until it's answered. Then the click would land on the notification, so the icon gets the
    /// keyboard's context menu key instead, which sends the app the same menu request.
    /// </remarks>
    public void RightClick()
    {
        AutomationElement icon          = FindIcon(AppDriver.DefaultTimeout);
        System.Drawing.Rectangle bounds = icon.BoundingRectangle;
        var center                      = new System.Drawing.Point(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));

        // Covered: The Context Menu Key On The Focused Icon
        if (!icon.Equals(_automation.FromPoint(center)))
        {
            icon.Focus();
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.APPS);
            return;
        }

        Mouse.Click(center, MouseButton.Right);
    }

    /// <summary>
    /// Closes the overflow window if it is open, so the desktop is left as it was.
    /// </summary>
    public void CloseOverflow()
    {
        if (FindTopLevel(OverflowClass) is null || Chevron()?.Patterns.Invoke.PatternOrDefault is not { } toggle)
        {
            return;
        }

        // The Chevron Toggles: Check Again Right Before Pressing, So A Just-Closed Overflow Isn't Reopened
        if (FindTopLevel(OverflowClass) is null)
        {
            return;
        }

        toggle.Invoke();
        Wait.Until(() => FindTopLevel(OverflowClass) is null, TimeSpan.FromSeconds(5), () => $"The overflow ({OverflowClass}) did not close.");
    }

    // =========================================================================
    // SEARCH
    // =========================================================================

    // Taskbar First, Then The Overflow (Opened If Needed); More Than One Match Is An Error, Never A Guess
    private AutomationElement? TryFindIcon(out string seen)
    {
        var names = new List<string>();
        foreach (Func<AutomationElement?> area in new Func<AutomationElement?>[] { () => FindTopLevel(TaskbarClass), OpenOverflow })
        {
            AutomationElement? root = area();
            if (root is null)
            {
                continue;
            }

            AutomationElement[] icons;
            try
            {
                icons = root.FindAllDescendants(cf => cf.ByAutomationId(IconId));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                continue;
            }

            string[] icon_names = [.. icons.Select(AppDriver.SafeName)];
            names.AddRange(icon_names);
            AutomationElement[] matches = [.. icons.Where((_, i) => icon_names[i] == AppInfo.DisplayName)];
            if (matches.Length > 1)
            {
                Assert.Fail($"{matches.Length} tray icons are named '{AppInfo.DisplayName}'. Quit any other copy of the app before running the UI tests.");
            }

            // A Just-Opened Overflow Lays Out After Its Buttons Exist; Until Then The Icon Has An Empty Rectangle To Click
            if (matches.Length == 1 && matches[0].BoundingRectangle.IsEmpty)
            {
                seen = string.Join(", ", names);
                return null;
            }

            if (matches.Length == 1)
            {
                seen = string.Join(", ", names);
                return matches[0];
            }
        }

        seen = string.Join(", ", names);
        return null;
    }

    // Opens "Show Hidden Icons" Unless The Overflow Is Already Open
    private AutomationElement? OpenOverflow()
    {
        if (FindTopLevel(OverflowClass) is { } open)
        {
            return open;
        }

        if (Chevron()?.Patterns.Invoke.PatternOrDefault is not { } invoke)
        {
            return null;
        }

        invoke.Invoke();
        AutomationElement? overflow = null;
        Wait.Until(() => (overflow = FindTopLevel(OverflowClass)) is not null, TimeSpan.FromSeconds(5),
            () => $"'{ChevronName}' did not open the overflow window ({OverflowClass}).");
        return overflow;
    }

    private AutomationElement? Chevron() =>
        FindTopLevel(TaskbarClass)?.FindFirstDescendant(cf => cf.ByName(ChevronName));

    private AutomationElement? FindTopLevel(string className)
    {
        try
        {
            return _automation.GetDesktop().FindFirstChild(cf => cf.ByClassName(className));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

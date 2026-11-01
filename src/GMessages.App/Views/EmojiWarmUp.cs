using System.Diagnostics;
using GMessages.Presentation.Messaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GMessages.App.Views;

/// <summary>
/// Draws the emoji picker's emoji once in the background, so the picker opens fully drawn.
/// </summary>
/// <remarks>
/// <para>
/// The first time Windows draws a color emoji costs about 12 ms (Plan 7, 46fbf4d); once drawn, the glyph is kept for the
/// rest of the run. A few seconds after the chats show, a nearly clear line of text in the window's live tree (it must
/// really draw, so it is not fully clear) shows the catalog's emoji two at a time, ten times a second, in the picker's
/// font and size. The picker still draws its emoji a few per frame (see <see cref="EmojiRowFactory"/>): the UI tests
/// showed this warm-up's glyphs don't spare the picker's popup its first-draw cost.
/// </para>
/// <para>
/// Throttled so the app never feels it: about 25 ms of drawing per 100 ms at most, and only while the window shows (a
/// hidden window draws nothing, so nothing would be warmed). It stops for good when the window's view goes, and the next
/// window picks up where it left off. One timing line is logged when done (counts and milliseconds only).
/// </para>
/// </remarks>
internal static class EmojiWarmUp
{
    // The Picker's Glyphs: Segoe UI Emoji At The Reaction Buttons' Size (A Glyph Is Kept Per Size)
    private const double GlyphSize = 20;

    // How Many Emoji Each Tick Draws, How Often, And How Long After The Chats Show It Starts
    private const int PerTick = 2;

    private static readonly TimeSpan Tick       = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);

    // A Warm-Up Is Running, Or All Are Drawn
    private static bool s_running;
    private static bool s_done;

    /// <summary>
    /// Starts warming the emoji in a panel of the window, unless one is running or all are drawn.
    /// </summary>
    /// <param name="host">A grid in the window's live tree; the line of text goes in its last row (never a sized-to-content
    /// one it could grow), behind its other children.</param>
    public static void Start(Grid host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (s_running || s_done)
        {
            return;
        }

        // Every Emoji Of The Catalog Not Yet Drawn In This Run, In Picker Order
        List<string> waiting = [.. EmojiCatalog.Categories
            .SelectMany(category => category.Emoji)
            .Select(entry => entry.Emoji)
            .Distinct(StringComparer.Ordinal)
            .Where(emoji => !EmojiRowFactory.Drawn.Contains(emoji))];
        if (waiting.Count == 0)
        {
            s_done = true;
            return;
        }

        var line = new TextBlock
        {
            FontFamily          = new FontFamily("Segoe UI Emoji"),
            FontSize            = GlyphSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            IsHitTestVisible    = false,
            Opacity             = 0.01,
        };
        AutomationProperties.SetAccessibilityView(line, AccessibilityView.Raw);
        Grid.SetRow(line, Math.Max(0, host.RowDefinitions.Count - 1));
        host.Children.Insert(0, line);

        DispatcherQueueTimer timer = host.DispatcherQueue.CreateTimer();
        var clock                  = Stopwatch.StartNew();
        int next                   = 0;
        s_running                  = true;
        timer.Interval             = StartDelay;

        // Ends This Warm-Up: The Line Goes, And A Later Window Starts Again From What Is Left
        void Stop()
        {
            timer.Stop();
            host.Children.Remove(line);
            s_running = false;
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            host.Unloaded -= OnUnloaded;
            Stop();
        }

        host.Unloaded += OnUnloaded;
        timer.Tick    += (_, _) =>
        {
            timer.Interval = Tick;

            // A Hidden Window Draws Nothing: Wait
            if (host.XamlRoot is not { IsHostVisible: true })
            {
                return;
            }

            // Not Marked Drawn For The Picker: The UI Tests Showed The Picker Drawing Them All At Once After This Still
            // Stood Still For 391 ms, So The Picker Keeps Its Few-Per-Frame Drawing Either Way
            // TODO: The warm-up's glyphs don't carry over to the picker's popup as measured; find what the first draw really caches before relying on it
            if (next >= waiting.Count)
            {
                host.Unloaded -= OnUnloaded;
                Stop();
                s_done = true;
                AppLog.EmojiWarmedUp(App.ShellLog, waiting.Count, clock.ElapsedMilliseconds);
                return;
            }

            line.Text = string.Concat(waiting.Skip(next).Take(PerTick));
            next     += PerTick;
        };
        timer.Start();
    }
}

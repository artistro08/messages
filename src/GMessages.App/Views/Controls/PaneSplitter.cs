using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GMessages.App.Views.Controls;

/// <summary>
/// The edge between the conversation list and the chat: drag it to resize the list.
/// </summary>
/// <remarks>
/// <para>
/// Shows the left-right resize cursor on hover. A drag raises <see cref="Resized"/> with how far the pointer has moved
/// since the press (window coordinates, so the edge moving under the pointer doesn't change it), then
/// <see cref="ResizeCompleted"/> on release. Focused (Tab), the Left and Right arrow keys resize in steps: each step is
/// a <see cref="Resized"/> of one step followed by <see cref="ResizeCompleted"/>.
/// </para>
/// <para>
/// The owner (<c>ChatsView</c>) turns the distance into a width, keeps it within its limits and saves it on
/// completion.
/// </para>
/// </remarks>
public sealed partial class PaneSplitter : UserControl
{
    // How Far One Arrow Key Moves The Edge, In Effective Pixels
    private const double KeyStep = 16;

    // Where The Pointer Was Pressed (Window X), While A Drag Runs
    private double? _pressX;

    /// <summary>
    /// Creates the edge: transparent, the full height, with the resize cursor and a tab stop.
    /// </summary>
    public PaneSplitter()
    {
        Content               = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        IsTabStop             = true;
        UseSystemFocusVisuals = true;
        ProtectedCursor       = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        // Behavior
        PointerPressed     += OnPointerPressed;
        PointerMoved       += OnPointerMoved;
        PointerReleased    += OnPointerEnded;
        PointerCaptureLost += OnPointerEnded;
        KeyDown            += OnKeyDown;
    }

    /// <summary>
    /// Raised while resizing, with how far the edge should move from where the resize started (positive is right).
    /// </summary>
    public event EventHandler<double>? Resized;

    /// <summary>
    /// Raised once a resize ends (the pointer released, or after each arrow key step).
    /// </summary>
    public event EventHandler? ResizeCompleted;

    /// <summary>
    /// Shows the edge to UI Automation (a plain user control has no peer of its own), so it can be found and focused.
    /// </summary>
    /// <returns>The peer.</returns>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CapturePointer(e.Pointer))
        {
            return;
        }

        _pressX   = e.GetCurrentPoint(null).Position.X;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressX is not { } press_x)
        {
            return;
        }

        Resized?.Invoke(this, e.GetCurrentPoint(null).Position.X - press_x);
        e.Handled = true;
    }

    // Released Or Capture Lost (Another Window Took It): The Resize Ends Where It Is
    private void OnPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_pressX is null)
        {
            return;
        }

        _pressX = null;
        ReleasePointerCaptures();
        ResizeCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right) || _pressX is not null)
        {
            return;
        }

        e.Handled = true;
        Resized?.Invoke(this, e.Key == VirtualKey.Right ? KeyStep : -KeyStep);
        ResizeCompleted?.Invoke(this, EventArgs.Empty);
    }
}

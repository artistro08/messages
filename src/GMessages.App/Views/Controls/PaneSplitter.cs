using GMessages.App.Windowing;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace GMessages.App.Views.Controls;

/// <summary>
/// Shows the edge between the conversation list and the chat. A drag on the edge resizes the list.
/// </summary>
/// <remarks>
/// <para>
/// The edge shows the left-right resize cursor on hover. A drag raises <see cref="Resized"/> with the distance of the
/// pointer from the press. The distance is in window coordinates, so the edge that moves under the pointer does not
/// change it. The release raises <see cref="ResizeCompleted"/>. With the focus from Tab, the Left and Right arrow keys
/// resize in steps. Each step raises <see cref="Resized"/> once, then <see cref="ResizeCompleted"/>.
/// </para>
/// <para>
/// The owner, <c>ChatsView</c>, turns the distance into a list width between the narrowest width and half the window.
/// It saves the width when the resize completes.
/// </para>
/// </remarks>
public sealed partial class PaneSplitter : UserControl
{
    // An arrow key moves the edge by this distance in effective pixels.
    private const double KeyStep = 16;

    // Holds the window X of the press while a drag runs.
    private double? _pressX;

    /// <summary>
    /// Creates a transparent edge of full height with the resize cursor and a tab stop.
    /// </summary>
    public PaneSplitter()
    {
        Content               = new Border { Background = new SolidColorBrush(Windows.UI.Colors.Transparent) };
        IsTabStop             = true;
        UseSystemFocusVisuals = true;
        PointerCursor.Use(this, PointerCursor.SizeWestEast);

        PointerPressed     += OnPointerPressed;
        PointerMoved       += OnPointerMoved;
        PointerReleased    += OnPointerEnded;
        PointerCaptureLost += OnPointerEnded;
        KeyDown            += OnKeyDown;
    }

    /// <summary>
    /// Raised during a resize with the distance of the edge from its start. A positive distance is to the right.
    /// </summary>
    public event EventHandler<double>? Resized;

    /// <summary>
    /// Raised when a resize ends: on the pointer release, or after each arrow key step.
    /// </summary>
    public event EventHandler? ResizeCompleted;

    /// <summary>
    /// Shows the edge to UI Automation, so that a client can find and focus it. A plain user control has no peer.
    /// </summary>
    /// <returns>The peer.</returns>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

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
        if (_pressX is not { } pressX)
        {
            return;
        }

        Resized?.Invoke(this, e.GetCurrentPoint(null).Position.X - pressX);
        e.Handled = true;
    }

    // A release or a capture loss ends the resize at its current place. Another window can take the capture.
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

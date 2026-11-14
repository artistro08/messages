using System.ComponentModel;
using Windows.System;

namespace GMessages.App.Windowing;

/// <summary>
/// Gives the key of <see cref="XamlWindow.KeyPressed"/>. A handler sets Handled to true when it used the key.
/// </summary>
/// <param name="key">The key that went down.</param>
/// <param name="isOwnedWindow">Whether the key goes to a window that the app window owns.</param>
internal sealed class KeyPressedEventArgs(VirtualKey key, bool isOwnedWindow) : HandledEventArgs
{
    /// <summary>Gets the key that went down.</summary>
    public VirtualKey Key { get; } = key;

    /// <summary>
    /// Gets a value indicating whether the key goes to a window that the app window owns, such as the layer of the
    /// video player. Such a window has its own keys.
    /// </summary>
    public bool IsOwnedWindow { get; } = isOwnedWindow;
}

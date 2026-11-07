namespace GMessages.Presentation.Connection;

/// <summary>
/// Specifies the connection state of the app with the phone.
/// </summary>
public enum ConnectionState
{
    /// <summary>No saved pairing exists. The user must sign in and pair.</summary>
    NotPaired,

    /// <summary>The app found a saved pairing, and the first connect runs.</summary>
    Connecting,

    /// <summary>The phone is paired, and the app listens for texts.</summary>
    Connected,

    /// <summary>The phone is paired, but the connection dropped for a short time. The client tries again.</summary>
    Reconnecting,

    /// <summary>Google ended the session. The user must sign in and pair again.</summary>
    SignedOut,
}

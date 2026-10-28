namespace GMessages.Presentation.Connection;

/// <summary>
/// Where the app stands with the phone.
/// </summary>
public enum ConnectionState
{
    /// <summary>No saved pairing: the user has to sign in and pair.</summary>
    NotPaired,

    /// <summary>A saved pairing was found and the first connect is running.</summary>
    Connecting,

    /// <summary>Paired and listening for texts.</summary>
    Connected,

    /// <summary>Paired, but the connection dropped for a moment and is being retried.</summary>
    Reconnecting,

    /// <summary>Google ended the session: the user has to sign in and pair again.</summary>
    SignedOut,
}

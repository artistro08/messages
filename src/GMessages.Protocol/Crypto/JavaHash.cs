namespace GMessages.Protocol.Crypto;

/// <summary>
/// Computes the Java-style hash with the multiplier 31 over signed bytes. Pairing uses it to put the 2 UKEY2 keys in order.
/// </summary>
/// <remarks>
/// Ports <c>byteHash</c> in <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go"/>.
/// The bytes are signed and the arithmetic wraps, exactly as the Go int32 math does.
/// </remarks>
public static class JavaHash
{
    /// <summary>
    /// Computes the hash.
    /// </summary>
    /// <remarks>
    /// The hash starts at 1 and becomes <c>31 * hash + (sbyte)b</c> for each byte.
    /// </remarks>
    /// <param name="bytes">The bytes to hash.</param>
    /// <returns>The 32-bit hash.</returns>
    public static int ByteHash(ReadOnlySpan<byte> bytes)
    {
        int hash = 1;
        foreach (byte b in bytes)
        {
            hash = unchecked((31 * hash) + (sbyte)b);
        }

        return hash;
    }
}

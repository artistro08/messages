namespace GMessages.Protocol.Crypto;

/// <summary>
/// The Java-style 31-multiplier hash over signed bytes, used to order the two UKEY2 keys.
/// </summary>
/// <remarks>
/// Ports <c>byteHash</c> in <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/pair_google.go"/>.
/// Bytes are treated as signed and the arithmetic wraps, exactly like Go's int32 math.
/// </remarks>
public static class JavaHash
{
    /// <summary>
    /// Computes the hash.
    /// </summary>
    /// <remarks>
    /// Starts at 1 and computes <c>31 * hash + (sbyte)b</c> for each byte.
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

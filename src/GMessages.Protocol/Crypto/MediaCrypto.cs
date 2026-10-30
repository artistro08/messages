using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace GMessages.Protocol.Crypto;

/// <summary>
/// LibGM's chunked AES-256-GCM media encryption, used for every uploaded and downloaded attachment.
/// </summary>
/// <remarks>
/// Ports <c>EncryptData</c> and <c>DecryptData</c> from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/aesgcm.go">crypto/aesgcm.go</see>
/// and the chunk-size limit from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/aesgcmstream.go">crypto/aesgcmstream.go</see>.
/// The layout is a 2-byte header (<c>0x00</c>, then log2 of the raw chunk size), followed by chunks of
/// <c>nonce (12) || ciphertext || tag (16)</c>. Each raw chunk is 32768 bytes (32740 bytes of plaintext) except the
/// last, and every chunk has a fresh random nonce. The AAD of a chunk is 5 bytes: <c>1</c> for the last chunk or
/// <c>0</c>, then the zero-based chunk index as a big-endian uint32, so chunks can't be reordered, dropped or cut.
/// Keys and media bytes are never logged.
/// </remarks>
public static class MediaCrypto
{
    /// <summary>The key length LibGM uses (AES-256).</summary>
    public const int KeySize = 32;

    /// <summary>The raw size of each outgoing chunk (Go: <c>outgoingRawChunkSize = 1 &lt;&lt; 15</c>).</summary>
    public const int OutgoingRawChunkSize = 1 << 15;

    /// <summary>The largest raw chunk size a received file may declare (Go: <c>maxRawChunkSize = 1 &lt;&lt; 22</c>).</summary>
    public const int MaxRawChunkSize = 1 << 22;

    private const int NonceSize     = 12;
    private const int TagSize       = 16;
    private const int ChunkOverhead = NonceSize + TagSize;
    private const int HeaderSize    = 2;

    /// <summary>
    /// Creates a fresh random media key.
    /// </summary>
    /// <remarks>Mirrors Go's <c>crypto.GenerateKey(32)</c> in <c>UploadMedia</c>.</remarks>
    /// <returns>32 random bytes.</returns>
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>
    /// Encrypts media the way LibGM uploads it.
    /// </summary>
    /// <remarks>
    /// Byte-for-byte <c>EncryptData</c>: the header is <c>{0, 15}</c>, then one chunk per 32740 bytes of plaintext.
    /// Empty input gives the header alone, as in Go.
    /// </remarks>
    /// <param name="plain">The media bytes.</param>
    /// <param name="key">The 32-byte key.</param>
    /// <returns>The encrypted file.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not 32 bytes (Go's <c>NewAESGCMHelper</c> refuses it too).</exception>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Unsupported AES key length (got={key.Length} expected={KeySize}).", nameof(key));
        }

        // Header
        int plain_chunk = OutgoingRawChunkSize - ChunkOverhead;
        int chunk_count = (plain.Length + plain_chunk - 1) / plain_chunk;
        byte[] output   = new byte[HeaderSize + plain.Length + (ChunkOverhead * chunk_count)];
        output[0]       = 0;
        output[1]       = (byte)BitOperations.Log2(OutgoingRawChunkSize);

        // Chunks
        using var gcm = new AesGcm(key, TagSize);
        Span<byte> aad = stackalloc byte[5];
        int write      = HeaderSize;
        for (int index = 0; index < chunk_count; index++)
        {
            int start               = index * plain_chunk;
            ReadOnlySpan<byte> part = plain.Slice(start, Math.Min(plain_chunk, plain.Length - start));
            WriteAad(aad, (uint)index, index == chunk_count - 1);

            Span<byte> nonce  = output.AsSpan(write, NonceSize);
            Span<byte> cipher = output.AsSpan(write + NonceSize, part.Length);
            Span<byte> tag    = output.AsSpan(write + NonceSize + part.Length, TagSize);
            RandomNumberGenerator.Fill(nonce);
            gcm.Encrypt(nonce, part, cipher, tag, aad);

            write += part.Length + ChunkOverhead;
        }

        return output;
    }

    /// <summary>
    /// Decrypts media made by LibGM or <see cref="Encrypt"/>.
    /// </summary>
    /// <remarks>
    /// Byte-for-byte <c>DecryptData</c>: any raw chunk size in the header up to <see cref="MaxRawChunkSize"/> is
    /// accepted, and the last chunk is the one that reaches the end of the data. Empty input or a key that isn't 32
    /// bytes is returned unchanged, as in Go.
    /// </remarks>
    /// <param name="data">The encrypted file.</param>
    /// <param name="key">The 32-byte key from the message's <c>MediaContent</c>.</param>
    /// <returns>The media bytes.</returns>
    /// <exception cref="CryptographicException">The header is bad, a chunk is truncated, or a tag doesn't match (tampered, reordered or cut data, or the wrong key).</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        // Pass Through, As In Go
        if (data.Length == 0 || key.Length != KeySize)
        {
            return data.ToArray();
        }

        // Header
        if (data.Length < HeaderSize || data[0] != 0)
        {
            throw new CryptographicException("Invalid media header signature.");
        }

        if (data[1] > BitOperations.Log2(MaxRawChunkSize))
        {
            throw new CryptographicException("Media chunk size too large.");
        }

        int raw_chunk           = 1 << data[1];
        ReadOnlySpan<byte> body = data[HeaderSize..];
        int chunk_count         = (int)((body.Length + (long)raw_chunk - 1) / raw_chunk);

        // Every Chunk (The Last Is The Shortest) Must Hold A Nonce And A Tag
        int last_length = body.Length - ((chunk_count - 1) * raw_chunk);
        if (chunk_count > 0 && (raw_chunk < ChunkOverhead || last_length < ChunkOverhead))
        {
            throw new CryptographicException("Media chunk is truncated.");
        }

        byte[] output = new byte[body.Length - (ChunkOverhead * chunk_count)];

        // Chunks
        using var gcm = new AesGcm(key, TagSize);
        Span<byte> aad = stackalloc byte[5];
        int write      = 0;
        for (int index = 0; index < chunk_count; index++)
        {
            int start                = index * raw_chunk;
            ReadOnlySpan<byte> chunk = body.Slice(start, Math.Min(raw_chunk, body.Length - start));

            WriteAad(aad, (uint)index, index == chunk_count - 1);
            int plain_length = chunk.Length - ChunkOverhead;
            gcm.Decrypt(chunk[..NonceSize], chunk.Slice(NonceSize, plain_length), chunk[^TagSize..], output.AsSpan(write, plain_length), aad);
            write += plain_length;
        }

        return output;
    }

    // Port Of calculateAAD: [isLast, BE uint32 index]
    private static void WriteAad(Span<byte> aad, uint index, bool isLast)
    {
        aad[0] = isLast ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(aad[1..], index);
    }
}

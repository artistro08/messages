using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace GMessages.Protocol.Crypto;

/// <summary>
/// Encrypts and decrypts media with the chunked AES-256-GCM of LibGM. Each uploaded and downloaded attachment uses it.
/// </summary>
/// <remarks>
/// Ports <c>EncryptData</c> and <c>DecryptData</c> from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/aesgcm.go">crypto/aesgcm.go</see>
/// and the chunk size limit from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/aesgcmstream.go">crypto/aesgcmstream.go</see>.
/// The layout starts with a 2-byte header: <c>0x00</c>, then log2 of the raw chunk size. Chunks of
/// <c>nonce (12) || ciphertext || tag (16)</c> follow. Each raw chunk is 32768 bytes, with 32740 bytes of plaintext.
/// Only the last chunk can be shorter. Each chunk has a new random nonce. The AAD of a chunk is 5 bytes. The first
/// byte is <c>1</c> for the last chunk, else <c>0</c>. The zero-based chunk index follows as a big-endian uint32.
/// Thus nobody can reorder, drop or cut chunks. Keys and media bytes never go in a log.
/// </remarks>
public static class MediaCrypto
{
    /// <summary>The key length of LibGM, for AES-256.</summary>
    public const int KeySize = 32;

    /// <summary>The raw size of each outgoing chunk. Go has <c>outgoingRawChunkSize = 1 &lt;&lt; 15</c>.</summary>
    public const int OutgoingRawChunkSize = 1 << 15;

    /// <summary>The largest raw chunk size that a received file can declare. Go has <c>maxRawChunkSize = 1 &lt;&lt; 22</c>.</summary>
    public const int MaxRawChunkSize = 1 << 22;

    private const int NonceSize     = 12;
    private const int TagSize       = 16;
    private const int ChunkOverhead = NonceSize + TagSize;
    private const int HeaderSize    = 2;

    /// <summary>
    /// Creates a new random media key.
    /// </summary>
    /// <remarks>Ported from the Go <c>crypto.GenerateKey(32)</c> call in <c>UploadMedia</c>.</remarks>
    /// <returns>32 random bytes.</returns>
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>
    /// Encrypts media in the same way as the LibGM upload.
    /// </summary>
    /// <remarks>
    /// The output is byte-for-byte the layout of <c>EncryptData</c>. The header is <c>{0, 15}</c>. Then 1 chunk follows
    /// for each 32740 bytes of plaintext. As in Go, empty input gives only the header.
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

        int plainChunk  = OutgoingRawChunkSize - ChunkOverhead;
        int chunkCount  = (plain.Length + plainChunk - 1) / plainChunk;
        byte[] output   = new byte[HeaderSize + plain.Length + (ChunkOverhead * chunkCount)];
        output[0]       = 0;
        output[1]       = (byte)BitOperations.Log2(OutgoingRawChunkSize);

        using var gcm = new AesGcm(key, TagSize);
        Span<byte> aad = stackalloc byte[5];
        int write      = HeaderSize;
        for (int index = 0; index < chunkCount; index++)
        {
            int start               = index * plainChunk;
            ReadOnlySpan<byte> part = plain.Slice(start, Math.Min(plainChunk, plain.Length - start));
            WriteAad(aad, (uint)index, index == chunkCount - 1);

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
    /// Decrypts media from LibGM or from <see cref="Encrypt"/>.
    /// </summary>
    /// <remarks>
    /// Ported from <c>DecryptData</c>. The method accepts each raw chunk size in the header up to
    /// <see cref="MaxRawChunkSize"/>. Go <c>DecryptData</c> has no limit, but the Go stream decoder has this limit. The
    /// last chunk is the chunk that reaches the end of the data. As in Go, the method returns empty input unchanged. It
    /// also returns the data unchanged when the key is not 32 bytes.
    /// </remarks>
    /// <param name="data">The encrypted file.</param>
    /// <param name="key">The 32-byte key from the message's <c>MediaContent</c>.</param>
    /// <returns>The media bytes.</returns>
    /// <exception cref="CryptographicException">The header is bad, a chunk is truncated, or a tag doesn't match (tampered, reordered or cut data, or the wrong key).</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        // As in Go, the data passes through.
        if (data.Length == 0 || key.Length != KeySize)
        {
            return data.ToArray();
        }

        if (data.Length < HeaderSize || data[0] != 0)
        {
            throw new CryptographicException("Invalid media header signature.");
        }

        if (data[1] > BitOperations.Log2(MaxRawChunkSize))
        {
            throw new CryptographicException("Media chunk size too large.");
        }

        int rawChunk            = 1 << data[1];
        ReadOnlySpan<byte> body = data[HeaderSize..];
        int chunkCount          = (int)((body.Length + (long)rawChunk - 1) / rawChunk);

        // Each chunk must hold a nonce and a tag. The last chunk is the shortest.
        int lastLength = body.Length - ((chunkCount - 1) * rawChunk);
        if (chunkCount > 0 && (rawChunk < ChunkOverhead || lastLength < ChunkOverhead))
        {
            throw new CryptographicException("Media chunk is truncated.");
        }

        byte[] output = new byte[body.Length - (ChunkOverhead * chunkCount)];

        using var gcm = new AesGcm(key, TagSize);
        Span<byte> aad = stackalloc byte[5];
        int write      = 0;
        for (int index = 0; index < chunkCount; index++)
        {
            int start                = index * rawChunk;
            ReadOnlySpan<byte> chunk = body.Slice(start, Math.Min(rawChunk, body.Length - start));

            WriteAad(aad, (uint)index, index == chunkCount - 1);
            int plainLength = chunk.Length - ChunkOverhead;
            gcm.Decrypt(chunk[..NonceSize], chunk.Slice(NonceSize, plainLength), chunk[^TagSize..], output.AsSpan(write, plainLength), aad);
            write += plainLength;
        }

        return output;
    }

    // This method ports calculateAAD. The AAD is [isLast, big-endian uint32 index].
    private static void WriteAad(Span<byte> aad, uint index, bool isLast)
    {
        aad[0] = isLast ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(aad[1..], index);
    }
}

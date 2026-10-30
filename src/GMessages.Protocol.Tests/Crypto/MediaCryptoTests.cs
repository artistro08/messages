using System.Buffers.Binary;
using System.Security.Cryptography;
using GMessages.Protocol.Crypto;

namespace GMessages.Protocol.Tests.Crypto;

/// <summary>
/// Checks <see cref="MediaCrypto"/> against LibGM's chunked AES-GCM layout.
/// </summary>
/// <remarks>
/// Go isn't installed on the build machine, so there is no Go-encrypted golden vector yet; these structural tests
/// rebuild the layout by hand from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/aesgcm.go">crypto/aesgcm.go</see>:
/// header <c>{0, log2(chunk)}</c>, chunks of <c>nonce || ct || tag</c>, AAD <c>[last, BE index]</c>.
/// </remarks>
[TestClass]
public class MediaCryptoTests
{
    private const int PlainChunk = MediaCrypto.OutgoingRawChunkSize - 28;

    private static byte[] RandomBytes(int length)
    {
        byte[] bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    // Encrypts One Chunk By Hand, As Go's encryptChunk Does
    private static byte[] HandChunk(AesGcm gcm, ReadOnlySpan<byte> plain, uint index, bool last)
    {
        byte[] aad = new byte[5];
        aad[0]     = last ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(1), index);

        byte[] chunk = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(chunk.AsSpan(0, 12));
        gcm.Encrypt(chunk.AsSpan(0, 12), plain, chunk.AsSpan(12, plain.Length), chunk.AsSpan(12 + plain.Length), aad);
        return chunk;
    }

    /// <summary>
    /// Every size round-trips, including chunk boundaries, and the output is the header plus 28 bytes per chunk.
    /// </summary>
    [TestMethod]
    public void RoundTrip_Sizes()
    {
        byte[] key = MediaCrypto.GenerateKey();
        Assert.AreEqual(32, key.Length);

        foreach (int n in new[] { 0, 1, PlainChunk - 1, PlainChunk, PlainChunk + 1, 100000, 3 * PlainChunk })
        {
            byte[] plain     = RandomBytes(n);
            byte[] encrypted = MediaCrypto.Encrypt(plain, key);
            int chunks       = (n + PlainChunk - 1) / PlainChunk;

            Assert.AreEqual(2 + n + (28 * chunks), encrypted.Length, $"size {n}");
            CollectionAssert.AreEqual(plain, MediaCrypto.Decrypt(encrypted, key), $"size {n}");
        }
    }

    /// <summary>
    /// A file built by hand the way Go builds it (three chunks, Go's AAD) decrypts.
    /// </summary>
    [TestMethod]
    public void Decrypt_GoLayoutByHand()
    {
        byte[] key   = MediaCrypto.GenerateKey();
        byte[] plain = RandomBytes((2 * PlainChunk) + 10);
        using var gcm = new AesGcm(key, 16);

        byte[] file = [0, 15,
            .. HandChunk(gcm, plain.AsSpan(0, PlainChunk), 0, false),
            .. HandChunk(gcm, plain.AsSpan(PlainChunk, PlainChunk), 1, false),
            .. HandChunk(gcm, plain.AsSpan(2 * PlainChunk), 2, true)];

        CollectionAssert.AreEqual(plain, MediaCrypto.Decrypt(file, key));
    }

    /// <summary>
    /// A Go-layout file whose plaintext fills exactly two chunks decrypts: the last chunk is a full 32768 raw bytes
    /// with the last-chunk flag set.
    /// </summary>
    [TestMethod]
    public void Decrypt_GoLayout_FullLastChunk()
    {
        byte[] key   = MediaCrypto.GenerateKey();
        byte[] plain = RandomBytes(2 * PlainChunk);
        using var gcm = new AesGcm(key, 16);

        byte[] last = HandChunk(gcm, plain.AsSpan(PlainChunk), 1, true);
        byte[] file = [0, 15,
            .. HandChunk(gcm, plain.AsSpan(0, PlainChunk), 0, false),
            .. last];

        Assert.HasCount(MediaCrypto.OutgoingRawChunkSize, last);
        CollectionAssert.AreEqual(plain, MediaCrypto.Decrypt(file, key));
    }

    /// <summary>
    /// The header is <c>{0, 15}</c> on encrypt; decrypt accepts another chunk size from the header and refuses one
    /// above <c>1 &lt;&lt; 22</c> or a non-zero first byte.
    /// </summary>
    [TestMethod]
    public void Header_ChunkSize()
    {
        byte[] key = MediaCrypto.GenerateKey();

        // Outgoing Header
        byte[] encrypted = MediaCrypto.Encrypt(RandomBytes(10), key);
        Assert.AreEqual(0, encrypted[0]);
        Assert.AreEqual(15, encrypted[1]);

        // A 1 KiB Chunk Size From The Header
        byte[] plain = RandomBytes(1500);
        using var gcm = new AesGcm(key, 16);
        byte[] small = [0, 10, .. HandChunk(gcm, plain.AsSpan(0, 1024 - 28), 0, false), .. HandChunk(gcm, plain.AsSpan(1024 - 28), 1, true)];
        CollectionAssert.AreEqual(plain, MediaCrypto.Decrypt(small, key));

        // Too Large, Or A Bad Signature
        byte[] too_large = [.. encrypted];
        too_large[1]     = 23;
        Assert.ThrowsExactly<CryptographicException>(() => MediaCrypto.Decrypt(too_large, key));

        byte[] bad_signature = [.. encrypted];
        bad_signature[0]     = 1;
        Assert.ThrowsExactly<CryptographicException>(() => MediaCrypto.Decrypt(bad_signature, key));
    }

    /// <summary>
    /// Empty input or a key that isn't 32 bytes comes back unchanged, as in Go.
    /// </summary>
    [TestMethod]
    public void Decrypt_EmptyOrShortKey_PassesThrough()
    {
        byte[] data = [0, 15, 1, 2, 3];

        Assert.AreEqual(0, MediaCrypto.Decrypt([], MediaCrypto.GenerateKey()).Length);
        CollectionAssert.AreEqual(data, MediaCrypto.Decrypt(data, new byte[16]));
        Assert.ThrowsExactly<ArgumentException>(() => MediaCrypto.Encrypt(data, new byte[16]));
    }

    /// <summary>
    /// A flipped ciphertext bit fails the tag check.
    /// </summary>
    [TestMethod]
    public void Tampered_Throws()
    {
        byte[] key       = MediaCrypto.GenerateKey();
        byte[] encrypted = MediaCrypto.Encrypt(RandomBytes(100), key);
        encrypted[20]   ^= 0x01;

        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => MediaCrypto.Decrypt(encrypted, key));
    }

    /// <summary>
    /// Swapping two full chunks fails, because the AAD carries the chunk index.
    /// </summary>
    [TestMethod]
    public void Reordered_Throws()
    {
        byte[] key       = MediaCrypto.GenerateKey();
        byte[] encrypted = MediaCrypto.Encrypt(RandomBytes((2 * PlainChunk) + 5), key);
        int raw          = MediaCrypto.OutgoingRawChunkSize;

        byte[] swapped = [.. encrypted.AsSpan(0, 2), .. encrypted.AsSpan(2 + raw, raw), .. encrypted.AsSpan(2, raw), .. encrypted.AsSpan(2 + (2 * raw))];

        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => MediaCrypto.Decrypt(swapped, key));
    }

    /// <summary>
    /// A cut last chunk fails, whether bytes are cut from it, the whole last chunk is dropped, or less than a tag is left.
    /// </summary>
    [TestMethod]
    public void TruncatedLastChunk_Throws()
    {
        byte[] key       = MediaCrypto.GenerateKey();
        byte[] encrypted = MediaCrypto.Encrypt(RandomBytes(PlainChunk + 100), key);
        int raw          = MediaCrypto.OutgoingRawChunkSize;

        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => MediaCrypto.Decrypt(encrypted.AsSpan(0, encrypted.Length - 1), key));
        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => MediaCrypto.Decrypt(encrypted.AsSpan(0, 2 + raw), key));
        Assert.ThrowsExactly<CryptographicException>(() => MediaCrypto.Decrypt(encrypted.AsSpan(0, 2 + raw + 20), key));
    }
}

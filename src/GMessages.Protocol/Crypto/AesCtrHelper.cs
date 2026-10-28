using System.Security.Cryptography;

namespace GMessages.Protocol.Crypto;

/// <summary>
/// AES-256-CTR encryption with an HMAC-SHA256 tag, as used for the encrypted request payloads.
/// </summary>
/// <remarks>
/// Ports <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/aesctr.go"/>.
/// The wire layout is <c>ciphertext || iv (16) || hmac (32)</c>, with the MAC covering ciphertext and IV.
/// .NET has no CTR mode, so the keystream is built by encrypting big-endian 128-bit counter blocks with ECB, like Go's CTR does.
/// Key material is never logged.
/// </remarks>
/// <param name="aesKey">The AES key (32 bytes for AES-256).</param>
/// <param name="hmacKey">The HMAC-SHA256 key.</param>
public sealed class AesCtrHelper(byte[] aesKey, byte[] hmacKey)
{
    private const int BlockSize = 16;
    private const int MacSize   = 32;

    /// <summary>
    /// Gets the AES key.
    /// </summary>
    public byte[] AesKey { get; } = aesKey;

    /// <summary>
    /// Gets the HMAC key.
    /// </summary>
    public byte[] HmacKey { get; } = hmacKey;

    /// <summary>
    /// Creates a helper with fresh random 32-byte AES and HMAC keys.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>NewAESCTRHelper</c>.
    /// </remarks>
    /// <returns>A new helper.</returns>
    public static AesCtrHelper CreateRandom() => new(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Encrypts and authenticates <paramref name="plaintext"/> with a random IV.
    /// </summary>
    /// <remarks>
    /// Output is <c>ciphertext || iv || hmac</c>, so it is always 48 bytes longer than the input.
    /// </remarks>
    /// <param name="plaintext">The data to encrypt.</param>
    /// <returns>The encrypted payload.</returns>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        byte[] output = new byte[plaintext.Length + BlockSize + MacSize];
        byte[] iv     = RandomNumberGenerator.GetBytes(BlockSize);

        // Ciphertext And IV
        Xor(plaintext, output, iv);
        iv.CopyTo(output, plaintext.Length);

        // Mac
        HMACSHA256.HashData(HmacKey, output.AsSpan(0, plaintext.Length + BlockSize), output.AsSpan(plaintext.Length + BlockSize));
        return output;
    }

    /// <summary>
    /// Verifies the MAC and decrypts a payload made by <see cref="Encrypt"/>.
    /// </summary>
    /// <remarks>
    /// The MAC is checked in constant time before any decryption, so tampered data is never processed.
    /// </remarks>
    /// <param name="data">The <c>ciphertext || iv || hmac</c> payload.</param>
    /// <returns>The plaintext.</returns>
    /// <exception cref="CryptographicException">The input is shorter than 48 bytes ("input data is too short") or the MAC is wrong ("HMAC mismatch").</exception>
    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        if (data.Length < 48)
        {
            throw new CryptographicException("input data is too short");
        }

        // Verify Mac
        ReadOnlySpan<byte> body = data[..^MacSize];
        byte[] expected         = HMACSHA256.HashData(HmacKey, body);
        if (!CryptographicOperations.FixedTimeEquals(data[^MacSize..], expected))
        {
            throw new CryptographicException("HMAC mismatch");
        }

        // Decrypt
        byte[] plaintext = new byte[body.Length - BlockSize];
        Xor(body[..^BlockSize], plaintext, body[^BlockSize..]);
        return plaintext;
    }

    /// <summary>
    /// XORs <paramref name="input"/> with the CTR keystream starting at <paramref name="iv"/>.
    /// </summary>
    private void Xor(ReadOnlySpan<byte> input, Span<byte> output, ReadOnlySpan<byte> iv)
    {
        using Aes aes      = Aes.Create();
        aes.Key            = AesKey;
        Span<byte> counter = stackalloc byte[BlockSize];
        Span<byte> stream  = stackalloc byte[BlockSize];
        iv.CopyTo(counter);

        for (int offset = 0; offset < input.Length; offset += BlockSize)
        {
            aes.EncryptEcb(counter, stream, PaddingMode.None);
            int n = Math.Min(BlockSize, input.Length - offset);
            for (int i = 0; i < n; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ stream[i]);
            }

            // Increment Big-Endian Counter
            for (int i = BlockSize - 1; i >= 0 && ++counter[i] == 0; i--)
            {
            }
        }
    }
}

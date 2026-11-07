using System.Security.Cryptography;

namespace GMessages.Protocol.Crypto;

/// <summary>
/// Encrypts with AES-256-CTR and adds an HMAC-SHA256 tag, as for the encrypted request payloads.
/// </summary>
/// <remarks>
/// Ports <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/aesctr.go"/>.
/// The wire layout is <c>ciphertext || iv (16) || hmac (32)</c>. The MAC covers the ciphertext and the IV.
/// .NET has no CTR mode. Thus the class encrypts big-endian 128-bit counter blocks with ECB, as the Go CTR does.
/// Key material never goes in a log.
/// </remarks>
/// <param name="aesKey">The AES key, 32 bytes for AES-256.</param>
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
    /// Creates a helper with new random 32-byte AES and HMAC keys.
    /// </summary>
    /// <remarks>
    /// Ported from <c>NewAESCTRHelper</c>.
    /// </remarks>
    /// <returns>A new helper.</returns>
    public static AesCtrHelper CreateRandom() => new(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Encrypts and authenticates <paramref name="plaintext"/> with a random IV.
    /// </summary>
    /// <remarks>
    /// The output is <c>ciphertext || iv || hmac</c>. Thus it is always 48 bytes longer than the input.
    /// </remarks>
    /// <param name="plaintext">The data to encrypt.</param>
    /// <returns>The encrypted payload.</returns>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        byte[] output = new byte[plaintext.Length + BlockSize + MacSize];
        byte[] iv     = RandomNumberGenerator.GetBytes(BlockSize);

        Xor(plaintext, output, iv);
        iv.CopyTo(output, plaintext.Length);

        HMACSHA256.HashData(HmacKey, output.AsSpan(0, plaintext.Length + BlockSize), output.AsSpan(plaintext.Length + BlockSize));
        return output;
    }

    /// <summary>
    /// Verifies the MAC and decrypts a payload from <see cref="Encrypt"/>.
    /// </summary>
    /// <remarks>
    /// The method checks the MAC in constant time before it decrypts. Thus it never processes changed data.
    /// </remarks>
    /// <param name="data">The <c>ciphertext || iv || hmac</c> payload.</param>
    /// <returns>The plaintext.</returns>
    /// <exception cref="CryptographicException">The input is shorter than 48 bytes ("input data is too short") or the MAC is wrong ("HMAC mismatch").</exception>
    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        if (data.Length < BlockSize + MacSize)
        {
            throw new CryptographicException("input data is too short");
        }

        ReadOnlySpan<byte> body = data[..^MacSize];
        byte[] expected         = HMACSHA256.HashData(HmacKey, body);
        if (!CryptographicOperations.FixedTimeEquals(data[^MacSize..], expected))
        {
            throw new CryptographicException("HMAC mismatch");
        }

        byte[] plaintext = new byte[body.Length - BlockSize];
        Xor(body[..^BlockSize], plaintext, body[^BlockSize..]);
        return plaintext;
    }

    // The method XORs the input with the CTR keystream that starts at the IV.
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

            // The counter is 1 big-endian 128-bit number. A carry moves to the byte on the left.
            for (int i = BlockSize - 1; i >= 0 && ++counter[i] == 0; i--)
            {
            }
        }
    }
}

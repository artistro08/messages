using System.Security.Cryptography;
using System.Text;
using GMessages.Protocol.Crypto;

namespace GMessages.Protocol.Tests.Crypto;

/// <summary>
/// Checks the C# crypto helpers against golden vectors produced by the Go LibGM.
/// </summary>
/// <remarks>
/// Go's ECDSA signatures and AES-CTR IVs are random, so signatures are cross-verified and IVs are
/// exercised through decryption instead of being compared byte for byte.
/// See <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/aesctr.go"/>.
/// </remarks>
[TestClass]
public class CryptoGoldenTests
{
    private static AesCtrHelper GoHelper(System.Text.Json.JsonElement aes) =>
        new(Golden.Hex(aes.GetProperty("aesKeyHex")), Golden.Hex(aes.GetProperty("hmacKeyHex")));

    /// <summary>
    /// Every ciphertext Go produced decrypts to its plaintext.
    /// </summary>
    [TestMethod]
    public void Decrypt_EveryGoCiphertext_ReturnsPlaintext()
    {
        var aes = Golden.Load("crypto").GetProperty("aesctr");
        AesCtrHelper helper = GoHelper(aes);

        int count = 0;
        foreach (var c in aes.GetProperty("cases").EnumerateArray())
        {
            CollectionAssert.AreEqual(Golden.Hex(c.GetProperty("plaintextHex")), helper.Decrypt(Golden.Hex(c.GetProperty("encryptedHex"))));
            count++;
        }

        Assert.AreEqual(6, count);
    }

    /// <summary>
    /// Encrypt output decrypts back and is 48 bytes (IV plus MAC) longer than the input.
    /// </summary>
    [TestMethod]
    public void EncryptThenDecrypt_RoundTrips_AndLengthIsPlainPlus48()
    {
        AesCtrHelper helper = AesCtrHelper.CreateRandom();

        foreach (int n in new[] { 0, 1, 15, 16, 17, 40, 1000 })
        {
            byte[] plain = new byte[n];
            RandomNumberGenerator.Fill(plain);

            byte[] encrypted = helper.Encrypt(plain);

            Assert.AreEqual(n + 48, encrypted.Length);
            CollectionAssert.AreEqual(plain, helper.Decrypt(encrypted));
        }
    }

    /// <summary>
    /// A flipped byte fails the MAC check.
    /// </summary>
    [TestMethod]
    public void Decrypt_TamperedGoCiphertext_ThrowsHmacMismatch()
    {
        var aes = Golden.Load("crypto").GetProperty("aesctr");
        byte[] data = Golden.Hex(aes.GetProperty("cases")[4].GetProperty("encryptedHex"));
        data[0] ^= 1;

        CryptographicException ex = Assert.ThrowsExactly<CryptographicException>(() => GoHelper(aes).Decrypt(data));
        Assert.AreEqual("HMAC mismatch", ex.Message);
    }

    /// <summary>
    /// Input shorter than IV plus MAC is rejected.
    /// </summary>
    [TestMethod]
    public void Decrypt_Under48Bytes_ThrowsTooShort()
    {
        AesCtrHelper helper = AesCtrHelper.CreateRandom();

        CryptographicException ex = Assert.ThrowsExactly<CryptographicException>(() => helper.Decrypt(new byte[47]));
        Assert.AreEqual("input data is too short", ex.Message);
    }

    /// <summary>
    /// The Java-style hash matches Go for every input.
    /// </summary>
    [TestMethod]
    public void ByteHash_MatchesGoForEveryInput()
    {
        int count = 0;
        foreach (var c in Golden.Load("crypto").GetProperty("byteHash").EnumerateArray())
        {
            Assert.AreEqual(c.GetProperty("result").GetInt32(), JavaHash.ByteHash(Golden.Hex(c.GetProperty("inputHex"))));
            count++;
        }

        Assert.AreEqual(5, count);
    }

    /// <summary>
    /// <see cref="HKDF"/> with SHA-256 and a 32-byte output matches Go's <c>doHKDF</c>.
    /// </summary>
    [TestMethod]
    public void Hkdf_MatchesGoForEveryCase()
    {
        int count = 0;
        foreach (var c in Golden.Load("crypto").GetProperty("hkdf").EnumerateArray())
        {
            byte[] actual = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: Golden.Hex(c.GetProperty("keyHex")),
                outputLength: 32,
                salt: Golden.Hex(c.GetProperty("saltHex")),
                info: Golden.Hex(c.GetProperty("infoHex")));
            CollectionAssert.AreEqual(Golden.Hex(c.GetProperty("resultHex")), actual);
            count++;
        }

        Assert.AreEqual(4, count);
    }

    /// <summary>
    /// A key rebuilt from Go's d/x/y produces Go's SPKI bytes.
    /// </summary>
    [TestMethod]
    public void RefreshKey_FromGoJwk_SpkiMatchesGo()
    {
        var jwk = Golden.Load("crypto").GetProperty("jwk");
        var key = RefreshKey.FromJwk(Golden.Hex(jwk.GetProperty("dHex")), Golden.Hex(jwk.GetProperty("xHex")), Golden.Hex(jwk.GetProperty("yHex")));

        CollectionAssert.AreEqual(Golden.Hex(jwk.GetProperty("spkiHex")), key.PublicKeySpki());
    }

    /// <summary>
    /// Go's signature verifies with the C# key, and a C# signature verifies with a key imported from Go's SPKI.
    /// </summary>
    [TestMethod]
    public void GoSignature_VerifiesWithCSharpKey_AndCSharpSignature_VerifiesWithGoSpki()
    {
        var jwk = Golden.Load("crypto").GetProperty("jwk");
        var key = RefreshKey.FromJwk(Golden.Hex(jwk.GetProperty("dHex")), Golden.Hex(jwk.GetProperty("xHex")), Golden.Hex(jwk.GetProperty("yHex")));
        byte[] signed = Encoding.UTF8.GetBytes(jwk.GetProperty("signedText").GetString()!);

        // Go Signature, C# Key
        using ECDsa csKey = key.ToECDsa();
        Assert.IsTrue(csKey.VerifyData(signed, Golden.Hex(jwk.GetProperty("signatureHex")), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        // C# Signature, Go SPKI
        using ECDsa goPublic = ECDsa.Create();
        goPublic.ImportSubjectPublicKeyInfo(Golden.Hex(jwk.GetProperty("spkiHex")), out _);
        Assert.IsTrue(goPublic.VerifyData(signed, key.SignSha256Der(signed), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }
}

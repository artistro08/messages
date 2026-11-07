using System.Security.Cryptography;

namespace GMessages.Protocol.Crypto;

/// <summary>
/// Holds the P-256 key that signs the token refresh requests. The saved session keeps it as a JWK.
/// </summary>
/// <remarks>
/// Ports the <c>JWK</c> type in <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/ecdsa.go"/>.
/// The values are raw big-endian coordinates. The class pads a short value on the left to 32 bytes. Key material
/// never goes in a log.
/// </remarks>
public sealed class RefreshKey
{
    private RefreshKey(byte[] d, byte[] x, byte[] y)
    {
        D = d;
        X = x;
        Y = y;
    }

    /// <summary>
    /// Gets the private scalar, 32 bytes.
    /// </summary>
    public byte[] D { get; }

    /// <summary>
    /// Gets the public X coordinate, 32 bytes.
    /// </summary>
    public byte[] X { get; }

    /// <summary>
    /// Gets the public Y coordinate, 32 bytes.
    /// </summary>
    public byte[] Y { get; }

    /// <summary>
    /// Generates a new P-256 key.
    /// </summary>
    /// <remarks>
    /// Ported from <c>GenerateECDSAKey</c>.
    /// </remarks>
    /// <returns>A new key.</returns>
    public static RefreshKey Generate()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p    = ecdsa.ExportParameters(true);
        return new RefreshKey(p.D!, p.Q.X!, p.Q.Y!);
    }

    /// <summary>
    /// Builds a key from the stored JWK parts.
    /// </summary>
    /// <remarks>
    /// As in Go, the method pads a part shorter than 32 bytes on the left with zeros.
    /// </remarks>
    /// <param name="d">The private scalar.</param>
    /// <param name="x">The public X coordinate.</param>
    /// <param name="y">The public Y coordinate.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A part is longer than 32 bytes.</exception>
    public static RefreshKey FromJwk(byte[] d, byte[] x, byte[] y) => new(Pad(d), Pad(x), Pad(y));

    /// <summary>
    /// Creates an <see cref="ECDsa"/> that holds this key.
    /// </summary>
    /// <remarks>
    /// The caller owns the result and must dispose it.
    /// </remarks>
    /// <returns>A new <see cref="ECDsa"/>.</returns>
    public ECDsa ToECDsa() => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        D     = D,
        Q     = new ECPoint { X = X, Y = Y },
    });

    /// <summary>
    /// Gets the public key as a DER SubjectPublicKeyInfo.
    /// </summary>
    /// <remarks>
    /// The output is the same as from the Go <c>x509.MarshalPKIXPublicKey</c>.
    /// </remarks>
    /// <returns>The SPKI bytes.</returns>
    public byte[] PublicKeySpki()
    {
        using ECDsa ecdsa = ToECDsa();
        return ecdsa.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// Signs <paramref name="data"/> with ECDSA over its SHA-256 hash.
    /// </summary>
    /// <remarks>
    /// The output is an ASN.1 DER sequence, as from the Go <c>ecdsa.SignASN1</c>. The signatures are random, so 2
    /// calls give different results.
    /// </remarks>
    /// <param name="data">The data to sign.</param>
    /// <returns>The DER signature.</returns>
    public byte[] SignSha256Der(ReadOnlySpan<byte> data)
    {
        using ECDsa ecdsa = ToECDsa();
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static byte[] Pad(byte[] value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, 32);
        byte[] padded = new byte[32];
        value.CopyTo(padded, 32 - value.Length);
        return padded;
    }
}

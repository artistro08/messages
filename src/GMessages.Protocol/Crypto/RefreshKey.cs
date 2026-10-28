using System.Security.Cryptography;

namespace GMessages.Protocol.Crypto;

/// <summary>
/// The P-256 key (a JWK in the saved session) used to sign token refresh requests.
/// </summary>
/// <remarks>
/// Ports the <c>JWK</c> type in <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/crypto/ecdsa.go"/>.
/// Values are raw big-endian coordinates; short ones are left-padded to 32 bytes. Key material is never logged.
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
    /// Gets the private scalar (32 bytes).
    /// </summary>
    public byte[] D { get; }

    /// <summary>
    /// Gets the public X coordinate (32 bytes).
    /// </summary>
    public byte[] X { get; }

    /// <summary>
    /// Gets the public Y coordinate (32 bytes).
    /// </summary>
    public byte[] Y { get; }

    /// <summary>
    /// Generates a new P-256 key.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>GenerateECDSAKey</c>.
    /// </remarks>
    /// <returns>A new key.</returns>
    public static RefreshKey Generate()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p    = ecdsa.ExportParameters(true);
        return new RefreshKey(p.D!, p.Q.X!, p.Q.Y!);
    }

    /// <summary>
    /// Builds a key from stored JWK parts.
    /// </summary>
    /// <remarks>
    /// Parts shorter than 32 bytes are left-padded with zeros, as Go does.
    /// </remarks>
    /// <param name="d">The private scalar.</param>
    /// <param name="x">The public X coordinate.</param>
    /// <param name="y">The public Y coordinate.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A part is longer than 32 bytes.</exception>
    public static RefreshKey FromJwk(byte[] d, byte[] x, byte[] y) => new(Pad(d), Pad(x), Pad(y));

    /// <summary>
    /// Creates an <see cref="ECDsa"/> holding this key.
    /// </summary>
    /// <remarks>
    /// The caller owns and must dispose the result.
    /// </remarks>
    /// <returns>A new <see cref="ECDsa"/>.</returns>
    public ECDsa ToECDsa() => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        D     = D,
        Q     = new ECPoint { X = X, Y = Y },
    });

    /// <summary>
    /// Gets the public key as DER SubjectPublicKeyInfo.
    /// </summary>
    /// <remarks>
    /// Matches Go's <c>x509.MarshalPKIXPublicKey</c>.
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
    /// Output is an ASN.1 DER sequence like Go's <c>ecdsa.SignASN1</c>. Signatures are random, so two calls differ.
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

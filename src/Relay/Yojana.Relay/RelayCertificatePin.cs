using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Yojana.Relay;

/// <summary>
/// ERP service interfaces on customer premises commonly present a self-signed certificate. The operator may pin that
/// one certificate by the SHA-256 of its DER bytes; any other certificate with a chain or name error is refused.
/// Never "accept any certificate".
/// </summary>
public static class RelayCertificatePin
{
    public static string? Normalise(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return null;
        var hex = pin.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex
            : throw new InvalidOperationException("relay_configuration_invalid");
    }

    public static RemoteCertificateValidationCallback Validator(string? pin)
    {
        var expected = Normalise(pin);
        return (_, certificate, _, errors) => errors == SslPolicyErrors.None
            || expected is not null && certificate is not null
            && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected),
                SHA256.HashData(certificate.GetRawCertData()));
    }
}

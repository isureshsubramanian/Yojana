using System.Net.Security;
using Yojana.Relay.Contracts;

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
        try { return ServiceCertificatePin.Normalise(pin); }
        catch (InvalidOperationException) { throw new InvalidOperationException("relay_configuration_invalid"); }
    }

    public static RemoteCertificateValidationCallback Validator(string? pin)
    {
        _ = Normalise(pin);
        return ServiceCertificatePin.Validator(pin);
    }
}

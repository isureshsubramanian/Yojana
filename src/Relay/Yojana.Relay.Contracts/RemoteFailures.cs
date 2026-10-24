using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Yojana.Relay.Contracts;

public static class RemoteFailureCode
{
    public const string Unreachable = "unreachable";
    public const string TlsHandshake = "tls_handshake";
    public const string CertificateRejected = "certificate_rejected";
    public const string AuthenticationRefused = "authentication_refused";
    public const string AuthorisationRefused = "authorisation_refused";
    public const string RequestRefused = "request_refused";
    public const string RateLimited = "rate_limited";
    public const string Timeout = "timeout";
    public const string RemoteError = "remote_error";
    public const string Unknown = "unknown";

    public static bool IsKnown(string? value) => value is Unreachable or TlsHandshake or CertificateRejected
        or AuthenticationRefused or AuthorisationRefused or RequestRefused or RateLimited or Timeout
        or RemoteError or Unknown;
}

public sealed record RemoteFailure(string Code, string Detail);

/// <summary>A remote exception that exposes only the status and message needed by the shared failure classifier.</summary>
public interface IRemoteFailureSource
{
    HttpStatusCode FailureStatusCode { get; }
    string? FailureDetail { get; }
    string? FailureCode => null;
}

public sealed class CertificateRejectedException(string expected, string presented, Exception? inner = null)
    : AuthenticationException($"Expected certificate SHA-256 {expected}; presented {presented}.", inner)
{
    public string Expected { get; } = expected;
    public string Presented { get; } = presented;
}

public static partial class RemoteFailureClassifier
{
    public const int DetailLimit = 200;

    public static RemoteFailure Classify(Exception exception) => exception switch
    {
        CertificateRejectedException mismatch => Failure(RemoteFailureCode.CertificateRejected, mismatch.Message),
        HttpRequestException { InnerException: CertificateRejectedException mismatch } =>
            Failure(RemoteFailureCode.CertificateRejected, mismatch.Message),
        HttpRequestException { InnerException: AuthenticationException } value =>
            Failure(RemoteFailureCode.TlsHandshake, value.Message),
        AuthenticationException value => Failure(RemoteFailureCode.TlsHandshake, value.Message),
        HttpRequestException { InnerException: SocketException } value =>
            Failure(RemoteFailureCode.Unreachable, value.Message),
        SocketException value => Failure(RemoteFailureCode.Unreachable, value.Message),
        TaskCanceledException value => Failure(RemoteFailureCode.Timeout, value.Message),
        TimeoutException value => Failure(RemoteFailureCode.Timeout, value.Message),
        IRemoteFailureSource { FailureCode: { } code } source when RemoteFailureCode.IsKnown(code) =>
            FromWire(code, source.FailureDetail),
        IRemoteFailureSource source => Classify(source.FailureStatusCode, source.FailureDetail),
        HttpRequestException { StatusCode: { } status } value => Classify(status, value.Message),
        HttpRequestException value => Failure(RemoteFailureCode.Unreachable, value.Message),
        _ => Failure(RemoteFailureCode.Unknown, exception.Message),
    };

    public static RemoteFailure Classify(HttpStatusCode status, string? detail = null) => status switch
    {
        HttpStatusCode.Unauthorized => Failure(RemoteFailureCode.AuthenticationRefused, detail),
        HttpStatusCode.Forbidden => Failure(RemoteFailureCode.AuthorisationRefused, detail),
        HttpStatusCode.BadRequest => Failure(RemoteFailureCode.RequestRefused, detail),
        HttpStatusCode.RequestTimeout => Failure(RemoteFailureCode.Timeout, detail),
        HttpStatusCode.TooManyRequests => Failure(RemoteFailureCode.RateLimited, detail),
        _ when (int)status >= 500 => Failure(RemoteFailureCode.RemoteError, detail),
        _ when (int)status >= 400 => Failure(RemoteFailureCode.RequestRefused, detail),
        _ => Failure(RemoteFailureCode.Unknown, detail),
    };

    public static RemoteFailure FromWire(string? code, string? detail) =>
        Failure(RemoteFailureCode.IsKnown(code) ? code! : RemoteFailureCode.Unknown, detail);

    public static string Sanitise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "No further detail was provided.";
        var clean = AuthorizationHeader().Replace(value, "$1: [redacted]");
        clean = BearerToken().Replace(clean, "[redacted]");
        clean = SecretAssignment().Replace(clean, "$1=[redacted]");
        clean = EmailAddress().Replace(clean, "[redacted-email]");
        clean = ControlCharacters().Replace(clean, " ");
        clean = Whitespace().Replace(clean, " ").Trim();
        return clean.Length <= DetailLimit ? clean : clean[..DetailLimit];
    }

    private static RemoteFailure Failure(string code, string? detail) => new(code, Sanitise(detail));

    // A name that only contains a secret word counts too: remote session cookies carry a product prefix
    // (for example "<PREFIX>SESSION=") and JSON bodies quote both name and value.
    [GeneratedRegex(@"(?i)\b([A-Z0-9_.-]*(?:cookie|token|password|passphrase|secret|session(?:id)?|routeid)[A-Z0-9_.-]*)""?\s*[:=]\s*""?([^\s,;""]+)""?")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Z0-9._~+/=-]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)\b(authorization)\s*:\s*[^\r\n,;]+")]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"[\x00-\x1F\x7F]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Shared leaf-certificate fingerprint rules for direct and relay routes.</summary>
public static class ServiceCertificatePin
{
    public static string? Normalise(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return null;
        var hex = pin.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex
            : throw new InvalidOperationException("service_certificate_pin_invalid");
    }

    public static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public static System.Net.Security.RemoteCertificateValidationCallback Validator(string? pin,
        Action<string, string>? mismatch = null)
    {
        var expected = Normalise(pin);
        return (_, certificate, _, errors) =>
        {
            if (expected is null) return errors == System.Net.Security.SslPolicyErrors.None;
            var presented = certificate is null ? "NONE" : Fingerprint(certificate);
            var accepted = certificate is not null && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected), Convert.FromHexString(presented));
            if (!accepted) mismatch?.Invoke(expected, presented);
            return accepted;
        };
    }
}

public static class RelayProtocol
{
    public const int Version = 2;
}

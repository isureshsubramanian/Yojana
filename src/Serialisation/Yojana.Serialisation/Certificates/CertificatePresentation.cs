using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Certificates;

public sealed record CertificatePresentation(CertificateIssuerSnapshot Issuer, CertificateBrandingSnapshot Branding);

/// <summary>Resolves immutable issue-time presentation without binding the certificate engine to tenant-setting storage.</summary>
public interface ICertificatePresentationPort
{
    Task<CertificatePresentation> ResolveAsync(Guid issuerEntityId, string documentKind, string formType,
        string language, CancellationToken cancellationToken);
}

/// <summary>Neutral host fallback. A tenant or legal-entity module can replace this narrow port.</summary>
public sealed class NeutralCertificatePresentationPort : ICertificatePresentationPort
{
    public Task<CertificatePresentation> ResolveAsync(Guid issuerEntityId, string documentKind, string formType,
        string language, CancellationToken cancellationToken) => Task.FromResult(new CertificatePresentation(
        new CertificateIssuerSnapshot("Issuing entity", null),
        new CertificateBrandingSnapshot("default", null, null)));
}

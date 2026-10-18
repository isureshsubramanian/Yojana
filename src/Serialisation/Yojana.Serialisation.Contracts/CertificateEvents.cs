namespace Yojana.Serialisation.Contracts;

public sealed record CertificateIssued(
    PublishedEventHeader Header,
    Guid CertificateId,
    string DocumentKind,
    string FormType,
    int CertificateVersion,
    string TrackingNumber,
    string PayloadHash,
    Guid TriggeringEventId,
    Guid? SupersedesCertificateId,
    string Provenance,
    DateTime IssuedAt) : IPublishedUnitEvent
{
    public const string Schema = "Certificate.Issued";
    public const string Version = "1.0";
}

public sealed record CertificateSuperseded(
    PublishedEventHeader Header,
    Guid CertificateId,
    Guid SupersedingCertificateId,
    string DocumentKind,
    int CertificateVersion,
    string Reason,
    Guid TriggeringEventId,
    DateTime SupersededAt) : IPublishedUnitEvent
{
    public const string Schema = "Certificate.Superseded";
    public const string Version = "1.0";
}

public sealed record CertificateReprinted(
    PublishedEventHeader Header,
    Guid ReprintId,
    Guid CertificateId,
    int CertificateVersion,
    string TrackingNumber,
    string Reason,
    bool Copy,
    DateTime ReprintedAt) : IPublishedUnitEvent
{
    public const string Schema = "Certificate.Reprinted";
    public const string Version = "1.0";
}

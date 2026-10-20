using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Certificates;

public sealed class RenderedOutboundDocumentPort(CertificateQueries certificates, SerialisationDbContext db,
    ITenantContext tenant, ICertificateRenderer renderer) : IRenderedOutboundDocumentPort
{
    public async Task<OutboundDocumentDescription?> DescribeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var number = await db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.CertificateId == documentId)
            .Select(row => row.TrackingNumber).SingleOrDefaultAsync(cancellationToken);
        if (number is null) return null;
        var extension = renderer.ContentType.Split(';')[0] switch { "application/pdf" => ".pdf", "text/html" => ".html", _ => "" };
        var name = string.Concat(number.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        return new((name.Length == 0 ? "certificate" : name) + extension, renderer.ContentType);
    }

    public async Task<RenderedOutboundDocument?> ReadAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var description = await DescribeAsync(documentId, cancellationToken);
        if (description is null) return null;
        var content = await certificates.RenderAsync(documentId, cancellationToken);
        if (content is null) return null;
        var result = new RenderedOutboundDocument(description.FileName, content.MediaType, content.Bytes);
        return result;
    }
}

using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Certificates;

/// <summary>Tenant-filtered read seam from active certificate layouts to presentation settings.</summary>
public sealed class CertificateLayoutLanguageSource(SerialisationDbContext db, ITenantContext tenant)
    : ICertificateLayoutLanguageSource
{
    public async Task<IReadOnlyList<string>> ActiveLanguagesAsync(CancellationToken cancellationToken) =>
        await db.CertificateTemplateVersions.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.Status == CertificateTemplateStatus.Active)
            .Select(row => row.Language)
            .Distinct()
            .OrderBy(language => language)
            .ToListAsync(cancellationToken);
}

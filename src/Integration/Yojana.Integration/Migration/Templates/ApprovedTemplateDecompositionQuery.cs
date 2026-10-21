using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Templates;

public sealed class ApprovedTemplateDecompositionQuery(IntegrationDbContext db, ITenantContext tenant,
    Yojana.Serialisation.Contracts.IPolicyCatalogueQuery policies)
    : IApprovedTemplateDecompositionQuery
{
    public async Task<ApprovedTemplateDecomposition?> ForStagedContractLineAsync(string sourceSystem,
        string contractLineSourceKey, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var line = await db.Set<ContractLineStagingRow>().AsNoTracking().Where(row => row.TenantId == tenantId
                && row.SourceSystem == sourceSystem && row.SourcePk == contractLineSourceKey
                && row.AbsentSinceBatchId == null)
            .Select(row => new { row.ContractId }).SingleOrDefaultAsync(cancellationToken);
        if (line is null) return null;
        var contract = await db.Set<ServiceContractStagingRow>().AsNoTracking().Where(row =>
                row.TenantId == tenantId && row.SourceSystem == sourceSystem && row.ContractId == line.ContractId
                && row.AbsentSinceBatchId == null)
            .Select(row => new { row.TemplateName }).SingleOrDefaultAsync(cancellationToken);
        if (contract?.TemplateName is null) return null;
        var mapping = await db.TemplateMappings.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.SourceSystem == sourceSystem && row.TemplateName == contract.TemplateName
                && row.State == TemplateMappingState.Approved)
            .Select(row => new { row.TemplateMappingId, row.TemplateKey, row.TemplateName })
            .SingleOrDefaultAsync(cancellationToken);
        if (mapping is null) return null;
        var blockRows = await db.TemplateMappingBlocks.AsNoTracking().Where(block => block.TenantId == tenantId
                && block.TemplateMappingId == mapping.TemplateMappingId)
            .OrderBy(block => block.Position)
            .Select(block => new { block.FamilyCode, block.PolicyId, block.PolicyVersion })
            .ToListAsync(cancellationToken);
        var published = (await policies.ReadAsync(cancellationToken)).Policies
            .Select(policy => policy.PolicyId).ToHashSet();
        if (blockRows.Count == 0 || blockRows.Any(block => block.PolicyId is null
                || block.PolicyVersion is null || !published.Contains(block.PolicyId.Value))) return null;
        var blocks = blockRows.Select(block => new ApprovedTemplateBlock(block.FamilyCode,
            block.PolicyId!.Value, block.PolicyVersion!.Value)).ToList();
        return new(mapping.TemplateKey, mapping.TemplateName, blocks);
    }
}

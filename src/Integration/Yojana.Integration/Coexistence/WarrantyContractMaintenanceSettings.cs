using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Coexistence;

/// <summary>Tenant-wide owner of OD-36's contract-maintenance choice.</summary>
public sealed class WarrantyContractMaintenanceSettings(
    IntegrationDbContext db,
    ITenantContext tenant,
    ITenantSettings settings,
    IClock clock,
    IConnectionAuditSink audit)
{
    public const string SettingKey = "warranty.contracts_maintained_in";

    public async Task<WarrantyContractMaintenanceView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        var value = stored?.Value ?? WarrantyContractsMaintainedIn.IncumbentUntilCutover;
        Validate(value);
        return new(value, stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<WarrantyContractMaintenanceView> UpdateAsync(Guid auditConnectionId,
        UpdateWarrantyContractMaintenance request, string actor, CancellationToken cancellationToken)
    {
        CoexistenceGuard.Actor(actor);
        Validate(request.MaintainedIn);
        _ = await db.Connections.AsNoTracking().SingleOrDefaultAsync(connection =>
                connection.TenantId == tenant.TenantId && connection.ConnectionId == auditConnectionId,
                cancellationToken)
            ?? throw new CoexistenceRuleException("connection_not_found", "The connection was not found.");
        var before = await GetAsync(cancellationToken);
        if (before.Version != request.ExpectedVersion)
            throw new TenantSettingVersionConflictException(SettingKey, request.ExpectedVersion, before.Version);
        var saved = await settings.SetAsync(SettingKey, request.MaintainedIn, request.ExpectedVersion,
            cancellationToken);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), auditConnectionId,
            ConnectionAuditEventType.WarrantyContractMaintenanceChanged, actor, clock.UtcNow,
            JsonSerializer.Serialize(new { Before = before.MaintainedIn, After = request.MaintainedIn },
                IntegrationContracts.Json)), cancellationToken);
        return new(request.MaintainedIn, saved.Version);
    }

    private static void Validate(string value)
    {
        if (value is not (WarrantyContractsMaintainedIn.IncumbentUntilCutover
            or WarrantyContractsMaintainedIn.Module))
            throw new CoexistenceRuleException("warranty_contract_maintenance_invalid",
                "Choose module or incumbent_until_cutover.");
    }
}

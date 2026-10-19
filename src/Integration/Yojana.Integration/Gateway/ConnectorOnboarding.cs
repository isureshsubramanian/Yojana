using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Gateway;

public sealed record ConnectorOnboardingChecklistItem(string Code, string Label, bool Required, string Detail);

public sealed record ConnectorOnboardingChecklist(
    string ConnectorCode,
    string DisplayName,
    string CustomerCostDisclosure,
    IReadOnlyList<ConnectorOnboardingChecklistItem> Items);

/// <summary>Tenant-owned catalogue data; operators may replace every generic label and disclosure after seeding.</summary>
public sealed class ConnectorCatalogue(IntegrationDbContext db, ITenantContext tenant, IClock clock)
{
    private static readonly string[] Codes =
    [
        "CX01", "CX02", "CX03", "CX04", "CX05", "CX06", "CX07", "CX08",
        "CX09", "CX10", "CX11", "CX12", "CX13", "CX14", "CX15", "CX16", "CX90", "CX91",
    ];

    public async Task<int> SeedGenericEntriesAsync(CancellationToken cancellationToken)
    {
        var existing = await db.ConnectorCatalogue.Where(x => x.TenantId == tenant.TenantId)
            .Select(x => x.ConnectorCode).ToListAsync(cancellationToken);
        var missing = Codes.Except(existing, StringComparer.Ordinal).ToList();
        foreach (var code in missing)
        {
            db.ConnectorCatalogue.Add(new ConnectorCatalogueEntry
            {
                TenantId = tenant.TenantId,
                ConnectorCode = code,
                DisplayName = $"Connector {code}",
                CustomerCostDisclosure = "Confirm licence, API access, hosting, implementation and recurring usage costs with the customer before activation.",
                Enabled = false,
                UpdatedAt = clock.UtcNow,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        return missing.Count;
    }
}

/// <summary>Builds an onboarding contract from stored capabilities rather than connector-specific branches.</summary>
public sealed class ConnectorOnboardingChecklistGenerator(
    IntegrationDbContext db,
    ITenantContext tenant)
{
    public async Task<ConnectorOnboardingChecklist> GenerateAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connection = await db.Connections.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, cancellationToken);
        var catalogue = await db.ConnectorCatalogue.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectorCode == connection.ConnectorCode, cancellationToken);
        var declarations = await db.ConnectionCapabilities.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId)
            .OrderBy(x => x.CanonicalMessage).ToListAsync(cancellationToken);
        var items = new List<ConnectorOnboardingChecklistItem>
        {
            new("identity", "Create a least-privilege connection identity", true,
                "Record the granted permissions and verify unattended access."),
            new("credentials", "Store and test connection credentials", true,
                "Set expiry and rotation dates before activation."),
            new("poll", "Configure modified-since polling", true,
                "Choose each entity's interval, initial watermark and overlap window."),
            new("costs", "Acknowledge customer-side costs", true, catalogue.CustomerCostDisclosure),
        };
        items.AddRange(declarations.Select(x => new ConnectorOnboardingChecklistItem(
            $"capability:{x.CanonicalMessage}", $"Confirm {x.CanonicalMessage}",
            x.SupportLevel != CapabilitySupport.Unsupported,
            $"Declared support: {x.SupportLevel}. Confirm the configured degradation path.")));
        return new(connection.ConnectorCode, catalogue.DisplayName, catalogue.CustomerCostDisclosure, items);
    }
}

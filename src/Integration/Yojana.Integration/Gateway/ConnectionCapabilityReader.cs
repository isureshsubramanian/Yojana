using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Gateway;

public sealed record CapabilityDeclaration(string CanonicalMessage, string SupportLevel);
public sealed record DegradationRuleSetting(int RuleNumber, bool Enabled, string SettingsJson);
public sealed record ConnectionCapabilities(Guid ConnectionId, IReadOnlyDictionary<string, CapabilityDeclaration> Messages,
    IReadOnlyDictionary<int, DegradationRuleSetting> DegradationRules, Guid? LedgerConnectionId)
{
    public CapabilityDeclaration For(string message) => Messages.TryGetValue(message, out var value)
        ? value : new(message, CapabilitySupport.Unsupported);
}

/// <summary>Reads the adapter declaration and tenant choices; it contains no adapter-specific branch.</summary>
public sealed class ConnectionCapabilityReader(IntegrationDbContext db, ITenantContext tenant)
{
    public async Task<ConnectionCapabilities> ReadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var capabilities = await db.ConnectionCapabilities.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).ToListAsync(cancellationToken);
        var rules = await db.ConnectionDegradationSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).ToListAsync(cancellationToken);
        var ledger = await db.ConnectionGatewaySettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .Select(x => x.LedgerConnectionId).SingleOrDefaultAsync(cancellationToken);
        return new(connectionId,
            capabilities.ToDictionary(x => x.CanonicalMessage, x => new CapabilityDeclaration(x.CanonicalMessage, x.SupportLevel), StringComparer.Ordinal),
            rules.ToDictionary(x => x.RuleNumber, x => new DegradationRuleSetting(x.RuleNumber, x.Enabled, x.SettingsJson)), ledger);
    }
}

public static class DegradationRule
{
    public const int SerialsUnknown = 1;
    public const int SerialsIncomplete = 2;
    public const int OutboundManual = 3;
    public const int PollOnly = 4;
    public const int DownstreamLedger = 5;
    public const int SerialDedupe = 6;
    public const int ModuleCreatesSerials = 7;
}

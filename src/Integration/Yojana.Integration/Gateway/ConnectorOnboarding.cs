using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Gateway;

/// <summary>
/// One onboarding step. <paramref name="EvidencePrompt"/> is set when confirming the item needs a written record
/// (who acknowledged the costs, which user was created); <paramref name="Status"/> is pending, confirmed, or stale
/// when the item text changed after it was confirmed.
/// </summary>
public sealed record ConnectorOnboardingChecklistItem(
    string Code,
    string Label,
    bool Required,
    string Detail,
    string? EvidencePrompt = null,
    string Status = OnboardingItemStatus.Pending,
    string? Evidence = null,
    string? ConfirmedBy = null,
    DateTime? ConfirmedAt = null);

public sealed record ConnectorOnboardingChecklist(
    string ConnectorCode,
    string DisplayName,
    string CustomerCostDisclosure,
    IReadOnlyList<ConnectorOnboardingChecklistItem> Items)
{
    public bool RequiredItemsConfirmed => Items.Where(x => x.Required)
        .All(x => x.Status == OnboardingItemStatus.Confirmed);
}

public static class OnboardingItemStatus
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Stale = "stale";
}

public static class OnboardingItemCode
{
    public const string IntegrationUser = "integration-user";
    public const string Credentials = "credentials";
    public const string Poll = "poll";
    public const string Costs = "costs";
}

public sealed record ConfirmOnboardingItemRequest(string? Evidence);

public sealed class OnboardingRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}

/// <summary>Tenant-owned catalogue data; operators may replace every generic label and disclosure after seeding.</summary>
public sealed class ConnectorCatalogue(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IConnectorOnboardingProfiles profiles)
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
                CustomerCostDisclosure = profiles.For(code).CustomerCostDisclosure,
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
    ITenantContext tenant,
    IConnectorOnboardingProfiles profiles)
{
    public async Task<ConnectorOnboardingChecklist> GenerateAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, cancellationToken)
            ?? throw new OnboardingRuleException("onboarding_connection_not_found", "The connection was not found.");
        var catalogue = await db.ConnectorCatalogue.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectorCode == connection.ConnectorCode, cancellationToken);
        var declarations = await db.ConnectionCapabilities.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId)
            .OrderBy(x => x.CanonicalMessage).ToListAsync(cancellationToken);
        var confirmations = await db.ConnectionOnboardingConfirmations.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId)
            .ToDictionaryAsync(x => x.ItemCode, StringComparer.Ordinal, cancellationToken);
        var user = profiles.For(connection.ConnectorCode).IntegrationUser;
        var items = new List<ConnectorOnboardingChecklistItem>
        {
            new(OnboardingItemCode.IntegrationUser, user.Label, true, user.Detail, user.EvidencePrompt),
            new(OnboardingItemCode.Credentials, "Store and test connection credentials", true,
                "Set expiry and rotation dates before activation."),
            new(OnboardingItemCode.Poll, "Configure modified-since polling", true,
                "Choose each entity's interval, initial watermark and overlap window."),
            new(OnboardingItemCode.Costs, "Disclose and acknowledge customer-side costs", true,
                catalogue.CustomerCostDisclosure,
                "Who at the customer acknowledged these costs, and the reference (for example the quote number)"),
        };
        items.AddRange(declarations.Select(x => new ConnectorOnboardingChecklistItem(
            $"capability:{x.CanonicalMessage}", $"Confirm {x.CanonicalMessage}",
            x.SupportLevel != CapabilitySupport.Unsupported,
            $"Declared support: {x.SupportLevel}. Confirm the configured degradation path.")));
        return new(connection.ConnectorCode, catalogue.DisplayName, catalogue.CustomerCostDisclosure,
            items.Select(item => WithConfirmation(item, confirmations.GetValueOrDefault(item.Code))).ToList());
    }

    internal static string TextHash(ConnectorOnboardingChecklistItem item) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Label + "\n" + item.Detail)));

    private static ConnectorOnboardingChecklistItem WithConfirmation(ConnectorOnboardingChecklistItem item,
        ConnectionOnboardingConfirmation? confirmation) => confirmation is null ? item : item with
    {
        Status = string.Equals(confirmation.TextHash, TextHash(item), StringComparison.OrdinalIgnoreCase)
            ? OnboardingItemStatus.Confirmed : OnboardingItemStatus.Stale,
        Evidence = confirmation.Evidence,
        ConfirmedBy = confirmation.ConfirmedBy,
        ConfirmedAt = confirmation.ConfirmedAt,
    };
}

/// <summary>Records an operator's confirmation of one checklist item against the text shown at the time.</summary>
public sealed class ConnectorOnboardingConfirmations(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    ConnectorOnboardingChecklistGenerator checklists,
    IConnectionAuditSink audit)
{
    private const int EvidenceMaxLength = 500;

    public async Task<ConnectorOnboardingChecklist> ConfirmAsync(Guid connectionId, string itemCode,
        ConfirmOnboardingItemRequest request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new OnboardingRuleException("audit_actor_required", "The authenticated user has no actor id.");
        var status = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).Select(x => x.Status).SingleOrDefaultAsync(cancellationToken)
            ?? throw new OnboardingRuleException("onboarding_connection_not_found", "The connection was not found.");
        if (status == ConnectionStatus.Archived)
            throw new OnboardingRuleException("onboarding_connection_archived",
                "An archived connection cannot be onboarded.");
        var checklist = await checklists.GenerateAsync(connectionId, cancellationToken);
        var item = checklist.Items.SingleOrDefault(x => x.Code == itemCode)
            ?? throw new OnboardingRuleException("onboarding_item_not_found", "The checklist item was not found.");
        var evidence = request.Evidence?.Trim() ?? "";
        if (item.EvidencePrompt is not null && evidence.Length == 0)
            throw new OnboardingRuleException("onboarding_evidence_required", item.EvidencePrompt + " is required.");
        if (evidence.Length > EvidenceMaxLength)
            throw new OnboardingRuleException("onboarding_evidence_too_long",
                $"Evidence must not exceed {EvidenceMaxLength} characters.");

        var now = clock.UtcNow;
        var hash = ConnectorOnboardingChecklistGenerator.TextHash(item);
        var row = await db.ConnectionOnboardingConfirmations.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.ItemCode == itemCode, cancellationToken);
        if (row is null)
        {
            row = new ConnectionOnboardingConfirmation
            {
                TenantId = tenant.TenantId, ConnectionId = connectionId, ItemCode = itemCode,
                TextHash = hash, Evidence = evidence, ConfirmedBy = actor, ConfirmedAt = now,
            };
            db.ConnectionOnboardingConfirmations.Add(row);
        }
        else
        {
            row.TextHash = hash;
            row.Evidence = evidence;
            row.ConfirmedBy = actor;
            row.ConfirmedAt = now;
        }
        // COUPLING (shared unit of work): the SQL audit sink, EfConnectionAuditSink, saves this scoped
        // IntegrationDbContext inside its SERIALIZABLE transaction, so the pending confirmation commits atomically with
        // its connection_event row. The sink is used directly or as the journal behind JournalledConnectionAuditSink;
        // with the journalled sink the immutable copy is written first. Resolving the SQL sink with its own context
        // would let a confirmation exist without its audit record.
        await audit.AppendAsync(new(tenant.TenantId, Guid.NewGuid(), connectionId,
            ConnectionAuditEventType.OnboardingItemConfirmed, actor, now,
            JsonSerializer.Serialize(new { item = itemCode, text_hash = hash, evidence })), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await checklists.GenerateAsync(connectionId, cancellationToken);
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Certificates;

/// <summary>Tenant-owned release-record timing, isolated from trigger handlers behind this narrow policy contract.</summary>
public sealed class ReleaseRecordIssuePointSettings(ITenantSettings settings)
{
    public const string Key = "certificates.release_record_issue_point";
    public static readonly ReleaseRecordIssuePointPolicy Default = new(ReleaseRecordIssuePoint.AtBuildRelease, []);

    // Coupled to the factory registration in SerialisationModule: exposing ITenantSettings directly to certificate
    // handlers would expose PlatformDbContext beside SerialisationDbContext and break Wolverine transaction discovery.
    public async Task<ReleaseRecordIssuePointSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(Key, cancellationToken);
        var policy = stored is null ? Default : Deserialise(stored.Value);
        return new(policy.DefaultIssuePoint, policy.ItemClassOverrides,
            stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<ReleaseRecordIssuePointSettingsView> SetAsync(UpdateReleaseRecordIssuePointSettings update,
        CancellationToken cancellationToken)
    {
        var policy = Normalise(new(update.DefaultIssuePoint, update.ItemClassOverrides));
        var stored = await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options),
            update.ExpectedVersion, cancellationToken);
        return new(policy.DefaultIssuePoint, policy.ItemClassOverrides, stored.Version);
    }

    public async Task<string> ResolveAsync(string? itemClass, CancellationToken cancellationToken)
    {
        var view = await GetAsync(cancellationToken);
        return Resolve(view, itemClass);
    }

    public static string Resolve(ReleaseRecordIssuePointSettingsView view, string? itemClass) =>
        itemClass is null
            ? view.DefaultIssuePoint
            : view.ItemClassOverrides.FirstOrDefault(row =>
                string.Equals(row.ItemClass, itemClass, StringComparison.OrdinalIgnoreCase))?.IssuePoint
                ?? view.DefaultIssuePoint;

    private static ReleaseRecordIssuePointPolicy Deserialise(string text) =>
        Normalise(JsonSerializer.Deserialize<ReleaseRecordIssuePointPolicy>(text, LedgerJson.Options) ?? Default);

    private static ReleaseRecordIssuePointPolicy Normalise(ReleaseRecordIssuePointPolicy policy)
    {
        RequireIssuePoint(policy.DefaultIssuePoint);
        var rows = policy.ItemClassOverrides.Select(row => new ReleaseRecordIssuePointOverride(
                row.ItemClass.Trim(), row.IssuePoint.Trim()))
            .Where(row => row.ItemClass.Length > 0)
            .OrderBy(row => row.ItemClass, StringComparer.OrdinalIgnoreCase).ToList();
        if (rows.Any(row => row.ItemClass.Length > 100))
            throw new CommandRefusedException("certificate_issue_point_item_class_invalid",
                "An item class override cannot exceed 100 characters.");
        if (rows.GroupBy(row => row.ItemClass, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new CommandRefusedException("certificate_issue_point_item_class_duplicate",
                "Each item class can have only one release-record issue-point override.");
        foreach (var row in rows) RequireIssuePoint(row.IssuePoint);
        return new(policy.DefaultIssuePoint.Trim(), rows);
    }

    private static void RequireIssuePoint(string value)
    {
        if (!ReleaseRecordIssuePoint.All.Contains(value.Trim(), StringComparer.Ordinal))
            throw new CommandRefusedException("certificate_issue_point_unknown",
                $"Unknown release-record issue point '{value}'.");
    }
}

public sealed class ReleaseRecordIssuePolicy(
    SerialisationDbContext db,
    ReleaseRecordIssuePointSettings settings)
{
    public const string SelectionTrigger = "Certificate.SelectionIssued";
    public const string RequestedTrigger = "Certificate.IssueRequested";

    public async Task<ReleaseRecordIssueDecision> DecideAsync(CertificateUnitFact unit, string? itemClass,
        string triggerSchema, bool chainExists, CancellationToken cancellationToken)
    {
        if (chainExists) return ReleaseRecordIssueDecision.Allowed;

        var issuePoint = await settings.ResolveAsync(itemClass, cancellationToken);
        var manual = triggerSchema is SelectionTrigger or RequestedTrigger;
        if (manual)
        {
            if (issuePoint == ReleaseRecordIssuePoint.OnRequest) return ReleaseRecordIssueDecision.Allowed;
            if (issuePoint == ReleaseRecordIssuePoint.AtBuildRelease && unit.Build is not null)
                return ReleaseRecordIssueDecision.Allowed;
            if (await HasCustomerSaleAsync(unit.UnitId, cancellationToken)) return ReleaseRecordIssueDecision.Allowed;
            return NotReached(issuePoint);
        }

        // A replacement shipment is the replacement unit's first customer shipment even though its custody reason is
        // "replacement", not "sale". A corrected link establishes the same fact for the unit that physically shipped.
        // These published replacement facts are the narrow issue-point contract; relying on custody reason would miss both.
        var firstSale = triggerSchema is CustodyChanged.Schema or OwnershipOpened.Schema
            or ReplacementShipped.Schema or ReplacementLinkCorrected.Schema;
        var allowed = issuePoint switch
        {
            ReleaseRecordIssuePoint.AtBuildRelease => unit.Build is not null
                ? triggerSchema == UnitReleased.Schema
                : firstSale,
            ReleaseRecordIssuePoint.AtFirstShipment => firstSale,
            ReleaseRecordIssuePoint.OnRequest => false,
            _ => false,
        };
        return allowed ? ReleaseRecordIssueDecision.Allowed : NotReached(issuePoint);
    }

    public async Task<bool> HasCustomerSaleAsync(Guid unitId, CancellationToken cancellationToken) =>
        (await CustomerSaleTimesAsync([unitId], cancellationToken)).ContainsKey(unitId);

    public async Task<IReadOnlyDictionary<Guid, DateTime>> CustomerSaleTimesAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0) return new Dictionary<Guid, DateTime>();
        var rows = await db.Episodes.AsNoTracking().Where(row => unitIds.Contains(row.UnitId)
                && row.Reason == EpisodeReason.Sale && row.PartyType == PartyType.Customer
                && (row.Kind == EpisodeKind.Ownership
                    || row.Kind == EpisodeKind.Custody && row.LocationKind == LocationKind.CustomerSite))
            .Select(row => new { row.UnitId, row.ValidFrom, row.ClosingEventId })
            .ToListAsync(cancellationToken);
        var closingIds = rows.Select(row => row.ClosingEventId).OfType<Guid>().Distinct().ToList();
        var closingFacts = closingIds.Count == 0
            ? []
            : await db.Events.AsNoTracking().Where(row => closingIds.Contains(row.EventId))
                .Select(row => new { row.EventId, row.EventType, row.Attributes }).ToListAsync(cancellationToken);
        var corrected = closingFacts.Where(row => row.EventType == UnitEventType.ShipmentReversed
                || HasCorrection(row.Attributes))
            .Select(row => row.EventId).ToHashSet();
        return rows.Where(row => row.ClosingEventId is null || !corrected.Contains(row.ClosingEventId.Value))
            .GroupBy(row => row.UnitId)
            .ToDictionary(group => group.Key, group => group.Min(row => row.ValidFrom));
    }

    public async Task<bool> SaleFactWasCorrectedAsync(Guid unitId, Guid triggeringEventId,
        CancellationToken cancellationToken)
    {
        var original = await db.Events.AsNoTracking().Where(row => row.UnitId == unitId
                && row.EventId == triggeringEventId)
            .Select(row => new { row.DocumentType, row.DocumentId, row.DocumentLine })
            .SingleOrDefaultAsync(cancellationToken);
        if (original is null) return false;
        return await db.Events.AsNoTracking().AnyAsync(row => row.UnitId == unitId
            && row.EventType == UnitEventType.ShipmentReversed
            && (row.ReversesEventId == triggeringEventId
                || original.DocumentType != null && original.DocumentId != null
                && row.DocumentType == original.DocumentType && row.DocumentId == original.DocumentId
                && (original.DocumentLine == null || row.DocumentLine == original.DocumentLine)), cancellationToken);
    }

    private static ReleaseRecordIssueDecision NotReached(string issuePoint) => issuePoint switch
    {
        ReleaseRecordIssuePoint.AtFirstShipment => new(false, "release_record_issue_point_not_reached",
            "The unit has not yet shipped to a customer."),
        ReleaseRecordIssuePoint.OnRequest => new(false, "release_record_waiting_for_request",
            "This tenant issues release records only on request."),
        _ => new(false, "release_record_issue_point_not_reached",
            "The configured build-release issue point was not reached by this trigger."),
    };

    private static bool HasCorrection(string? attributes)
    {
        if (attributes is null) return false;
        using var document = JsonDocument.Parse(attributes);
        return document.RootElement.TryGetProperty("correction_id", out _);
    }
}

public sealed record ReleaseRecordIssueDecision(bool Issue, string? Reason, string? Detail)
{
    public static readonly ReleaseRecordIssueDecision Allowed = new(true, null, null);
}

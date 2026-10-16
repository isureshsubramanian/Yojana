using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

/// <summary>
/// Freezes the as-built into an immutable BuildRecord version (ADR-0001 BuildRecord): identifiers, item and model
/// version, the open AssemblyLinks with their serials, test results and the release signature, hashed with SHA-256.
/// Written by ReleaseUnit (version 1) and by every reassembly (the next version), never changed afterwards.
/// </summary>
internal static class BuildRecords
{
    public static async Task<BuildRecord> FreezeAsync(SerialisationDbContext db, TenantId tenantId, Unit unit, UnitItemAssignment assignment, Guid releaseEventId, DateTime releasedAt,
        string? releaseSignatureRef, string? revision, Guid? facilityId, string? testResultsJson, CancellationToken cancellationToken)
    {
        var openIdentifiers = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && i.UnitId == unit.UnitId && i.ValidTo == null).OrderBy(i => i.Type).ToListAsync(cancellationToken);
        if (openIdentifiers.Count == 0)
        {
            throw new CommandRefusedException("no_identifier", $"Unit {unit.UnitId} has no open identifier; a release record needs one.", "AssignIdentifier first");
        }

        var children = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ParentUnitId == unit.UnitId && l.RemovedAt == null).OrderBy(l => l.Slot).ToListAsync(cancellationToken);
        // Links added in this unit of work are tracked but not yet queryable; include them.
        children.AddRange(db.ChangeTracker.Entries<AssemblyLink>().Where(e => e.State == EntityState.Added && e.Entity.ParentUnitId == unit.UnitId && e.Entity.RemovedAt == null).Select(e => e.Entity));
        children = children.DistinctBy(c => c.LinkId).Where(c => db.ChangeTracker.Entries<AssemblyLink>().All(e => e.Entity.LinkId != c.LinkId || e.Entity.RemovedAt == null)).OrderBy(c => c.Slot).ToList();
        var childUnitIds = children.Where(c => c.ChildUnitId is not null).Select(c => c.ChildUnitId!.Value).ToList();
        var childSerials = childUnitIds.Count == 0 ? [] : await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && childUnitIds.Contains(i.UnitId) && i.ValidTo == null).Select(i => new { i.UnitId, i.Type, i.NormalisedValue }).ToListAsync(cancellationToken);
        var version = (await db.BuildRecords.AsNoTracking().Where(b => b.TenantId == tenantId && b.UnitId == unit.UnitId).Select(b => (int?)b.Version).MaxAsync(cancellationToken) ?? 0) + 1;
        var payload = JsonSerializer.Serialize(new
        {
            unit_id = unit.UnitId,
            version,
            released_at = releasedAt,
            item_id = assignment.ItemId,
            model_version_id = assignment.ModelVersionId,
            revision,
            facility_id = facilityId,
            lot_id = unit.LotId,
            identifiers = openIdentifiers.Select(i => new { i.Type, i.Scheme, value = i.NormalisedValue, i.Qualifier, issuer = i.IssuerPartyRef }),
            as_built = children.Select(c => new
            {
                c.Slot, c.Relation, child_unit_id = c.ChildUnitId, child_item_id = c.ChildItemId, child_lot_id = c.ChildLotId, c.Quantity, c.ReleaseStatus, through_phantom_item_id = c.ThroughPhantomItemId,
                serials = c.ChildUnitId is null ? null : childSerials.Where(s => s.UnitId == c.ChildUnitId).Select(s => new { s.Type, s.NormalisedValue }),
            }),
            test_results = testResultsJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(testResultsJson),
            release_signature_ref = releaseSignatureRef,
            release_event_id = releaseEventId,
        }, LedgerJson.Options);
        var record = new BuildRecord
        {
            TenantId = tenantId, BuildRecordId = TimeOrderedId.New(releasedAt), UnitId = unit.UnitId, Version = version, ReleasedAt = releasedAt, ReleaseEventId = releaseEventId,
            ItemId = assignment.ItemId, ModelVersionId = assignment.ModelVersionId, Revision = revision, FacilityId = facilityId,
            Payload = payload, PayloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), ReleaseSignatureRef = releaseSignatureRef,
        };
        db.BuildRecords.Add(record);
        return record;
    }
}

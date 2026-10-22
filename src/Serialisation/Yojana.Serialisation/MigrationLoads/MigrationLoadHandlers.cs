using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.MigrationLoads;

/// <summary>
/// <see cref="ApplyMigratedUnits"/> (P9-T10, OD-52): one chunk of a reviewed load plan written set-based through
/// <see cref="BulkIngestion"/> in one transaction. Per unit: a creating fact (origin migrated, the plan's history
/// depth and inferred condition), one state-neutral <see cref="UnitEventType.MigratedHistory"/> fact per source
/// movement, then the current custody row (in stock at a warehouse) and the current ownership row, each opened by
/// its own fact at its real date. Units already in <c>migration_load_unit</c> for the load come back as already
/// applied; a unit whose identifier is open on another unit is refused with the rule, never merged.
/// </summary>
public static class ApplyMigratedUnitsHandler
{
    public const string Source = "serialisation.migration";
    public const int MaxUnitsPerChunk = 2000;

    public static async Task<ApplyMigratedUnitsResult> Handle(ApplyMigratedUnits command, SerialisationDbContext db, BulkIngestion bulk,
        IdentifierService identifiers, EffectiveItemProfiles items, LedgerPublication publication, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        if (command.Units.Count > MaxUnitsPerChunk)
        {
            throw new CommandRefusedException("chunk_too_large", $"A chunk carries at most {MaxUnitsPerChunk} units; this one has {command.Units.Count}.", "send smaller chunks");
        }

        // TIGHT COUPLING (OD-52, migrated history publishes nothing per unit)
        // WHAT:  the ownership and custody facts below are the same event types a live sale writes, so the append
        //        observers (CustomerSaleCertificateObserver) would issue a release certificate per unit and the mapper
        //        would publish per-unit events. The scoped LedgerPublication switch is the seam both honour.
        // WHY:   a first-customer load is ~420 000 units; per-unit certificates and outbound events would flood the
        //        render worker and every connected system with facts that already happened in the source.
        // WHAT BREAKS IF SEPARATED: dropping this line renders a certificate for every migrated owned unit. Migrated
        //        certificates (provenance migrated) are a separate apply step; the summary MigrationLoadApplied is
        //        published by the caller.
        publication.Suppressed = true;

        var tenantId = tenant.TenantId;
        var now = clock.UtcNow;
        var planIds = command.Units.Select(u => u.PlanUnitId).ToList();
        var done = await db.MigrationLoadUnits.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.LoadId == command.LoadId && planIds.Contains(r.PlanUnitId))
            .ToDictionaryAsync(r => r.PlanUnitId, r => r.UnitId, cancellationToken);

        var profiles = new Dictionary<Guid, ItemSerialisationProfile?>();
        foreach (var itemId in command.Units.Select(u => u.ItemId).Distinct())
        {
            profiles[itemId] = await items.GetAsync(itemId, now, cancellationToken);
        }

        var outcomes = new List<MigratedUnitOutcome>();
        var prepared = new List<(MigratedUnit Unit, List<PreparedIdentifier> Ids)>();
        foreach (var unit in command.Units.DistinctBy(u => u.PlanUnitId))
        {
            if (done.TryGetValue(unit.PlanUnitId, out var existingUnit))
            {
                outcomes.Add(new MigratedUnitOutcome(unit.PlanUnitId, existingUnit, MigratedUnitOutcome.AlreadyApplied, []));
                continue;
            }

            var reasons = Refusals(unit);
            var profile = profiles[unit.ItemId];
            if (profile is null) reasons.Add("item_unknown");
            else if (profile.TrackingMode != ItemSerialisationProfile.TrackingModes.Serial) reasons.Add("item_not_serial_tracked");
            var ids = unit.Identifiers.Select(input => identifiers.Prepare(input, unit.ItemId, null)).ToList();
            reasons.AddRange(ids.Where(p => p.Blocks).SelectMany(p => p.Validation.Reasons.Select(r => p.Input.Type + ":" + r)));
            if (reasons.Count > 0)
            {
                outcomes.Add(new MigratedUnitOutcome(unit.PlanUnitId, null, MigratedUnitOutcome.Refused, reasons));
                continue;
            }

            prepared.Add((unit, ids));
        }

        // Identity rule 1 as one set: values repeated inside the chunk, values open on a unit already in the module.
        var probe = prepared.SelectMany(p => p.Ids.Select(id => new UnitIdentifier
        {
            TenantId = tenantId, IdentifierId = Guid.Empty, UnitId = Guid.Empty, Type = id.Input.Type, Scheme = id.Policy.Scheme,
            ScopeKey = id.ScopeKey, RawValue = id.Input.RawValue, NormalisedValue = id.Normalised, ActorRef = command.ActorRef,
        })).ToList();
        var conflicts = (await bulk.ValidateIdentifiersAsync(probe, cancellationToken))
            .Select(c => (c.Scheme, c.ScopeKey, c.NormalisedValue, c.Reason)).ToList();
        if (conflicts.Count > 0)
        {
            var byKey = conflicts.ToLookup(c => (c.Scheme, c.ScopeKey, c.NormalisedValue), c => c.Reason);
            prepared.RemoveAll(p =>
            {
                var hit = p.Ids.SelectMany(id => byKey[(id.Policy.Scheme, id.ScopeKey, id.Normalised)].Select(r => id.Input.Type + ":" + r)).Distinct().ToList();
                if (hit.Count == 0) return false;
                outcomes.Add(new MigratedUnitOutcome(p.Unit.PlanUnitId, null, MigratedUnitOutcome.Refused, hit));
                return true;
            });
        }

        var events = new List<UnitEventDraft>();
        var units = new List<Unit>();
        var identifierRows = new List<UnitIdentifier>();
        var assignments = new List<UnitItemAssignment>();
        var episodes = new List<CustodyEpisode>();
        var register = new List<MigrationLoadUnit>();
        foreach (var (unit, ids) in prepared)
        {
            var unitId = TimeOrderedId.New(unit.CreatedAt);
            var createdEventId = TimeOrderedId.New(unit.CreatedAt);
            var status = UnitLifecycle.Require(null, UnitEventType.Commissioned, UnitOrigin.Migrated, unitId);
            var written = 0;
            string Key(string type, DateTime at) => IdempotencyKey.Derive(
                IdempotencyKey.ForCommand(Source, command.LoadId, unitId, type, at), (written++).ToString(System.Globalization.CultureInfo.InvariantCulture));

            events.Add(new UnitEventDraft
            {
                UnitId = unitId, EventTime = unit.CreatedAt, EventType = UnitEventType.Commissioned, PresetEventId = createdEventId,
                BizStep = "commissioning", Disposition = UnitLifecycle.DispositionFor(status), LegalEntityId = command.LegalEntityId,
                ActorRef = command.ActorRef, IdempotencyKey = Key(UnitEventType.Commissioned, unit.CreatedAt),
                Attributes = JsonObjectText.Of(new Dictionary<string, object?>
                {
                    ["load_id"] = command.LoadId, ["plan_unit_id"] = unit.PlanUnitId, ["origin"] = UnitOrigin.Migrated,
                    ["history_depth"] = unit.HistoryDepth, ["source"] = command.Source,
                    ["condition_provenance"] = unit.ConditionProvenance,
                    ["identifiers"] = ids.Select(id => new { type = id.Input.Type, scheme = id.Policy.Scheme, value = id.Normalised, issuer_party_ref = id.Input.IssuerPartyRef }).ToList(),
                }),
                Delta = new CurrentStateDelta
                {
                    Create = new CreateCurrentState(unit.ItemId, status, UnitLifecycle.DispositionFor(status)),
                    ConditionGrade = unit.ConditionGrade,
                },
            });

            foreach (var movement in unit.History)
            {
                var at = movement.At < unit.CreatedAt ? unit.CreatedAt : movement.At;
                events.Add(new UnitEventDraft
                {
                    UnitId = unitId, EventTime = at, EventType = UnitEventType.MigratedHistory, LegalEntityId = command.LegalEntityId,
                    ActorRef = command.ActorRef, IdempotencyKey = Key(UnitEventType.MigratedHistory, at),
                    Attributes = JsonObjectText.Of(new Dictionary<string, object?>
                    {
                        ["load_id"] = command.LoadId, ["movement_kind"] = movement.MovementKind, ["provenance"] = movement.Provenance,
                        ["document_label"] = movement.DocumentLabel, ["document_number"] = movement.DocumentNumber,
                        ["party_code"] = movement.PartyCode, ["warehouse_code"] = movement.WarehouseCode, ["quantity"] = movement.Quantity,
                    }),
                });
            }

            // HIDDEN COUPLING with EpisodeService.PrepareOwnershipOpenAsync: live ownership openings raise an entitlement
            // requirement exception per unit (EntitlementRequirementWriter). Migrated owners do not: the entitlement gap
            // report and BackfillEntitlements run as the last step of every load (P9-T09) and are the single place
            // migrated units without cover are found. If a load ever skips that step, units sold without cover go unseen.
            if (unit.Custody is { } custody)
            {
                var at = custody.From < unit.CreatedAt ? unit.CreatedAt : custody.From;
                var eventId = TimeOrderedId.New(at);
                var episodeId = TimeOrderedId.New(at);
                episodes.Add(new CustodyEpisode
                {
                    TenantId = tenantId, EpisodeId = episodeId, UnitId = unitId, Kind = EpisodeKind.Custody, PartyType = PartyType.Us,
                    LegalEntityId = command.LegalEntityId, LocationId = custody.LocationId, LocationKind = custody.LocationKind,
                    Reason = EpisodeReason.MigrationOpening, Source = EpisodeSource.Migration, Confidence = Confidence.Medium,
                    ValidFrom = at, OpeningEventId = eventId,
                });
                events.Add(new UnitEventDraft
                {
                    UnitId = unitId, EventTime = at, EventType = UnitEventType.CustodyChanged, PresetEventId = eventId, LocationId = custody.LocationId,
                    LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef, IdempotencyKey = Key(UnitEventType.CustodyChanged, at),
                    Attributes = JsonObjectText.Of(new { load_id = command.LoadId, episode_id = episodeId, reason = EpisodeReason.MigrationOpening, source = EpisodeSource.Migration }),
                    Delta = new CurrentStateDelta
                    {
                        LocationId = (Guid?)custody.LocationId, LocationKind = (string?)custody.LocationKind, CustodyKind = (string?)EpisodeKind.Custody,
                        OpenCustodyEpisodeId = (Guid?)episodeId,
                    },
                });
            }

            if (unit.Ownership is { } owner)
            {
                var at = owner.From < unit.CreatedAt ? unit.CreatedAt : owner.From;
                var eventId = TimeOrderedId.New(at);
                var episodeId = TimeOrderedId.New(at);
                episodes.Add(new CustodyEpisode
                {
                    TenantId = tenantId, EpisodeId = episodeId, UnitId = unitId, Kind = EpisodeKind.Ownership, PartyRef = owner.PartyRef,
                    PartyType = owner.PartyType, LegalEntityId = command.LegalEntityId, Reason = EpisodeReason.MigrationOpening,
                    Source = EpisodeSource.Migration, Confidence = owner.Pseudonymised ? Confidence.Low : Confidence.Medium,
                    ValidFrom = at, OpeningEventId = eventId,
                });
                events.Add(new UnitEventDraft
                {
                    UnitId = unitId, EventTime = at, EventType = UnitEventType.OwnershipOpened, PresetEventId = eventId,
                    LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef, IdempotencyKey = Key(UnitEventType.OwnershipOpened, at),
                    Attributes = JsonObjectText.Of(new
                    {
                        load_id = command.LoadId, episode_id = episodeId, party_ref = owner.PartyRef, party_type = owner.PartyType,
                        reason = EpisodeReason.MigrationOpening, source = EpisodeSource.Migration, pseudonymised = owner.Pseudonymised,
                        direct_customer_party_ref = owner.DirectCustomerPartyRef,
                    }),
                    Delta = new CurrentStateDelta { OwnerPartyRef = (Guid?)owner.PartyRef, OpenOwnershipEpisodeId = (Guid?)episodeId },
                });
            }

            units.Add(new Unit
            {
                TenantId = tenantId, UnitId = unitId, LifecycleStatus = status, Origin = UnitOrigin.Migrated, BrandIsOurs = true,
                SerialisationPoint = profiles[unit.ItemId]!.SerialisationPoint, RetentionClass = profiles[unit.ItemId]!.RetentionClass, HistoryDepth = unit.HistoryDepth,
                CreatedAt = now, CreatedEventId = createdEventId, CreatedEventTime = unit.CreatedAt,
            });
            identifierRows.AddRange(ids.Select(id => id.ToRow(tenantId, unitId, unit.CreatedAt, command.ActorRef, now, createdEventId)));
            assignments.Add(new UnitItemAssignment
            {
                TenantId = tenantId, AssignmentId = TimeOrderedId.New(unit.CreatedAt), UnitId = unitId, ItemId = unit.ItemId,
                ValidFrom = unit.CreatedAt, Reason = ItemAssignmentReason.Initial, EventId = createdEventId,
            });
            register.Add(new MigrationLoadUnit
            {
                TenantId = tenantId, LoadId = command.LoadId, PlanUnitId = unit.PlanUnitId, UnitId = unitId, CreatedEventId = createdEventId,
                CreatedEventTime = unit.CreatedAt, EventCount = written, AppliedAt = now, ActorRef = command.ActorRef,
            });
            outcomes.Add(new MigratedUnitOutcome(unit.PlanUnitId, unitId, MigratedUnitOutcome.Applied, []));
        }

        var eventsWritten = 0;
        if (prepared.Count > 0)
        {
            // The register commits with the ledger: either a unit and its register row exist, or neither does.
            await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
            var result = await bulk.IngestAsync(new BulkIngestion.Batch(events, units, identifierRows, assignments, episodes), cancellationToken);
            eventsWritten = result.EventsInserted;
            db.MigrationLoadUnits.AddRange(register);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }

        return new ApplyMigratedUnitsResult(outcomes,
            outcomes.Count(o => o.Outcome == MigratedUnitOutcome.Applied),
            outcomes.Count(o => o.Outcome == MigratedUnitOutcome.AlreadyApplied),
            outcomes.Count(o => o.Outcome == MigratedUnitOutcome.Refused), eventsWritten);
    }

    private static List<string> Refusals(MigratedUnit unit)
    {
        var reasons = new List<string>();
        if (unit.Identifiers.Count == 0) reasons.Add("identifier_required");
        if (unit.HistoryDepth is not (HistoryDepth.Full or HistoryDepth.Partial or HistoryDepth.OpeningOnly)) reasons.Add("history_depth_unknown");
        if (unit.ConditionGrade is { } grade && !ConditionGrades.All.Contains(grade)) reasons.Add("condition_grade_unknown");
        if (unit.Ownership is { PartyType: var partyType } && partyType == PartyType.Us) reasons.Add("ownership_party_is_us");
        if (unit.Identifiers.GroupBy(i => i.Type).Any(g => g.Count() > 1)) reasons.Add("identifier_type_repeated");
        return reasons;
    }
}

/// <summary>
/// <see cref="RollBackMigrationLoad"/> (OD-53): removes every row a load wrote, in bounded chunks, inside one
/// transaction. Refused as a whole while any unit of the load carries a fact the load did not write (a claim, a
/// correction, a shipment after cutover), so a rollback never erases work done in the module.
/// Temporal history tables keep their forensic copy of removed unit and episode rows.
/// </summary>
public static class RollBackMigrationLoadHandler
{
    public const int TouchedListLimit = 200;
    private const int Chunk = 1000;

    public static async Task<RollBackMigrationLoadResult> Handle(RollBackMigrationLoad command, SerialisationDbContext db, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = db.MigrationLoadUnits.AsNoTracking().Where(r => r.TenantId == tenantId && r.LoadId == command.LoadId);
        if (!await rows.AnyAsync(cancellationToken))
        {
            return new RollBackMigrationLoadResult(command.LoadId, RollBackMigrationLoadResult.NothingToRemove, 0, 0, []);
        }

        // A unit is touched when the ledger holds more facts for it than the load wrote (live or archived partitions).
        var touched = await rows
            .Select(r => new
            {
                r.UnitId,
                r.EventCount,
                Live = db.Events.Count(e => e.TenantId == tenantId && e.UnitId == r.UnitId),
                Archived = db.ArchivedEvents.Count(e => e.TenantId == tenantId && e.UnitId == r.UnitId),
            })
            .Where(x => x.Live + x.Archived > x.EventCount)
            .OrderBy(x => x.UnitId).Select(x => x.UnitId).Take(TouchedListLimit).ToListAsync(cancellationToken);
        if (touched.Count > 0)
        {
            return new RollBackMigrationLoadResult(command.LoadId, RollBackMigrationLoadResult.RefusedTouched, 0, 0, touched);
        }

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var unitsRemoved = 0;
        var eventsRemoved = 0;
        while (true)
        {
            var ids = await rows.OrderBy(r => r.PlanUnitId).Select(r => r.UnitId).Take(Chunk).ToListAsync(cancellationToken);
            if (ids.Count == 0) break;
            await db.LedgerBaseOrderReferences.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId)).ExecuteDeleteAsync(cancellationToken);
            await db.Identifiers.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId)).ExecuteDeleteAsync(cancellationToken);
            await db.ItemAssignments.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId)).ExecuteDeleteAsync(cancellationToken);
            await db.Episodes.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId)).ExecuteDeleteAsync(cancellationToken);
            await db.Current.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId)).ExecuteDeleteAsync(cancellationToken);
            eventsRemoved += await db.Events.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId)).ExecuteDeleteAsync(cancellationToken);
            eventsRemoved += await db.ArchivedEvents.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId)).ExecuteDeleteAsync(cancellationToken);
            unitsRemoved += await db.Units.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId)).ExecuteDeleteAsync(cancellationToken);
            await db.MigrationLoadUnits.Where(x => x.TenantId == tenantId && x.LoadId == command.LoadId && ids.Contains(x.UnitId)).ExecuteDeleteAsync(cancellationToken);
        }

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new RollBackMigrationLoadResult(command.LoadId, RollBackMigrationLoadResult.RolledBack, unitsRemoved, eventsRemoved, []);
    }
}

/// <summary><see cref="MigrationLoadUnitsQuery"/>: the units of a load in plan-unit order, keyset paged.</summary>
public static class MigrationLoadUnitsQueryHandler
{
    public static async Task<MigrationLoadUnitsPage> Handle(MigrationLoadUnitsQuery query, SerialisationDbContext db, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(query.Take, 1, 5000);
        var tenantId = tenant.TenantId;
        var page = await db.MigrationLoadUnits.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.LoadId == query.LoadId && (query.AfterPlanUnitId == null || r.PlanUnitId.CompareTo(query.AfterPlanUnitId.Value) > 0))
            .OrderBy(r => r.PlanUnitId).Take(take + 1)
            .Select(r => new { r.PlanUnitId, r.UnitId, r.AppliedAt }).ToListAsync(cancellationToken);
        var items = page.Take(take).Select(r => new MigrationLoadUnitRow(r.PlanUnitId, r.UnitId, r.AppliedAt)).ToList();
        return new MigrationLoadUnitsPage(items, page.Count > take ? items[^1].PlanUnitId : null);
    }
}

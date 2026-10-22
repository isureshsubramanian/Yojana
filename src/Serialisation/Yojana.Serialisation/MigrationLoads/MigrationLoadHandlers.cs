using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
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
        MigrationCoverBulkWriter coverWriter, CertificateVersioningEngine certificates, IdentifierService identifiers,
        EffectiveItemProfiles items, LedgerPublication publication, ICurrencyCatalogue currencies,
        ITenantContext tenant, IClock clock,
        CancellationToken cancellationToken)
    {
        if (command.Units.Count > MaxUnitsPerChunk)
        {
            throw new CommandRefusedException("chunk_too_large", $"A chunk carries at most {MaxUnitsPerChunk} units; this one has {command.Units.Count}.", "send smaller chunks");
        }

        // TIGHT COUPLING (OD-52, migrated history publishes nothing per unit)
        // WHAT:  the ownership and custody facts below are the same event types a live sale writes, so the append
        //        observers (CustomerSaleCertificateObserver) would issue a release certificate per unit and the mapper
        //        would publish per-unit events. The scoped LedgerPublication switch is the seam both honour.
        // WHY:   a first-customer load is ~420 000 units; automatic certificate instances and outbound events would
        //        flood every connected system with facts that already happened in the source.
        // WHAT BREAKS IF SEPARATED: dropping this line records an additional system-generated certificate for every
        //        migrated owned unit. The single migrated certificate instance is recorded explicitly below, without
        //        rendering; the summary MigrationLoadApplied is published by the caller.
        publication.Suppressed = true;

        var tenantId = tenant.TenantId;
        var now = clock.UtcNow;
        var cutover = command.CutoverDate ?? DateOnly.FromDateTime(now);
        var planIds = command.Units.Select(u => u.PlanUnitId).ToList();
        var done = await db.MigrationLoadUnits.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.LoadId == command.LoadId && planIds.Contains(r.PlanUnitId))
            .ToDictionaryAsync(r => r.PlanUnitId, r => r.UnitId, cancellationToken);

        var profiles = new Dictionary<Guid, ItemSerialisationProfile?>();
        foreach (var itemId in command.Units.Select(u => u.ItemId).Distinct())
        {
            profiles[itemId] = await items.GetAsync(itemId, now, cancellationToken);
        }

        var policyIds = command.Units.SelectMany(unit => unit.Entitlements ?? [])
            .Select(entitlement => entitlement.PolicyVersionId).Distinct().ToList();
        var policies = policyIds.Count == 0
            ? new Dictionary<Guid, WarrantyPolicyVersion>()
            : await db.PolicyVersions.AsNoTracking().Where(row => row.TenantId == tenantId
                    && policyIds.Contains(row.PolicyVersionId))
                .ToDictionaryAsync(row => row.PolicyVersionId, cancellationToken);
        var familyCodes = command.Units.SelectMany(unit => unit.Entitlements ?? [])
            .Select(entitlement => entitlement.CoverageFamily).Distinct().ToList();
        var families = familyCodes.Count == 0
            ? new Dictionary<string, CoverageFamily>(StringComparer.Ordinal)
            : await db.CoverageFamilies.AsNoTracking().Where(row => row.TenantId == tenantId
                    && familyCodes.Contains(row.FamilyCode))
                .ToDictionaryAsync(row => row.FamilyCode, StringComparer.Ordinal, cancellationToken);

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
            reasons.AddRange(ReplacementRefusals(unit, currencies));
            reasons.AddRange(CoverRefusals(unit, cutover, policies, families));
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

        // A link rides on the endpoint with the greater plan id. The other endpoint is therefore in this chunk or an
        // earlier register row; a refused/missing endpoint can be reported now without depending on a later chunk.
        var relatedPlanIds = prepared.SelectMany(row => row.Unit.ReplacementLinks ?? [])
            .SelectMany(link => new[] { link.ReplacedPlanUnitId, link.ReplacementPlanUnitId }).Distinct().ToList();
        var loadedRelated = relatedPlanIds.Count == 0 ? new HashSet<Guid>() : (await db.MigrationLoadUnits.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.LoadId == command.LoadId
                && relatedPlanIds.Contains(row.PlanUnitId)).Select(row => row.PlanUnitId).ToListAsync(cancellationToken))
            .ToHashSet();
        while (true)
        {
            var available = prepared.Select(row => row.Unit.PlanUnitId).Concat(loadedRelated).ToHashSet();
            var removed = prepared.RemoveAll(row =>
            {
                var broken = (row.Unit.ReplacementLinks ?? []).Any(link =>
                    !available.Contains(link.ReplacedPlanUnitId) || !available.Contains(link.ReplacementPlanUnitId));
                if (!broken) return false;
                outcomes.Add(new MigratedUnitOutcome(row.Unit.PlanUnitId, null, MigratedUnitOutcome.Refused,
                    ["replacement_unit_not_loaded"]));
                return true;
            });
            if (removed == 0) break;
        }

        var events = new List<UnitEventDraft>();
        var units = new List<Unit>();
        var identifierRows = new List<UnitIdentifier>();
        var assignments = new List<UnitItemAssignment>();
        var episodes = new List<CustodyEpisode>();
        var register = new List<MigrationLoadUnit>();
        var entitlementRows = new List<Entitlement>();
        var evidenceRows = new List<EntitlementEvidence>();
        var usageRows = new List<EntitlementUsageLimit>();
        var entitlementEvents = new List<UnitEventDraft>();
        var replacementRows = new List<ReplacementLink>();
        var planUnits = new Dictionary<Guid, (Guid UnitId, Guid ItemId, Guid CreatedEventId, DateTime CreatedAt)>();
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
            // report that Integration's ApplyService runs as the last step of every load (P9-T09) is the single place
            // migrated units without cover are found, and an approved BackfillEntitlements closes them, starting from the
            // delivery recorded on the ownership fact (MigratedDeliveryEvidence). If a load ever skips that report, units
            // sold without cover go unseen until the daily no-entitlement job.
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
                    Attributes = JsonObjectText.Of(new Dictionary<string, object?>
                    {
                        ["load_id"] = command.LoadId, ["episode_id"] = episodeId, ["party_ref"] = owner.PartyRef,
                        ["party_type"] = owner.PartyType, ["reason"] = EpisodeReason.MigrationOpening,
                        ["source"] = EpisodeSource.Migration, ["pseudonymised"] = owner.Pseudonymised,
                        ["direct_customer_party_ref"] = owner.DirectCustomerPartyRef,
                        [MigratedDeliveryEvidence.AtKey] = owner.Delivery?.At,
                        [MigratedDeliveryEvidence.LabelKey] = owner.Delivery?.DocumentLabel,
                        [MigratedDeliveryEvidence.NumberKey] = owner.Delivery?.DocumentNumber,
                    }),
                    Delta = new CurrentStateDelta { OwnerPartyRef = (Guid?)owner.PartyRef, OpenOwnershipEpisodeId = (Guid?)episodeId },
                });
            }

            var firstEntitlementEvent = entitlementEvents.Count;
            var cover = MigratedCoverRules.Prepare(tenantId, command.LoadId, unitId, command.LegalEntityId,
                unit.Ownership?.PartyRef, unit.Ownership?.PartyType, unit.ConditionGrade, unit.CreatedAt,
                cutover, now, command.ActorRef, unit.Entitlements ?? [], policies, Key);
            entitlementRows.AddRange(cover.Entitlements);
            evidenceRows.AddRange(cover.Evidence);
            usageRows.AddRange(cover.UsageLimits);
            entitlementEvents.AddRange(cover.Events);

            var coverage = EntitlementCoverage.From(cover.Entitlements.Select(row =>
                new EntitlementCoverageEntry(row.EntitlementId, row.Status, row.EndDate, row.Direction)));
            for (var index = firstEntitlementEvent; index < entitlementEvents.Count; index++)
            {
                entitlementEvents[index] = entitlementEvents[index] with { Delta = new CurrentStateDelta
                {
                    LiveEntitlementIds = coverage.LiveEntitlementIds,
                    WarrantyEndMax = new Optional<DateOnly?>(coverage.WarrantyEndMax),
                } };
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
            // HIDDEN COUPLING (OD-53/OD-61 touched rule): EventCount and
            // MigrationCoverReplacement.EventsWritten are summed by replacement and rollback to distinguish migration
            // facts from later operational work. There is no foreign-key seam between the ledgers and these counters.
            // If this count stops matching every fact written above, safe replacements are refused or touched units can
            // be rolled back. Keep both replacement-register and rollback comments/counting logic in step.
            register.Add(new MigrationLoadUnit
            {
                TenantId = tenantId, LoadId = command.LoadId, PlanUnitId = unit.PlanUnitId, UnitId = unitId, CreatedEventId = createdEventId,
                CreatedEventTime = unit.CreatedAt,
                EventCount = written + (unit.Certificate is null ? 0 : 1),
                AppliedAt = now, ActorRef = command.ActorRef,
            });
            planUnits[unit.PlanUnitId] = (unitId, unit.ItemId, createdEventId, unit.CreatedAt);
            outcomes.Add(new MigratedUnitOutcome(unit.PlanUnitId, unitId, MigratedUnitOutcome.Applied, []));
        }

        var links = prepared.SelectMany(row => row.Unit.ReplacementLinks ?? [])
            .DistinctBy(link => link.PlanReplacementLinkId).ToList();
        var priorPlanIds = links.SelectMany(link => new[] { link.ReplacedPlanUnitId, link.ReplacementPlanUnitId })
            .Where(planId => !planUnits.ContainsKey(planId)).Distinct().ToList();
        if (priorPlanIds.Count > 0)
        {
            var prior = await (from loaded in db.MigrationLoadUnits.AsNoTracking()
                               join current in db.Current.AsNoTracking()
                                   on new { loaded.TenantId, loaded.UnitId } equals new { current.TenantId, current.UnitId }
                               where loaded.TenantId == tenantId && loaded.LoadId == command.LoadId
                                   && priorPlanIds.Contains(loaded.PlanUnitId)
                               select new { loaded.PlanUnitId, loaded.UnitId, current.ItemId, loaded.CreatedEventId,
                                   CreatedAt = loaded.CreatedEventTime }).ToListAsync(cancellationToken);
            foreach (var row in prior)
                planUnits[row.PlanUnitId] = (row.UnitId, row.ItemId, row.CreatedEventId, row.CreatedAt);
        }
        foreach (var link in links)
        {
            var original = planUnits[link.ReplacedPlanUnitId];
            var replacement = planUnits[link.ReplacementPlanUnitId];
            replacementRows.Add(new ReplacementLink
            {
                TenantId = tenantId, ReplacementLinkId = link.PlanReplacementLinkId,
                WarrantyReplacementId = null, MigrationLoadId = command.LoadId,
                Direction = link.Direction,
                ReplacementReason = link.ReplacementReason, State = link.State,
                SourceKind = ReplacementSourceKind.Migrated, SourceId = command.LoadId,
                SourceNumber = link.SourceKey.Length <= 40 ? link.SourceKey : link.SourceKey[..40],
                OriginalUnitId = original.UnitId, ReplacementUnitId = replacement.UnitId,
                OriginalItemId = original.ItemId, ReplacementItemId = replacement.ItemId,
                SourceEntitlementId = null, PolicyApplied = link.PolicyApplied,
                PricePolicy = link.PricePolicy, DifferenceAmount = link.DifferenceAmount, Currency = link.Currency,
                ReplacementConditionAllowed = link.ReplacementConditionAllowed,
                CreatedAt = now, CreatedBy = command.ActorRef,
            });
        }

        var eventsWritten = 0;
        var entitlementsWritten = 0;
        var replacementLinksWritten = 0;
        var certificatesRecorded = 0;
        if (prepared.Count > 0)
        {
            // The register commits with the ledger: either a unit and its register row exist, or neither does.
            await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
            var result = await bulk.IngestAsync(new BulkIngestion.Batch(events, units, identifierRows, assignments, episodes), cancellationToken);
            eventsWritten = result.EventsInserted;
            var cover = await coverWriter.WriteAsync(new MigrationCoverBulkWriter.Batch(entitlementRows,
                evidenceRows, usageRows, replacementRows), cancellationToken);
            entitlementsWritten = cover.Entitlements;
            replacementLinksWritten = cover.ReplacementLinks;
            if (entitlementEvents.Count > 0)
            {
                var coverEvents = await bulk.IngestAsync(new BulkIngestion.Batch(entitlementEvents, [], [], [], []),
                    cancellationToken);
                eventsWritten += coverEvents.EventsInserted;
            }
            // One batched issue for the chunk (OD-55, P9-T10c): the engine applies the same rules and writes the same
            // rows as a single issue, with one read per table instead of about fifteen round trips per unit.
            var certificateUnits = prepared.Where(row => row.Unit.Certificate is not null).Select(row => row.Unit).ToList();
            var issuedCertificates = await certificates.IssueMigratedReleaseRecordsAsync(certificateUnits.Select(unit =>
            {
                var certificate = unit.Certificate!;
                var mapped = planUnits[unit.PlanUnitId];
                return new IssueCertificateVersion(mapped.CreatedEventId,
                    mapped.UnitId, CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease,
                    null, now, command.ActorRef, command.LegalEntityId, certificate.Provenance,
                    certificate.Unknowns, certificate.Evidence, TriggerSchema: "Migration.PlanCertificate",
                    MigrationLoadId: command.LoadId);
            }).ToList(), cancellationToken);
            for (var index = 0; index < certificateUnits.Count; index++)
            {
                var issued = issuedCertificates[index];
                if (!issued.Issued)
                    throw new CommandRefusedException(issued.RefusalRule ?? "migration_certificate_refused",
                        issued.RefusalDetail ?? $"Certificate for plan unit {certificateUnits[index].PlanUnitId} was refused.");
                certificatesRecorded++;
                eventsWritten++;
            }
            db.MigrationLoadUnits.AddRange(register);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }

        return new ApplyMigratedUnitsResult(outcomes,
            outcomes.Count(o => o.Outcome == MigratedUnitOutcome.Applied),
            outcomes.Count(o => o.Outcome == MigratedUnitOutcome.AlreadyApplied),
            outcomes.Count(o => o.Outcome == MigratedUnitOutcome.Refused), eventsWritten,
            entitlementsWritten, replacementLinksWritten, certificatesRecorded);
    }

    private static IEnumerable<string> CoverRefusals(MigratedUnit unit, DateOnly cutover,
        Dictionary<Guid, WarrantyPolicyVersion> policies,
        Dictionary<string, CoverageFamily> families)
    {
        var reasons = MigratedCoverRules.Refusals(unit.Entitlements ?? [], cutover, policies, families).ToList();
        if (unit.Certificate is { } certificate)
        {
            if (certificate.Version != 1) reasons.Add("migration_certificate_version_not_one");
            if (certificate.Provenance != CertificateProvenance.Migrated)
                reasons.Add("migration_certificate_provenance_invalid");
            if (certificate.Unknowns.Count == 0) reasons.Add("migration_certificate_unknowns_required");
            if (certificate.Evidence.Count == 0) reasons.Add("migration_certificate_evidence_required");
        }
        else reasons.Add("migration_certificate_required");
        if ((unit.ReplacementLinks ?? []).Any(link => link.ReplacedPlanUnitId == link.ReplacementPlanUnitId
                || unit.PlanUnitId != link.ReplacedPlanUnitId && unit.PlanUnitId != link.ReplacementPlanUnitId))
            reasons.Add("replacement_link_invalid");
        if ((unit.ReplacementLinks ?? []).Any(link => link.PolicyApplied != "unknown"))
            reasons.Add("replacement_policy_must_be_unknown");
        return reasons.Distinct(StringComparer.Ordinal);
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

    private static IEnumerable<string> ReplacementRefusals(MigratedUnit unit, ICurrencyCatalogue currencies)
    {
        foreach (var link in unit.ReplacementLinks ?? [])
        {
            if (!ReplacementDirection.All.Contains(link.Direction)) yield return "replacement_direction_unknown";
            if (!ReplacementReason.All.Contains(link.ReplacementReason)) yield return "replacement_reason_unknown";
            if (!MigratedReplacementLinkState.All.Contains(link.State)) yield return "replacement_state_not_terminal";
            if (!Yojana.Serialisation.Contracts.ReplacementConditionAllowed.All.Contains(link.ReplacementConditionAllowed))
                yield return "replacement_condition_allowed_unknown";
            if (!ReplacementPricePolicy.All.Contains(link.PricePolicy)) yield return "replacement_price_policy_unknown";
            if (link.DifferenceAmount < 0) yield return "replacement_difference_amount_negative";
            if (link.PricePolicy == ReplacementPricePolicy.Zero && link.DifferenceAmount != 0)
                yield return "replacement_zero_price_difference";
            if (string.IsNullOrEmpty(link.Currency) || link.Currency.Length != 3 ||
                link.Currency.Any(character => !char.IsAsciiLetterUpper(character)) ||
                link.Currency != "XXX" && currencies.Find(link.Currency) is null)
                yield return "replacement_currency_invalid";
            if (link.DifferenceAmount > 0 && link.Currency == "XXX")
                yield return "replacement_currency_required";
        }
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

    public static async Task<RollBackMigrationLoadResult> Handle(RollBackMigrationLoad command, SerialisationDbContext db,
        IUnitCoverDeviationProjection coverDeviation, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = db.MigrationLoadUnits.AsNoTracking().Where(r => r.TenantId == tenantId && r.LoadId == command.LoadId);
        var replacementRows = await db.MigrationCoverReplacements.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.LoadId == command.LoadId)
            .OrderBy(r => r.UnitId).ToListAsync(cancellationToken);
        if (!await rows.AnyAsync(cancellationToken) && replacementRows.Count == 0)
        {
            return new RollBackMigrationLoadResult(command.LoadId, RollBackMigrationLoadResult.NothingToRemove, 0, 0, []);
        }

        var affectedIds = await rows.Select(row => row.UnitId)
            .Concat(db.MigrationCoverReplacements.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.LoadId == command.LoadId)
                .Select(row => row.UnitId)).Distinct().ToListAsync(cancellationToken);

        // HIDDEN COUPLING (OD-53/OD-61 touched rule): MigrationLoadUnit.EventCount and
        // MigrationCoverReplacement.EventsWritten must count every migration-authored fact. The live and archived
        // ledger has no ownership column, so this sum is the only safe discriminator. If either writer changes its
        // count without this query, rollback may erase operational work or refuse an untouched load. Keep both writer
        // comments and this query in step.
        var touched = await db.Units.AsNoTracking()
            .Where(row => row.TenantId == tenantId && affectedIds.Contains(row.UnitId))
            .Select(row => new
            {
                row.UnitId,
                Live = db.Events.Count(e => e.TenantId == tenantId && e.UnitId == row.UnitId),
                Archived = db.ArchivedEvents.Count(e => e.TenantId == tenantId && e.UnitId == row.UnitId),
                Loaded = db.MigrationLoadUnits.Where(item => item.TenantId == tenantId && item.UnitId == row.UnitId)
                    .Sum(item => (int?)item.EventCount) ?? 0,
                Replaced = db.MigrationCoverReplacements.Where(item => item.TenantId == tenantId && item.UnitId == row.UnitId)
                    .Sum(item => (int?)item.EventsWritten) ?? 0,
            })
            .Where(x => x.Live + x.Archived > x.Loaded + x.Replaced)
            .OrderBy(x => x.UnitId).Select(x => x.UnitId).Take(TouchedListLimit).ToListAsync(cancellationToken);

        var baseUnitIds = await rows.Select(row => row.UnitId).ToListAsync(cancellationToken);
        var dependent = baseUnitIds.Count == 0 ? [] : await db.MigrationCoverReplacements.AsNoTracking()
            .Where(row => row.TenantId == tenantId && baseUnitIds.Contains(row.UnitId)
                && row.LoadId != command.LoadId)
            .Select(row => row.UnitId).Distinct().ToListAsync(cancellationToken);
        // A base load owns the unit beneath every later cover replacement. Removing that base would make the later
        // register and cover orphaned even though every fact is migration-authored; the delta must be undone first.
        touched.AddRange(dependent);
        foreach (var replacement in replacementRows)
        {
            if (await db.MigrationCoverReplacements.AsNoTracking().AnyAsync(row => row.TenantId == tenantId
                    && row.UnitId == replacement.UnitId && row.LoadId != command.LoadId
                    && row.ReplacedAt > replacement.ReplacedAt, cancellationToken))
                touched.Add(replacement.UnitId);
        }
        touched = touched.Distinct().Order().Take(TouchedListLimit).ToList();
        if (touched.Count > 0)
        {
            return new RollBackMigrationLoadResult(command.LoadId, RollBackMigrationLoadResult.RefusedTouched, 0, 0, touched);
        }

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var unitsRemoved = 0;
        var eventsRemoved = 0;
        var entitlementsRemoved = 0;
        var replacementLinksRemoved = 0;
        var certificatesRemoved = 0;
        var coverReplacementsReversed = 0;

        foreach (var replacementChunk in replacementRows.Chunk(Chunk))
        {
            var replacementUnitIds = replacementChunk.Select(row => row.UnitId).ToList();
            var snapshots = replacementChunk.SelectMany(row =>
                    JsonSerializer.Deserialize<List<MigrationClosedEntitlementSnapshot>>(row.ClosedEntitlements,
                        LedgerJson.Options) ?? [])
                .ToList();
            var newEntitlements = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenantId
                    && row.MigrationLoadId == command.LoadId && replacementUnitIds.Contains(row.UnitId))
                .Select(row => new { row.EntitlementId, row.CreatedEventId }).ToListAsync(cancellationToken);
            var newEntitlementIds = newEntitlements.Select(row => row.EntitlementId).ToList();
            if (newEntitlementIds.Count > 0)
            {
                await db.EntitlementEvidence.Where(row => row.TenantId == tenantId
                    && newEntitlementIds.Contains(row.EntitlementId)).ExecuteDeleteAsync(cancellationToken);
                await db.EntitlementUsageLimits.Where(row => row.TenantId == tenantId
                    && newEntitlementIds.Contains(row.EntitlementId)).ExecuteDeleteAsync(cancellationToken);
                entitlementsRemoved += await db.Entitlements.Where(row => row.TenantId == tenantId
                    && newEntitlementIds.Contains(row.EntitlementId)).ExecuteDeleteAsync(cancellationToken);
            }

            var replacementEventIds = newEntitlements.Select(row => row.CreatedEventId)
                .Concat(snapshots.Select(row => row.ClosedEventId)).Distinct().ToList();
            if (replacementEventIds.Count > 0)
            {
                eventsRemoved += await db.Events.Where(row => row.TenantId == tenantId
                    && replacementEventIds.Contains(row.EventId)).ExecuteDeleteAsync(cancellationToken);
                eventsRemoved += await db.ArchivedEvents.Where(row => row.TenantId == tenantId
                    && replacementEventIds.Contains(row.EventId)).ExecuteDeleteAsync(cancellationToken);
            }

            var snapshotIds = snapshots.Select(row => row.EntitlementId).ToList();
            var oldEntitlements = snapshotIds.Count == 0 ? [] : await db.Entitlements
                .Where(row => row.TenantId == tenantId && snapshotIds.Contains(row.EntitlementId))
                .ToListAsync(cancellationToken);
            var snapshotsById = snapshots.ToDictionary(row => row.EntitlementId);
            foreach (var entitlement in oldEntitlements)
            {
                var snapshot = snapshotsById[entitlement.EntitlementId];
                entitlement.Status = snapshot.PreviousStatus;
                entitlement.ClosedReason = snapshot.PreviousClosedReason;
                entitlement.ClosedEffective = snapshot.PreviousClosedEffective;
                entitlement.ClosedAt = snapshot.PreviousClosedAt;
            }
            await db.SaveChangesAsync(cancellationToken);

            var survivingUnitIds = replacementUnitIds.Except(baseUnitIds).ToList();
            if (survivingUnitIds.Count > 0)
            {
                var cover = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenantId
                        && survivingUnitIds.Contains(row.UnitId))
                    .Select(row => new { row.UnitId, row.EntitlementId, row.Status, row.EndDate, row.Direction })
                    .ToListAsync(cancellationToken);
                var current = await db.Current.Where(row => row.TenantId == tenantId
                        && survivingUnitIds.Contains(row.UnitId))
                    .ToDictionaryAsync(row => row.UnitId, cancellationToken);
                foreach (var unitId in survivingUnitIds)
                {
                    var coverage = EntitlementCoverage.From(cover.Where(row => row.UnitId == unitId)
                        .Select(row => new EntitlementCoverageEntry(row.EntitlementId, row.Status,
                            row.EndDate, row.Direction)));
                    current[unitId].LiveEntitlementIds = JsonSerializer.Serialize(coverage.LiveEntitlementIds,
                        LedgerJson.Options);
                    current[unitId].WarrantyEndMax = coverage.WarrantyEndMax;
                }
                await db.SaveChangesAsync(cancellationToken);
                await coverDeviation.RebuildManyAsync(survivingUnitIds, cancellationToken);
            }

            coverReplacementsReversed += await db.MigrationCoverReplacements.Where(row => row.TenantId == tenantId
                    && row.LoadId == command.LoadId && replacementUnitIds.Contains(row.UnitId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        while (true)
        {
            var ids = await rows.OrderBy(r => r.PlanUnitId).Select(r => r.UnitId).Take(Chunk).ToListAsync(cancellationToken);
            if (ids.Count == 0) break;
            replacementLinksRemoved += await db.ReplacementLinks.Where(x => x.TenantId == tenantId
                && x.MigrationLoadId == command.LoadId
                && (ids.Contains(x.OriginalUnitId) || ids.Contains(x.ReplacementUnitId)))
                .ExecuteDeleteAsync(cancellationToken);
            await db.CoverDeviationChanges.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.CoverDeviationValues.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.CoverDeviations.Where(x => x.TenantId == tenantId && ids.Contains(x.UnitId))
                .ExecuteDeleteAsync(cancellationToken);
            var entitlementIds = await db.Entitlements.Where(x => x.TenantId == tenantId
                    && x.MigrationLoadId == command.LoadId && ids.Contains(x.UnitId))
                .Select(x => x.EntitlementId).ToListAsync(cancellationToken);
            if (entitlementIds.Count > 0)
            {
                await db.EntitlementEvidence.Where(x => x.TenantId == tenantId
                    && entitlementIds.Contains(x.EntitlementId)).ExecuteDeleteAsync(cancellationToken);
                await db.EntitlementUsageLimits.Where(x => x.TenantId == tenantId
                    && entitlementIds.Contains(x.EntitlementId)).ExecuteDeleteAsync(cancellationToken);
                entitlementsRemoved += await db.Entitlements.Where(x => x.TenantId == tenantId
                    && entitlementIds.Contains(x.EntitlementId)).ExecuteDeleteAsync(cancellationToken);
            }
            var certificateIds = await db.CertificateInstances.Where(x => x.TenantId == tenantId
                    && x.MigrationLoadId == command.LoadId && ids.Contains(x.UnitId))
                .Select(x => x.CertificateId).ToListAsync(cancellationToken);
            if (certificateIds.Count > 0)
            {
                // TIGHT COUPLING (certificate immutability rollback exception)
                // WHAT: the immutable instance/signature triggers read this session value and permit deletion only
                // for provenance=migrated rows carrying this exact load id. WHY: OD-53 requires an untouched rehearsal
                // to disappear, while every non-migration certificate remains immutable. WHAT BREAKS IF SEPARATED:
                // rollback fails halfway, or a broader trigger exception permits deletion of issued certificates.
                // Keep the matching trigger comments and predicates in P9T10MigrationCover in step.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"EXEC sys.sp_set_session_context @key=N'yojana_migration_rollback_load_id', @value={command.LoadId}",
                    cancellationToken);
                try
                {
                    await db.ReprintLogs.Where(x => x.TenantId == tenantId
                        && certificateIds.Contains(x.CertificateId)).ExecuteDeleteAsync(cancellationToken);
                    await db.CertificateTriggers.Where(x => x.TenantId == tenantId
                        && x.CertificateId != null && certificateIds.Contains(x.CertificateId.Value))
                        .ExecuteDeleteAsync(cancellationToken);
                    await db.CertificateSignatures.Where(x => x.TenantId == tenantId
                        && certificateIds.Contains(x.CertificateId)).ExecuteDeleteAsync(cancellationToken);
                    certificatesRemoved += await db.CertificateInstances.Where(x => x.TenantId == tenantId
                        && certificateIds.Contains(x.CertificateId)).ExecuteDeleteAsync(cancellationToken);
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "EXEC sys.sp_set_session_context @key=N'yojana_migration_rollback_load_id', @value=NULL",
                        cancellationToken);
                }
            }
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
        return new RollBackMigrationLoadResult(command.LoadId, RollBackMigrationLoadResult.RolledBack,
            unitsRemoved, eventsRemoved, [], entitlementsRemoved, replacementLinksRemoved, certificatesRemoved,
            coverReplacementsReversed);
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

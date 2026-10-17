using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Commands;

/// <summary>
/// <see cref="CommissionUnits"/> (ADR-0001 PhysicalUnit state machine; Identity rules 1–6, 10; case 6, 13). Validates
/// every line as a set, refuses or skips the ones a rule stops, and writes the rest in one unit of work: the creating
/// event per unit, the unit row, its identifiers, the initial item assignment and the opening custody row. Replaying
/// the same command id returns the same units and writes nothing (idempotency keys derive from the command id and
/// line index). Batches of <see cref="BulkThreshold"/> lines or more take the set-based path.
/// </summary>
public static class CommissionUnitsHandler
{
    public const int BulkThreshold = 500;
    public const string Source = "serialisation.command";

    public static Task<CommissionUnitsResult> Handle(CommissionUnits command, SerialisationDbContext db, IUnitEventWriter writer, BulkIngestion bulk, IdentifierService identifiers,
        EffectiveItemProfiles items, SerialRangeService ranges, SerialReuseSettings reuseSettings, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
        => HandleCore(command, db, writer, bulk, identifiers, items, ranges, reuseSettings, tenant, clock, false, null, cancellationToken);

    /// <summary>
    /// P3-T06 seam: LateSerialization is the only caller allowed to bypass the item port's old non-serialised mode.
    /// It still uses this complete commissioning path, and supplies the outer correction metadata for every fact.
    /// </summary>
    internal static Task<CommissionUnitsResult> HandleLateAsync(CommissionUnits command, CorrectionMetadata metadata,
        SerialisationDbContext db, IUnitEventWriter writer, BulkIngestion bulk, IdentifierService identifiers,
        EffectiveItemProfiles items, SerialRangeService ranges, SerialReuseSettings reuseSettings, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
        => HandleCore(command, db, writer, bulk, identifiers, items, ranges, reuseSettings, tenant, clock, true, metadata, cancellationToken);

    private static async Task<CommissionUnitsResult> HandleCore(CommissionUnits command, SerialisationDbContext db, IUnitEventWriter writer, BulkIngestion bulk, IdentifierService identifiers,
        EffectiveItemProfiles items, SerialRangeService ranges, SerialReuseSettings reuseSettings, ITenantContext tenant, IClock clock, bool commissionedLate,
        CorrectionMetadata? correction, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var profile = await items.GetAsync(command.ItemId, command.EventTime, cancellationToken)
            ?? throw new CommandRefusedException("item_unknown", $"Item {command.ItemId} is not known to the host.", "register the item or check the connector's item mapping");
        if (!commissionedLate && profile.TrackingMode != ItemSerialisationProfile.TrackingModes.Serial)
        {
            throw new CommandRefusedException("item_not_serial_tracked", $"Item {profile.ItemCode} is {profile.TrackingMode}-tracked; only serial-tracked items are commissioned.", "change the item's tracking mode (LateSerialization in P3 handles existing stock)");
        }

        if (!UnitOriginIsKnown(command.Origin))
        {
            throw new CommandRefusedException("origin_unknown", $"Origin '{command.Origin}' is not a UnitOrigin value.");
        }

        // Replay: lines whose creating event already exists come back with their unit ids and nothing is written.
        var lineKeys = command.Lines.Select((_, i) => IdempotencyKey.For(Source, "command", command.CommandId, i, Guid.Empty, UnitEventType.Commissioned, command.EventTime)).ToList();
        var existing = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && lineKeys.Contains(e.IdempotencyKey) && e.EventTime == command.EventTime)
            .Select(e => new { e.IdempotencyKey, e.UnitId, e.EventType }).ToDictionaryAsync(e => e.IdempotencyKey, cancellationToken);

        var results = new CommissionLineResult[command.Lines.Count];
        var prepared = new List<(int Index, List<PreparedIdentifier> Identifiers)>();
        for (var i = 0; i < command.Lines.Count; i++)
        {
            var line = command.Lines[i];
            if (existing.TryGetValue(lineKeys[i], out var done))
            {
                results[i] = new CommissionLineResult(i, line.ClientReference, done.UnitId, CommissionLineResult.Duplicate, []);
                continue;
            }

            if (line.Identifiers.Count == 0)
            {
                if (commissionedLate)
                {
                    results[i] = new CommissionLineResult(i, line.ClientReference, null, CommissionLineResult.Refused, ["late_serial_required"]);
                    continue;
                }
                if (profile.SerialisationPoint == SerialisationPoint.Receipt)
                {
                    results[i] = new CommissionLineResult(i, line.ClientReference, null, CommissionLineResult.Refused, ["pending_identifier_not_allowed_at_receipt"]);
                    continue;
                }

                prepared.Add((i, []));
                continue;
            }

            var ids = line.Identifiers.Select(input => identifiers.Prepare(input, command.ItemId, null)).ToList();
            var reasons = ids.Where(p => p.Blocks).SelectMany(p => p.Validation.Reasons.Select(r => p.Input.Type + ":" + r)).ToList();
            if (profile.ExpiryRequired && line.ExpiryDate is null)
            {
                reasons.Add("expiry_required"); // case 19: a blank expiry on a controlled item is an error, never a silent omission
            }

            if (ids.GroupBy(p => p.Input.Type).Any(g => g.Count() > 1))
            {
                reasons.Add("identifier_type_repeated_on_line");
            }

            if (reasons.Count > 0)
            {
                results[i] = new CommissionLineResult(i, line.ClientReference, null, CommissionLineResult.Refused, reasons);
                continue;
            }

            prepared.Add((i, ids));
        }

        // Set validation (Identity rule 1): duplicates inside the batch, values open on another unit (scrap-and-reuse allowed by item policy, case 13).
        var inBatch = prepared.SelectMany(p => p.Identifiers.Select(id => (p.Index, id))).GroupBy(x => (x.id.Policy.Scheme, x.id.ScopeKey, x.id.Normalised)).Where(g => g.Count() > 1).SelectMany(g => g.Select(x => x.Index)).ToHashSet();
        var probe = prepared.SelectMany(p => p.Identifiers.Select(id => new UnitIdentifier { TenantId = tenantId, IdentifierId = Guid.Empty, UnitId = Guid.Empty, Type = id.Input.Type, Scheme = id.Policy.Scheme, ScopeKey = id.ScopeKey, RawValue = id.Input.RawValue, NormalisedValue = id.Normalised, ActorRef = command.ActorRef })).ToList();
        var conflicts = (await bulk.ValidateIdentifiersAsync(probe, cancellationToken)).Where(c => c.Reason == "already_open_on_another_unit").ToDictionary(c => (c.Scheme, c.ScopeKey, c.NormalisedValue), c => c.ExistingUnitId!.Value);
        var reuseFrom = new Dictionary<int, (Guid SourceUnit, List<Guid> IdentifiersToClose, string Outcome)>();
        if (conflicts.Count > 0)
        {
            var holders = await db.Units.AsNoTracking().Where(u => u.TenantId == tenantId && conflicts.Values.Contains(u.UnitId)).ToDictionaryAsync(u => u.UnitId, u => u.LifecycleStatus, cancellationToken);
            foreach (var (index, ids) in prepared.ToList())
            {
                var hits = ids.Where(id => conflicts.ContainsKey((id.Policy.Scheme, id.ScopeKey, id.Normalised))).ToList();
                if (hits.Count == 0) continue;
                var holderUnits = hits.Select(h => conflicts[(h.Policy.Scheme, h.ScopeKey, h.Normalised)]).Distinct().ToList();
                var reusable = profile.SerialReusePolicy == ItemSerialisationProfile.ReusePolicies.AfterScrap && holderUnits.Count == 1 && holders.TryGetValue(holderUnits[0], out var status) && UnitLifecycle.IsScrapped(status);
                if (reusable)
                {
                    var scrapped = holderUnits[0];
                    var toClose = await db.Identifiers.Where(x => x.TenantId == tenantId && x.UnitId == scrapped && x.ValidTo == null).Select(x => x.IdentifierId).ToListAsync(cancellationToken);
                    reuseFrom[index] = (scrapped, toClose, "reused_after_scrap");
                }
                else
                {
                    results[index] = new CommissionLineResult(index, command.Lines[index].ClientReference, null, CommissionLineResult.Refused,
                        hits.Select(h => $"{h.Input.Type}:identifier_in_use:{conflicts[(h.Policy.Scheme, h.ScopeKey, h.Normalised)]:N}").ToList());
                    prepared.RemoveAll(p => p.Index == index);
                }
            }
        }

        // Closed identifiers still belong to their history. In particular, VoidUnit closes them, so the open-only
        // uniqueness index cannot decide whether a new unit may take the string. OD-07 is evaluated here, at the only
        // boundary that creates the new identity, and an allowed reuse carries derived_from_unit_id (Identity rule 6).
        var historical = new List<UnitIdentifier>();
        foreach (var values in prepared.SelectMany(p => p.Identifiers).Select(i => i.Normalised).Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var chunk = values.ToList();
            historical.AddRange(await db.Identifiers.AsNoTracking()
                .Where(i => i.TenantId == tenantId && i.ValidTo != null && chunk.Contains(i.NormalisedValue))
                .ToListAsync(cancellationToken));
        }
        if (historical.Count > 0)
        {
            // SerialReuseSettings is factory-registered in SerialisationModule: exposing ITenantSettings here would add
            // PlatformDbContext to Wolverine's handler graph and make the host reject the command handler.
            var tenantReuse = await reuseSettings.GetAsync(cancellationToken);
            var resolvedReuse = SerialReuseSettings.Resolve(tenantReuse, profile.ItemClass);
            var historicalByKey = historical.GroupBy(i => (i.Scheme, i.ScopeKey, i.NormalisedValue))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.ValidTo).ThenByDescending(i => i.ValidFrom).ToList());
            var holderIds = historical.Select(i => i.UnitId).Distinct().ToList();
            var holderStatuses = await db.Units.AsNoTracking().Where(u => u.TenantId == tenantId && holderIds.Contains(u.UnitId))
                .ToDictionaryAsync(u => u.UnitId, u => u.LifecycleStatus, cancellationToken);

            foreach (var (index, ids) in prepared.ToList())
            {
                if (reuseFrom.ContainsKey(index)) continue;
                var hits = ids.Select(id => historicalByKey.GetValueOrDefault((id.Policy.Scheme, id.ScopeKey, id.Normalised))?.FirstOrDefault())
                    .OfType<UnitIdentifier>().ToList();
                if (hits.Count == 0) continue;
                var sources = hits.Select(i => i.UnitId).Distinct().ToList();
                var source = sources.Count == 1 ? sources[0] : Guid.Empty;
                var voided = source != Guid.Empty && holderStatuses.GetValueOrDefault(source) == LifecycleStatus.Void
                    && hits.All(i => i.CorrectionReason is not null && VoidUnitReason.All.Contains(i.CorrectionReason));
                // Only a voided unit's serial is governed by OD-07 here. Other closed rows (a value corrected away from
                // the wrong unit, a scrapped unit's re-used serial) keep their P1 behaviour: the open-only index decides.
                if (!voided) continue;
                var allowed = voided && resolvedReuse == SerialReusePolicyValue.NeverShippedAndNotExternal;
                if (allowed)
                {
                    var facts = await UnitCorrectionFacts.ReadAsync(db, tenantId, source, cancellationToken);
                    allowed = !facts.EverShipped && !facts.AppearsOnExternalDocument;
                }

                if (allowed)
                {
                    reuseFrom[index] = (source, [], SerialReuseOutcome.ReusedFromVoidedUnit);
                }
                else
                {
                    const string reason = "serial_reuse_forbidden";
                    results[index] = new CommissionLineResult(index, command.Lines[index].ClientReference, null, CommissionLineResult.Refused,
                        hits.Select(h => $"{h.Type}:{reason}:{h.UnitId:N}").ToList());
                    prepared.RemoveAll(p => p.Index == index);
                }
            }
        }

        foreach (var index in inBatch)
        {
            if (results[index] is null)
            {
                results[index] = new CommissionLineResult(index, command.Lines[index].ClientReference, null, CommissionLineResult.Refused, ["duplicate_in_batch"]);
                prepared.RemoveAll(p => p.Index == index);
            }
        }

        if (!command.SkipInvalidLines && results.Any(r => r is { Outcome: CommissionLineResult.Refused }))
        {
            var first = results.First(r => r is { Outcome: CommissionLineResult.Refused });
            throw new CommandRefusedException("line_refused", $"Line {first.Index} refused: {string.Join(", ", first.Reasons)}. Nothing was written.", "fix the line or send SkipInvalidLines = true to commission the valid lines");
        }

        // Build the rows: one creating event per unit; dependents name it.
        var now = clock.UtcNow;
        var batchEvents = new List<UnitEventDraft>();
        var units = new List<Unit>();
        var identifierRows = new List<UnitIdentifier>();
        var assignments = new List<UnitItemAssignment>();
        var episodes = new List<CustodyEpisode>();
        foreach (var (index, ids) in prepared)
        {
            var unitId = TimeOrderedId.New(command.EventTime);
            var eventId = TimeOrderedId.New(command.EventTime);
            var pending = ids.Count == 0;
            var eventType = pending ? UnitEventType.CreatedPendingIdentifier : UnitEventType.Commissioned;
            var status = UnitLifecycle.Require(null, eventType, command.Origin, unitId);
            var line = command.Lines[index];
            var episodeId = command.LocationId is null && command.LocationKind is null ? (Guid?)null : TimeOrderedId.New(command.EventTime);
            var attributes = new Dictionary<string, object?>
            {
                ["command_id"] = command.CommandId, ["line"] = index, ["item_code"] = profile.ItemCode, ["origin"] = command.Origin,
                ["identifiers"] = ids.Select(id => new { type = id.Input.Type, scheme = id.Policy.Scheme, value = id.Normalised, issuer_party_ref = id.Input.IssuerPartyRef }).ToList(),
            };
            if (commissionedLate) attributes["commissioned_late"] = true;
            if (reuseFrom.TryGetValue(index, out var reuse))
            {
                attributes["derived_from_unit_id"] = reuse.SourceUnit;
                attributes["serial_reuse_outcome"] = reuse.Outcome;
            }

            batchEvents.Add(new UnitEventDraft
            {
                UnitId = unitId, EventTime = command.EventTime, EventType = eventType, PresetEventId = eventId,
                BizStep = pending ? "creating_class_instance" : "commissioning", Disposition = UnitLifecycle.DispositionFor(status),
                LocationId = command.LocationId, LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef,
                DocumentType = command.Document?.Type, DocumentId = command.Document?.Id, DocumentLine = command.Document?.Line,
                IdempotencyKey = lineKeys[index],
                Attributes = correction is null ? JsonObjectText.Of(attributes) : CorrectionLedger.Attributes(correction, attributes),
                Delta = new CurrentStateDelta
                {
                    Create = new CreateCurrentState(command.ItemId, status, UnitLifecycle.DispositionFor(status)) { ModelVersionId = command.ModelVersionId, LotId = line.LotId },
                    LocationId = command.LocationId, LocationKind = command.LocationKind,
                    CustodyKind = episodeId is null ? null : EpisodeKind.Custody, OpenCustodyEpisodeId = episodeId,
                },
            });
            units.Add(new Unit
            {
                TenantId = tenantId, UnitId = unitId, LifecycleStatus = status, Origin = command.Origin, LotId = line.LotId, ExpiryDate = line.ExpiryDate, BrandIsOurs = command.BrandIsOurs ?? command.Origin != UnitOrigin.TradeIn,
                DerivedFromUnitId = reuseFrom.TryGetValue(index, out var r) ? r.SourceUnit : null,
                SerialisationPoint = profile.SerialisationPoint, RetentionClass = profile.RetentionClass, CreatedAt = now, CreatedEventId = eventId,
            });
            identifierRows.AddRange(ids.Select(id => id.ToRow(tenantId, unitId, command.EventTime, command.ActorRef, now, eventId)));
            assignments.Add(new UnitItemAssignment { TenantId = tenantId, AssignmentId = TimeOrderedId.New(command.EventTime), UnitId = unitId, ItemId = command.ItemId, ModelVersionId = command.ModelVersionId, ValidFrom = command.EventTime, Reason = ItemAssignmentReason.Initial, EventId = eventId });
            if (episodeId is { } ep)
            {
                episodes.Add(new CustodyEpisode
                {
                    TenantId = tenantId, EpisodeId = ep, UnitId = unitId, Kind = EpisodeKind.Custody, PartyType = PartyType.Us, LegalEntityId = command.LegalEntityId,
                    LocationId = command.LocationId, LocationKind = command.LocationKind, Reason = command.Origin == UnitOrigin.Manufactured ? EpisodeReason.Production : EpisodeReason.Receipt,
                    Source = command.Source ?? EpisodeSource.Receipt, Confidence = Confidence.High, ValidFrom = command.EventTime,
                    OpeningDocumentType = command.Document?.Type, OpeningDocumentId = command.Document?.Id, OpeningDocumentLine = command.Document?.Line, OpeningEventId = eventId,
                });
            }

            results[index] = new CommissionLineResult(index, line.ClientReference, unitId, pending ? CommissionLineResult.Pending : CommissionLineResult.Commissioned, []);
        }

        // Scrap-and-reuse: the scrapped unit's identifiers close now (Identity rule 6), in the same unit of work.
        if (reuseFrom.Count > 0)
        {
            var closing = reuseFrom.Values.SelectMany(v => v.IdentifiersToClose).ToList();
            if (closing.Count > 0)
                await db.Identifiers.Where(i => i.TenantId == tenantId && closing.Contains(i.IdentifierId)).ForEachAsync(i => { i.ValidTo = command.EventTime; i.CorrectionReason = "reused_after_scrap"; }, cancellationToken);
        }

        if (prepared.Count >= BulkThreshold)
        {
            await bulk.IngestAsync(new BulkIngestion.Batch(batchEvents, units, identifierRows, assignments, episodes), cancellationToken);
        }
        else if (prepared.Count > 0)
        {
            await writer.AppendAsync(batchEvents, cancellationToken);
            db.Units.AddRange(units);
            db.Identifiers.AddRange(identifierRows);
            db.ItemAssignments.AddRange(assignments);
            db.Episodes.AddRange(episodes);
        }

        // Serials that were reserved from a range are counted against it (case 7); serials outside any range are fine too.
        var internalSerials = identifierRows.Where(i => i.Type == IdentifierType.InternalSerial).Select(i => (i.UnitId, i.NormalisedValue)).ToList();
        if (internalSerials.Count > 0)
        {
            await ranges.RecordCommissionedAsync(internalSerials, cancellationToken);
        }

        return new CommissionUnitsResult(results, results.Count(r => r.Outcome == CommissionLineResult.Commissioned), results.Count(r => r.Outcome == CommissionLineResult.Pending),
            results.Count(r => r.Outcome == CommissionLineResult.Refused), results.Count(r => r.Outcome == CommissionLineResult.Duplicate));
    }

    private static bool UnitOriginIsKnown(string origin) => origin is UnitOrigin.Manufactured or UnitOrigin.Purchased or UnitOrigin.VendorAsn or UnitOrigin.TradeIn or UnitOrigin.CustomerSupplied or UnitOrigin.LegacyUnknown or UnitOrigin.Migrated;
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Commands;

/// <summary>P3-T06 / case 122: one audited correction covering the item fact, existing stock and installed kit units.</summary>
public static class LateSerializationHandler
{
    public static Task<LateSerializationResult> Handle(LateSerialization command, LateSerializationService service, CancellationToken cancellationToken) =>
        service.ExecuteAsync(command, enforceApproval: true, publish: true, cancellationToken: cancellationToken);
}

public static class PreviewLateSerializationHandler
{
    // Preview rehearses the real command in an isolated transaction. This is coupled to PublishedEventMapper through
    // LedgerPublication exactly like entitlement bulk preview: facts and outbox messages from rehearsal must not escape.
    public static async Task<CorrectionPreview> Handle(PreviewLateSerialization preview, IServiceScopeFactory scopes,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant.TenantId);
        scope.ServiceProvider.GetRequiredService<LedgerPublication>().Suppressed = true;
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<LateSerializationService>();
            var execution = await service.ExecuteDetailedAsync(preview.Command, enforceApproval: false, publish: false, cancellationToken: cancellationToken);
            var refusal = execution.ApprovalRefusal;
            var outcome = refusal is null ? UnitOutcome.Outcomes.Change : UnitOutcome.Outcomes.Block;
            var units = execution.Commissioning.Select(c => new UnitOutcome(Guid.Empty, c.Serial, outcome, refusal?.Rule,
                c.ForKit ? "commission late and capture in kit" : "commission late into open stock", refusal?.UnblockingStep)).ToList();
            var events = execution.Commissioning.Select(c => new CorrectionEventPreview(Guid.Empty, UnitEventType.Commissioned,
                preview.Command.EffectiveAt, c.ForKit ? "commissioned_late; kit component" : "commissioned_late; open stock")).ToList();
            events.AddRange(execution.CapturedParents.Select(p => new CorrectionEventPreview(p, UnitEventType.AsBuiltCorrected,
                preview.Command.KitCaptures.Where(c => execution.KitParentByLink.GetValueOrDefault(c.ExistingLinkId) == p).Min(c => c.FittedAt), "late serial capture")));
            return new(null, CorrectionCommand.LateSerialization, units.Count, refusal is null ? units.Count : 0, 0,
                refusal is null ? 0 : units.Count, units, events, [], [], [], refusal,
                UnitsToCommission: execution.Commissioning.Select(c => new CorrectionCommissionPreview(c.Serial, c.LocationId, c.LocationKind, c.SourceDocument, c.ForKit)).ToList(),
                Exemptions: execution.Coverage.Where(c => c.ExemptQuantity > 0).Select(c => new CorrectionExemptionPreview(c.LocationId, c.LocationKind, c.ExemptQuantity, c.ExemptReason!)).ToList(),
                KitsNeedingAsBuiltCapture: execution.KitRequirements.Select(k => new CorrectionAsBuiltRequirementPreview(k.ParentUnitId, k.AssemblyLinkId, k.Quantity, k.CapturedByCommand)).ToList());
        }
        catch (CommandRefusedException ex)
        {
            return new(null, CorrectionCommand.LateSerialization, 0, 0, 0, 1, [], [], [], [], [], ex.Refusal);
        }
        catch (DbUpdateException ex)
        {
            return new(null, CorrectionCommand.LateSerialization, 0, 0, 0, 1, [], [], [], [], [],
                new("write_conflict", ex.InnerException?.Message ?? ex.Message, "refresh the preview and resolve the conflicting serial or item change"));
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }
}

public sealed class LateSerializationService(
    LateSerializationPlanner planner,
    SerialisationDbContext db,
    IUnitEventWriter writer,
    BulkIngestion bulk,
    IdentifierService identifiers,
    EffectiveItemProfiles items,
    SerialRangeService ranges,
    SerialReuseSettings reuseSettings,
    CorrectAsBuiltPlanner asBuiltPlanner,
    CorrectionLedger corrections,
    CorrectionApprovalSettings approvalSettings,
    ITenantContext tenant,
    IClock clock,
    IMessageBus bus)
{
    public async Task<LateSerializationResult> ExecuteAsync(LateSerialization command, bool enforceApproval, bool publish, CancellationToken cancellationToken) =>
        (await ExecuteDetailedAsync(command, enforceApproval, publish, cancellationToken)).Result;

    internal async Task<LateSerializationExecution> ExecuteDetailedAsync(LateSerialization command, bool enforceApproval, bool publish, CancellationToken cancellationToken)
    {
        if (await db.ItemTrackingModeChanges.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CorrectionId == command.Correction.CorrectionId, cancellationToken) is { } replay)
        {
            if (replay.ItemId != command.ItemId)
                throw new CommandRefusedException("correction_id_in_use", $"Correction id {command.Correction.CorrectionId} belongs to another item's tracking change.", "send a new correction id");
            var remaining = await RemainingKitQuantityAsync(command.ItemId, cancellationToken);
            return LateSerializationExecution.Replayed(replay, remaining);
        }

        var plan = await planner.PlanAsync(command, cancellationToken);
        var metadata = await corrections.PrepareAsync(CorrectionCommand.LateSerialization, command.Correction, command.CommandId,
            command.Reason, command.Note, null, command.EffectiveAt, CorrectionInitiatingRole.SerialAdministrator, cancellationToken);
        var facts = new CorrectionApprovalFacts(ExemptionRequested: plan.Coverage.Any(c => c.ExemptQuantity > 0));
        var policy = await approvalSettings.GetAsync(cancellationToken);
        var decision = CorrectionApprovalRules.Evaluate(policy, CorrectionCommand.LateSerialization, facts, command.CommittedBy.Roles);
        var approved = decision.Status != CorrectionApprovalStatus.Refused &&
            (decision.Status == CorrectionApprovalStatus.Allowed ||
             CorrectionApprovalRules.ApprovalsSatisfied(policy, CorrectionCommand.LateSerialization, facts, command.CommittedBy, metadata.Approvers ?? []));
        var approvalRefusal = approved ? null : decision.Refusal ?? new CommandRefusal("approval_required", "LateSerialization needs the tenant policy's approvers.", "route the identical preview to the required approvers");
        if (enforceApproval && approvalRefusal is not null)
            throw new CommandRefusedException(approvalRefusal.Rule, approvalRefusal.Message, approvalRefusal.UnblockingStep);

        var changeId = TimeOrderedId.New(command.EffectiveAt);
        var commissioning = new List<LateCommissionedUnit>();
        var capturedUnits = new Dictionary<Guid, IReadOnlyList<Guid>>();
        if (!plan.Reversed)
        {
            foreach (var stock in command.Stock)
            {
                var commissioned = await CommissionAsync(stock.CommissionCommandId, stock.Origin, stock.Units, command.EffectiveAt,
                    stock.SourceDocument, stock.LocationId, stock.LocationKind, command, metadata, cancellationToken);
                AddCommissioning(commissioning, commissioned, stock.Units, stock.LocationId, stock.LocationKind, stock.SourceDocument, false);
            }

            foreach (var capture in command.KitCaptures)
            {
                var commissioned = await CommissionAsync(capture.CommissionCommandId, capture.Origin, capture.Units, command.EffectiveAt,
                    capture.SourceDocument, null, null, command, metadata, cancellationToken);
                AddCommissioning(commissioning, commissioned, capture.Units, null, null, capture.SourceDocument, true);
                capturedUnits[capture.ExistingLinkId] = commissioned.Lines.Select(l => l.UnitId!.Value).ToList();
            }

            // CorrectAsBuilt resolves units through persisted query paths. Save inside Wolverine's ambient transaction;
            // a later refusal still rolls the whole command back.
            await db.SaveChangesAsync(cancellationToken);
            if (plan.Captures.Count > 0)
            {
                var asBuilt = BuildAsBuilt(command, plan, capturedUnits, metadata);
                var asBuiltPlan = await asBuiltPlanner.PlanAsync(asBuilt, cancellationToken);

                // TIGHT COUPLING (P3-T06 LateSerialization composition)
                // WHAT: this calls CorrectAsBuilt's approved executor with the outer correction metadata and suppresses
                // its separate correction_record. WHY: ADR-0001 defines one LateSerialization approval and KPI count.
                // WHAT BREAKS IF SEPARATED: duplicating the link writer lets interval/cycle rules drift; recording both
                // commands double-counts one business correction. Keep the matching comment on ExecuteApprovedAsync.
                await CorrectAsBuiltHandler.ExecuteApprovedAsync(asBuilt, asBuiltPlan, metadata, db, writer, corrections, tenant, clock,
                    recordCorrection: false, cancellationToken: cancellationToken);
                // P6 hook: the Unit.AsBuiltCorrected facts above trigger release-record reissue for every affected parent.
            }
        }

        var coverageJson = JsonSerializer.Serialize(plan.Coverage, LedgerJson.Options);
        var row = new ItemTrackingModeChange
        {
            TenantId = tenant.TenantId,
            ChangeId = changeId,
            ItemId = command.ItemId,
            PreviousTrackingMode = plan.PreviousTrackingMode,
            TrackingMode = plan.TrackingMode,
            EffectiveAt = command.EffectiveAt,
            RecordedAt = clock.UtcNow,
            CorrectionId = metadata.CorrectionId,
            CorrectsCorrectionId = metadata.CorrectsCorrectionId,
            ActorRef = command.ActorRef,
            CommissionedCount = commissioning.Count,
            ExemptedCount = plan.Coverage.Sum(c => c.ExemptQuantity),
            KitsCaptured = plan.Captures.Count,
            StockCoverage = coverageJson,
        };
        db.ItemTrackingModeChanges.Add(row);
        var affectedParents = plan.Captures.Select(c => c.Link.ParentUnitId).Distinct().ToList();
        var affectedUnits = commissioning.Select(c => c.UnitId).Concat(affectedParents).Distinct().Count();
        corrections.Record(CorrectionCommand.LateSerialization, metadata, command.ActorRef, command.EffectiveAt, affectedUnits);
        await db.SaveChangesAsync(cancellationToken);

        if (publish)
        {
            var header = new PublishedSummaryHeader(ItemTrackingModeChanged.Schema, ItemTrackingModeChanged.Version, changeId,
                command.EffectiveAt, row.RecordedAt, command.LegalEntityId, command.ActorRef, command.SourceDocument);
            var publishedCorrection = new PublishedCorrection(metadata.CorrectionId, metadata.ReasonCode, metadata.InitiatedVia,
                metadata.CorrectsCorrectionId, (metadata.Approvers ?? []).Select(a => a.UserRef).ToList());
            await bus.PublishAsync(new ItemTrackingModeChanged(header, publishedCorrection, command.ItemId, plan.PreviousTrackingMode,
                plan.TrackingMode, command.EffectiveAt, plan.Reversed, plan.Coverage), new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        }

        var result = new LateSerializationResult(changeId, metadata.CorrectionId, command.ItemId, plan.PreviousTrackingMode,
            plan.TrackingMode, commissioning.Count, row.ExemptedCount, plan.Captures.Count, plan.KitRequirements.Where(k => !k.CapturedByCommand).Sum(k => k.Quantity), plan.Reversed);
        return new(result, approvalRefusal, commissioning, plan.Coverage, plan.KitRequirements, affectedParents,
            plan.KitRequirements.ToDictionary(k => k.AssemblyLinkId, k => k.ParentUnitId));
    }

    private async Task<CommissionUnitsResult> CommissionAsync(Guid commandId, string origin, IReadOnlyList<CommissionLine> lines,
        DateTime effectiveAt, DocumentRef document, Guid? locationId, string? locationKind, LateSerialization outer,
        CorrectionMetadata metadata, CancellationToken cancellationToken)
    {
        var command = new CommissionUnits(commandId, outer.ItemId, origin, lines, effectiveAt, outer.ActorRef, outer.LegalEntityId,
            Document: document, LocationId: locationId, LocationKind: locationKind, Source: "late_serialization");
        var result = await CommissionUnitsHandler.HandleLateAsync(command, metadata, db, writer, bulk, identifiers, items, ranges,
            reuseSettings, tenant, clock, cancellationToken);
        if (result.Commissioned != lines.Count)
            throw new CommandRefusedException("late_commissioning_incomplete", "Every supplied late serial must commission a new unit.", "resolve duplicate or invalid serials and preview again");
        return result;
    }

    private static void AddCommissioning(List<LateCommissionedUnit> target, CommissionUnitsResult result, IReadOnlyList<CommissionLine> lines,
        Guid? locationId, string? locationKind, DocumentRef document, bool forKit)
    {
        foreach (var line in result.Lines)
        {
            var serial = lines[line.Index].Identifiers[0].RawValue;
            target.Add(new(line.UnitId!.Value, serial, locationId, locationKind, document, forKit));
        }
    }

    private static CorrectAsBuilt BuildAsBuilt(LateSerialization command, LateSerializationPlan plan,
        Dictionary<Guid, IReadOnlyList<Guid>> capturedUnits, CorrectionMetadata metadata)
    {
        var targets = plan.Captures.GroupBy(c => (c.Link.ParentUnitId, c.Input.FittedAt)).Select(group =>
        {
            var changes = new List<AsBuiltLinkCorrection>();
            foreach (var capture in group)
            {
                changes.Add(new(AsBuiltCorrectionChange.Remove, capture.Link.LinkId));
                changes.AddRange(capturedUnits[capture.Link.LinkId].Select(unitId => new AsBuiltLinkCorrection(AsBuiltCorrectionChange.Add,
                    Installed: new(new(UnitId: unitId), Relation: capture.Link.Relation, Slot: capture.Link.Slot,
                        ThroughPhantomItemId: capture.Link.ThroughPhantomItemId, ReleaseStatus: capture.Link.ReleaseStatus,
                        ReleaseDocRef: capture.Link.ReleaseDocRef))));
            }
            return new AsBuiltParentCorrection(new(UnitId: group.Key.ParentUnitId), group.Key.FittedAt, changes);
        }).ToList();
        return new CorrectAsBuilt(command.AsBuiltCommandId!.Value, targets, CorrectAsBuiltReason.LateSerialization,
            command.ActorRef, command.LegalEntityId, metadata, command.CommittedBy, command.Note);
    }

    private async Task<int> RemainingKitQuantityAsync(Guid itemId, CancellationToken cancellationToken) =>
        (await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenant.TenantId && l.ChildItemId == itemId && l.RemovedAt == null)
            .Select(l => l.Quantity).ToListAsync(cancellationToken)).Sum(q => DecimalQuantity(q));

    private static int DecimalQuantity(decimal? quantity) => quantity is { } q && q == decimal.Truncate(q) ? (int)q : 0;
}

public sealed class LateSerializationPlanner(SerialisationDbContext db, EffectiveItemProfiles items, IStockQuantityPort stock,
    ITenantContext tenant, IClock clock)
{
    internal async Task<LateSerializationPlan> PlanAsync(LateSerialization command, CancellationToken cancellationToken)
    {
        CorrectionRules.ValidateReason(CorrectionCommand.LateSerialization, command.Reason);
        if (command.EffectiveAt > clock.UtcNow)
            throw new CommandRefusedException("effective_time_in_future", "The tracking-mode change cannot take effect in the future.", "enter the effective time that has already occurred");
        if (command.SourceDocument.Id == Guid.Empty || string.IsNullOrWhiteSpace(command.SourceDocument.Type))
            throw new CommandRefusedException("source_document_required", "The tracking-mode change needs its source document.");

        var profile = await items.GetAsync(command.ItemId, command.EffectiveAt, cancellationToken)
            ?? throw new CommandRefusedException("item_unknown", $"Item {command.ItemId} is not known to the host.", "register the item or check the connector's item mapping");
        var latest = await db.ItemTrackingModeChanges.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && c.ItemId == command.ItemId && c.EffectiveAt <= command.EffectiveAt)
            .OrderByDescending(c => c.EffectiveAt).ThenByDescending(c => c.RecordedAt).FirstOrDefaultAsync(cancellationToken);
        var current = profile.TrackingMode;

        if (command.Correction.CorrectsCorrectionId is { } priorCorrectionId)
        {
            if (command.Stock.Count > 0 || command.KitCaptures.Count > 0)
                throw new CommandRefusedException("reversal_inputs_not_empty", "A tracking-mode reversal cannot commission stock or capture kit links.", "remove stock and kit capture inputs");
            var prior = await db.ItemTrackingModeChanges.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CorrectionId == priorCorrectionId, cancellationToken)
                ?? throw new CommandRefusedException("corrected_tracking_change_unknown", $"Correction {priorCorrectionId} is not a tracking-mode change.", "select the LateSerialization correction to reverse");
            if (prior.ItemId != command.ItemId)
                throw new CommandRefusedException("corrected_item_mismatch", "The referenced tracking-mode correction belongs to another item.");
            if (latest?.CorrectionId != prior.CorrectionId)
                throw new CommandRefusedException("tracking_change_not_current", "The referenced tracking-mode correction is no longer the current change.", "reverse the current tracking-mode correction first");
            var activity = await HasSerialisedActivityAsync(command.ItemId, prior.EffectiveAt, cancellationToken);
            if (activity)
                throw new CommandRefusedException("serialised_activity_after_tracking_change", "The tracking-mode change cannot be reversed after serialised units were created.", "correct or retire the serialised units before proposing a different item change");
            return new(prior.TrackingMode, prior.PreviousTrackingMode, true, [], [], [], []);
        }

        if (current == ItemSerialisationProfile.TrackingModes.Serial)
            throw new CommandRefusedException("item_already_serial_tracked", $"Item {profile.ItemCode} is already serial-tracked.");
        if (current != ItemSerialisationProfile.TrackingModes.None)
            throw new CommandRefusedException("item_not_non_serialised", $"Item {profile.ItemCode} is '{current}', not non-serialised.", "use the tracking-mode workflow appropriate to the current mode");

        var open = await stock.GetOpenStockAsync(command.ItemId, command.EffectiveAt, cancellationToken);
        var coverage = LateSerializationCoverageRules.Validate(open, command.Stock);
        var openLinks = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenant.TenantId && l.ChildItemId == command.ItemId && l.RemovedAt == null).ToListAsync(cancellationToken);
        var links = openLinks.ToDictionary(l => l.LinkId);
        var captures = new List<PlannedLateKitCapture>();
        var captured = new HashSet<Guid>();
        foreach (var input in command.KitCaptures)
        {
            if (!captured.Add(input.ExistingLinkId)) throw new CommandRefusedException("kit_link_repeated", $"Assembly link {input.ExistingLinkId} is captured more than once.");
            if (!links.TryGetValue(input.ExistingLinkId, out var link))
                throw new CommandRefusedException("kit_link_unknown", $"Assembly link {input.ExistingLinkId} is not an open non-serialised link for item {command.ItemId}.", "refresh the kit list and select an open link");
            var quantity = WholeQuantity(link.Quantity, "kit_quantity_not_whole", $"Assembly link {link.LinkId} does not contain a whole-unit quantity.");
            if (input.Units.Count != quantity)
                throw new CommandRefusedException("kit_serial_count_mismatch", $"Assembly link {link.LinkId} needs {quantity} serials but {input.Units.Count} were supplied.", "supply one serial for every fitted unit");
            ValidateLines(input.Units);
            if (input.FittedAt > clock.UtcNow) throw new CommandRefusedException("effective_time_in_future", "A kit capture cannot be fitted in the future.");
            captures.Add(new(input, link, quantity));
        }

        var commandIds = command.Stock.Select(s => s.CommissionCommandId).Concat(command.KitCaptures.Select(c => c.CommissionCommandId)).ToList();
        if (commandIds.Any(id => id == Guid.Empty) || commandIds.Distinct().Count() != commandIds.Count)
            throw new CommandRefusedException("commission_command_id_invalid", "Every stock and kit batch needs a distinct non-empty commissioning command id.");
        if (captures.Count > 0 && (command.AsBuiltCommandId is null || command.AsBuiltCommandId == Guid.Empty))
            throw new CommandRefusedException("asbuilt_command_id_required", "Kit capture needs a non-empty AsBuilt command id.");

        var requirements = openLinks.Select(l => new LateKitRequirement(l.ParentUnitId, l.LinkId,
            WholeQuantity(l.Quantity, "kit_quantity_not_whole", $"Assembly link {l.LinkId} does not contain a whole-unit quantity."), captured.Contains(l.LinkId))).ToList();
        return new(current, ItemSerialisationProfile.TrackingModes.Serial, false, coverage, captures, requirements, openLinks);
    }

    private async Task<bool> HasSerialisedActivityAsync(Guid itemId, DateTime since, CancellationToken cancellationToken)
    {
        var unitIds = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenant.TenantId && a.ItemId == itemId)
            .Select(a => a.UnitId).Distinct().ToListAsync(cancellationToken);
        foreach (var chunk in unitIds.Chunk(2000))
        {
            if (await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenant.TenantId && chunk.Contains(e.UnitId) && e.EventTime >= since, cancellationToken)
                || await db.ArchivedEvents.AsNoTracking().AnyAsync(e => e.TenantId == tenant.TenantId && chunk.Contains(e.UnitId) && e.EventTime >= since, cancellationToken))
                return true;
        }
        return false;
    }

    private static void ValidateLines(IReadOnlyList<CommissionLine> lines)
    {
        if (lines.Any(l => l.Identifiers.Count == 0))
            throw new CommandRefusedException("late_serial_required", "Every late-serialised unit needs at least one identifier.", "supply one serial per unit");
    }

    private static int WholeQuantity(decimal? value, string rule, string message) =>
        value is { } quantity && quantity >= 0 && quantity == decimal.Truncate(quantity) && quantity <= int.MaxValue
            ? (int)quantity
            : throw new CommandRefusedException(rule, message);
}

public static class LateSerializationCoverageRules
{
    public static IReadOnlyList<PublishedLateStockCoverage> Validate(IReadOnlyList<OpenStockQuantity> open,
        IReadOnlyList<LateSerializationStockLocation> supplied)
    {
        var openByLocation = Unique(open.Select(o => (Key: (o.LocationId, o.LocationKind), Value: Whole(o.Quantity))), "stock_port_location_repeated");
        var suppliedByLocation = Unique(supplied.Select(s => (Key: (s.LocationId, s.LocationKind), Value: s)), "stock_location_repeated");
        var keys = openByLocation.Keys.Union(suppliedByLocation.Keys).ToList();
        var answer = new List<PublishedLateStockCoverage>();
        foreach (var key in keys)
        {
            var quantity = openByLocation.GetValueOrDefault(key);
            var input = suppliedByLocation.GetValueOrDefault(key);
            var serials = input?.Units.Count ?? 0;
            var exempt = input?.ExemptQuantity ?? 0;
            if (exempt < 0) throw new CommandRefusedException("exempt_quantity_invalid", "An exempt quantity cannot be negative.");
            if (exempt > 0 && string.IsNullOrWhiteSpace(input?.ExemptReason))
                throw new CommandRefusedException("exempt_reason_required", "Every late-serialisation exemption needs exempt_reason.", "record why these open stock units cannot be serialised");
            if (string.IsNullOrWhiteSpace(key.LocationKind))
                throw new CommandRefusedException("stock_location_kind_required", "Every open stock bucket needs a location kind.");
            if (serials + exempt < quantity)
                throw new CommandRefusedException("open_stock_not_covered", $"Location '{key.LocationKind}' has {quantity} open units but only {serials + exempt} are covered.", "supply one serial per remaining unit or explicitly exempt it with exempt_reason");
            if (serials + exempt > quantity)
                throw new CommandRefusedException("open_stock_quantity_exceeded", $"Location '{key.LocationKind}' has {quantity} open units but {serials + exempt} were supplied.", "refresh the stock quantity and remove surplus serials or exemptions");
            if (input is not null && (input.SourceDocument.Id == Guid.Empty || string.IsNullOrWhiteSpace(input.SourceDocument.Type)))
                throw new CommandRefusedException("source_document_required", "Every late stock coverage row needs a source document.");
            if (input is not null && input.Units.Any(l => l.Identifiers.Count == 0))
                throw new CommandRefusedException("late_serial_required", "Every late-serialised stock unit needs at least one identifier.", "supply one serial per unit");
            if (input is not null)
                answer.Add(new(key.LocationId, key.LocationKind, quantity, serials, exempt, input.ExemptReason, input.SourceDocument));
        }
        return answer;
    }

    private static Dictionary<(Guid? LocationId, string LocationKind), T> Unique<T>(IEnumerable<((Guid? LocationId, string LocationKind) Key, T Value)> values, string rule)
    {
        var result = new Dictionary<(Guid?, string), T>();
        foreach (var (key, value) in values)
            if (!result.TryAdd(key, value)) throw new CommandRefusedException(rule, $"Location '{key.LocationKind}' appears more than once.", "combine the location into one row");
        return result;
    }

    private static int Whole(decimal quantity) => quantity >= 0 && quantity == decimal.Truncate(quantity) && quantity <= int.MaxValue
        ? (int)quantity
        : throw new CommandRefusedException("open_stock_not_unit_quantity", $"Open stock quantity {quantity} is not a non-negative whole-unit quantity.", "resolve the inventory quantity before changing tracking mode");
}

internal sealed record LateSerializationPlan(
    string PreviousTrackingMode,
    string TrackingMode,
    bool Reversed,
    IReadOnlyList<PublishedLateStockCoverage> Coverage,
    IReadOnlyList<PlannedLateKitCapture> Captures,
    IReadOnlyList<LateKitRequirement> KitRequirements,
    IReadOnlyList<AssemblyLink> OpenKitLinks);
internal sealed record PlannedLateKitCapture(LateSerializationKitCapture Input, AssemblyLink Link, int Quantity);
internal sealed record LateKitRequirement(Guid ParentUnitId, Guid AssemblyLinkId, int Quantity, bool CapturedByCommand);
internal sealed record LateCommissionedUnit(Guid UnitId, string Serial, Guid? LocationId, string? LocationKind, DocumentRef SourceDocument, bool ForKit);
internal sealed record LateSerializationExecution(
    LateSerializationResult Result,
    CommandRefusal? ApprovalRefusal,
    IReadOnlyList<LateCommissionedUnit> Commissioning,
    IReadOnlyList<PublishedLateStockCoverage> Coverage,
    IReadOnlyList<LateKitRequirement> KitRequirements,
    IReadOnlyList<Guid> CapturedParents,
    IReadOnlyDictionary<Guid, Guid> KitParentByLink)
{
    public static LateSerializationExecution Replayed(ItemTrackingModeChange row, int remaining) =>
        new(new(row.ChangeId, row.CorrectionId, row.ItemId, row.PreviousTrackingMode, row.TrackingMode, row.CommissionedCount,
            row.ExemptedCount, row.KitsCaptured, remaining, row.CorrectsCorrectionId is not null, true), null, [], [], [], [], new Dictionary<Guid, Guid>());
}

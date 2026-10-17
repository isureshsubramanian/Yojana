using System.Text.Json;
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

namespace Yojana.Serialisation.Commands;

/// <summary>
/// P3-T07: voids a never-externalised pending or pre-release unit without deleting it. The same command reverses a
/// prior void when <see cref="CorrectionMetadata.CorrectsCorrectionId"/> names that VoidUnit correction.
/// </summary>
public static class VoidUnitHandler
{
    public static async Task<VoidUnitResult> Handle(VoidUnit command, SerialisationDbContext db, IUnitEventWriter writer,
        CorrectionLedger corrections, CorrectionApprovalSettings approvals, SerialReuseSettings reuseSettings,
        IItemQueryPort items, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var replay = await ReplayAsync(command, db, tenantId, cancellationToken);
        if (replay is not null) return replay;

        var plan = await PlanAsync(command, db, corrections, approvals, reuseSettings, items, tenant, cancellationToken);
        var eventType = plan.Reversed ? UnitEventType.VoidReversed : UnitEventType.Voided;
        var eventId = TimeOrderedId.New(command.EventTime);
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.UnitId, eventType, command.EventTime);
        var identifierIds = new List<Guid>();

        if (plan.Reversed)
        {
            foreach (var closed in plan.Identifiers)
            {
                var reopened = CloneForReversal(closed, tenantId, command, eventId, clock.UtcNow);
                db.Identifiers.Add(reopened);
                identifierIds.Add(reopened.IdentifierId);
            }
        }
        else
        {
            foreach (var identifier in plan.Identifiers)
            {
                identifier.ValidTo = command.EventTime;
                identifier.CorrectionReason = command.Reason;
                identifierIds.Add(identifier.IdentifierId);
            }
        }

        var attributes = CorrectionLedger.Attributes(plan.Metadata, new
        {
            command_id = command.CommandId,
            previous_lifecycle_status = plan.PreviousStatus,
            lifecycle_status = plan.NextStatus,
            reversed = plan.Reversed,
            closed_identifier_ids = plan.Reversed ? Array.Empty<Guid>() : identifierIds.ToArray(),
            restored_identifier_ids = plan.Reversed ? identifierIds.ToArray() : Array.Empty<Guid>(),
            serial_reuse_outcome = plan.SerialReuseOutcome,
            production_order = command.ProductionOrder,
        });
        await writer.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = command.UnitId,
                EventTime = command.EventTime,
                EventType = eventType,
                PresetEventId = eventId,
                BizStep = plan.Reversed ? "commissioning" : "decommissioning",
                Disposition = UnitLifecycle.DispositionFor(plan.NextStatus),
                LegalEntityId = command.LegalEntityId,
                ActorRef = command.ActorRef,
                DocumentType = command.ProductionOrder?.Type,
                DocumentId = command.ProductionOrder?.Id,
                DocumentLine = command.ProductionOrder?.Line,
                IdempotencyKey = key,
                Attributes = attributes,
                Delta = new CurrentStateDelta { LifecycleStatus = plan.NextStatus, Disposition = UnitLifecycle.DispositionFor(plan.NextStatus) },
            },
        ], cancellationToken);
        plan.Unit.LifecycleStatus = plan.NextStatus;
        await corrections.RecordAsync(CorrectionCommand.VoidUnit, plan.Metadata, command.ActorRef, command.EventTime, [command.UnitId], cancellationToken);
        return new(command.UnitId, plan.PreviousStatus, plan.NextStatus, identifierIds, plan.SerialReuseOutcome,
            plan.Metadata.CorrectionId, plan.Reversed);
    }

    internal static async Task<CorrectionPreview> PreviewAsync(VoidUnit command, SerialisationDbContext db, CorrectionLedger corrections,
        CorrectionApprovalSettings approvals, SerialReuseSettings reuseSettings, IItemQueryPort items, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = await PlanAsync(command, db, corrections, approvals, reuseSettings, items, tenant, cancellationToken);
            var serial = await SerialAsync(db, tenant.TenantId, command.UnitId, cancellationToken);
            var summary = plan.Reversed
                ? $"Restore lifecycle status to {plan.NextStatus} and open {plan.Identifiers.Count} replacement identifier row(s)."
                : $"Set lifecycle status to void and close {plan.Identifiers.Count} identifier row(s); serial policy outcome: {plan.SerialReuseOutcome}.";
            return new(null, CorrectionCommand.VoidUnit, 1, 1, 0, 0,
                [new(command.UnitId, serial, UnitOutcome.Outcomes.Change, null, summary)],
                [new(command.UnitId, plan.Reversed ? UnitEventType.VoidReversed : UnitEventType.Voided, command.EventTime, summary)],
                [], [], []);
        }
        catch (CommandRefusedException ex)
        {
            var serial = await SerialAsync(db, tenant.TenantId, command.UnitId, cancellationToken);
            return new(null, CorrectionCommand.VoidUnit, 1, 0, 0, 1,
                [new(command.UnitId, serial, UnitOutcome.Outcomes.Block, ex.Rule, ex.Message, ex.UnblockingStep)],
                [], [], [], [], ex.Refusal);
        }
    }

    private static async Task<Plan> PlanAsync(VoidUnit command, SerialisationDbContext db, CorrectionLedger corrections,
        CorrectionApprovalSettings approvals, SerialReuseSettings reuseSettings, IItemQueryPort items, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        CorrectionRules.ValidateReason(CorrectionCommand.VoidUnit, command.Reason);
        if (command.ProductionOrder is { Type: not "production_order" })
            throw new CommandRefusedException("production_order_invalid", "VoidUnit's production-order reference must have type 'production_order'.", "supply the Manufacturing production-order reference");
        var metadata = await corrections.PrepareAsync(CorrectionCommand.VoidUnit, command.Correction, command.CommandId,
            command.Reason, command.Note, null, command.EventTime, CorrectionInitiatingRole.ProductionSupervisor, cancellationToken);
        var unit = await db.Units.FirstOrDefaultAsync(u => u.TenantId == tenant.TenantId && u.UnitId == command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        var prior = metadata.CorrectsCorrectionId is { } priorId
            ? await db.Corrections.AsNoTracking().SingleAsync(c => c.TenantId == tenant.TenantId && c.CorrectionId == priorId, cancellationToken)
            : null;
        var reversed = prior?.Command == CorrectionCommand.VoidUnit;
        var documentFacts = await UnitCorrectionFacts.ReadAsync(db, tenant.TenantId, command.UnitId, cancellationToken);
        Approve(await approvals.GetAsync(cancellationToken), command, metadata,
            new(documentFacts.EverShipped, documentFacts.AppearsOnExternalDocument));

        if (await db.AssemblyLinks.AsNoTracking().AnyAsync(l => l.TenantId == tenant.TenantId && l.ParentUnitId == command.UnitId && l.RemovedAt == null, cancellationToken))
        {
            throw new CommandRefusedException("components_still_issued", "The unit still has issued components.",
                "reverse-issue the components in Manufacturing, then retry VoidUnit");
        }

        if (reversed)
        {
            if (unit.LifecycleStatus != LifecycleStatus.Void)
                throw new CommandRefusedException("void_not_current", $"Unit {unit.UnitId} is {unit.LifecycleStatus}, not void.", "select the current void correction");
            var fact = await FindVoidFactAsync(db, tenant.TenantId, unit.UnitId, prior!.CorrectionId, cancellationToken)
                ?? throw new CommandRefusedException("void_fact_unknown", "The correction record exists but its unit.voided fact is unavailable.", "restore the archived ledger partition, then retry");
            if (command.EventTime <= fact.EventTime)
                throw new CommandRefusedException("reversal_not_later", "The reversing correction must take effect after the void.", "choose an effective time after the void");
            if (fact.Reversed)
                throw new CommandRefusedException("void_already_reversed", "That void correction was already a reversal rather than the void being corrected.", "select the original void correction");
            var identifiers = await db.Identifiers.Where(i => i.TenantId == tenant.TenantId && fact.ClosedIdentifierIds.Contains(i.IdentifierId)).ToListAsync(cancellationToken);
            if (identifiers.Count != fact.ClosedIdentifierIds.Count)
                throw new CommandRefusedException("void_identifiers_unavailable", "One or more identifiers closed by the void are unavailable.", "restore the identifier history before reversing the void");
            await RefuseReusedAsync(db, tenant.TenantId, unit.UnitId, fact.EventTime, identifiers, cancellationToken);
            return new(unit, metadata, true, unit.LifecycleStatus, fact.PreviousStatus, identifiers, SerialReuseOutcome.VoidReversed);
        }

        var next = UnitLifecycle.Require(unit.LifecycleStatus, UnitEventType.Voided, unit.Origin, unit.UnitId);
        var open = await db.Identifiers.Where(i => i.TenantId == tenant.TenantId && i.UnitId == unit.UnitId && i.ValidTo == null).ToListAsync(cancellationToken);
        if (open.Any(i => i.ValidFrom > command.EventTime))
            throw new CommandRefusedException("effective_before_identifier", "The void is dated before one or more current identifiers became valid.", "choose an effective time on or after the latest identifier");
        var assignment = await db.ItemAssignments.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant.TenantId && a.UnitId == unit.UnitId && a.ValidTo == null, cancellationToken)
            ?? throw new CommandRefusedException("no_item_assignment", $"Unit {unit.UnitId} has no current item assignment.");
        if (assignment.ValidFrom > command.EventTime)
            throw new CommandRefusedException("effective_before_item", "The void is dated before the current item assignment became valid.", "choose an effective time on or after the current item assignment");
        var profile = await items.GetAsync(assignment.ItemId, cancellationToken)
            ?? throw new CommandRefusedException("item_unknown", $"Item {assignment.ItemId} is not known to the host.");
        // SerialReuseSettings is factory-registered in SerialisationModule: exposing its ITenantSettings dependency to
        // Wolverine would add PlatformDbContext to this handler graph and prevent the host from starting.
        var policy = SerialReuseSettings.Resolve(await reuseSettings.GetAsync(cancellationToken), profile.ItemClass);
        var outcome = policy == SerialReusePolicyValue.NeverShippedAndNotExternal
            ? SerialReuseOutcome.EligibleForReuse
            : SerialReuseOutcome.NeverReuse;
        return new(unit, metadata, false, unit.LifecycleStatus, next, open, outcome);
    }

    private static void Approve(CorrectionApprovalPolicy policy, VoidUnit command, CorrectionMetadata metadata, CorrectionApprovalFacts facts)
    {
        var roles = (command.CommittedBy?.Roles ?? []).Concat(metadata.Approvers?.SelectMany(a => a.Roles) ?? []).Distinct(StringComparer.Ordinal).ToList();
        var decision = CorrectionApprovalRules.Evaluate(policy, CorrectionCommand.VoidUnit, facts, roles);
        if (decision.Status != CorrectionApprovalStatus.Allowed && decision.Refusal is { } refusal)
            throw new CommandRefusedException(refusal.Rule, refusal.Message, refusal.UnblockingStep);
    }

    private static UnitIdentifier CloneForReversal(UnitIdentifier source, TenantId tenantId, VoidUnit command, Guid eventId, DateTime recorded) => new()
    {
        TenantId = tenantId,
        IdentifierId = TimeOrderedId.New(command.EventTime),
        UnitId = source.UnitId,
        Type = source.Type,
        Scheme = source.Scheme,
        IssuerPartyRef = source.IssuerPartyRef,
        EnterpriseIdentifier = source.EnterpriseIdentifier,
        Qualifier = source.Qualifier,
        RawValue = source.RawValue,
        NormalisedValue = source.NormalisedValue,
        ScopeKey = source.ScopeKey,
        ValidFrom = command.EventTime,
        SupersedesIdentifierId = source.IdentifierId,
        CorrectionReason = "void_reversed",
        DuplicateAcknowledged = source.DuplicateAcknowledged,
        ActorRef = command.ActorRef,
        RecordedTime = recorded,
        EventId = eventId,
    };

    private static async Task RefuseReusedAsync(SerialisationDbContext db, TenantId tenantId, Guid unitId, DateTime voidedAt,
        IReadOnlyList<UnitIdentifier> identifiers, CancellationToken cancellationToken)
    {
        foreach (var identifier in identifiers)
        {
            if (await db.Identifiers.AsNoTracking().AnyAsync(i => i.TenantId == tenantId && i.UnitId != unitId
                    && i.Scheme == identifier.Scheme && i.ScopeKey == identifier.ScopeKey && i.NormalisedValue == identifier.NormalisedValue
                    && i.ValidFrom > voidedAt, cancellationToken))
            {
                throw new CommandRefusedException("serial_reused", $"Serial '{identifier.NormalisedValue}' was re-used after this unit was voided.",
                    "the void cannot be reversed; keep the lineage and correct the later unit if it is wrong");
            }
        }
    }

    private static async Task<VoidFact?> FindVoidFactAsync(SerialisationDbContext db, TenantId tenantId, Guid unitId, Guid correctionId,
        CancellationToken cancellationToken)
    {
        var marker = correctionId.ToString();
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId && e.EventType == UnitEventType.Voided
                && e.Attributes != null && e.Attributes.Contains(marker))
            .Select(e => new { e.EventTime, e.Attributes }).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId && e.EventType == UnitEventType.Voided
                && e.Attributes != null && e.Attributes.Contains(marker))
            .Select(e => new { e.EventTime, e.Attributes }).ToListAsync(cancellationToken);
        foreach (var row in live.Concat(archived).OrderByDescending(e => e.EventTime))
        {
            using var json = JsonDocument.Parse(row.Attributes!);
            var root = json.RootElement;
            if (!root.TryGetProperty("correction_id", out var id) || id.GetGuid() != correctionId) continue;
            return new(row.EventTime, root.GetProperty("previous_lifecycle_status").GetString()!,
                root.GetProperty("closed_identifier_ids").EnumerateArray().Select(x => x.GetGuid()).ToList(),
                root.TryGetProperty("reversed", out var reversed) && reversed.GetBoolean());
        }
        return null;
    }

    private static async Task<VoidUnitResult?> ReplayAsync(VoidUnit command, SerialisationDbContext db, TenantId tenantId, CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.UnitId, UnitEventType.Voided, command.EventTime),
            IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.UnitId, UnitEventType.VoidReversed, command.EventTime),
        };
        var row = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && keys.Contains(e.IdempotencyKey) && e.EventTime == command.EventTime, cancellationToken);
        if (row is not null) return Replay(command.UnitId, row.Attributes);
        var archived = await db.ArchivedEvents.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && keys.Contains(e.IdempotencyKey) && e.EventTime == command.EventTime, cancellationToken);
        return archived is null ? null : Replay(command.UnitId, archived.Attributes);
    }

    private static VoidUnitResult Replay(Guid unitId, string? attributes)
    {
        using var json = JsonDocument.Parse(attributes ?? "{}");
        var root = json.RootElement;
        var reversed = root.TryGetProperty("reversed", out var reversal) && reversal.GetBoolean();
        var idsName = reversed ? "restored_identifier_ids" : "closed_identifier_ids";
        return new(unitId, root.GetProperty("previous_lifecycle_status").GetString()!, root.GetProperty("lifecycle_status").GetString()!,
            root.GetProperty(idsName).EnumerateArray().Select(x => x.GetGuid()).ToList(), root.GetProperty("serial_reuse_outcome").GetString()!,
            root.GetProperty("correction_id").GetGuid(), reversed, true);
    }

    private static async Task<string?> SerialAsync(SerialisationDbContext db, TenantId tenantId, Guid unitId, CancellationToken cancellationToken) =>
        await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && i.UnitId == unitId)
            .OrderBy(i => i.Type == IdentifierType.InternalSerial ? 0 : 1).ThenByDescending(i => i.ValidFrom)
            .Select(i => i.NormalisedValue).FirstOrDefaultAsync(cancellationToken);

    private sealed record Plan(Unit Unit, CorrectionMetadata Metadata, bool Reversed, string PreviousStatus, string NextStatus,
        IReadOnlyList<UnitIdentifier> Identifiers, string SerialReuseOutcome);
    private sealed record VoidFact(DateTime EventTime, string PreviousStatus, IReadOnlyList<Guid> ClosedIdentifierIds, bool Reversed);
}

public static class PreviewVoidUnitHandler
{
    public static Task<CorrectionPreview> Handle(PreviewVoidUnit preview, SerialisationDbContext db, CorrectionLedger corrections,
        CorrectionApprovalSettings approvals, SerialReuseSettings reuseSettings, IItemQueryPort items, ITenantContext tenant,
        CancellationToken cancellationToken) =>
        VoidUnitHandler.PreviewAsync(preview.Command, db, corrections, approvals, reuseSettings, items, tenant, cancellationToken);
}

using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

// TIGHT COUPLING (P3-T03 pairwise identifier swap)
// WHAT: both old UnitIdentifier rows are flushed closed before either replacement row is added, inside the same
// transaction. WHY: SQL Server checks the filtered unique indexes immediately and has no deferred-constraint mode.
// WHAT BREAKS IF SEPARATED: inserting either replacement while the counterpart remains open rejects a valid swap.
// The matching warning lives beside the indexes in SerialisationDbContext; keep both comments and this order together.

/// <summary>
/// P3-T03: atomically moves one identifier type between two units, records one fact per unit and one correction KPI row,
/// and sends asset-tag print requests through the outbox. Release-record reissue belongs to P6, whose consumer will use
/// <see cref="UnitIdentifiersSwapped"/>; this handler deliberately emits no certificate command.
/// </summary>
public static class SwapIdentifiersHandler
{
    public static async Task<SwapIdentifiersResult> Handle(
        SwapIdentifiers command,
        SerialisationDbContext db,
        IUnitEventWriter writer,
        IdentifierService identifiers,
        CorrectionLedger corrections,
        CorrectionApprovalSettings approvalSettings,
        IMessageBus bus,
        ITenantContext tenant,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var keys = Keys(command);
        var replay = await db.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EventTime == command.EventTime && (e.IdempotencyKey == keys.First || e.IdempotencyKey == keys.Second))
            .ToListAsync(cancellationToken);
        if (replay.Count > 0) return Replay(command, replay);

        var inspection = await InspectAsync(command, db, identifiers, corrections, approvalSettings, tenant, cancellationToken);
        if (inspection.Refusal is { } refusal) throw new CommandRefusedException(refusal.Rule, refusal.Message, refusal.UnblockingStep);

        var owned = db.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = owned ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var swapId = command.SwapId is { } supplied && supplied != Guid.Empty ? supplied : command.CommandId;
            var now = clock.UtcNow;
            var firstEventId = TimeOrderedId.New(command.EventTime);
            var secondEventId = TimeOrderedId.New(command.EventTime);
            var firstReplacement = inspection.FirstReceivesSecond.ToRow(tenantId, command.FirstUnitId, command.EventTime, command.ActorRef, now, firstEventId);
            var secondReplacement = inspection.SecondReceivesFirst.ToRow(tenantId, command.SecondUnitId, command.EventTime, command.ActorRef, now, secondEventId);

            firstReplacement.SupersedesIdentifierId = inspection.SecondIdentifier.IdentifierId;
            secondReplacement.SupersedesIdentifierId = inspection.FirstIdentifier.IdentifierId;
            firstReplacement.SwappedWithIdentifierId = secondReplacement.IdentifierId;
            secondReplacement.SwappedWithIdentifierId = firstReplacement.IdentifierId;
            firstReplacement.CorrectionReason = command.Reason;
            secondReplacement.CorrectionReason = command.Reason;
            firstReplacement.DuplicateAcknowledged = inspection.SecondIdentifier.DuplicateAcknowledged;
            secondReplacement.DuplicateAcknowledged = inspection.FirstIdentifier.DuplicateAcknowledged;

            inspection.FirstIdentifier.ValidTo = command.EventTime;
            inspection.FirstIdentifier.SupersededByIdentifierId = secondReplacement.IdentifierId;
            inspection.FirstIdentifier.CorrectionReason = command.Reason;
            inspection.SecondIdentifier.ValidTo = command.EventTime;
            inspection.SecondIdentifier.SupersededByIdentifierId = firstReplacement.IdentifierId;
            inspection.SecondIdentifier.CorrectionReason = command.Reason;

            // This is an intermediate database state, not an intermediate commit: the ambient transaction still owns
            // both closures, both openings, both ledger facts, the correction row and every outbox request.
            await db.SaveChangesAsync(cancellationToken);
            db.Identifiers.AddRange(firstReplacement, secondReplacement);

            var drafts = new[]
            {
                Draft(command, inspection.Metadata, command.FirstUnitId, command.SecondUnitId, swapId, inspection.FirstIdentifier, firstReplacement, firstEventId, keys.First),
                Draft(command, inspection.Metadata, command.SecondUnitId, command.FirstUnitId, swapId, inspection.SecondIdentifier, secondReplacement, secondEventId, keys.Second),
            };
            await writer.AppendAsync(drafts, cancellationToken);
            await corrections.RecordAsync(CorrectionCommand.SwapIdentifiers, inspection.Metadata, command.ActorRef, command.EventTime, [command.FirstUnitId, command.SecondUnitId], cancellationToken);

            var tags = command.IdentifierType == IdentifierType.CustomerAssetTag
                ? new[] { firstReplacement, secondReplacement }
                : inspection.AssetTags;
            foreach (var tag in tags)
            {
                var triggeringEventId = tag.UnitId == command.FirstUnitId ? firstEventId : secondEventId;
                await bus.PublishAsync(new AssetTagPrintJobRequested(TimeOrderedId.New(command.EventTime), tag.UnitId, tag.IdentifierId,
                    tag.NormalisedValue, swapId, inspection.Metadata.CorrectionId, triggeringEventId, now, command.LegalEntityId, command.ActorRef),
                    new DeliveryOptions { TenantId = tenantId.ToString() });
            }

            // Force the final uniqueness check while this handler can still return the shared refusal contract. The
            // Wolverine transaction remains open, so this persists neither ledger facts nor outbox messages early.
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);

            return new(swapId, command.FirstUnitId, command.SecondUnitId, firstReplacement.IdentifierId, secondReplacement.IdentifierId,
                inspection.Metadata.CorrectionId, tags.Length, false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw new CommandRefusedException("identifier_in_use", "An identifier became unavailable while the swap was being committed.", "refresh the preview and resolve the conflicting open identifier");
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    internal static async Task<SwapInspection> InspectAsync(
        SwapIdentifiers command,
        SerialisationDbContext db,
        IdentifierService identifiers,
        CorrectionLedger corrections,
        CorrectionApprovalSettings approvalSettings,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        if (command.FirstUnitId == command.SecondUnitId) throw new CommandRefusedException("two_units_required", "An identifier swap needs two different units.", "select the counterpart unit");
        if (string.IsNullOrWhiteSpace(command.IdentifierType)) throw new CommandRefusedException("identifier_type_required", "An identifier type is required.");
        CorrectionRules.ValidateReason(CorrectionCommand.SwapIdentifiers, command.Reason);

        var tenantId = tenant.TenantId;
        var units = await db.Units.AsNoTracking().Where(u => u.TenantId == tenantId && (u.UnitId == command.FirstUnitId || u.UnitId == command.SecondUnitId)).ToListAsync(cancellationToken);
        if (units.Count != 2)
        {
            var missing = new[] { command.FirstUnitId, command.SecondUnitId }.Except(units.Select(u => u.UnitId));
            throw new CommandRefusedException("unit_unknown", $"Unit(s) {string.Join(", ", missing)} do not exist.");
        }

        var open = await db.Identifiers
            .Where(i => i.TenantId == tenantId && (i.UnitId == command.FirstUnitId || i.UnitId == command.SecondUnitId) && i.Type == command.IdentifierType && i.ValidTo == null)
            .ToListAsync(cancellationToken);
        var first = open.SingleOrDefault(i => i.UnitId == command.FirstUnitId)
            ?? throw new CommandRefusedException("no_open_identifier", $"Unit {command.FirstUnitId} has no open {command.IdentifierType}.", "assign the missing identifier before swapping");
        var second = open.SingleOrDefault(i => i.UnitId == command.SecondUnitId)
            ?? throw new CommandRefusedException("no_open_identifier", $"Unit {command.SecondUnitId} has no open {command.IdentifierType}.", "assign the missing identifier before swapping");
        if (first.NormalisedValue == second.NormalisedValue && first.ScopeKey == second.ScopeKey)
            throw new CommandRefusedException("no_change", "Both units already carry the same normalised identifier in the same scope.", "nothing to swap");

        var itemRows = await db.ItemAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && (a.UnitId == command.FirstUnitId || a.UnitId == command.SecondUnitId) && a.ValidTo == null)
            .Select(a => new { a.UnitId, a.ItemId }).ToListAsync(cancellationToken);
        if (itemRows.Count != 2) throw new CommandRefusedException("no_item_assignment", "Both units need a current item assignment before their identifier scopes can be checked.");
        var items = itemRows.ToDictionary(a => a.UnitId, a => a.ItemId);
        var owners = await db.Episodes.AsNoTracking()
            .Where(e => e.TenantId == tenantId && (e.UnitId == command.FirstUnitId || e.UnitId == command.SecondUnitId) && e.Kind == EpisodeKind.Ownership && e.ValidTo == null)
            .Select(e => new { e.UnitId, e.PartyRef }).ToDictionaryAsync(e => e.UnitId, e => e.PartyRef, cancellationToken);

        var firstReceivesSecond = identifiers.Prepare(Input(second), items[command.FirstUnitId], owners.GetValueOrDefault(command.FirstUnitId));
        var secondReceivesFirst = identifiers.Prepare(Input(first), items[command.SecondUnitId], owners.GetValueOrDefault(command.SecondUnitId));
        RefuseInvalid(firstReceivesSecond, command.FirstUnitId);
        RefuseInvalid(secondReceivesFirst, command.SecondUnitId);
        await RefuseThirdPartyCollision(firstReceivesSecond, command.FirstUnitId, command.SecondUnitId, identifiers, cancellationToken);
        await RefuseThirdPartyCollision(secondReceivesFirst, command.FirstUnitId, command.SecondUnitId, identifiers, cancellationToken);

        var metadata = await corrections.PrepareAsync(CorrectionCommand.SwapIdentifiers, command.Correction, command.CommandId, command.Reason, command.Note,
            null, command.EventTime, CorrectionInitiatingRole.SerialAdministrator, cancellationToken);
        await ValidateReversalAsync(command, metadata, first, second, db, tenantId, cancellationToken);

        var shipped = await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenantId
            && (e.UnitId == command.FirstUnitId || e.UnitId == command.SecondUnitId) && e.EventType == UnitEventType.Shipped, cancellationToken);
        var committer = command.CommittedBy ?? throw new CommandRefusedException("committer_required", "A swap names who asks to commit it and the roles they hold.");
        if (!string.Equals(committer.UserRef, command.ActorRef, StringComparison.Ordinal))
            throw new CommandRefusedException("committer_mismatch", "The correction actor and committer must be the same signed-in user.");
        var policy = await approvalSettings.GetAsync(cancellationToken);
        var facts = new CorrectionApprovalFacts(UnitHasShipped: shipped);
        var decision = CorrectionApprovalRules.Evaluate(policy, CorrectionCommand.SwapIdentifiers, facts, committer.Roles);
        if (decision.Status == CorrectionApprovalStatus.Refused)
            throw new CommandRefusedException(decision.Refusal!.Rule, decision.Refusal.Message, decision.Refusal.UnblockingStep);
        var approved = CorrectionApprovalRules.ApprovalsSatisfied(policy, CorrectionCommand.SwapIdentifiers, facts, committer, metadata.Approvers!);
        var refusal = approved ? null : decision.Refusal ?? new CommandRefusal("approval_required", "The identifier swap needs the tenant policy's approver roles.", "route the identical preview to an authorised approver");

        var assetTags = await db.Identifiers.AsNoTracking()
            .Where(i => i.TenantId == tenantId && (i.UnitId == command.FirstUnitId || i.UnitId == command.SecondUnitId)
                && i.Type == IdentifierType.CustomerAssetTag && i.ValidTo == null)
            .ToArrayAsync(cancellationToken);
        return new(first, second, firstReceivesSecond, secondReceivesFirst, metadata, assetTags, refusal);
    }

    private static async Task ValidateReversalAsync(SwapIdentifiers command, CorrectionMetadata metadata, UnitIdentifier first, UnitIdentifier second,
        SerialisationDbContext db, TenantId tenantId, CancellationToken cancellationToken)
    {
        if (metadata.CorrectsCorrectionId is not { } priorId) return;
        var prior = await db.Corrections.AsNoTracking().SingleAsync(c => c.TenantId == tenantId && c.CorrectionId == priorId, cancellationToken);
        if (prior.Command != CorrectionCommand.SwapIdentifiers)
            throw new CommandRefusedException("corrected_correction_not_swap", $"Correction {priorId} is '{prior.Command}', not an identifier swap.", "select the earlier swap being reversed");

        var candidates = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.EventTime == prior.EventTime && e.EventType == UnitEventType.IdentifiersSwapped).ToListAsync(cancellationToken);
        var priorFacts = candidates.Where(e => AttributeGuid(e.Attributes, "correction_id") == priorId).ToList();
        var sameUnits = priorFacts.Select(e => e.UnitId).ToHashSet().SetEquals([command.FirstUnitId, command.SecondUnitId]);
        var sameType = priorFacts.Count == 2 && priorFacts.All(e => AttributeString(e.Attributes, "identifier_type") == command.IdentifierType);
        if (!sameUnits || !sameType)
            throw new CommandRefusedException("corrected_swap_differs", $"Correction {priorId} swapped another unit pair or identifier type.", "select the two units and identifier type from the earlier swap");
        var firstPriorReceived = priorFacts.Single(e => e.UnitId == command.FirstUnitId);
        var secondPriorReceived = priorFacts.Single(e => e.UnitId == command.SecondUnitId);
        if (AttributeNestedGuid(firstPriorReceived.Attributes, "received_identifier", "identifier_id") != first.IdentifierId
            || AttributeNestedGuid(secondPriorReceived.Attributes, "received_identifier", "identifier_id") != second.IdentifierId)
            throw new CommandRefusedException("corrected_swap_not_current", $"Correction {priorId} is no longer the current identifier state of both units.", "review the later identifier corrections before reversing this swap");
    }

    private static UnitEventDraft Draft(SwapIdentifiers command, CorrectionMetadata metadata, Guid unitId, Guid counterpartUnitId, Guid swapId,
        UnitIdentifier given, UnitIdentifier received, Guid eventId, string key) => new()
    {
        UnitId = unitId,
        EventTime = command.EventTime,
        EventType = UnitEventType.IdentifiersSwapped,
        PresetEventId = eventId,
        LegalEntityId = command.LegalEntityId,
        ActorRef = command.ActorRef,
        DocumentType = command.Document?.Type,
        DocumentId = command.Document?.Id,
        DocumentLine = command.Document?.Line,
        IdempotencyKey = key,
        Attributes = CorrectionLedger.Attributes(metadata, new
        {
            command_id = command.CommandId,
            swap_id = swapId,
            counterpart_unit_id = counterpartUnitId,
            identifier_type = command.IdentifierType,
            given_identifier = Published(given),
            received_identifier = Published(received),
        }),
        Delta = null,
    };

    private static object Published(UnitIdentifier identifier) => new
    {
        identifier_id = identifier.IdentifierId,
        type = identifier.Type,
        scheme = identifier.Scheme,
        value = identifier.NormalisedValue,
        issuer_party_ref = identifier.IssuerPartyRef,
    };

    private static IdentifierInput Input(UnitIdentifier source) =>
        new(source.Type, source.RawValue, source.IssuerPartyRef, source.Qualifier, source.EnterpriseIdentifier);

    private static void RefuseInvalid(PreparedIdentifier prepared, Guid unitId)
    {
        if (prepared.Blocks)
            throw new CommandRefusedException("identifier_invalid", $"The identifier moving to unit {unitId} fails validation: {string.Join(", ", prepared.Validation.Reasons)}.", "correct the invalid identifier before swapping");
    }

    private static async Task RefuseThirdPartyCollision(PreparedIdentifier prepared, Guid firstUnitId, Guid secondUnitId, IdentifierService identifiers, CancellationToken cancellationToken)
    {
        if (await identifiers.HolderOfAsync(prepared.Policy.Scheme, prepared.ScopeKey, prepared.Normalised, cancellationToken) is { } holder
            && holder.UnitId != firstUnitId && holder.UnitId != secondUnitId)
            throw new CommandRefusedException("identifier_in_use", $"'{prepared.Normalised}' would collide with open identifier {holder.IdentifierId} on unit {holder.UnitId} in its new scope.", "resolve the third unit before swapping");
    }

    private static (string First, string Second) Keys(SwapIdentifiers command) =>
        (IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.FirstUnitId, UnitEventType.IdentifiersSwapped, command.EventTime),
         IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.SecondUnitId, UnitEventType.IdentifiersSwapped, command.EventTime));

    private static SwapIdentifiersResult Replay(SwapIdentifiers command, List<UnitEvent> events)
    {
        if (events.Count != 2) throw new CommandRefusedException("replay_inconsistent", "Only one side of the identifier swap is present in the ledger.");
        var first = events.SingleOrDefault(e => e.UnitId == command.FirstUnitId)
            ?? throw new CommandRefusedException("replay_inconsistent", "The recorded swap does not contain the first unit.");
        var second = events.SingleOrDefault(e => e.UnitId == command.SecondUnitId)
            ?? throw new CommandRefusedException("replay_inconsistent", "The recorded swap does not contain the second unit.");
        return new(AttributeGuid(first.Attributes, "swap_id") ?? Guid.Empty, command.FirstUnitId, command.SecondUnitId,
            AttributeNestedGuid(first.Attributes, "received_identifier", "identifier_id") ?? Guid.Empty,
            AttributeNestedGuid(second.Attributes, "received_identifier", "identifier_id") ?? Guid.Empty,
            AttributeGuid(first.Attributes, "correction_id") ?? Guid.Empty, 0, true);
    }

    private static Guid? AttributeGuid(string? json, string name)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetGuid() : null;
    }

    private static string? AttributeString(string? json, string name)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static Guid? AttributeNestedGuid(string? json, string parent, string name)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(parent, out var nested) && nested.TryGetProperty(name, out var value) ? value.GetGuid() : null;
    }
}

internal sealed record SwapInspection(
    UnitIdentifier FirstIdentifier,
    UnitIdentifier SecondIdentifier,
    PreparedIdentifier FirstReceivesSecond,
    PreparedIdentifier SecondReceivesFirst,
    CorrectionMetadata Metadata,
    UnitIdentifier[] AssetTags,
    CommandRefusal? Refusal);

public static class PreviewSwapIdentifiersHandler
{
    public static async Task<CorrectionPreview> Handle(
        PreviewSwapIdentifiers preview,
        SerialisationDbContext db,
        IdentifierService identifiers,
        CorrectionLedger corrections,
        CorrectionApprovalSettings approvalSettings,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var command = preview.Command;
        try
        {
            var inspection = await SwapIdentifiersHandler.InspectAsync(command, db, identifiers, corrections, approvalSettings, tenant, cancellationToken);
            var blocked = inspection.Refusal is not null;
            var outcome = blocked ? UnitOutcome.Outcomes.Block : UnitOutcome.Outcomes.Change;
            var units = new[]
            {
                new UnitOutcome(command.FirstUnitId, inspection.FirstIdentifier.NormalisedValue, outcome, inspection.Refusal?.Rule,
                    $"give {inspection.FirstIdentifier.NormalisedValue}; receive {inspection.SecondIdentifier.NormalisedValue}", inspection.Refusal?.UnblockingStep),
                new UnitOutcome(command.SecondUnitId, inspection.SecondIdentifier.NormalisedValue, outcome, inspection.Refusal?.Rule,
                    $"give {inspection.SecondIdentifier.NormalisedValue}; receive {inspection.FirstIdentifier.NormalisedValue}", inspection.Refusal?.UnblockingStep),
            };
            var events = new[]
            {
                new CorrectionEventPreview(command.FirstUnitId, UnitEventType.IdentifiersSwapped, command.EventTime, $"receive {inspection.SecondIdentifier.NormalisedValue}"),
                new CorrectionEventPreview(command.SecondUnitId, UnitEventType.IdentifiersSwapped, command.EventTime, $"receive {inspection.FirstIdentifier.NormalisedValue}"),
            };
            var printJobs = inspection.AssetTags.Select(t => new CorrectionPrintJobPreview(t.UnitId, "customer_asset_tag", "identifier_swap")).ToList();
            return new(null, CorrectionCommand.SwapIdentifiers, 2, blocked ? 0 : 2, 0, blocked ? 2 : 0, units, events, [],
                // P6 adds release-record commands. Unit.IdentifiersSwapped is the stable hook; P3 previews only what it records now.
                [], printJobs, inspection.Refusal);
        }
        catch (CommandRefusedException ex)
        {
            return new(null, CorrectionCommand.SwapIdentifiers, 0, 0, 0, 1, [], [], [], [], [], ex.Refusal);
        }
    }
}

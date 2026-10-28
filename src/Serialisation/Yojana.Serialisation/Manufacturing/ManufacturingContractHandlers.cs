using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Wolverine;

namespace Yojana.Serialisation.Manufacturing;

/// <summary>
/// The Serialisation side of the production-order contract. It knows only Manufacturing's integration messages and
/// the module's command contracts; it never reads a Manufacturing table or depends on its transaction timing.
/// </summary>
public sealed class ManufacturingContractService(
    SerialisationDbContext db,
    IUnitEventWriter writer,
    EpisodeService episodes,
    UnitResolver resolver,
    CorrectionLedger corrections,
    CorrectionApprovalSettings approvals,
    SerialReuseSettings reuseSettings,
    IItemQueryPort items,
    ITenantContext tenant,
    IClock clock,
    IMessageBus bus)
{
    // TIGHT COUPLING (Wolverine transaction discovery)
    // WHAT: both message handlers keep SerialisationDbContext in their signatures although this service owns all data
    // work. WHY: Wolverine discovers the transactional outbox from a handler dependency graph, not from method bodies.
    // WHAT BREAKS IF SEPARATED: removing that parameter lets effects commit apart from the receipt or outcome event.
    public async Task<ManufacturingComponentReturnedResult> ComponentReturnedAsync(
        ManufacturingComponentReturned message,
        CancellationToken cancellationToken)
    {
        RequireTransaction();
        var hash = PayloadHash(message);
        if (await ReplayAsync<ManufacturingComponentReturnedResult>(message.Envelope.MessageId, ManufacturingComponentReturned.Schema, hash, cancellationToken) is { } replay)
            return replay with { Duplicate = true };

        var refusals = new List<ManufacturingContractRefusal>();
        var closed = new List<Guid>();
        var returned = new List<Guid>();
        if (message.ProductionOrder.Type != "production_order")
        {
            refusals.Add(Refusal(null, null, "production_order_invalid", "The production-order reference must have type 'production_order'.", "supply the Manufacturing production-order reference"));
        }
        else if (message.WarehouseLocationId == Guid.Empty)
        {
            refusals.Add(Refusal(null, null, "warehouse_required", "A returned component needs its warehouse location.", "supply the warehouse location id from Manufacturing"));
        }
        else if (message.Components.Count == 0 && (message.HeldComponents is null or { Count: 0 }))
        {
            refusals.Add(Refusal(null, null, "components_required", "ComponentReturned names no assembly links or held serials.", "supply every physically returned component"));
        }
        else
        {
            var repeated = message.Components.GroupBy(component => component.AssemblyLinkId).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            foreach (var linkId in repeated)
                refusals.Add(Refusal(null, linkId, "component_repeated", $"Assembly link {linkId} appears more than once.", "send each returned assembly link once"));

            var requested = message.Components.Where(component => !repeated.Contains(component.AssemblyLinkId)).ToList();
            var linkIds = requested.Select(component => component.AssemblyLinkId).ToList();
            var links = await db.AssemblyLinks.Where(link => link.TenantId == tenant.TenantId && linkIds.Contains(link.LinkId)).ToListAsync(cancellationToken);
            var linksById = links.ToDictionary(link => link.LinkId);
            var orderParents = (await AllocationsAsync(message.ProductionOrder, cancellationToken)).Select(allocation => allocation.UnitId).ToHashSet();
            var valid = new List<AssemblyLink>();
            foreach (var component in requested)
            {
                if (!linksById.TryGetValue(component.AssemblyLinkId, out var link))
                {
                    refusals.Add(Refusal(component.ParentUnitId, component.AssemblyLinkId, "link_unknown", $"Assembly link {component.AssemblyLinkId} does not exist.", "refresh the order's open component links and retry"));
                    continue;
                }

                if (link.ParentUnitId != component.ParentUnitId)
                {
                    refusals.Add(Refusal(component.ParentUnitId, component.AssemblyLinkId, "link_parent_mismatch", $"Assembly link {link.LinkId} belongs to parent {link.ParentUnitId}.", "send the parent recorded on the open assembly link"));
                    continue;
                }

                if (!orderParents.Contains(component.ParentUnitId))
                {
                    refusals.Add(Refusal(component.ParentUnitId, component.AssemblyLinkId, "unit_not_allocated_to_order", $"Unit {component.ParentUnitId} was not allocated to production order {message.ProductionOrder.Id}.", "send the production order that allocated the parent unit"));
                    continue;
                }

                if (link.RemovedAt is not null)
                {
                    refusals.Add(Refusal(component.ParentUnitId, component.AssemblyLinkId, "link_already_closed", $"Assembly link {link.LinkId} was already removed.", "replay the original message id or omit the already returned link"));
                    continue;
                }

                if (link.InstalledAt > message.Envelope.OccurredAt)
                {
                    refusals.Add(Refusal(component.ParentUnitId, component.AssemblyLinkId, "return_before_issue", $"Assembly link {link.LinkId} was installed after the component-return time.", "correct the Manufacturing event time and retry with a new message id"));
                    continue;
                }

                valid.Add(link);
            }

            foreach (var group in valid.GroupBy(link => link.ParentUnitId))
            {
                var commandId = DeterministicId(message.Envelope.MessageId, $"disassembly:{group.Key:N}");
                var disassembly = await RecordDisassemblyHandler.Handle(new RecordDisassembly(
                    commandId,
                    group.Key,
                    group.Select(link => link.LinkId).ToList(),
                    message.Envelope.OccurredAt,
                    message.Envelope.Source,
                    message.Envelope.LegalEntityId,
                    AssemblyRemovalReason.ReverseIssue,
                    message.ProductionOrder), db, writer, tenant, cancellationToken);
                closed.AddRange(disassembly.ClosedLinkIds);

                // The custody handler checks open links in the database. Flush the link closures inside the same
                // transaction so its mismatch-instance guard sees the reverse issue that just happened.
                await db.SaveChangesAsync(cancellationToken);
                foreach (var childUnitId in group.Select(link => link.ChildUnitId).OfType<Guid>().Distinct())
                {
                    var custodyId = DeterministicId(message.Envelope.MessageId, $"custody:{childUnitId:N}");
                    await ChangeCustodyHandler.Handle(new ChangeCustody(
                        custodyId,
                        childUnitId,
                        LocationKind.Warehouse,
                        EpisodeReason.Return,
                        message.Envelope.OccurredAt,
                        message.Envelope.Source,
                        message.Envelope.LegalEntityId,
                        message.WarehouseLocationId,
                        Source: EpisodeSource.Manufacturing,
                        Document: message.ProductionOrder), db, writer, episodes, tenant, cancellationToken);
                    returned.Add(childUnitId);
                }
            }

            // A serial identifies a unit only together with its item: the ERP may number a CPU and a DIMM alike.
            var repeatedHeld = (message.HeldComponents ?? []).GroupBy(component =>
                    (Serial: component.Serial.ToUpperInvariant(), component.ItemId))
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            foreach (var repeatedSerial in repeatedHeld)
                refusals.Add(Refusal(null, null, "component_repeated", $"Held component serial '{repeatedSerial.Serial}' appears more than once.",
                    "send each returned serial once"));
            foreach (var component in (message.HeldComponents ?? []).Where(component =>
                         !repeatedHeld.Contains((component.Serial.ToUpperInvariant(), component.ItemId))))
            {
                var unit = await resolver.ResolveAsync(new DocumentSerial(component.Serial), component.ItemId,
                    cancellationToken);
                if (unit is null)
                {
                    refusals.Add(Refusal(null, null, "component_unknown",
                        $"Returned component serial '{component.Serial}' is unknown; no unit was commissioned.",
                        "receive the existing component before returning it"));
                    continue;
                }
                var hold = await db.ProductionComponentHolds.SingleOrDefaultAsync(row =>
                    row.TenantId == tenant.TenantId && row.UnitId == unit.UnitId
                    && row.ProductionOrderType == message.ProductionOrder.Type
                    && row.ProductionOrderId == message.ProductionOrder.Id && row.ReturnedAt == null,
                    cancellationToken);
                if (hold is null)
                {
                    refusals.Add(Refusal(unit.UnitId, null, "component_not_held_for_order",
                        $"Component serial '{component.Serial}' is not held against this production order.",
                        "send the order that holds the component"));
                    continue;
                }
                if (hold.AssemblyLinkId is { } heldLink && hold.ParentUnitId is { } heldParent)
                {
                    var disassembly = await RecordDisassemblyHandler.Handle(new RecordDisassembly(
                        DeterministicId(message.Envelope.MessageId, $"held-disassembly:{heldLink:N}"), heldParent,
                        [heldLink], message.Envelope.OccurredAt, message.Envelope.Source,
                        message.Envelope.LegalEntityId, AssemblyRemovalReason.ReverseIssue,
                        message.ProductionOrder), db, writer, tenant, cancellationToken);
                    closed.AddRange(disassembly.ClosedLinkIds);
                    await db.SaveChangesAsync(cancellationToken);
                }
                var returnDocument = message.Receipt ?? message.ProductionOrder;
                var move = await episodes.PrepareCustodyMoveAsync(unit.UnitId,
                    new CustodyTarget(LocationKind.Warehouse, message.WarehouseLocationId,
                        Reason: EpisodeReason.Return, Source: EpisodeSource.Manufacturing),
                    UnitEventType.CustodyChanged,
                    IdempotencyKey.For(message.Envelope.Source, returnDocument.Type, returnDocument.Id,
                        component.LineNo, unit.UnitId, UnitEventType.CustodyChanged,
                        message.Envelope.OccurredAt), message.Envelope.OccurredAt,
                    message.Envelope.Source, message.Envelope.LegalEntityId,
                    returnDocument with { Line = component.LineNo },
                    new Dictionary<string, object?>
                    {
                        ["message_id"] = message.Envelope.MessageId,
                        ["production_order_type"] = message.ProductionOrder.Type,
                        ["production_order_id"] = message.ProductionOrder.Id,
                        ["component_return"] = true,
                    }, null, false, cancellationToken);
                await writer.AppendAsync([move.Draft], cancellationToken);
                hold.ReturnedAt = message.Envelope.OccurredAt;
                returned.Add(unit.UnitId);
            }
        }

        var result = new ManufacturingComponentReturnedResult(message.Envelope.MessageId, message.ProductionOrder,
            closed.Distinct().Order().ToList(), returned.Distinct().Order().ToList(), refusals);
        Record(message.Envelope.MessageId, ManufacturingComponentReturned.Schema, hash, result);
        return result;
    }

    public async Task<ProductionOrderQuantityChangeResult> QuantityChangeAsync(
        ManufacturingProductionOrderQuantityChangeRequested message,
        CancellationToken cancellationToken)
    {
        RequireTransaction();
        var hash = PayloadHash(message);
        if (await ReplayAsync<ProductionOrderQuantityChangeResult>(message.Envelope.MessageId, ManufacturingProductionOrderQuantityChangeRequested.Schema, hash, cancellationToken) is { } replay)
            return replay with { Duplicate = true };

        var refusals = new List<ManufacturingContractRefusal>();
        var selected = new List<Guid>();
        var voided = new List<Guid>();
        var reduction = message.OldQuantity - message.NewQuantity;
        if (message.ProductionOrder.Type != "production_order")
        {
            refusals.Add(Refusal(null, null, "production_order_invalid", "The production-order reference must have type 'production_order'.", "supply the Manufacturing production-order reference"));
        }
        else if (message.OldQuantity < 0 || message.NewQuantity < 0)
        {
            refusals.Add(Refusal(null, null, "quantity_negative", "Production-order quantities cannot be negative.", "supply the posted old and new quantities"));
        }
        else if (reduction < 0)
        {
            refusals.Add(Refusal(null, null, "quantity_not_reduced", "The requested production-order quantity is greater than its old quantity.", "send increases through the Manufacturing allocation flow"));
        }
        else
        {
            var allocations = await AllocationsAsync(message.ProductionOrder, cancellationToken);
            var byUnit = allocations.ToDictionary(allocation => allocation.UnitId);
            if (message.SurplusUnitIds is { } explicitUnits)
            {
                if (explicitUnits.Count != explicitUnits.Distinct().Count())
                    refusals.Add(Refusal(null, null, "surplus_unit_repeated", "The explicit surplus list contains a unit more than once.", "send each surplus unit once"));
                if (explicitUnits.Count != reduction)
                    refusals.Add(Refusal(null, null, "surplus_count_mismatch", $"The explicit surplus list has {explicitUnits.Count} unit(s), but the quantity reduction is {reduction}.", "send exactly old_quantity minus new_quantity unit ids"));
                if (refusals.Count == 0) selected.AddRange(explicitUnits);
            }
            else if (reduction > 0)
            {
                var candidateIds = allocations.Where(allocation => allocation.LifecycleStatus != LifecycleStatus.Void).Select(allocation => allocation.UnitId).ToList();
                var external = await ExternalDocumentUnitsAsync(candidateIds, cancellationToken);
                selected.AddRange(allocations.Where(allocation => allocation.LifecycleStatus != LifecycleStatus.Void && !external.Contains(allocation.UnitId))
                    .OrderByDescending(allocation => allocation.AllocatedAt).ThenByDescending(allocation => allocation.Seq)
                    .Take(reduction).Select(allocation => allocation.UnitId));
            }

            foreach (var unitId in selected.ToList())
            {
                if (!byUnit.ContainsKey(unitId))
                {
                    refusals.Add(Refusal(unitId, null, "unit_not_allocated_to_order", $"Unit {unitId} was not allocated to production order {message.ProductionOrder.Id}.", "send a unit allocated by this production order"));
                    continue;
                }

                var reason = message.NewQuantity == 0 ? VoidUnitReason.WorkOrderCancelled : VoidUnitReason.WorkOrderQuantityReduced;
                var configured = await approvals.GetAsync(cancellationToken);
                var commitRoles = configured.Commands.FirstOrDefault(rule => rule.Command == CorrectionCommand.VoidUnit)?.CommitRoles ?? [];
                var commandId = DeterministicId(message.Envelope.MessageId, $"void-command:{unitId:N}");
                var correctionId = DeterministicId(message.Envelope.MessageId, $"void-correction:{unitId:N}");
                var correction = new CorrectionMetadata(correctionId, reason, $"Production order quantity changed from {message.OldQuantity} to {message.NewQuantity}.",
                    [$"manufacturing-message:{message.Envelope.MessageId:N}"], CorrectionInitiatedVia.Integration, null, CorrectionInitiatingRole.System);
                try
                {
                    await VoidUnitHandler.Handle(new VoidUnit(commandId, unitId, reason, message.Envelope.OccurredAt,
                        message.Envelope.Source, message.Envelope.LegalEntityId, message.ProductionOrder, correction.Note,
                        correction, new CorrectionCommitter(message.Envelope.Source, commitRoles)), db, writer, corrections,
                        approvals, reuseSettings, items, tenant, clock, cancellationToken);
                    voided.Add(unitId);
                }
                catch (CommandRefusedException ex)
                {
                    refusals.Add(Refusal(unitId, null, ex.Rule, ex.Message, ex.UnblockingStep));
                }
            }
        }

        var canApply = refusals.Count == 0 && selected.Count == voided.Count;
        var result = new ProductionOrderQuantityChangeResult(message.Envelope.MessageId, message.ProductionOrder,
            message.OldQuantity, message.NewQuantity, selected, voided, refusals, canApply);
        await bus.PublishAsync(new UnitProductionOrderReconciled(
            new PublishedSummaryHeader(UnitProductionOrderReconciled.Schema, UnitProductionOrderReconciled.Version,
                message.Envelope.MessageId, message.Envelope.OccurredAt, clock.UtcNow, message.Envelope.LegalEntityId,
                message.Envelope.Source, message.ProductionOrder),
            message.Envelope.MessageId, message.ProductionOrder, message.OldQuantity, message.NewQuantity, selected, voided,
            refusals, canApply), new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        Record(message.Envelope.MessageId, ManufacturingProductionOrderQuantityChangeRequested.Schema, hash, result);
        return result;
    }

    private async Task<IReadOnlyList<Allocation>> AllocationsAsync(DocumentRef order, CancellationToken cancellationToken)
    {
        var live = await (from unit in db.Units.AsNoTracking()
                          join created in db.Events.AsNoTracking()
                              on new { unit.TenantId, EventId = unit.CreatedEventId } equals new { created.TenantId, created.EventId }
                          where unit.TenantId == tenant.TenantId && created.DocumentType == order.Type && created.DocumentId == order.Id
                          select new Allocation(unit.UnitId, unit.LifecycleStatus, created.EventTime, created.Seq)).ToListAsync(cancellationToken);
        var archived = await (from unit in db.Units.AsNoTracking()
                              join created in db.ArchivedEvents.AsNoTracking()
                                  on new { unit.TenantId, EventId = unit.CreatedEventId } equals new { created.TenantId, created.EventId }
                              where unit.TenantId == tenant.TenantId && created.DocumentType == order.Type && created.DocumentId == order.Id
                              select new Allocation(unit.UnitId, unit.LifecycleStatus, created.EventTime, created.Seq)).ToListAsync(cancellationToken);
        return live.Concat(archived).GroupBy(allocation => allocation.UnitId).Select(group => group.OrderByDescending(allocation => allocation.Seq).First()).ToList();
    }

    private async Task<HashSet<Guid>> ExternalDocumentUnitsAsync(List<Guid> unitIds, CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0) return [];
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && unitIds.Contains(e.UnitId) && e.DocumentType != null)
            .Select(e => new { e.UnitId, e.DocumentType }).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && unitIds.Contains(e.UnitId) && e.DocumentType != null)
            .Select(e => new { e.UnitId, e.DocumentType }).ToListAsync(cancellationToken);
        return live.Concat(archived).Where(row => UnitCorrectionFacts.IsExternalDocument(row.DocumentType)).Select(row => row.UnitId).ToHashSet();
    }

    private async Task<T?> ReplayAsync<T>(Guid messageId, string messageType, string payloadHash, CancellationToken cancellationToken)
    {
        var receipt = await db.ManufacturingMessageReceipts.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.MessageId == messageId, cancellationToken);
        if (receipt is null) return default;
        if (!string.Equals(receipt.MessageType, messageType, StringComparison.Ordinal) || !string.Equals(receipt.PayloadHash, payloadHash, StringComparison.Ordinal))
            throw new CommandRefusedException("message_id_payload_mismatch", $"Manufacturing message id {messageId} was already used by a different payload.", "send the changed request with a new message id");
        return JsonSerializer.Deserialize<T>(receipt.ResultJson, LedgerJson.Options)
            ?? throw new InvalidOperationException($"Manufacturing receipt {messageId} has an unreadable result.");
    }

    private void Record<T>(Guid messageId, string messageType, string payloadHash, T result) =>
        db.ManufacturingMessageReceipts.Add(new ManufacturingMessageReceipt
        {
            TenantId = tenant.TenantId,
            MessageId = messageId,
            MessageType = messageType,
            PayloadHash = payloadHash,
            ResultJson = JsonSerializer.Serialize(result, LedgerJson.Options),
            ProcessedAt = clock.UtcNow,
        });

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The Manufacturing contract must run inside SerialisationDbContext's Wolverine transaction.");
    }

    private static string PayloadHash<T>(T message) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(message, LedgerJson.Options))).ToLowerInvariant();

    private static Guid DeterministicId(Guid messageId, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{messageId:N}|{purpose}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static ManufacturingContractRefusal Refusal(Guid? unitId, Guid? linkId, string rule, string message, string? unblockingStep) =>
        new(unitId, linkId, rule, message, unblockingStep);

    private sealed record Allocation(Guid UnitId, string LifecycleStatus, DateTime AllocatedAt, long Seq);
}

public static class ManufacturingComponentReturnedHandler
{
    // TIGHT COUPLING: this DbContext parameter is the transaction-discovery side of the service comment above. Removing
    // it makes the receipt, module effects and outbox reply non-atomic even though the handler still compiles.
    public static Task<ManufacturingComponentReturnedResult> Handle(ManufacturingComponentReturned message,
        ManufacturingContractService service, SerialisationDbContext db, CancellationToken cancellationToken) =>
        service.ComponentReturnedAsync(message, cancellationToken);
}

public static class ManufacturingProductionOrderQuantityChangeRequestedHandler
{
    // TIGHT COUPLING: keep this DbContext parameter for Wolverine transaction discovery; see the service comment above.
    public static Task<ProductionOrderQuantityChangeResult> Handle(ManufacturingProductionOrderQuantityChangeRequested message,
        ManufacturingContractService service, SerialisationDbContext db, CancellationToken cancellationToken) =>
        service.QuantityChangeAsync(message, cancellationToken);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Attributes;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Integration.Ordering;
using Yojana.Integration.ErpChange;
using Yojana.Integration.Backfills;
using SerialDocumentRef = Yojana.Serialisation.Contracts.DocumentRef;

namespace Yojana.Integration.Gateway;

public sealed record TranslateInboundMessage(Guid InboundMessageId);

public sealed record GatewayTranslationResult(Guid InboundMessageId, string MessageType, bool Translated,
    int UnitsAffected, IReadOnlyList<string> Exceptions);

// TIGHT COUPLING (canonical projection visibility)
// WHAT: this orchestration handler stays NonTransactional while InboundGateway commits its canonical projection before
// invoking a Serialisation consumed-event handler; IntegrationDocumentQueryPort documents the matching read side.
// WHY: the consumed-event handler runs in a separate message scope and SQL connection, so it cannot read an uncommitted
// integration projection. WHAT BREAKS IF SEPARATED: the handler refuses a valid document as unavailable.
[NonTransactional]
public sealed class TranslateInboundMessageHandler
{
    public static async Task<GatewayTranslationResult> Handle(TranslateInboundMessage command, InboundGateway gateway,
        InboundTranslationRecovery recovery, RetriedDeadLetterSettler retried, CancellationToken cancellationToken)
    {
        try
        {
            var result = await gateway.TranslateAsync(command.InboundMessageId, cancellationToken);
            await retried.SettleInboundAsync(command.InboundMessageId, cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            await recovery.RecordFailureAsync(command.InboundMessageId, exception, cancellationToken);
            throw;
        }
    }
}

public sealed class InboundGateway(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    GatewayReferenceStore references, ConnectionCapabilityReader capabilities, BoundedWaitResequencer resequencer,
    IMessageBus bus, InboundTranslationRecoveryOptions recoveryOptions, ITenantCurrency currency,
    IErpChangeDivergenceSink erpChangeDivergences)
{
    public async Task<GatewayTranslationResult> TranslateAsync(Guid inboundMessageId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var stale = now - recoveryOptions.GracePeriod;
        var wasReleasedLate = await db.InboundMessages.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.InboundMessageId == inboundMessageId)
            .Select(x => x.TranslationStatus == InboundTranslationStatus.Late)
            .SingleAsync(cancellationToken);
        var claimed = await db.InboundMessages.Where(x => x.TenantId == tenant.TenantId
                && x.InboundMessageId == inboundMessageId && x.IsLatest
                && db.Connections.Any(connection => connection.TenantId == x.TenantId
                    && connection.ConnectionId == x.ConnectionId && connection.Status == ConnectionStatus.Active)
                && x.TranslationStatus != InboundTranslationStatus.Completed
                && x.TranslationStatus != InboundTranslationStatus.Superseded
                && x.TranslationStatus != InboundTranslationStatus.NeedsAttention
                && x.TranslationStatus != InboundTranslationStatus.Held
                && (x.TranslationStatus != InboundTranslationStatus.Processing
                    || x.TranslationStartedAt == null || x.TranslationStartedAt <= stale)
                && (x.NextTranslationAttemptAt == null || x.NextTranslationAttemptAt <= now))
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.TranslationStatus, InboundTranslationStatus.Processing)
                .SetProperty(x => x.TranslationStartedAt, now)
                .SetProperty(x => x.NextTranslationAttemptAt, (DateTime?)null)
                .SetProperty(x => x.LastTranslationError, (string?)null)
                .SetProperty(x => x.TranslationAttemptCount, x => x.TranslationAttemptCount + 1), cancellationToken);
        if (claimed == 0)
        {
            var skipped = await db.InboundMessages.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
                && x.InboundMessageId == inboundMessageId, cancellationToken);
            return new(skipped.InboundMessageId, skipped.MessageType, false, 0, []);
        }
        var row = await db.InboundMessages.SingleAsync(x => x.TenantId == tenant.TenantId
            && x.InboundMessageId == inboundMessageId, cancellationToken);
        if (!row.IsLatest || row.TranslationStatus == InboundTranslationStatus.Superseded)
            return new(row.InboundMessageId, row.MessageType, false, 0, []);
        if (row.TranslationStatus == InboundTranslationStatus.Completed)
            return new(row.InboundMessageId, row.MessageType, false, 0, []);
        if (row.CaptureMode == BackfillCaptureModes.ComparisonOnly)
        {
            // An ERP-change target is evidence for comparison, not a second source of operational history.
            // Keep the accepted payload replayable while completing its durable translation hand-off without routing it.
            row.TranslationStatus = InboundTranslationStatus.Completed;
            row.TranslatedAt = now;
            row.TranslationStartedAt = null;
            await db.SaveChangesAsync(cancellationToken);
            return new(row.InboundMessageId, row.MessageType, false, 0, []);
        }

        var declaration = await capabilities.ReadAsync(row.ConnectionId, cancellationToken);
        var support = declaration.For(row.MessageType).SupportLevel;
        if (support == CapabilitySupport.Unsupported)
        {
            await RaiseAsync(row, "unsupported_inbound", $"{row.MessageType} is unsupported by this connection.", cancellationToken);
            row.TranslationStatus = InboundTranslationStatus.Completed;
            row.TranslatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return new(row.InboundMessageId, row.MessageType, true, 0, ["unsupported"]);
        }

        // A re-read of an original applied before its cancellation is a normal later version, not this case.
        if (row.MessageType != nameof(DocumentCancelled)
            && await WasCancelledBeforeAppliedAsync(row, cancellationToken)
            && DocumentReference(row) is { } cancelledOriginal
            && !await OriginalWasAppliedAsync(row.ConnectionId, cancelledOriginal, cancellationToken))
        {
            await RaiseAsync(row, "cancelled_before_applied",
                "The original document was not applied because its cancellation completed first.", cancellationToken);
            row.TranslationStatus = InboundTranslationStatus.Completed;
            row.TranslatedAt = clock.UtcNow;
            row.TranslationStartedAt = null;
            await db.SaveChangesAsync(cancellationToken);
            return new(row.InboundMessageId, row.MessageType, true, 0, ["cancelled_before_applied"]);
        }

        if (await ForeignAmountReasonAsync(row, cancellationToken) is { } foreignAmountReason)
        {
            // A connector may pass a local amount as the canonical price and home currency. If it only has the
            // document's foreign amount, keep the raw/canonical message for a person; this boundary never converts it.
            await RaiseAsync(row, "currency_not_home", foreignAmountReason, cancellationToken);
            row.TranslationStatus = InboundTranslationStatus.NeedsAttention;
            row.TranslationStartedAt = null;
            row.LastTranslationError = foreignAmountReason;
            row.AttentionReasonCode = "currency_not_home";
            await db.SaveChangesAsync(cancellationToken);
            return new(row.InboundMessageId, row.MessageType, false, 0, [foreignAmountReason]);
        }

        var secondary = await IsSecondaryDocumentAsync(row, cancellationToken);
        var result = secondary ? Done(row, 0, []) : row.MessageType switch
        {
            nameof(ItemUpserted) => await TranslateItemAsync(row, cancellationToken),
            nameof(BomUpserted) => await TranslateBomAsync(row, cancellationToken),
            nameof(PartyUpserted) => await TranslatePartyAsync(row, cancellationToken),
            nameof(GoodsReceiptPosted) => await TranslateReceiptAsync(row, cancellationToken),
            nameof(ProductionCompleted) => await TranslateDocumentAsync<ProductionCompleted>(row, p => p.DocumentRef,
                (env, doc) => new ManufacturingProductionReceiptPosted(env, doc), cancellationToken),
            nameof(ComponentsIssued) => await TranslateDocumentAsync<ComponentsIssued>(row, p => p.DocumentRef,
                (env, doc) => new ManufacturingComponentIssued(env, doc), cancellationToken),
            nameof(DeliveryPosted) => await TranslateDeliveryAsync(row, wasReleasedLate, cancellationToken),
            nameof(InvoicePosted) => await TranslateDocumentAsync<InvoicePosted>(row, p => p.DocumentRef,
                (env, doc) => new SalesInvoicePosted(env, doc), cancellationToken),
            nameof(ReturnReceived) => await TranslateDocumentAsync<ReturnReceived>(row, p => p.DocumentRef,
                (env, doc) => new SalesReturnPosted(env, doc), cancellationToken),
            nameof(TransferPosted) => await TranslateDocumentAsync<TransferPosted>(row, p => p.DocumentRef,
                (env, doc) => new InventoryTransferPosted(env, doc), cancellationToken),
            nameof(InventoryAdjusted) => await TranslateDocumentAsync<InventoryAdjusted>(row, p => p.DocumentRef,
                (env, doc) => new InventoryAdjustmentPosted(env, doc), cancellationToken),
            nameof(WarrantyLineSold) => await TranslateDocumentAsync<WarrantyLineSold>(row, p => p.DocumentRef,
                (env, doc) => new SalesInvoicePosted(env, doc), cancellationToken),
            nameof(DocumentCancelled) => await TranslateCancellationAsync(row, wasReleasedLate, cancellationToken),
            _ => throw new InvalidOperationException($"canonical_message_unknown: {row.MessageType}"),
        };
        if (result.Translated)
        {
            foreach (var exception in result.Exceptions)
                await RaiseAsync(row, "consumed_event_exception", exception, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(row).State = EntityState.Detached;
            await db.InboundMessages.Where(x => x.TenantId == tenant.TenantId
                    && x.InboundMessageId == inboundMessageId && x.IsLatest
                    && x.TranslationStatus == InboundTranslationStatus.Processing)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.TranslationStatus, InboundTranslationStatus.Completed)
                    .SetProperty(x => x.TranslatedAt, clock.UtcNow)
                    .SetProperty(x => x.TranslationStartedAt, (DateTime?)null), cancellationToken);
            if (!secondary && row.MessageType != nameof(DocumentCancelled) && DocumentReference(row) is { } document)
                await resequencer.ReleaseSatisfiedDocumentsAsync(row.ConnectionId,
                    [BoundedWaitResequencer.DocumentDependencyKey(DocumentExternalId(document))], cancellationToken);
        }
        else if (result.Exceptions.Contains("bounded_wait"))
        {
            db.Entry(row).State = EntityState.Detached;
            await db.InboundMessages.Where(x => x.TenantId == tenant.TenantId
                    && x.InboundMessageId == inboundMessageId && x.IsLatest
                    && x.TranslationStatus == InboundTranslationStatus.Processing)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.TranslationStatus, InboundTranslationStatus.Held)
                    .SetProperty(x => x.TranslationStartedAt, (DateTime?)null), cancellationToken);
        }
        return result;
    }

    private async Task<bool> IsSecondaryDocumentAsync(InboundMessage row, CancellationToken cancellationToken)
    {
        if (row.MessageType == nameof(DocumentCancelled)) return false;
        using var canonical = JsonDocument.Parse(row.CanonicalMessage);
        if (!canonical.RootElement.GetProperty("payload").TryGetProperty("document_ref", out var reference)) return false;
        var external = reference.Deserialize<ExternalDocumentRef>(IntegrationContracts.Json)!;
        var id = await references.FindAsync(row.ConnectionId, "document", external.ExternalId ?? external.Number, cancellationToken);
        if (id is null) return false;
        var incumbent = await db.CanonicalDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.DocumentId == id && x.ConnectionId != row.ConnectionId, cancellationToken);
        if (incumbent is null) return false;
        // Only an explicit, currently valid map can identify the same fact across connections. Equal remote numbers
        // in different companies are insufficient. The incumbent projection remains authoritative during overlap;
        // the secondary version stays in the inbox and any divergence becomes an operator review.
        var original = JsonNode.Parse(incumbent.Payload)!.AsObject();
        var incoming = JsonNode.Parse(canonical.RootElement.GetProperty("payload").GetRawText())!.AsObject();
        original.Remove("document_ref");
        incoming.Remove("document_ref");
        if (incumbent.MessageType != row.MessageType || !JsonNode.DeepEquals(original, incoming))
        {
            await RaiseAsync(row, "cross_connection_document_conflict", "The mapped business document differs from the incumbent connection; review before cutover.", cancellationToken);
            await erpChangeDivergences.ObserveDocumentAsync(row.ConnectionId, id.Value,
                $"{row.ErpEntity}:{row.ErpExternalId}", original.ToJsonString(), incoming.ToJsonString(),
                incumbent.SourceInboundMessageId, row.InboundMessageId, cancellationToken);
        }
        return true;
    }

    private async Task<GatewayTranslationResult> TranslateItemAsync(InboundMessage row, CancellationToken cancellationToken)
    {
        var message = Message<ItemUpserted>(row);
        await references.UpsertItemAsync(row.ConnectionId, message.Payload, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Done(row, 0, []);
    }

    private async Task<GatewayTranslationResult> TranslateBomAsync(InboundMessage row,
        CancellationToken cancellationToken)
    {
        var message = Message<BomUpserted>(row);
        _ = await references.UpsertBomAsync(row.ConnectionId, message.Payload, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Done(row, 0, []);
    }

    private async Task<GatewayTranslationResult> TranslatePartyAsync(InboundMessage row, CancellationToken cancellationToken)
    {
        var message = Message<PartyUpserted>(row);
        await references.UpsertPartyAsync(row.ConnectionId, message.Payload, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Done(row, 0, []);
    }

    private async Task<GatewayTranslationResult> TranslateDeliveryAsync(InboundMessage row, bool wasReleasedLate,
        CancellationToken cancellationToken)
    {
        var message = Message<DeliveryPosted>(row);
        if (!wasReleasedLate)
        {
            var serials = message.Payload.Lines.SelectMany(x => x.Serials).Select(x => x.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (serials.Count > 0)
            {
                var known = await bus.InvokeAsync<ResolveKnownSerialsResult>(new ResolveKnownSerials(serials), Options(), cancellationToken);
                if (known.Unknown.Count > 0)
                {
                    await resequencer.HoldAsync(row.InboundMessageId, row.ConnectionId, known.Unknown,
                        row.OccurredAt, cancellationToken);
                    return new(row.InboundMessageId, row.MessageType, false, 0, ["bounded_wait"]);
                }
            }
        }
        var doc = await ProjectDocumentAsync(row, message.Payload.DocumentRef, message.Payload, cancellationToken);
        await PrepareReferencesAsync(row, message.Payload.Lines, message.Payload.CustomerPartyExternalId,
            message.Payload.ShipToRef, cancellationToken);
        if (message.Payload.BaseOrderRef is not null)
            await EnsureDocumentRefAsync(row.ConnectionId, message.Payload.BaseOrderRef, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var first = await InvokeAsync(new SalesShipmentPosted(await ThinAsync(row, message.Envelope, cancellationToken), doc), cancellationToken);
        if (!first.Exceptions.Any(x => x.Contains("unknown serial", StringComparison.Ordinal))) return Done(row, first.UnitsAffected, first.Exceptions);

        var commissioned = 0;
        foreach (var line in message.Payload.Lines)
        {
            if (line.Serials.Count == 0) continue;
            var item = await references.FindAsync(row.ConnectionId, "item", line.ItemExternalId, cancellationToken)
                ?? throw new InvalidOperationException("item_reference_missing");
            var result = await bus.InvokeAsync<CommissionUnitsResult>(new CommissionUnits(
                DeterministicId.Create(row.MessageId, line.LineNo.ToString(System.Globalization.CultureInfo.InvariantCulture)), item, UnitOrigin.LegacyUnknown,
                line.Serials.Select(serial => new CommissionLine([new IdentifierInput(IdentifierType.Provisional, serial.Value)], serial.Value)).ToList(),
                row.OccurredAt.AddMilliseconds(-1), Source(row), await LegalEntityAsync(row.ConnectionId, cancellationToken),
                Document: doc, LocationId: line.LocationExternalId is null ? null
                    : await references.FindAsync(row.ConnectionId, "location", line.LocationExternalId, cancellationToken),
                LocationKind: line.LocationExternalId is null ? null : LocationKind.Warehouse,
                Source: $"integration:{row.ConnectorCode}", SkipInvalidLines: true, HistoryDepth: HistoryDepth.OpeningOnly),
                Options(), cancellationToken);
            foreach (var created in result.Lines.Where(x => x.Outcome == CommissionLineResult.Commissioned))
            {
                commissioned++;
                await RaiseAsync(row, "provisional_unit", $"Serial '{created.ClientReference}' did not resolve; provisional unit {created.UnitId:N} was created.", cancellationToken);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        var retried = await InvokeAsync(new SalesShipmentPosted(await ThinAsync(row, message.Envelope, cancellationToken), doc), cancellationToken);
        return Done(row, first.UnitsAffected + retried.UnitsAffected,
            first.Exceptions.Where(x => !x.Contains("unknown serial", StringComparison.Ordinal)).Concat(retried.Exceptions).ToList());
    }

    private async Task<GatewayTranslationResult> TranslateReceiptAsync(InboundMessage row, CancellationToken cancellationToken)
    {
        var message = Message<GoodsReceiptPosted>(row);
        var result = await TranslateDocumentAsync<GoodsReceiptPosted>(row, p => p.DocumentRef,
            (env, doc) => new PurchasingReceiptPosted(env, doc), cancellationToken,
            payload => DeduplicateReceiptAsync(row, payload, cancellationToken));
        await resequencer.ReleaseSatisfiedAsync(row.ConnectionId,
            message.Payload.Lines.SelectMany(x => x.Serials).Select(x => x.Value).ToList(), cancellationToken);
        return result;
    }

    private async Task<GatewayTranslationResult> TranslateCancellationAsync(InboundMessage row, bool wasReleasedLate,
        CancellationToken cancellationToken)
    {
        var message = Message<DocumentCancelled>(row);
        // The canonical document keeps only its latest message, so once a cancellation is applied the original no
        // longer shows there; a re-delivered cancellation must not wait for an original it already followed.
        if (!await OriginalWasAppliedAsync(row.ConnectionId, message.Payload.DocumentRef, cancellationToken)
            && !await CancellationWasAppliedAsync(row.ConnectionId, message.Payload.DocumentRef, cancellationToken))
        {
            var dependency = BoundedWaitResequencer.DocumentDependencyKey(
                DocumentExternalId(message.Payload.DocumentRef));
            if (!wasReleasedLate)
            {
                await resequencer.HoldAsync(row.InboundMessageId, row.ConnectionId, [dependency], row.OccurredAt,
                    cancellationToken);
                return new(row.InboundMessageId, row.MessageType, false, 0, ["bounded_wait"]);
            }

            await ProjectDocumentAsync(row, message.Payload.DocumentRef, message.Payload, cancellationToken);
            await RaiseAsync(row, "cancellation_original_missing",
                "The cancellation's original document did not arrive within the ordering window.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return Done(row, 0, []);
        }

        return await TranslateDocumentAsync<DocumentCancelled>(row, payload => payload.DocumentRef,
            (envelope, document) => new DocumentCancellationPosted(envelope, document), cancellationToken);
    }

    private async Task<GatewayTranslationResult> TranslateDocumentAsync<TPayload>(InboundMessage row,
        Func<TPayload, ExternalDocumentRef> documentOf, Func<ThinEnvelope, SerialDocumentRef, object> consumed,
        CancellationToken cancellationToken, Func<TPayload, Task<TPayload>>? prepare = null)
    {
        var message = Message<TPayload>(row);
        if (prepare is not null) message = message with { Payload = await prepare(message.Payload) };
        await PreparePayloadReferencesAsync(row, message.Payload!, cancellationToken);
        var document = await ProjectDocumentAsync(row, documentOf(message.Payload), message.Payload!, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var result = await InvokeAsync(consumed(await ThinAsync(row, message.Envelope, cancellationToken), document), cancellationToken);
        return Done(row, result.UnitsAffected, result.Exceptions);
    }

    private async Task<bool> OriginalWasAppliedAsync(Guid connectionId, ExternalDocumentRef document,
        CancellationToken cancellationToken)
    {
        var id = await references.FindAsync(connectionId, "document", DocumentExternalId(document), cancellationToken);
        return id is not null && await db.CanonicalDocuments.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId && x.DocumentId == id
            && x.MessageType != nameof(DocumentCancelled)
            && db.InboundMessages.Any(message => message.TenantId == x.TenantId
                && message.InboundMessageId == x.SourceInboundMessageId
                && message.TranslatedAt != null), cancellationToken);
    }

    private async Task<bool> WasCancelledBeforeAppliedAsync(InboundMessage row, CancellationToken cancellationToken) =>
        DocumentReference(row) is { } document
        && await CancellationWasAppliedAsync(row.ConnectionId, document, cancellationToken);

    private async Task<bool> CancellationWasAppliedAsync(Guid connectionId, ExternalDocumentRef document,
        CancellationToken cancellationToken)
    {
        var id = await references.FindAsync(connectionId, "document", DocumentExternalId(document),
            cancellationToken);
        return id is not null && await db.CanonicalDocuments.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId && x.DocumentId == id
            && x.MessageType == nameof(DocumentCancelled)
            && db.InboundMessages.Any(message => message.TenantId == x.TenantId
                && message.InboundMessageId == x.SourceInboundMessageId
                && message.TranslatedAt != null), cancellationToken);
    }

    private static ExternalDocumentRef? DocumentReference(InboundMessage row) => row.MessageType switch
    {
        nameof(GoodsReceiptPosted) => Message<GoodsReceiptPosted>(row).Payload.DocumentRef,
        nameof(ProductionCompleted) => Message<ProductionCompleted>(row).Payload.DocumentRef,
        nameof(ComponentsIssued) => Message<ComponentsIssued>(row).Payload.DocumentRef,
        nameof(DeliveryPosted) => Message<DeliveryPosted>(row).Payload.DocumentRef,
        nameof(InvoicePosted) => Message<InvoicePosted>(row).Payload.DocumentRef,
        nameof(ReturnReceived) => Message<ReturnReceived>(row).Payload.DocumentRef,
        nameof(TransferPosted) => Message<TransferPosted>(row).Payload.DocumentRef,
        nameof(InventoryAdjusted) => Message<InventoryAdjusted>(row).Payload.DocumentRef,
        nameof(WarrantyLineSold) => Message<WarrantyLineSold>(row).Payload.DocumentRef,
        nameof(DocumentCancelled) => Message<DocumentCancelled>(row).Payload.DocumentRef,
        _ => null,
    };

    private static string DocumentExternalId(ExternalDocumentRef document) => document.ExternalId ?? document.Number;

    private async Task<GoodsReceiptPosted> DeduplicateReceiptAsync(InboundMessage row, GoodsReceiptPosted receipt,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<(string Item, string Serial)>();
        var lines = new List<CanonicalLine>();
        foreach (var line in receipt.Lines)
        {
            var serials = new List<CanonicalSerial>();
            foreach (var serial in line.Serials)
            {
                if (seen.Add((line.ItemExternalId.Trim().ToUpperInvariant(), serial.Value.Trim().ToUpperInvariant())))
                    serials.Add(serial);
                else
                    await RaiseAsync(row, "duplicate_serial", $"Serial '{serial.Value}' occurs more than once for item '{line.ItemExternalId}' in the receipt; review the source document.", cancellationToken);
            }
            lines.Add(line with { Serials = serials });
        }
        return receipt with { Lines = lines };
    }

    private async Task PreparePayloadReferencesAsync<TPayload>(InboundMessage row, TPayload payload, CancellationToken cancellationToken)
    {
        switch (payload)
        {
            case GoodsReceiptPosted x:
                await PrepareReferencesAsync(row, x.Lines, x.VendorPartyExternalId, null, cancellationToken);
                if (x.BaseOrderRef is not null) await EnsureDocumentRefAsync(row.ConnectionId, x.BaseOrderRef, cancellationToken);
                break;
            case ProductionCompleted x:
                await PrepareReferencesAsync(row, x.Lines.Concat(x.Components).Concat(x.ReturnedComponents ?? []).ToList(), null, null, cancellationToken);
                await EnsureDocumentRefAsync(row.ConnectionId, x.ProductionOrderRef, cancellationToken);
                break;
            case ComponentsIssued x:
                await PrepareReferencesAsync(row, x.Lines, null, null, cancellationToken);
                await EnsureDocumentRefAsync(row.ConnectionId, x.ProductionOrderRef, cancellationToken);
                break;
            case InvoicePosted x:
                await PrepareReferencesAsync(row, x.Lines, x.CustomerPartyExternalId, null, cancellationToken);
                foreach (var reference in x.BaseDeliveryRefs) await EnsureDocumentRefAsync(row.ConnectionId, reference, cancellationToken);
                break;
            case ReturnReceived x:
                await PrepareReferencesAsync(row, x.Lines, x.CustomerPartyExternalId, null, cancellationToken);
                if (x.BaseDeliveryRef is not null) await EnsureDocumentRefAsync(row.ConnectionId, x.BaseDeliveryRef, cancellationToken);
                break;
            case TransferPosted x:
                await PrepareReferencesAsync(row, x.Lines, null, x.FromLocationExternalId, cancellationToken);
                await references.ResolveAsync(row.ConnectionId, "location", x.ToLocationExternalId, false, cancellationToken);
                break;
            case InventoryAdjusted x:
                await PrepareReferencesAsync(row, x.Lines, null, null, cancellationToken);
                break;
            case WarrantyLineSold x:
                await PrepareReferencesAsync(row, [x.Line], x.CustomerPartyExternalId, null, cancellationToken);
                await references.ResolveAsync(row.ConnectionId, "item", x.WarrantyProductExternalId, true, cancellationToken);
                if (x.CoveredDeliveryRef is not null) await EnsureDocumentRefAsync(row.ConnectionId, x.CoveredDeliveryRef, cancellationToken);
                break;
            case DocumentCancelled:
                break;
        }
    }

    private async Task PrepareReferencesAsync(InboundMessage row, IReadOnlyList<CanonicalLine> lines,
        string? party, string? location, CancellationToken cancellationToken)
    {
        if (party is not null) await references.ResolveAsync(row.ConnectionId, "party", party, true, cancellationToken);
        if (location is not null) await references.ResolveAsync(row.ConnectionId, "location", location, true, cancellationToken);
        foreach (var line in lines)
        {
            if (await references.FindAsync(row.ConnectionId, "item", line.ItemExternalId, cancellationToken) is null)
            {
                await references.ResolveAsync(row.ConnectionId, "item", line.ItemExternalId, true, cancellationToken);
                await RaiseAsync(row, "item_mapping", $"Item '{line.ItemExternalId}' was provisionally mapped.", cancellationToken);
            }
            if (line.LocationExternalId is not null) await references.ResolveAsync(row.ConnectionId, "location", line.LocationExternalId, true, cancellationToken);
            if (line.PartyExternalId is not null) await references.ResolveAsync(row.ConnectionId, "party", line.PartyExternalId, true, cancellationToken);
            if (line.BaseDocumentRef is not null) await EnsureDocumentRefAsync(row.ConnectionId, line.BaseDocumentRef, cancellationToken);
        }
    }

    private async Task<SerialDocumentRef> ProjectDocumentAsync<TPayload>(InboundMessage row, ExternalDocumentRef external,
        TPayload payload, CancellationToken cancellationToken)
    {
        var id = await EnsureDocumentRefAsync(row.ConnectionId, external, cancellationToken);
        var document = db.CanonicalDocuments.Local.FirstOrDefault(x => x.TenantId == tenant.TenantId && x.DocumentId == id)
            ?? await db.CanonicalDocuments.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.DocumentId == id, cancellationToken);
        if (document is null)
        {
            document = new CanonicalDocument
            {
                TenantId = tenant.TenantId, DocumentId = id, ConnectionId = row.ConnectionId,
                ErpEntity = row.ErpEntity, ErpExternalId = row.ErpExternalId,
                MessageType = row.MessageType, Payload = JsonSerializer.Serialize(payload, IntegrationContracts.Json),
                OccurredAt = row.OccurredAt, UpdatedAt = clock.UtcNow, SourceInboundMessageId = row.InboundMessageId,
            };
            db.CanonicalDocuments.Add(document);
        }
        else
        {
            document.MessageType = row.MessageType;
            document.Payload = JsonSerializer.Serialize(payload, IntegrationContracts.Json);
            document.OccurredAt = row.OccurredAt;
            document.UpdatedAt = clock.UtcNow;
            document.SourceInboundMessageId = row.InboundMessageId;
        }
        return new(external.Type, id, external.Line);
    }

    private Task<Guid> EnsureDocumentRefAsync(Guid connectionId, ExternalDocumentRef reference, CancellationToken cancellationToken) =>
        references.ResolveAsync(connectionId, "document", reference.ExternalId ?? reference.Number, false, cancellationToken);

    private async Task<ThinEnvelope> ThinAsync(InboundMessage row, CanonicalEnvelope envelope, CancellationToken cancellationToken) =>
        new(envelope.MessageId, await LegalEntityAsync(row.ConnectionId, cancellationToken), row.OccurredAt, Source(row),
            envelope.AttributionCompleteness.RawValue ?? envelope.AttributionCompleteness.Value, row.ConnectionId,
            row.ConnectorCode, envelope.CaptureMode.RawValue ?? envelope.CaptureMode.Value);

    private async Task<Guid> LegalEntityAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.ConnectionGatewaySettings.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId)
            .Select(x => x.LegalEntityId).SingleAsync(cancellationToken);

    private async Task<string?> ForeignAmountReasonAsync(InboundMessage row, CancellationToken cancellationToken)
    {
        var home = await currency.GetAsync(cancellationToken);
        if (home is null) return null;
        IEnumerable<string?> supplied = row.MessageType switch
        {
            nameof(GoodsReceiptPosted) => LineCurrencies(Message<GoodsReceiptPosted>(row).Payload.Lines),
            nameof(ProductionCompleted) => LineCurrencies(Message<ProductionCompleted>(row).Payload.Lines
                .Concat(Message<ProductionCompleted>(row).Payload.Components)
                .Concat(Message<ProductionCompleted>(row).Payload.ReturnedComponents ?? [])),
            nameof(ComponentsIssued) => LineCurrencies(Message<ComponentsIssued>(row).Payload.Lines),
            nameof(DeliveryPosted) => LineCurrencies(Message<DeliveryPosted>(row).Payload.Lines),
            nameof(InvoicePosted) => InvoiceCurrencies(Message<InvoicePosted>(row).Payload),
            nameof(ReturnReceived) => LineCurrencies(Message<ReturnReceived>(row).Payload.Lines),
            nameof(TransferPosted) => LineCurrencies(Message<TransferPosted>(row).Payload.Lines),
            nameof(InventoryAdjusted) => LineCurrencies(Message<InventoryAdjusted>(row).Payload.Lines),
            nameof(WarrantyLineSold) => WarrantyCurrencies(Message<WarrantyLineSold>(row).Payload),
            _ => [],
        };
        var foreign = supplied.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToUpperInvariant())
            .FirstOrDefault(value => value != home);
        return foreign is null ? null : $"amount in {foreign}; the ERP's {home} amount is needed";

        static IEnumerable<string?> InvoiceCurrencies(InvoicePosted invoice) =>
            LineCurrencies(invoice.Lines).Append(invoice.Totals.Currency);
        static IEnumerable<string?> LineCurrencies(IEnumerable<CanonicalLine> lines) =>
            lines.Where(line => line.Price is not null).Select(line => line.Currency);
        static IEnumerable<string?> WarrantyCurrencies(WarrantyLineSold warranty) =>
            [warranty.Line.Price is null ? null : warranty.Line.Currency, warranty.Price.Currency];
    }

    private async Task<ConsumedEventResult> InvokeAsync(object message, CancellationToken cancellationToken) =>
        await bus.InvokeAsync<ConsumedEventResult>(message, Options(), cancellationToken);

    private DeliveryOptions Options() => new() { TenantId = tenant.TenantId.ToString() };
    private static string Source(InboundMessage row) => $"integration:{row.ConnectorCode}:{row.ConnectionId:N}";
    private static CanonicalMessage<T> Message<T>(InboundMessage row) =>
        JsonSerializer.Deserialize<CanonicalMessage<T>>(row.CanonicalMessage, IntegrationContracts.Json)
        ?? throw new InvalidOperationException("canonical_message_invalid");
    private static GatewayTranslationResult Done(InboundMessage row, int affected, IReadOnlyList<string> exceptions) =>
        new(row.InboundMessageId, row.MessageType, true, affected, exceptions);
    private async Task RaiseAsync(InboundMessage row, string kind, string detail, CancellationToken cancellationToken)
    {
        if (await db.ExceptionTasks.AnyAsync(x => x.TenantId == tenant.TenantId && x.InboundMessageId == row.InboundMessageId
            && x.Kind == kind && x.Detail == detail, cancellationToken)) return;
        db.ExceptionTasks.Add(new IntegrationExceptionTask
        {
            TenantId = tenant.TenantId, ExceptionTaskId = TimeOrderedId.New(clock.UtcNow), ConnectionId = row.ConnectionId,
            InboundMessageId = row.InboundMessageId, Kind = kind, SubjectRef = $"{row.ErpEntity}:{row.ErpExternalId}",
            Detail = detail, Status = "open", CreatedAt = clock.UtcNow,
        });
    }
}

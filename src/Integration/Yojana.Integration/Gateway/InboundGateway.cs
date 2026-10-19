using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Attributes;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Integration.Ordering;
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
        InboundTranslationRecovery recovery, CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.TranslateAsync(command.InboundMessageId, cancellationToken);
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
    IMessageBus bus, InboundTranslationRecoveryOptions recoveryOptions)
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

        var result = row.MessageType switch
        {
            nameof(ItemUpserted) => await TranslateItemAsync(row, cancellationToken),
            nameof(PartyUpserted) => await TranslatePartyAsync(row, cancellationToken),
            nameof(GoodsReceiptPosted) => await TranslateReceiptAsync(row, cancellationToken),
            nameof(ProductionCompleted) => await TranslateDocumentAsync<ProductionCompleted>(row, p => p.DocumentRef,
                (env, doc) => new ManufacturingProductionReceiptPosted(env, doc), cancellationToken),
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
            nameof(DocumentCancelled) => await TranslateDocumentAsync<DocumentCancelled>(row, p => p.DocumentRef,
                (env, doc) => new DocumentCancellationPosted(env, doc), cancellationToken),
            _ => throw new InvalidOperationException($"canonical_message_unknown: {row.MessageType}"),
        };
        if (result.Translated)
        {
            db.Entry(row).State = EntityState.Detached;
            await db.InboundMessages.Where(x => x.TenantId == tenant.TenantId
                    && x.InboundMessageId == inboundMessageId && x.IsLatest
                    && x.TranslationStatus == InboundTranslationStatus.Processing)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.TranslationStatus, InboundTranslationStatus.Completed)
                    .SetProperty(x => x.TranslatedAt, clock.UtcNow)
                    .SetProperty(x => x.TranslationStartedAt, (DateTime?)null), cancellationToken);
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

    private async Task<GatewayTranslationResult> TranslateItemAsync(InboundMessage row, CancellationToken cancellationToken)
    {
        var message = Message<ItemUpserted>(row);
        await references.UpsertItemAsync(row.ConnectionId, message.Payload, cancellationToken);
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
            (env, doc) => new PurchasingReceiptPosted(env, doc), cancellationToken);
        await resequencer.ReleaseSatisfiedAsync(row.ConnectionId,
            message.Payload.Lines.SelectMany(x => x.Serials).Select(x => x.Value).ToList(), cancellationToken);
        return result;
    }

    private async Task<GatewayTranslationResult> TranslateDocumentAsync<TPayload>(InboundMessage row,
        Func<TPayload, ExternalDocumentRef> documentOf, Func<ThinEnvelope, SerialDocumentRef, object> consumed,
        CancellationToken cancellationToken)
    {
        var message = Message<TPayload>(row);
        await PreparePayloadReferencesAsync(row, message.Payload!, cancellationToken);
        var document = await ProjectDocumentAsync(row, documentOf(message.Payload), message.Payload!, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var result = await InvokeAsync(consumed(await ThinAsync(row, message.Envelope, cancellationToken), document), cancellationToken);
        return Done(row, result.UnitsAffected, result.Exceptions);
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
                await PrepareReferencesAsync(row, x.Lines.Concat(x.Components).ToList(), null, null, cancellationToken);
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

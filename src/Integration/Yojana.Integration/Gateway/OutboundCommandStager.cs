using Yojana.Integration.Connections;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Integration.Outbox;

namespace Yojana.Integration.Gateway;

/// <summary>Stages canonical commands only. The relay owns delivery.</summary>
public sealed class OutboundCommandStager(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    ConnectionCapabilityReader declarations, IOutboundCommandParkingSink parking, IOutboundUnitSourcePort sources, IRenderedOutboundDocumentPort documents, IEnumerable<IOutboundSourcePolicy> sourcePolicies, ILogger<OutboundCommandStager> logger)
{
    public async Task<int> StageAsync(PublishedEventHeader header, IOutboundCommand command,
        CancellationToken cancellationToken, Guid? sourceUnitId = null, Guid? sourceLocationId = null)
    {
        var type = command.GetType().Name;
        var connections = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.Status == ConnectionStatus.Active).Select(x => x.ConnectionId).ToListAsync(cancellationToken);
        var count = 0;
        foreach (var originalConnection in connections)
        {
            var configured = await db.ConnectionGatewaySettings.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == tenant.TenantId && x.ConnectionId == originalConnection, cancellationToken);
            if (configured?.ReadOnly == true || configured?.OutboundMode == "none") continue;
            var declaration = await declarations.ReadAsync(originalConnection, cancellationToken);
            var capability = declaration.For(type);
            var connection = IsFinancial(type) && declaration.DegradationRules.GetValueOrDefault(DegradationRule.DownstreamLedger)?.Enabled == true
                ? declaration.LedgerConnectionId ?? originalConnection : originalConnection;
            await using var activity = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, connection, cancellationToken);
            var connectorCode = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connection && x.Status == ConnectionStatus.Active).Select(x => x.ConnectorCode).SingleOrDefaultAsync(cancellationToken);
            if (connectorCode is null) continue;
            var targetSettings = await db.ConnectionGatewaySettings.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == tenant.TenantId && x.ConnectionId == connection, cancellationToken);
            if (targetSettings?.ReadOnly == true || targetSettings?.OutboundMode == "none") continue;
            var idempotency = Derive(header.EventId, type);
            if (await db.OutboundCommands.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connection
                && x.IdempotencyKey == idempotency, cancellationToken)) continue;
            var manual = capability.SupportLevel is CapabilitySupport.Manual or CapabilitySupport.Unsupported;
            var payload = await EnrichAsync(command, header with { UnitId = sourceUnitId ?? header.UnitId }, connection, connectorCode, targetSettings?.OutboundMode ?? "reference_only", sourceLocationId, cancellationToken);
            var row = new OutboundCommandRow
            {
                TenantId = tenant.TenantId, OutboundCommandId = TimeOrderedId.New(clock.UtcNow), ConnectionId = connection,
                CommandType = type, IdempotencyKey = idempotency, ModuleDocumentRef = command.ModuleDocumentRef,
                ErpReferenceText = command.ErpReferenceText,
                Payload = JsonSerializer.Serialize(payload, payload.GetType(), IntegrationContracts.Json),
                Status = manual ? OutboundCommandStatus.Parked : OutboundCommandStatus.Pending,
                Outcome = manual ? OutboundConnectorOutcome.Unsupported : null,
                LastFailure = manual ? "The connection capability requires manual processing for this command." : null,
                CompletedAt = manual ? clock.UtcNow : null,
                CreatedAt = clock.UtcNow,
            };
            db.OutboundCommands.Add(row);
            if (manual)
                await parking.ParkAsync(row, OutboundConnectorOutcome.Unsupported, row.LastFailure!, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            count++;
        }
        return count;
    }

    private async Task<IOutboundCommand> EnrichAsync(IOutboundCommand command, PublishedEventHeader header, Guid connectionId, string connectorCode, string mode, Guid? sourceLocationId, CancellationToken ct)
    {
        if (command is AttachDocument attachment)
        {
            if (mode == "documents" && OutboundDocumentContentReference.TryDocumentId(attachment.ContentRef, out var documentId)
                && await documents.DescribeAsync(documentId, ct) is { } rendered)
                return attachment with { DisplayName = rendered.FileName };
            return attachment;
        }
        if (command is PostWarrantyExpense) return command;
        var unitId = command switch
        {
            ChangeStockStatusOrLocation stock => stock.Unit.UnitId,
            CreateServiceOrder service => service.Unit.UnitId,
            _ => header.UnitId,
        };
        if (command is CreateReplacementDelivery delivery && delivery.CustomerRef.StartsWith("unit-owner/", StringComparison.Ordinal)
            && Guid.TryParse(delivery.CustomerRef[11..], out var original)) unitId = original;
        Guid? requestedCustomer = command is CreateReplacementDelivery named && Guid.TryParse(named.CustomerRef, out var party) ? party : null;
        var source = await sources.ReadAsync(unitId, header.Document, ct, requestedCustomer, allowOwnershipFallback: command is not CreateCreditMemo, custodyAt: header.EventTime);
        async Task<string?> External(string kind, Guid? id)
        {
            if (id is null) return null;
            var matches = await db.ExternalIdMaps.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && x.ConnectionId == connectionId && x.EntityType == kind && x.ModuleId == id && x.ValidTo == null)
                .Select(x => x.ExternalId).Take(2).ToListAsync(ct);
            return matches.Count == 1 ? matches[0] : null;
        }
        async Task<string> Customer(string reference) => string.IsNullOrWhiteSpace(reference)
            || reference.StartsWith("unit-owner/", StringComparison.Ordinal)
                ? await External("party", source.CustomerPartyRef) ?? "" : reference;
        switch (command)
        {
            case CreateReplacementDelivery value:
                var serial = await sources.ReadAsync(value.ReplacementUnit.UnitId, null, ct);
                var policy = sourcePolicies.SingleOrDefault(x => x.ConnectorCode == connectorCode);
                var useRecordedShipTo = mode == "documents" && source.ShipToLocationId is not null && string.IsNullOrWhiteSpace(value.ShipToRef);
                if (useRecordedShipTo && policy is not null)
                    useRecordedShipTo = await policy.UseRecordedReplacementShipToAsync(connectionId, ct);
                return value with { CustomerRef = await Customer(value.CustomerRef),
                    // Inbound deliveries map the ERP ship-to address as a "location"; the customer-site custody episode records it.
                    ShipToRef = string.IsNullOrWhiteSpace(value.ShipToRef) && useRecordedShipTo ? await External("location", source.ShipToLocationId) ?? "" : value.ShipToRef,
                    ReplacementUnit = value.ReplacementUnit with { IdentifierValue = serial.Serial ?? value.ReplacementUnit.IdentifierValue } };
            case CreateCreditMemo value:
                return value with { CustomerRef = await Customer(value.CustomerRef) };
            case CreateServiceOrder value:
                return value with { CustomerRef = await Customer(value.CustomerRef), Unit = value.Unit with { IdentifierValue = source.Serial ?? value.Unit.IdentifierValue } };
            case ChangeStockStatusOrLocation value:
                return value with { Unit = value.Unit with { IdentifierValue = source.Serial ?? value.Unit.IdentifierValue },
                    FromLocationOrStatus = value.FromLocationOrStatus ?? await External("location", sourceLocationId ?? source.LocationId) };
            default: return command;
        }
    }

    public int RecordExpenseSkip(PublishedEventHeader header)
    {
        logger.LogInformation("Outbound command {CommandType} skipped for event {EventId} ({Schema}): {ReasonCode}",
            nameof(PostWarrantyExpense), header.EventId, header.Schema, "no_posting_amounts");
        return 0;
    }

    private static bool IsFinancial(string type) => type is nameof(CreateCreditMemo) or nameof(PostWarrantyExpense);

    public static Guid Derive(Guid eventId, string purpose) => DeterministicId.Create(eventId, purpose);

    public static string ModuleRef(PublishedEventHeader header, string kind, Guid id) =>
        header.Document is { } document ? $"{document.Type}/{document.Id:N}" : $"{kind}/{id:N}";

    public static string ErpReference(PublishedEventHeader header, string suffix) =>
        $"YOJ-{header.EventId:N}-{suffix}";
}

/// <summary>Wolverine consumers for the ADR-0001 published surface. They depend only on Serialisation.Contracts.</summary>
public static class PublishedEventOutboundHandler
{
    public static Task<int> Handle(ReplacementShipped message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new CreateReplacementDelivery(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(CreateReplacementDelivery)),
            OutboundCommandStager.ModuleRef(message.Header, "replacement", message.WarrantyReplacementId),
            OutboundCommandStager.ErpReference(message.Header, "replacement"), message.CustomerPartyRef?.ToString("N") ?? "unit-owner/" + message.OriginalUnitId.ToString("N"), "",
            new(message.ReplacementUnitId, ""), OpenEnum.Known(message.PricePolicy),
            message.SourceNumber, message.Reason, Price: message.PricePolicy == "zero" ? null : new(message.DifferenceAmount, message.Currency)), ct, message.OriginalUnitId);

    public static async Task<int> Handle(RmaDispositioned message, OutboundCommandStager stage, CancellationToken ct)
    {
        var count = 0;
        if (message.Disposition == "credit")
            count += await stage.StageAsync(message.Header, new CreateCreditMemo(
                OutboundCommandStager.Derive(message.Header.EventId, nameof(CreateCreditMemo)),
                OutboundCommandStager.ModuleRef(message.Header, "rma", message.RmaId),
                OutboundCommandStager.ErpReference(message.Header, "credit"), message.CustomerPartyRef?.ToString("N") ?? "",
                [new(message.UnitId, "RMA disposition", 1, new(0, message.Currency ?? ""))], null, null), ct);
        count += await stage.StageAsync(message.Header, new ChangeStockStatusOrLocation(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(ChangeStockStatusOrLocation)),
            OutboundCommandStager.ModuleRef(message.Header, "rma", message.RmaId),
            OutboundCommandStager.ErpReference(message.Header, "stock"), new(message.UnitId, ""),
            null, message.Disposition, "RMA disposition"), ct, sourceLocationId: message.SourceLocationId);
        return count;
    }

    public static Task<int> Handle(ReplacementReturnOverdue message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new CreateCreditMemo(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(CreateCreditMemo)),
            OutboundCommandStager.ModuleRef(message.Header, "replacement", message.ReplacementLinkId),
            OutboundCommandStager.ErpReference(message.Header, "overdue"), message.PartyRef.ToString("N"),
            [new(message.OriginalUnitId, "Replacement not returned", 1, new(message.NonReturnAmount ?? 0, message.Currency ?? ""))], null, null), ct);

    public static Task<int> Handle(ClaimAdjudicated message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new PostWarrantyExpense(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(PostWarrantyExpense)),
            OutboundCommandStager.ModuleRef(message.Header, "claim", message.ClaimId),
            OutboundCommandStager.ErpReference(message.Header, "expense"),
            // Logical account roles are resolved by each connection's account map; no GL code is chosen here.
            message.ApprovedAmountsByLine.Where(x => x.ApprovedAmount > 0)
                .SelectMany(x => new[] { new JournalLine(message.CoverageSource, x.ApprovedAmount, 0, message.Currency ?? ""),
                    new JournalLine(message.CoverageSource + ".offset", 0, x.ApprovedAmount, message.Currency ?? "") }).ToList(),
            message.ReserveTag, false), ct);

    // These facts carry valuation/accrual instructions, not posting amounts. Finance owns their calculation;
    // the gateway must not turn them into empty journals or guess their value.
    public static Task<int> Handle(EntitlementCreated message, OutboundCommandStager stage, CancellationToken ct) => Task.FromResult(stage.RecordExpenseSkip(message.Header));

    public static Task<int> Handle(UnitGraded message, OutboundCommandStager stage, CancellationToken ct)
    {
        stage.RecordExpenseSkip(message.Header);
        return stage.StageAsync(message.Header, new ChangeStockStatusOrLocation(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(ChangeStockStatusOrLocation)),
            OutboundCommandStager.ModuleRef(message.Header, "unit", message.Header.UnitId),
            OutboundCommandStager.ErpReference(message.Header, "grade-stock"),
            new(message.Header.UnitId, ""), null, message.Grade, "condition grade"), ct);
    }

    public static Task<int> Handle(RmaReceived message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new ChangeStockStatusOrLocation(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(ChangeStockStatusOrLocation)),
            OutboundCommandStager.ModuleRef(message.Header, "rma", message.RmaId),
            OutboundCommandStager.ErpReference(message.Header, "received"),
            new(message.ReceivedUnitId ?? message.AuthorisedUnitId, ""),
            null, "quarantine", "RMA received"), ct, sourceLocationId: message.SourceLocationId);

    public static Task<int> Handle(RefurbishmentReleased message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new ChangeStockStatusOrLocation(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(ChangeStockStatusOrLocation)),
            OutboundCommandStager.ModuleRef(message.Header, "refurbishment", message.WorkOrderId),
            OutboundCommandStager.ErpReference(message.Header, "release"), new(message.ReleasedUnitId, ""),
            null, message.Grade, "refurbishment released"), ct, sourceLocationId: message.SourceLocationId);

    public static Task<int> Handle(CertificateIssued message, OutboundCommandStager stage, CancellationToken ct) =>
        Attach(message.Header, message.CertificateId, message.TrackingNumber, stage, ct);

    public static Task<int> Handle(CertificateSuperseded message, OutboundCommandStager stage, CancellationToken ct) =>
        Attach(message.Header, message.SupersedingCertificateId, $"certificate-v{message.CertificateVersion}", stage, ct);

    public static async Task<int> Handle(RmaAuthorised message, OutboundCommandStager stage, CancellationToken ct)
    {
        var attached = await Attach(message.Header, message.RmaId, message.RmaNumber, stage, ct);
        var service = await stage.StageAsync(message.Header, new CreateServiceOrder(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(CreateServiceOrder)),
            OutboundCommandStager.ModuleRef(message.Header, "rma", message.RmaId),
            OutboundCommandStager.ErpReference(message.Header, "service"), message.CustomerPartyRef?.ToString("N") ?? "",
            new(message.AuthorisedUnitId, ""), message.CoverageSource, "RMA authorised"), ct);
        return attached + service;
    }

    private static Task<int> Attach(PublishedEventHeader header, Guid id, string name,
        OutboundCommandStager stage, CancellationToken ct) => stage.StageAsync(header, new AttachDocument(
            OutboundCommandStager.Derive(header.EventId, nameof(AttachDocument)),
            OutboundCommandStager.ModuleRef(header, "document", id), OutboundCommandStager.ErpReference(header, "attachment"),
            header.UnitId.ToString("N"), name, OutboundDocumentContentReference.ForDocument(id), $"/api/documents/{id:N}"), ct);
}

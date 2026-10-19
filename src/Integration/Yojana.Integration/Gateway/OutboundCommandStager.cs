using Yojana.Integration.Connections;
using System.Text.Json;
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

/// <summary>Stages canonical commands only. Delivery is owned by P8-T07's later relay.</summary>
public sealed class OutboundCommandStager(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    ConnectionCapabilityReader declarations, IOutboundCommandParkingSink parking)
{
    public async Task<int> StageAsync(PublishedEventHeader header, IOutboundCommand command,
        CancellationToken cancellationToken)
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
            if (!await db.Connections.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connection && x.Status == ConnectionStatus.Active, cancellationToken)) continue;
            var targetSettings = await db.ConnectionGatewaySettings.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == tenant.TenantId && x.ConnectionId == connection, cancellationToken);
            if (targetSettings?.ReadOnly == true || targetSettings?.OutboundMode == "none") continue;
            var idempotency = Derive(header.EventId, type);
            if (await db.OutboundCommands.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connection
                && x.IdempotencyKey == idempotency, cancellationToken)) continue;
            var manual = capability.SupportLevel is CapabilitySupport.Manual or CapabilitySupport.Unsupported;
            var row = new OutboundCommandRow
            {
                TenantId = tenant.TenantId, OutboundCommandId = TimeOrderedId.New(clock.UtcNow), ConnectionId = connection,
                CommandType = type, IdempotencyKey = idempotency, ModuleDocumentRef = command.ModuleDocumentRef,
                ErpReferenceText = command.ErpReferenceText,
                Payload = JsonSerializer.Serialize(command, command.GetType(), IntegrationContracts.Json),
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
            OutboundCommandStager.ErpReference(message.Header, "replacement"), message.Header.UnitId.ToString("N"), "default",
            new(message.ReplacementUnitId, message.ReplacementNumber), OpenEnum.Known(message.PricePolicy),
            message.SourceNumber, message.Reason), ct);

    public static async Task<int> Handle(RmaDispositioned message, OutboundCommandStager stage, CancellationToken ct)
    {
        var count = 0;
        if (message.Disposition == "credit")
            count += await stage.StageAsync(message.Header, new CreateCreditMemo(
                OutboundCommandStager.Derive(message.Header.EventId, nameof(CreateCreditMemo)),
                OutboundCommandStager.ModuleRef(message.Header, "rma", message.RmaId),
                OutboundCommandStager.ErpReference(message.Header, "credit"), "owner",
                [new(message.UnitId, "RMA disposition", 1, new(0, "XXX"))], null, null), ct);
        count += await stage.StageAsync(message.Header, new ChangeStockStatusOrLocation(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(ChangeStockStatusOrLocation)),
            OutboundCommandStager.ModuleRef(message.Header, "rma", message.RmaId),
            OutboundCommandStager.ErpReference(message.Header, "stock"), new(message.UnitId, message.UnitId.ToString("N")),
            null, message.Disposition, "RMA disposition"), ct);
        return count;
    }

    public static Task<int> Handle(ReplacementReturnOverdue message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new CreateCreditMemo(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(CreateCreditMemo)),
            OutboundCommandStager.ModuleRef(message.Header, "replacement", message.ReplacementLinkId),
            OutboundCommandStager.ErpReference(message.Header, "overdue"), message.PartyRef.ToString("N"),
            [new(message.OriginalUnitId, "Replacement not returned", 1, new(0, "XXX"))], null, null), ct);

    public static Task<int> Handle(ClaimAdjudicated message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new PostWarrantyExpense(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(PostWarrantyExpense)),
            OutboundCommandStager.ModuleRef(message.Header, "claim", message.ClaimId),
            OutboundCommandStager.ErpReference(message.Header, "expense"),
            message.ApprovedAmountsByLine.Select(x => new JournalLine("warranty", x.ApprovedAmount, 0, "XXX")).ToList(),
            message.ReserveTag, false), ct);

    public static Task<int> Handle(EntitlementCreated message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new PostWarrantyExpense(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(PostWarrantyExpense)),
            OutboundCommandStager.ModuleRef(message.Header, "entitlement", message.EntitlementId),
            OutboundCommandStager.ErpReference(message.Header, "reserve"), [], message.AccrualCohortId?.ToString("N"),
            message.AccrualSuppressed, ReserveDrawdown: true), ct);

    public static async Task<int> Handle(UnitGraded message, OutboundCommandStager stage, CancellationToken ct)
    {
        var expense = await stage.StageAsync(message.Header, new PostWarrantyExpense(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(PostWarrantyExpense)),
            OutboundCommandStager.ModuleRef(message.Header, "unit", message.Header.UnitId),
            OutboundCommandStager.ErpReference(message.Header, "grade-expense"), [], null, false), ct);
        var stock = await stage.StageAsync(message.Header, new ChangeStockStatusOrLocation(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(ChangeStockStatusOrLocation)),
            OutboundCommandStager.ModuleRef(message.Header, "unit", message.Header.UnitId),
            OutboundCommandStager.ErpReference(message.Header, "grade-stock"),
            new(message.Header.UnitId, message.Header.UnitId.ToString("N")), null, message.Grade, "condition grade"), ct);
        return expense + stock;
    }

    public static Task<int> Handle(RmaReceived message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new ChangeStockStatusOrLocation(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(ChangeStockStatusOrLocation)),
            OutboundCommandStager.ModuleRef(message.Header, "rma", message.RmaId),
            OutboundCommandStager.ErpReference(message.Header, "received"),
            new(message.ReceivedUnitId ?? message.AuthorisedUnitId, (message.ReceivedUnitId ?? message.AuthorisedUnitId).ToString("N")),
            null, "quarantine", "RMA received"), ct);

    public static Task<int> Handle(RefurbishmentReleased message, OutboundCommandStager stage, CancellationToken ct) =>
        stage.StageAsync(message.Header, new ChangeStockStatusOrLocation(
            OutboundCommandStager.Derive(message.Header.EventId, nameof(ChangeStockStatusOrLocation)),
            OutboundCommandStager.ModuleRef(message.Header, "refurbishment", message.WorkOrderId),
            OutboundCommandStager.ErpReference(message.Header, "release"), new(message.ReleasedUnitId, message.ReleasedUnitId.ToString("N")),
            null, message.Grade, "refurbishment released"), ct);

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
            OutboundCommandStager.ErpReference(message.Header, "service"), message.Header.UnitId.ToString("N"),
            new(message.AuthorisedUnitId, message.AuthorisedUnitId.ToString("N")), message.CoverageSource, "RMA authorised"), ct);
        return attached + service;
    }

    private static Task<int> Attach(PublishedEventHeader header, Guid id, string name,
        OutboundCommandStager stage, CancellationToken ct) => stage.StageAsync(header, new AttachDocument(
            OutboundCommandStager.Derive(header.EventId, nameof(AttachDocument)),
            OutboundCommandStager.ModuleRef(header, "document", id), OutboundCommandStager.ErpReference(header, "attachment"),
            header.UnitId.ToString("N"), name, null, $"/api/documents/{id:N}"), ct);
}

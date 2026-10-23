using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Integration.Contracts;

namespace Yojana.Integration.Outbox;

/// <summary>Persists each remote side effect immediately, before the next independent remote call.</summary>
public interface IOutboundProgressStore
{
    Task RecordAsync(Guid connectionId, Guid outboundCommandId, OutboundDocument document, CancellationToken ct, bool mapDocument = true);
    Task CompleteAsync(Guid connectionId, Guid outboundCommandId, OutboundDocument document, string outcome, CancellationToken ct);
    Task<OutboundDocument?> ReadAsync(Guid connectionId, string externalId, CancellationToken ct);
}

internal sealed class OutboundProgressStore(IntegrationDbContext db, ITenantContext tenant, IClock clock) : IOutboundProgressStore
{
    public async Task RecordAsync(Guid connectionId, Guid outboundCommandId, OutboundDocument document, CancellationToken ct, bool mapDocument = true)
    {
        await ApplyAsync(connectionId, outboundCommandId, document, mapDocument, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteAsync(Guid connectionId, Guid outboundCommandId, OutboundDocument document, string outcome, CancellationToken ct)
    {
        var existing = await db.OutboundCommands.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.OutboundCommandId == outboundCommandId)
            .Select(x => x.CommandType).SingleOrDefaultAsync(ct);
        var command = await ApplyAsync(connectionId, outboundCommandId, document,
                existing != nameof(PushNativeWarrantyObject), ct)
            ?? throw new InvalidOperationException("The outbound command was not found.");
        command.Status = OutboundCommandStatus.Done;
        command.ErpExternalId = document.ErpExternalId;
        command.ErpChangeMarker = OutboundAcknowledgement.Marker(document);
        command.Outcome = outcome;
        command.LastFailure = null;
        command.NextAttemptAt = null;
        command.LockedUntil = null;
        command.CompletedAt = clock.UtcNow;
        // The final reply, per-document receipts and identity maps share one SQL commit.
        await db.SaveChangesAsync(ct);
    }

    private async Task<OutboundCommandRow?> ApplyAsync(Guid connectionId, Guid outboundCommandId, OutboundDocument document, bool mapDocument, CancellationToken ct)
    {
        var command = await db.OutboundCommands.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.OutboundCommandId == outboundCommandId, ct);
        if (command is null) return null; // Direct adapter probes have no gateway command to acknowledge.
        var moduleId = OutboundRelay.ModuleId(command);
        foreach (var part in new[] { document }.Concat(document.RelatedDocuments ?? []))
        {
            ExternalIdMap? map = null;
            if (mapDocument)
            {
                map = await db.ExternalIdMaps.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.EntityType == "document" && x.ExternalId == part.ErpExternalId && x.ValidTo == null, ct);
                if (map is not null && map.ModuleId != moduleId)
                    throw new OutboundMappingConflictException(part.ErpExternalId);
            }
            var receipt = await db.Set<OutboundDocumentReceipt>().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.ExternalId == part.ErpExternalId, ct);
            if (receipt is null)
                db.Add(new OutboundDocumentReceipt { TenantId = tenant.TenantId, ConnectionId = connectionId,
                    ExternalId = part.ErpExternalId, OutboundCommandId = command.OutboundCommandId, ChangeMarker = part.ErpChangeMarker });
            // Existing document receipts retain their original marker; only an acknowledged reference patch replaces it.
            else if (!mapDocument) receipt.ChangeMarker = part.ErpChangeMarker;
            if (!mapDocument) continue;
            if (map is null)
                db.ExternalIdMaps.Add(new ExternalIdMap { TenantId = tenant.TenantId, ExternalIdMapId = Guid.CreateVersion7(),
                    ConnectionId = connectionId, EntityType = "document", ExternalId = part.ErpExternalId, ModuleId = moduleId,
                    ValidFrom = clock.UtcNow, NaturalKeyHash = OutboundRelay.NaturalKeyHash(command.ModuleDocumentRef),
                    MappingSource = ExternalIdMappingSource.Outbound, MappedBy = $"system:connection:{connectionId:N}", MappedAt = clock.UtcNow });
        }
        return command;
    }

    public async Task<OutboundDocument?> ReadAsync(Guid connectionId, string externalId, CancellationToken ct)
    {
        var row = await db.Set<OutboundDocumentReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.ExternalId == externalId, ct);
        return row is null ? null : new(row.ExternalId, row.ChangeMarker);
    }
}

internal sealed class OutboundMappingConflictException(string externalId)
    : Exception($"External document '{externalId}' is already mapped to another module record.");

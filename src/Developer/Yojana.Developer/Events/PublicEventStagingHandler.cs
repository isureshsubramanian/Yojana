using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Attributes;
using Yojana.Developer.Contracts;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.Events;

/// <summary>Wolverine entry points for every internal fact in public catalogue v1.</summary>
[NonTransactional]
public static class PublicEventStagingHandler
{
    public static Task Handle(UnitCommissioned x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(CustodyChanged x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(OwnershipOpened x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(UnitIdentifierCorrected x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(UnitVoided x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(EntitlementCreated x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(EntitlementAmended x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(EntitlementTransferred x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(EntitlementClosed x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(ClaimSubmitted x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(ClaimAdjudicated x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(RecoveryClaimRaised x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(RecoveryClaimApproved x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(RmaAuthorised x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(RmaReceived x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(RmaDispositioned x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(ReplacementShipped x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(ReplacementReturned x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(RefurbishmentReleased x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(CertificateIssued x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(CertificateSuperseded x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(ImportCompleted x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(CampaignStarted x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(CampaignEnded x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
    public static Task Handle(BulkOperationCompleted x, PublicEventStager s, CancellationToken ct) => s.StageAsync(x, ct);
}

public sealed class PublicEventStager(DeveloperDbContext db, PublicEventTranslator translator,
    ITenantContext tenant, IClock clock)
{
    public async Task StageAsync(object message, CancellationToken cancellationToken)
    {
        var translated = await translator.TranslateAsync(message, cancellationToken);
        if (translated is null) return;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (translated.BulkOperationId is { } operationId)
                await StoreBulkAsync(translated, operationId, cancellationToken);

            if (translated.Envelope is { } envelope)
            {
                if (await db.PublicEvents.AnyAsync(row => row.TenantId == tenant.TenantId
                    && row.EventId == envelope.Id, cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return;
                }
                db.PublicEvents.Add(new PublicEvent
                {
                    TenantId = tenant.TenantId, EventId = envelope.Id, Type = envelope.Type,
                    Version = envelope.Version, RecordKind = envelope.Record.Kind,
                    RecordId = envelope.Record.Id, Sequence = envelope.Sequence,
                    OccurredAt = envelope.OccurredAt, RecordedAt = envelope.RecordedAt,
                    Body = JsonSerializer.Serialize(envelope, PublicEventTranslator.Json),
                    Test = envelope.Test, ItemFamily = translated.ItemFamily,
                    CustomerPartyRef = translated.CustomerPartyRef,
                    LegalEntityId = translated.LegalEntityId,
                });
                var subscriptions = await db.Subscriptions.Where(row => row.TenantId == tenant.TenantId
                        && (row.Status == WebhookSubscriptionStatus.Active
                            || row.Status == WebhookSubscriptionStatus.Paused))
                    .ToListAsync(cancellationToken);
                foreach (var subscription in subscriptions.Where(row => Matches(row, translated)))
                    db.Deliveries.Add(new WebhookDelivery
                    {
                        TenantId = tenant.TenantId, DeliveryId = Guid.CreateVersion7(clock.UtcNow),
                        SubscriptionId = subscription.SubscriptionId, EventId = envelope.Id,
                        Status = WebhookDeliveryStatus.Pending, CreatedAt = clock.UtcNow,
                        NextAttemptAt = clock.UtcNow,
                    });
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    private async Task StoreBulkAsync(PublicEventTranslation translated, Guid operationId,
        CancellationToken cancellationToken)
    {
        if (translated.Envelope is { } envelope && !await db.BulkOperations.AnyAsync(row =>
                row.TenantId == tenant.TenantId && row.OperationId == operationId, cancellationToken))
            db.BulkOperations.Add(new BulkOperation
            {
                TenantId = tenant.TenantId, OperationId = operationId,
                Kind = translated.BulkOperationKind ?? "bulk", UnitCount = translated.BulkOperationSize ?? 0,
                CompletedAt = envelope.OccurredAt, EventId = envelope.Id,
            });
        var ids = translated.BulkUnitIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0) return;
        var json = JsonSerializer.Serialize(ids, PublicEventTranslator.Json);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO developer.bulk_operation_unit (tenant_id, operation_id, unit_id)
            SELECT {tenant.TenantId.Value}, {operationId}, parsed.unit_id
            FROM OPENJSON({json}) WITH (unit_id uniqueidentifier '$') AS parsed
            WHERE NOT EXISTS (
                SELECT 1 FROM developer.bulk_operation_unit AS existing
                WHERE existing.tenant_id = {tenant.TenantId.Value}
                  AND existing.operation_id = {operationId}
                  AND existing.unit_id = parsed.unit_id)
            """, cancellationToken);
    }

    private static bool Matches(WebhookSubscription subscription, PublicEventTranslation translated)
    {
        var types = JsonSerializer.Deserialize<List<string>>(subscription.EventTypes,
            PublicEventTranslator.Json) ?? [];
        if (!types.Contains(translated.Envelope!.Type, StringComparer.Ordinal)) return false;
        var filters = JsonSerializer.Deserialize<WebhookFilters>(subscription.Filters,
            PublicEventTranslator.Json) ?? new();
        return Matches(filters.ItemFamilies, translated.ItemFamily)
            && Matches(filters.CustomerPartyRefs, translated.CustomerPartyRef)
            && Matches(filters.LegalEntityIds, translated.LegalEntityId);
    }

    private static bool Matches(IReadOnlyList<string>? values, string? value) =>
        values is not { Count: > 0 } || value is { } actual && values.Contains(actual, StringComparer.Ordinal);

    private static bool Matches(IReadOnlyList<Guid>? values, Guid? value) =>
        values is not { Count: > 0 } || value is { } actual && values.Contains(actual);
}


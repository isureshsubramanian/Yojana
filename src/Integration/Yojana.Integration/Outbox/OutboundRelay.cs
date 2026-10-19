using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Outbox;

public sealed record OutboundRelayResult(int Completed, int Retrying, int Parked);

/// <summary>Relays one connection at a time. The connector never sees persistence or another connector's lane.</summary>
public sealed class OutboundRelay(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    OutboxRelaySettings settings,
    IOutboundConnectorResolver connectors,
    IConnectionWorkerIsolation isolation,
    IConnectionRetryJitter jitter,
    IOutboundCommandParkingSink parking)
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryCeiling = TimeSpan.FromHours(6);

    public async Task<OutboundRelayResult> RelayConnectionAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connection = await db.Connections.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.Status == ConnectionStatus.Active, cancellationToken);
        var connector = connectors.Resolve(connection.ConnectorCode);
        // A connector whose HTTP pipeline owns the shared rate-limited lane must not be wrapped in the same
        // non-reentrant lane here. Connectors without that pipeline retain the relay's connection-wide serialisation.
        await using IConnectionWorkerLease? lane = connector.Capabilities.OwnsConnectionLane
            ? null : await isolation.AcquireAsync(new(tenant.TenantId, connectionId), cancellationToken);
        var maxAge = await settings.MaxAgeAsync(connectionId, cancellationToken);
        var completed = 0;
        var retrying = 0;
        var parked = 0;

        for (var processed = 0; processed < 100; processed++)
        {
            var command = await ClaimNextAsync(connectionId, cancellationToken);
            if (command is null) break;
            if (command.CreatedAt + maxAge <= clock.UtcNow)
            {
                await ParkAsync(command, "max_outbox_age", "The command exceeded its configured maximum outbox age.",
                    OutboundConnectorOutcome.Expired, cancellationToken);
                parked++;
                continue;
            }

            OutboundDocument? found;
            OutboundCreateResult? created = null;
            try
            {
                // Query-before-create is unconditional. An ERP idempotency key is an additional guard, not a substitute.
                found = await connector.FindByReferenceAsync(new(connectionId, command.CommandType,
                    command.ErpReferenceText), cancellationToken);
                if (found is null)
                {
                    created = await connector.CreateAsync(new(connectionId, command.OutboundCommandId,
                        command.CommandType, command.ErpReferenceText, command.Payload,
                        connector.Capabilities.AcceptsIdempotencyKey ? command.IdempotencyKey : null), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await RetryAsync(command, Failure(ex), cancellationToken);
                retrying++;
                continue;
            }

            try
            {
                if (found is not null)
                {
                    await CompleteAsync(command, found, OutboundConnectorOutcome.AlreadyExisted, cancellationToken);
                    completed++;
                }
                else if (created?.Outcome == OutboundConnectorOutcome.Created && created.Document is not null)
                {
                    await CompleteAsync(command, created.Document, created.Outcome, cancellationToken);
                    completed++;
                }
                else if (created?.Outcome is OutboundConnectorOutcome.Rejected or OutboundConnectorOutcome.Unsupported)
                {
                    await ParkAsync(command, created.Outcome, RequiredReason(created), created.Outcome, cancellationToken);
                    parked++;
                }
                else
                {
                    await RetryAsync(command, created?.Reason ?? "The connector returned no document.", cancellationToken);
                    retrying++;
                }
            }
            catch (ExternalMappingConflictException ex)
            {
                await ParkAsync(command, "external_mapping_conflict", ex.Message,
                    OutboundConnectorOutcome.Rejected, cancellationToken);
                parked++;
            }
        }

        return new(completed, retrying, parked);
    }

    private async Task<OutboundCommandRow?> ClaimNextAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = clock.UtcNow;
            var row = await db.OutboundCommands.Where(x => x.TenantId == tenant.TenantId
                    && x.ConnectionId == connectionId
                    && (x.Status == OutboundCommandStatus.Pending || x.Status == OutboundCommandStatus.Retry
                        || (x.Status == OutboundCommandStatus.Processing && x.LockedUntil <= now))
                    && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.OutboundCommandId)
                .FirstOrDefaultAsync(cancellationToken);
            if (row is null) return null;
            row.Status = OutboundCommandStatus.Processing;
            row.AttemptCount++;
            row.LastAttemptAt = now;
            row.LockedUntil = now + ClaimLease;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return row;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task CompleteAsync(OutboundCommandRow command, OutboundDocument document, string outcome,
        CancellationToken cancellationToken)
    {
        var moduleId = ModuleId(command);
        var existing = await db.ExternalIdMaps.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == command.ConnectionId && x.EntityType == "document"
            && x.ExternalId == document.ErpExternalId && x.ValidTo == null, cancellationToken);
        if (existing is not null && existing.ModuleId != moduleId)
            throw new ExternalMappingConflictException(document.ErpExternalId);
        if (existing is null)
        {
            db.ExternalIdMaps.Add(new ExternalIdMap
            {
                TenantId = tenant.TenantId,
                ExternalIdMapId = TimeOrderedId.New(clock.UtcNow),
                ConnectionId = command.ConnectionId,
                EntityType = "document",
                ExternalId = document.ErpExternalId,
                ModuleId = moduleId,
                ValidFrom = clock.UtcNow,
                NaturalKeyHash = NaturalKeyHash(command.ModuleDocumentRef),
                MappingSource = ExternalIdMappingSource.Outbound,
                MappedBy = $"system:connection:{command.ConnectionId:N}",
                MappedAt = clock.UtcNow,
            });
        }

        command.Status = OutboundCommandStatus.Done;
        command.ErpExternalId = document.ErpExternalId;
        // TIGHT COUPLING (own-write recognition): this persisted connector marker and EfCx01OwnWriteStore's exact
        // lookup are one durable acknowledgement invariant. A process-local marker would be lost on restart;
        // separating either side makes immediate remote post-write changes look foreign. Later markers must not match.
        command.ErpChangeMarker = document.ErpChangeMarker;
        command.Outcome = outcome;
        command.LastFailure = null;
        command.NextAttemptAt = null;
        command.LockedUntil = null;
        command.CompletedAt = clock.UtcNow;
        // SaveChanges is one SQL transaction: reply fields, mapping and done status cannot be observed separately.
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RetryAsync(OutboundCommandRow command, string failure,
        CancellationToken cancellationToken)
    {
        command.Status = OutboundCommandStatus.Retry;
        command.LastFailure = failure;
        command.LockedUntil = null;
        command.NextAttemptAt = clock.UtcNow + jitter.Backoff(command.AttemptCount, RetryCeiling);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ParkAsync(OutboundCommandRow command, string reasonCode, string failure, string outcome,
        CancellationToken cancellationToken)
    {
        command.Status = OutboundCommandStatus.Parked;
        command.Outcome = outcome;
        command.LastFailure = failure;
        command.NextAttemptAt = null;
        command.LockedUntil = null;
        command.CompletedAt = clock.UtcNow;
        await parking.ParkAsync(command, reasonCode, failure, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static Guid ModuleId(OutboundCommandRow command)
    {
        var candidate = command.ModuleDocumentRef[(command.ModuleDocumentRef.LastIndexOf('/') + 1)..];
        return Guid.TryParse(candidate, out var moduleId) ? moduleId : command.OutboundCommandId;
    }

    internal static string NaturalKeyHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string RequiredReason(OutboundCreateResult result) => string.IsNullOrWhiteSpace(result.Reason)
        ? $"The connector reported '{result.Outcome}'."
        : result.Reason;

    private static string Failure(Exception exception)
    {
        var value = $"{exception.GetType().Name}: {exception.Message}";
        return value.Length <= 2000 ? value : value[..2000];
    }

    private sealed class ExternalMappingConflictException(string externalId)
        : Exception($"External document '{externalId}' is already mapped to another module record.");
}

using Yojana.Integration.Connections;
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

public sealed record OutboundRelayResult(int Completed, int Retrying, int Parked, int AwaitingReview = 0);

/// <summary>Relays one connection at a time. The connector never sees persistence or another connector's lane.</summary>
public sealed class OutboundRelay(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    OutboxRelaySettings settings,
    IOutboundConnectorResolver connectors,
    IConnectionWorkerIsolation isolation,
    IConnectionRetryJitter jitter,
    IOutboundCommandParkingSink parking,
    IOutboundReviewTaskSink reviewTasks, IOutboundProgressStore progress)
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryCeiling = TimeSpan.FromHours(6);
    // A person reviews the remote record; minutes are the right resolution and keep remote reads modest.
    internal static readonly TimeSpan ReviewCheckInterval = TimeSpan.FromMinutes(5);

    public async Task<OutboundRelayResult> RelayConnectionAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.Status == ConnectionStatus.Active, cancellationToken);
        if (connection is null) return new(0, 0, 0);
        var gateway = await db.ConnectionGatewaySettings.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId, cancellationToken);
        if (gateway?.ReadOnly == true || gateway?.OutboundMode == "none") return new(0, 0, 0);
        var connector = connectors.Resolve(connection.ConnectorCode);
        var tracker = connectors.ResolveReviewTracker(connection.ConnectorCode);
        var maxAge = await settings.MaxAgeAsync(connectionId, cancellationToken);
        var completed = 0;
        var retrying = 0;
        var parked = 0;
        var awaiting = 0;
        // One pass attempts a command at most once. A short first backoff can fall due within the same pass, and
        // re-claiming it there would retry a failing remote back to back instead of on the next scheduled pass.
        var attempted = new List<Guid>();

        for (var processed = 0; processed < 100; processed++)
        {
            await using var activity = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, connectionId, cancellationToken);
            if (!await db.Connections.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.Status == ConnectionStatus.Active, cancellationToken)) break;
            // A connector whose HTTP pipeline owns the shared rate-limited lane must not be wrapped in the same
            // non-reentrant lane here. Connectors without that pipeline retain the relay's connection-wide serialisation.
            await using IConnectionWorkerLease? lane = connector.Capabilities.OwnsConnectionLane
                ? null : await isolation.AcquireAsync(new(tenant.TenantId, connectionId), cancellationToken);
            var command = await ClaimNextAsync(connectionId, attempted, cancellationToken);
            if (command is null) break;
            attempted.Add(command.OutboundCommandId);
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
                    command.ErpReferenceText, command.Payload), cancellationToken);
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
            catch (OutboundMappingConflictException ex)
            {
                await ParkAsync(command, "external_mapping_conflict", ex.Message, OutboundConnectorOutcome.Rejected, cancellationToken);
                parked++;
                continue;
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
                else if (created?.Outcome == OutboundConnectorOutcome.AwaitingReview
                    && !string.IsNullOrWhiteSpace(created.PendingRemoteKey))
                {
                    if (tracker is null)
                    {
                        // Without a tracker nothing would ever release the command; a person must finish it instead.
                        await ParkAsync(command, OutboundConnectorOutcome.Unsupported,
                            "The connector holds the record for review but cannot report its outcome.",
                            OutboundConnectorOutcome.Unsupported, cancellationToken);
                        parked++;
                    }
                    else
                    {
                        await AwaitReviewAsync(command, created, cancellationToken);
                        awaiting++;
                    }
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
            catch (OutboundMappingConflictException ex)
            {
                await ParkAsync(command, "external_mapping_conflict", ex.Message,
                    OutboundConnectorOutcome.Rejected, cancellationToken);
                parked++;
            }
        }

        if (tracker is not null)
        {
            var reviewed = await CheckReviewsAsync(connectionId, connector, tracker, cancellationToken);
            completed += reviewed.Completed;
            parked += reviewed.Parked;
        }
        return new(completed, retrying, parked, awaiting);
    }

    // Commands held for review are never claimed for create again (query-before-create cannot see a remote draft).
    // Each due one is pushed out by one interval BEFORE the remote read, so a crash or a remote failure simply
    // means the next sweep looks again; two workers cannot both act on the same check.
    private async Task<(int Completed, int Parked)> CheckReviewsAsync(Guid connectionId,
        IOutboundConnector connector, IOutboundReviewTracker tracker, CancellationToken cancellationToken)
    {
        var completed = 0;
        var parked = 0;
        for (var processed = 0; processed < 100; processed++)
        {
            await using var activity = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId,
                connectionId, cancellationToken);
            if (!await db.Connections.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.Status == ConnectionStatus.Active, cancellationToken)) break;
            await using IConnectionWorkerLease? lane = connector.Capabilities.OwnsConnectionLane
                ? null : await isolation.AcquireAsync(new(tenant.TenantId, connectionId), cancellationToken);
            var command = await ClaimDueReviewAsync(connectionId, cancellationToken);
            if (command is null) break;

            OutboundReviewState state;
            try
            {
                state = await tracker.CheckAsync(new(connectionId, command.CommandType, command.ErpReferenceText,
                    command.PendingRemoteKey!, command.Payload, command.OutboundCommandId), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OutboundMappingConflictException ex)
            {
                await ParkAsync(command, "external_mapping_conflict", ex.Message, OutboundConnectorOutcome.Rejected, cancellationToken);
                parked++;
                continue;
            }
            catch (Exception ex)
            {
                command.LastFailure = Failure(ex);
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            try
            {
                switch (state.State)
                {
                    case OutboundReviewStates.Posted when state.Document is not null:
                        await reviewTasks.CloseAsync(command, state.Document, cancellationToken);
                        await CompleteAsync(command, state.Document, OutboundConnectorOutcome.PostedAfterReview,
                            cancellationToken);
                        completed++;
                        break;
                    case OutboundReviewStates.Rejected:
                        await ParkAsync(command, "rejected_in_remote_review", state.Reason
                            ?? "The record was rejected in the remote system's review.",
                            OutboundConnectorOutcome.Rejected, cancellationToken);
                        parked++;
                        break;
                    case OutboundReviewStates.Withdrawn:
                        await ParkAsync(command, "withdrawn_in_remote_review", state.Reason
                            ?? "The record held for review was removed in the remote system without being posted.",
                            OutboundConnectorOutcome.Rejected, cancellationToken);
                        parked++;
                        break;
                    default:
                        command.LastFailure = null;
                        if (state.PendingRemoteKey is not null)
                        {
                            command.PendingRemoteKey = state.PendingRemoteKey;
                            await reviewTasks.OpenAsync(command, state.Reason ?? "The next remote document waits for review.", cancellationToken);
                        }
                        await db.SaveChangesAsync(cancellationToken);
                        break;
                }
            }
            catch (OutboundMappingConflictException ex)
            {
                db.ChangeTracker.Clear();
                var reloaded = await db.OutboundCommands.SingleAsync(x => x.TenantId == tenant.TenantId
                    && x.OutboundCommandId == command.OutboundCommandId, cancellationToken);
                await ParkAsync(reloaded, "external_mapping_conflict", ex.Message,
                    OutboundConnectorOutcome.Rejected, cancellationToken);
                parked++;
            }
        }
        return (completed, parked);
    }

    private async Task<OutboundCommandRow?> ClaimDueReviewAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = clock.UtcNow;
            var row = await db.OutboundCommands.Where(x => x.TenantId == tenant.TenantId
                    && x.ConnectionId == connectionId && x.Status == OutboundCommandStatus.AwaitingReview
                    && x.PendingRemoteKey != null && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
                .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.OutboundCommandId)
                .FirstOrDefaultAsync(cancellationToken);
            if (row is null) return null;
            row.NextAttemptAt = now + ReviewCheckInterval;
            row.LastAttemptAt = now;
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

    private async Task AwaitReviewAsync(OutboundCommandRow command, OutboundCreateResult created,
        CancellationToken cancellationToken)
    {
        command.Status = OutboundCommandStatus.AwaitingReview;
        command.PendingRemoteKey = created.PendingRemoteKey;
        command.Outcome = OutboundConnectorOutcome.AwaitingReview;
        command.LastFailure = null;
        command.LockedUntil = null;
        command.NextAttemptAt = clock.UtcNow + ReviewCheckInterval;
        await reviewTasks.OpenAsync(command, RequiredReason(created), cancellationToken);
        // One SQL transaction: the held status and its task appear together or not at all.
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<OutboundCommandRow?> ClaimNextAsync(Guid connectionId, IReadOnlyCollection<Guid> attempted,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = clock.UtcNow;
            var row = await db.OutboundCommands.Where(x => x.TenantId == tenant.TenantId
                    && x.ConnectionId == connectionId
                    && (x.Status == OutboundCommandStatus.Pending || x.Status == OutboundCommandStatus.Retry
                        || (x.Status == OutboundCommandStatus.Processing && x.LockedUntil <= now))
                    && (x.NextAttemptAt == null || x.NextAttemptAt <= now)
                    && !attempted.Contains(x.OutboundCommandId))
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

    private Task CompleteAsync(OutboundCommandRow command, OutboundDocument document, string outcome,
        CancellationToken cancellationToken) =>
        progress.CompleteAsync(command.ConnectionId, command.OutboundCommandId, document, outcome, cancellationToken);

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

}

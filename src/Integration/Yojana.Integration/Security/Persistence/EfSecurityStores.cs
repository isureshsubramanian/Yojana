using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Security.Persistence;

public sealed class EfTenantDataKeyStore(IntegrationDbContext db, ITenantContext tenant)
    : ITenantDataKeyStore, ITenantDataKeyRewrapStore
{
    public async Task<TenantDataKeyRecord?> GetActiveAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        (await db.TenantDataKeys.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive, cancellationToken))?.ToRecord();

    public async Task<TenantDataKeyRecord?> GetVersionAsync(TenantId tenantId, int version, CancellationToken cancellationToken) =>
        (await db.TenantDataKeys.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Version == version, cancellationToken))?.ToRecord();

    public async Task<IReadOnlyList<TenantDataKeyRecord>> ListAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        (await db.TenantDataKeys.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.Version).ToListAsync(cancellationToken)).Select(x => x.ToRecord()).ToList();

    public async Task AddAsync(TenantDataKeyRecord key, CancellationToken cancellationToken)
    {
        db.TenantDataKeys.Add(key.ToEntity());
        await db.SaveChangesAsync(cancellationToken);
    }

    // TIGHT COUPLING (active data-key replacement): this transaction and the filtered unique index configured in
    // IntegrationDbContext are one invariant. Retiring and inserting in separate commits can strand credential reads.
    public async Task ReplaceActiveAsync(TenantId tenantId, int expectedVersion, TenantDataKeyRecord replacement,
        DateTime rotatedAt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var changed = await db.TenantDataKeys.Where(x => x.TenantId == tenantId && x.Version == expectedVersion && x.IsActive)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsActive, false)
                .SetProperty(x => x.RotatedAt, rotatedAt), cancellationToken);
        if (changed != 1) throw new InvalidOperationException("The active tenant data key was not found.");
        db.TenantDataKeys.Add(replacement.ToEntity());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TenantDataKeyRecord>> ListForRewrapAsync(TenantDataKeyRewrapPosition? after,
        int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        var query = db.TenantDataKeys.AsNoTracking().Where(x => x.TenantId == tenant.TenantId);
        if (after is not null)
            query = query.Where(x => x.Version > after.DataKeyVersion);
        return (await query.OrderBy(x => x.Version).Take(take).ToListAsync(cancellationToken)).Select(x => x.ToRecord()).ToList();
    }

    public async Task ReplaceWrappingAsync(TenantDataKeyRecord expected, WrappedDataKey replacement,
        CancellationToken cancellationToken)
    {
        var changed = await db.TenantDataKeys.Where(x => x.TenantId == expected.TenantId
                && x.Version == expected.Version && x.KeyEncryptionKeyId == expected.KeyEncryptionKeyId
                && x.KeyEncryptionKeyVersion == expected.KeyEncryptionKeyVersion)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.KeyEncryptionKeyId, replacement.KeyId)
                .SetProperty(x => x.KeyEncryptionKeyVersion, replacement.KeyVersion)
                .SetProperty(x => x.KeyEncryptionAlgorithm, replacement.Algorithm)
                .SetProperty(x => x.WrappedKeyNonce, replacement.Nonce)
                .SetProperty(x => x.WrappedKeyCiphertext, replacement.Ciphertext)
                .SetProperty(x => x.WrappedKeyTag, replacement.Tag), cancellationToken);
        if (changed != 1) throw new InvalidOperationException("The tenant data key changed while it was being rewrapped.");
    }
}

public sealed class EfConnectionCredentialStore(IntegrationDbContext db) : IConnectionCredentialStore
{
    public async Task<ConnectionCredentialRecord?> GetCurrentAsync(TenantId tenantId, Guid connectionId,
        string purpose, CancellationToken cancellationToken) => (await db.ConnectionCredentials.AsNoTracking()
        .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
            && x.Purpose == purpose && x.RotatedAt == null, cancellationToken))?.ToRecord();

    public async Task<IReadOnlyList<ConnectionCredentialRecord>> ListVersionsAsync(TenantId tenantId,
        Guid connectionId, string purpose, CancellationToken cancellationToken) => (await db.ConnectionCredentials.AsNoTracking()
        .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Purpose == purpose)
        .OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken)).Select(x => x.ToRecord()).ToList();

    // TIGHT COUPLING (credential version replacement): rotating the current row and inserting the new version share
    // this SERIALIZABLE transaction and the current-row lookup index in IntegrationDbContext.
    public async Task SaveAsync(ConnectionCredentialRecord credential, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await db.ConnectionCredentials.Where(x => x.TenantId == credential.TenantId
                && x.ConnectionId == credential.ConnectionId && x.Purpose == credential.Purpose && x.RotatedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RotatedAt, credential.CreatedAt), cancellationToken);
        db.ConnectionCredentials.Add(credential.ToEntity());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class EfTenantDataKeyRewrapCheckpointStore(
    IntegrationDbContext db, ITenantContext tenant, IClock clock) : ITenantDataKeyRewrapCheckpointStore
{
    public async Task<TenantDataKeyRewrapCheckpoint?> GetAsync(string operationId, CancellationToken cancellationToken)
    {
        var row = await db.TenantDataKeyRewrapCheckpoints.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.OperationId == operationId, cancellationToken);
        return row is null ? null : new(row.OperationId, new(row.TenantId, row.DataKeyVersion));
    }

    public async Task SaveAsync(TenantDataKeyRewrapCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var row = await db.TenantDataKeyRewrapCheckpoints.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.OperationId == checkpoint.OperationId, cancellationToken);
        if (row is null)
            db.TenantDataKeyRewrapCheckpoints.Add(new()
            {
                TenantId = tenant.TenantId, OperationId = checkpoint.OperationId,
                DataKeyVersion = checkpoint.Position.DataKeyVersion, UpdatedAt = clock.UtcNow,
            });
        else
        {
            row.DataKeyVersion = checkpoint.Position.DataKeyVersion;
            row.UpdatedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteAsync(string operationId, CancellationToken cancellationToken)
    {
        await db.TenantDataKeyRewrapCheckpoints.Where(x => x.TenantId == tenant.TenantId
            && x.OperationId == operationId).ExecuteDeleteAsync(cancellationToken);
    }
}

public sealed class EfConnectionAuditSink(IntegrationDbContext db) : IConnectionAuditSink
{
    // TIGHT COUPLING (tenant audit sequence): the last-row lock, sequence allocation, hash seal and insert are one
    // SERIALIZABLE transaction. Splitting them permits two writers to fork the append-only tenant hash chain.
    public async Task<ConnectionAuditEvent> AppendAsync(NewConnectionAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var last = await db.ConnectionEvents.Where(x => x.TenantId == auditEvent.TenantId)
            .OrderByDescending(x => x.SequenceNo).FirstOrDefaultAsync(cancellationToken);
        var sealedEvent = ConnectionAuditChain.Seal(auditEvent, (last?.SequenceNo ?? 0) + 1,
            last?.RecordHash ?? ConnectionAuditChain.InitialHash);
        db.ConnectionEvents.Add(sealedEvent.ToEntity());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return sealedEvent;
    }

    public async IAsyncEnumerable<ConnectionAuditEvent> ReadAsync(TenantId tenantId, DateOnly fromDate,
        DateOnly toDate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var from = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var until = toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await foreach (var row in db.ConnectionEvents.AsNoTracking().Where(x => x.TenantId == tenantId
            && x.OccurredAt >= from && x.OccurredAt < until).OrderBy(x => x.SequenceNo).AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return row.ToRecord();
    }
}

public sealed class EfConnectionThrottleStore(IntegrationDbContext db)
    : IConnectionThrottleProfileStore, IConnectionDailyBudgetStore
{
    public async Task<StoredConnectionThrottleProfile?> GetAsync(ConnectionWorkerKey workerKey, CancellationToken cancellationToken) =>
        (await db.ConnectionThrottleProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == workerKey.TenantId
            && x.ConnectionId == workerKey.ConnectionId, cancellationToken))?.ToStored();

    public async Task<bool> CreateIfMissingAsync(StoredConnectionThrottleProfile profile, CancellationToken cancellationToken)
    {
        if (await db.ConnectionThrottleProfiles.AnyAsync(x => x.TenantId == profile.WorkerKey.TenantId
            && x.ConnectionId == profile.WorkerKey.ConnectionId, cancellationToken)) return false;
        db.ConnectionThrottleProfiles.Add(profile.ToEntity());
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }
    }

    public async Task SaveAsync(StoredConnectionThrottleProfile profile, CancellationToken cancellationToken)
    {
        var row = await db.ConnectionThrottleProfiles.SingleAsync(x => x.TenantId == profile.WorkerKey.TenantId
            && x.ConnectionId == profile.WorkerKey.ConnectionId, cancellationToken);
        row.Apply(profile);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConnectionDailyBudgetState?> LoadAsync(ConnectionWorkerKey workerKey, DateOnly budgetDateUtc,
        CancellationToken cancellationToken)
    {
        var row = await db.ConnectionThrottleProfiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == workerKey.TenantId && x.ConnectionId == workerKey.ConnectionId, cancellationToken);
        if (row is null) return null;
        return row.BudgetDateUtc == budgetDateUtc
            ? new(workerKey, budgetDateUtc, row.UsedCalls, row.ApproachAlertRaised, row.CapAlertRaised, row.BudgetRevision)
            : new(workerKey, budgetDateUtc, 0, false, false, row.BudgetRevision);
    }

    public async Task<bool> TrySaveAsync(ConnectionDailyBudgetState state, long expectedRevision,
        CancellationToken cancellationToken) => await db.ConnectionThrottleProfiles.Where(x =>
            x.TenantId == state.WorkerKey.TenantId && x.ConnectionId == state.WorkerKey.ConnectionId
            && x.BudgetRevision == expectedRevision).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.BudgetDateUtc, state.BudgetDateUtc)
                .SetProperty(x => x.UsedCalls, state.UsedCalls)
                .SetProperty(x => x.ApproachAlertRaised, state.ApproachAlertRaised)
                .SetProperty(x => x.CapAlertRaised, state.CapAlertRaised)
                .SetProperty(x => x.BudgetRevision, state.Revision), cancellationToken) == 1;
}

/// <summary>
/// Gives the process-wide per-connection throttle lanes a narrow, lifetime-safe budget port. Each operation owns a
/// fresh EF scope; the revision compare-and-swap remains the concurrency contract between the two calls.
/// </summary>
public sealed class ScopedEfConnectionDailyBudgetStore(IServiceScopeFactory scopes) : IConnectionDailyBudgetStore
{
    public async Task<ConnectionDailyBudgetState?> LoadAsync(ConnectionWorkerKey workerKey, DateOnly budgetDateUtc,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(workerKey.TenantId);
        return await scope.ServiceProvider.GetRequiredService<EfConnectionThrottleStore>()
            .LoadAsync(workerKey, budgetDateUtc, cancellationToken);
    }

    public async Task<bool> TrySaveAsync(ConnectionDailyBudgetState state, long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(state.WorkerKey.TenantId);
        return await scope.ServiceProvider.GetRequiredService<EfConnectionThrottleStore>()
            .TrySaveAsync(state, expectedRevision, cancellationToken);
    }
}

internal static class EfSecurityMappings
{
    public static TenantDataKeyEntity ToEntity(this TenantDataKeyRecord x) => new()
    {
        TenantId = x.TenantId, Version = x.Version, KeyEncryptionKeyId = x.KeyEncryptionKeyId,
        KeyEncryptionKeyVersion = x.KeyEncryptionKeyVersion, KeyEncryptionAlgorithm = x.KeyEncryptionAlgorithm,
        WrappedKeyNonce = x.WrappedKeyNonce, WrappedKeyCiphertext = x.WrappedKeyCiphertext,
        WrappedKeyTag = x.WrappedKeyTag, CreatedAt = x.CreatedAt, RotatedAt = x.RotatedAt,
        ExpiresAt = x.ExpiresAt, IsActive = x.IsActive,
    };
    public static TenantDataKeyRecord ToRecord(this TenantDataKeyEntity x) => new(x.TenantId, x.Version,
        x.KeyEncryptionKeyId, x.KeyEncryptionKeyVersion, x.KeyEncryptionAlgorithm, x.WrappedKeyNonce,
        x.WrappedKeyCiphertext, x.WrappedKeyTag, x.CreatedAt, x.RotatedAt, x.ExpiresAt, x.IsActive);
    public static ConnectionCredentialEntity ToEntity(this ConnectionCredentialRecord x) => new()
    {
        TenantId = x.TenantId, ConnectionId = x.ConnectionId, Purpose = x.Purpose, DataKeyVersion = x.DataKeyVersion,
        Nonce = x.Nonce, Ciphertext = x.Ciphertext, Tag = x.Tag, CreatedAt = x.CreatedAt, RotatedAt = x.RotatedAt,
        ExpiresAt = x.ExpiresAt, RotationDueAt = x.RotationDueAt,
    };
    public static ConnectionCredentialRecord ToRecord(this ConnectionCredentialEntity x) => new(x.TenantId,
        x.ConnectionId, x.Purpose, x.DataKeyVersion, x.Nonce, x.Ciphertext, x.Tag, x.CreatedAt, x.RotatedAt,
        x.ExpiresAt, x.RotationDueAt);
    public static ConnectionEventEntity ToEntity(this ConnectionAuditEvent x) => new()
    {
        TenantId = x.TenantId, SequenceNo = x.Sequence, EventId = x.EventId, ConnectionId = x.ConnectionId,
        EventType = x.EventType.ToString(), ActorRef = x.ActorRef, OccurredAt = x.OccurredAt,
        DetailsJson = x.DetailsJson, PreviousRecordHash = x.PreviousRecordHash, RecordHash = x.RecordHash,
    };
    public static ConnectionAuditEvent ToRecord(this ConnectionEventEntity x) => new(x.TenantId, x.SequenceNo,
        x.EventId, x.ConnectionId, Enum.Parse<ConnectionAuditEventType>(x.EventType), x.ActorRef,
        DateTime.SpecifyKind(x.OccurredAt, DateTimeKind.Utc),
        x.DetailsJson, x.PreviousRecordHash.TrimEnd(), x.RecordHash.TrimEnd());
    public static ConnectionThrottleProfileEntity ToEntity(this StoredConnectionThrottleProfile x)
    {
        var row = new ConnectionThrottleProfileEntity
        {
            TenantId = x.WorkerKey.TenantId, ConnectionId = x.WorkerKey.ConnectionId,
            ConnectorCode = x.ConnectorCode, Burst = 1, MaxConcurrentCalls = 1,
        };
        row.Apply(x);
        return row;
    }
    public static void Apply(this ConnectionThrottleProfileEntity row, StoredConnectionThrottleProfile x)
    {
        row.ConnectorCode = x.ConnectorCode; row.RequestsPerSecond = x.Profile.RequestsPerSecond;
        row.RequestsPerMinute = x.Profile.RequestsPerMinute; row.RequestsPerDay = x.Profile.RequestsPerDay;
        row.Burst = x.Profile.Burst; row.MaxConcurrentCalls = x.Profile.MaxConcurrentCalls;
        row.RetryAfterCeilingSeconds = checked((int)x.Profile.RetryAfterCeiling.TotalSeconds);
        row.AlertThresholdPercent = x.Profile.AlertThresholdPercent; row.UpdatedAt = x.UpdatedAtUtc;
    }
    public static StoredConnectionThrottleProfile ToStored(this ConnectionThrottleProfileEntity x) => new(
        new(x.TenantId, x.ConnectionId), x.ConnectorCode,
        new(x.RequestsPerSecond, x.RequestsPerMinute, x.RequestsPerDay, x.Burst, x.MaxConcurrentCalls,
            TimeSpan.FromSeconds(x.RetryAfterCeilingSeconds), x.AlertThresholdPercent), x.UpdatedAt);
}

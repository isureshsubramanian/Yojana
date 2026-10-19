using Microsoft.EntityFrameworkCore;
using Quartz;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Gateway;

public sealed record InboundTranslationRecoveryOptions(
    TimeSpan GracePeriod,
    TimeSpan InitialBackoff,
    TimeSpan MaximumBackoff,
    int MaximumAttempts,
    int BatchSize)
{
    public static readonly InboundTranslationRecoveryOptions Default = new(
        TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 8, 200);
}

/// <summary>Durably re-dispatches accepted translations that did not reach their terminal state.</summary>
public sealed class InboundTranslationRecovery(
    IDbContextOutbox<IntegrationDbContext> outbox,
    ITenantContext tenant,
    IClock clock,
    InboundTranslationRecoveryOptions options,
    IInboundMessageParkingSink parking)
{
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var db = outbox.DbContext;
        var now = clock.UtcNow;
        var stale = now - options.GracePeriod;
        var ids = await db.InboundMessages.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.IsLatest
                && ((x.TranslationStatus == InboundTranslationStatus.Pending && x.AcceptedAt <= stale
                        && (x.NextTranslationAttemptAt == null || x.NextTranslationAttemptAt <= now))
                    || (x.TranslationStatus == InboundTranslationStatus.Failed
                        && x.NextTranslationAttemptAt != null && x.NextTranslationAttemptAt <= now)
                    || (x.TranslationStatus == InboundTranslationStatus.Processing
                        && x.TranslationStartedAt != null && x.TranslationStartedAt <= stale)))
            .OrderBy(x => x.NextTranslationAttemptAt).ThenBy(x => x.AcceptedAt).ThenBy(x => x.InboundMessageId)
            .Select(x => x.InboundMessageId).Take(options.BatchSize).ToListAsync(cancellationToken);
        if (ids.Count == 0) return 0;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var id in ids)
        {
            await outbox.PublishAsync(new TranslateInboundMessage(id),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        }
        // Do not move next_attempt_at here: the dispatched handler uses that field as its claim gate.
        // A later sweep may publish a duplicate before this message runs, but the atomic handler claim
        // and terminal-state check make that harmless while preserving recovery after an outbox crash.
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
        if (db.Database.CurrentTransaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return ids.Count;
    }

    public async Task RecordFailureAsync(Guid inboundMessageId, Exception exception,
        CancellationToken cancellationToken)
    {
        var db = outbox.DbContext;
        db.ChangeTracker.Clear();
        var row = await db.InboundMessages.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.InboundMessageId == inboundMessageId, cancellationToken);
        if (row is null || !row.IsLatest || row.TranslationStatus == InboundTranslationStatus.Superseded) return;
        row.LastTranslationError = Error(exception);
        row.TranslationStartedAt = null;
        if (row.TranslationAttemptCount >= options.MaximumAttempts)
        {
            row.TranslationStatus = InboundTranslationStatus.NeedsAttention;
            row.AttentionReasonCode = "translation_attempts_exhausted";
            row.NextTranslationAttemptAt = null;
            await parking.ParkInboundAsync(row, row.AttentionReasonCode, row.LastTranslationError,
                cancellationToken);
        }
        else
        {
            row.TranslationStatus = InboundTranslationStatus.Failed;
            row.NextTranslationAttemptAt = clock.UtcNow + Backoff(row.InboundMessageId, row.TranslationAttemptCount);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private TimeSpan Backoff(Guid id, int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var baseMilliseconds = Math.Min(options.MaximumBackoff.TotalMilliseconds,
            options.InitialBackoff.TotalMilliseconds * Math.Pow(2, exponent));
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var jitter = 0.75 + (BitConverter.ToUInt32(bytes) / (double)uint.MaxValue * 0.5);
        return TimeSpan.FromMilliseconds(Math.Min(options.MaximumBackoff.TotalMilliseconds,
            baseMilliseconds * jitter));
    }

    private static string Error(Exception exception)
    {
        var value = $"{exception.GetType().Name}: {exception.Message}";
        return value.Length <= 2000 ? value : value[..2000];
    }
}

[DisallowConcurrentExecution]
public sealed class InboundTranslationRecoveryJob(
    ITenantContextSetter setter,
    InboundTranslationRecovery recovery) : TenantJob(setter)
{
    public const string JobName = "integration.inbound-translation-recovery";
    public const string Cron = "0 * * * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        recovery.SweepAsync(cancellationToken);
}

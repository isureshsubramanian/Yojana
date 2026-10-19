using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Backfills;

public sealed class BackfillService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IConnectorReconciliationSource source,
    InboundMessageStore inbox,
    ConnectionThrottleProfiles profiles,
    IConnectionThrottleWorkerRegistry workers) : IReconciliationBackfillScheduler
{
    public Task<Guid> StartAsync(StartBackfillRequest request, string actorRef,
        CancellationToken cancellationToken) => StartInternalAsync(request, RequiredActor(actorRef), null,
        cancellationToken);

    public Task<Guid> ScheduleAsync(Guid connectionId, string entity, DateOnly rangeStart,
        DateOnly rangeEnd, Guid reconciliationResultId, CancellationToken cancellationToken) =>
        StartInternalAsync(new(connectionId, entity, rangeStart, rangeEnd),
            $"system:reconciliation:{reconciliationResultId:N}", reconciliationResultId, cancellationToken);

    public async Task ConfirmCleanAsync(Guid connectionId, Guid reconciliationRunId,
        DateOnly rangeStart, DateOnly rangeEnd, IReadOnlySet<string> cleanEntities,
        CancellationToken cancellationToken)
    {
        if (cleanEntities.Count == 0) return;
        var runStartedAt = await db.ReconciliationRuns.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ReconciliationRunId == reconciliationRunId)
            .Select(row => row.StartedAt).SingleAsync(cancellationToken);
        var jobs = await db.BackfillCursors.Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && row.Status == BackfillStatus.AwaitingReconciliation
                && row.AwaitingReconciliationAt < runStartedAt && rangeStart <= row.RangeStart
                && rangeEnd >= row.RangeEnd).ToListAsync(cancellationToken);
        foreach (var job in jobs.Where(job => cleanEntities.Contains(job.Entity)))
        {
            job.Status = BackfillStatus.Completed;
            job.CompletionReconciliationRunId = reconciliationRunId;
            job.CompletedAt = clock.UtcNow;
            job.LastFailure = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RunPendingAsync(CancellationToken cancellationToken)
    {
        var ids = await db.BackfillCursors.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && (row.Status == BackfillStatus.Queued || row.Status == BackfillStatus.Running))
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.BackfillCursorId)
            .Select(row => row.BackfillCursorId).Take(25).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            try { await ProcessAsync(id, null, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* Cursor and failure are persisted; the next tenant run resumes this job. */ }
        }
        return ids.Count;
    }

    public async Task ProcessAsync(Guid backfillId, int? maximumPages,
        CancellationToken cancellationToken)
    {
        var job = await JobAsync(backfillId, cancellationToken);
        if (job.Status is BackfillStatus.Paused or BackfillStatus.Cancelled
            or BackfillStatus.Completed or BackfillStatus.AwaitingReconciliation) return;
        job.Status = BackfillStatus.Running;
        job.StartedAt ??= clock.UtcNow;
        job.LastFailure = null;
        await db.SaveChangesAsync(cancellationToken);
        var pagesThisRun = 0;
        try
        {
            while (maximumPages is null || pagesThisRun < maximumPages.Value)
            {
                await db.Entry(job).ReloadAsync(cancellationToken);
                if (job.Status is BackfillStatus.Paused or BackfillStatus.Cancelled) return;
                var key = new ConnectionWorkerKey(tenant.TenantId, job.ConnectionId);
                var profile = await profiles.GetAsync(key, cancellationToken);
                ConnectorRangePage page;
                await using (await workers.For(key).AcquireAsync(profile.Profile, cancellationToken))
                    page = await source.ReadRangePageAsync(job.ConnectionId, job.Entity, job.RangeStart,
                        job.RangeEnd, job.Cursor, job.PageSize, cancellationToken);

                foreach (var message in page.Messages)
                    await inbox.Accept(AsBackfill(message), cancellationToken);
                job.ProcessedMessages += page.Messages.Count;
                job.ProcessedPages++;
                pagesThisRun++;
                job.TotalMessages = page.TotalRecords ?? job.TotalMessages;
                job.Cursor = page.NextCursor;
                job.LastPageAt = clock.UtcNow;
                job.LastFailure = null;
                if (page.NextCursor is null)
                {
                    job.Status = BackfillStatus.AwaitingReconciliation;
                    job.AwaitingReconciliationAt = clock.UtcNow;
                }
                await db.SaveChangesAsync(cancellationToken);
                if (page.NextCursor is null) return;
            }
        }
        catch (Exception ex)
        {
            job.LastFailure = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public Task PauseAsync(Guid backfillId, string actorRef, CancellationToken cancellationToken) =>
        ChangeStatusAsync(backfillId, BackfillStatus.Paused,
            [BackfillStatus.Queued, BackfillStatus.Running], RequiredActor(actorRef), cancellationToken);

    public Task ResumeAsync(Guid backfillId, string actorRef, CancellationToken cancellationToken) =>
        ChangeStatusAsync(backfillId, BackfillStatus.Queued, [BackfillStatus.Paused],
            RequiredActor(actorRef), cancellationToken);

    public Task CancelAsync(Guid backfillId, string actorRef, CancellationToken cancellationToken) =>
        ChangeStatusAsync(backfillId, BackfillStatus.Cancelled,
            [BackfillStatus.Queued, BackfillStatus.Running, BackfillStatus.Paused,
                BackfillStatus.AwaitingReconciliation], RequiredActor(actorRef), cancellationToken);

    public async Task<BackfillPage> ListAsync(BackfillListRequest request,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(request.Take, 1, 200);
        var query = db.BackfillCursors.AsNoTracking().Where(row => row.TenantId == tenant.TenantId);
        if (request.ConnectionId is { } connectionId)
            query = query.Where(row => row.ConnectionId == connectionId);
        if (!string.IsNullOrWhiteSpace(request.Entity)) query = query.Where(row => row.Entity == request.Entity);
        if (!string.IsNullOrWhiteSpace(request.Status)) query = query.Where(row => row.Status == request.Status);
        if (Decode(request.Cursor) is { } cursor)
            query = query.Where(row => row.CreatedAt < cursor.CreatedAt
                || (row.CreatedAt == cursor.CreatedAt && row.BackfillCursorId.CompareTo(cursor.Id) < 0));
        var rows = await query.Join(db.Connections.AsNoTracking(), job => new { job.TenantId, job.ConnectionId },
                connection => new { connection.TenantId, connection.ConnectionId }, (job, connection) =>
                new BackfillProjection
                {
                    Id = job.BackfillCursorId,
                    ConnectionId = job.ConnectionId,
                    ConnectionName = connection.DisplayLabel,
                    Entity = job.Entity,
                    RangeStart = job.RangeStart,
                    RangeEnd = job.RangeEnd,
                    Status = job.Status,
                    ProcessedMessages = job.ProcessedMessages,
                    ProcessedPages = job.ProcessedPages,
                    TotalMessages = job.TotalMessages,
                    CreatedAt = job.CreatedAt,
                    StartedAt = job.StartedAt,
                    LastPageAt = job.LastPageAt,
                    AwaitingAt = job.AwaitingReconciliationAt,
                    CompletedAt = job.CompletedAt,
                    CompletionRunId = job.CompletionReconciliationRunId,
                    LastFailure = job.LastFailure,
                }).OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .Take(take + 1).ToListAsync(cancellationToken);
        var more = rows.Count > take;
        var page = rows.Take(take).Select(View).ToList();
        return new(page, more && page.Count != 0 ? Encode(new(page[^1].CreatedAt, page[^1].BackfillId)) : null);
    }

    public async Task<BackfillView?> DetailAsync(Guid backfillId, CancellationToken cancellationToken)
    {
        var page = await ListAsync(new(null, null, null, null, 200), cancellationToken);
        return page.Items.SingleOrDefault(row => row.BackfillId == backfillId)
            ?? await DetailProjectionAsync(backfillId, cancellationToken);
    }

    private async Task<BackfillView?> DetailProjectionAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.BackfillCursors.AsNoTracking().Where(job => job.TenantId == tenant.TenantId
                && job.BackfillCursorId == id)
            .Join(db.Connections.AsNoTracking(), job => new { job.TenantId, job.ConnectionId }, connection =>
                new { connection.TenantId, connection.ConnectionId }, (job, connection) => new BackfillProjection
            {
                Id = job.BackfillCursorId, ConnectionId = job.ConnectionId,
                ConnectionName = connection.DisplayLabel, Entity = job.Entity, RangeStart = job.RangeStart,
                RangeEnd = job.RangeEnd, Status = job.Status, ProcessedMessages = job.ProcessedMessages,
                ProcessedPages = job.ProcessedPages, TotalMessages = job.TotalMessages, CreatedAt = job.CreatedAt,
                StartedAt = job.StartedAt, LastPageAt = job.LastPageAt,
                AwaitingAt = job.AwaitingReconciliationAt, CompletedAt = job.CompletedAt,
                CompletionRunId = job.CompletionReconciliationRunId, LastFailure = job.LastFailure,
            }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : View(row);
    }

    private async Task<Guid> StartInternalAsync(StartBackfillRequest request, string actorRef,
        Guid? reconciliationResultId, CancellationToken cancellationToken)
    {
        var entity = request.Entity.Trim();
        if (request.ConnectionId == Guid.Empty || string.IsNullOrWhiteSpace(entity))
            throw new BackfillRuleException("backfill_target_required", "Connection and entity are required.");
        if (request.RangeEnd < request.RangeStart)
            throw new BackfillRuleException("backfill_range_invalid", "The backfill end date must not precede its start date.");
        if (request.PageSize is < 1 or > 1000)
            throw new BackfillRuleException("backfill_page_size_invalid", "Page size must be between 1 and 1,000.");
        if (!await db.Connections.AnyAsync(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == request.ConnectionId && row.Status == ConnectionStatus.Active,
                cancellationToken))
            throw new BackfillRuleException("connection_not_found", "The active connection was not found.");
        if (!await source.SupportsRangedReadsAsync(request.ConnectionId, entity, cancellationToken))
            throw new BackfillRuleException("backfill_range_unsupported",
                "The connector does not support ranged reads for this entity.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var existing = await db.BackfillCursors.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == request.ConnectionId && row.Entity == entity
            && row.Status != BackfillStatus.Completed && row.Status != BackfillStatus.Cancelled, cancellationToken);
        if (existing is not null)
        {
            if (request.RangeStart < existing.RangeStart) existing.RangeStart = request.RangeStart;
            if (request.RangeEnd > existing.RangeEnd) existing.RangeEnd = request.RangeEnd;
            if (existing.Status == BackfillStatus.AwaitingReconciliation)
            {
                existing.Status = BackfillStatus.Queued;
                existing.Cursor = null;
                existing.AwaitingReconciliationAt = null;
            }
            existing.RequestedByReconciliationResultId ??= reconciliationResultId;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return existing.BackfillCursorId;
        }
        var id = TimeOrderedId.New(clock.UtcNow);
        db.BackfillCursors.Add(new BackfillCursor
        {
            TenantId = tenant.TenantId,
            BackfillCursorId = id,
            ConnectionId = request.ConnectionId,
            Entity = entity,
            RangeStart = request.RangeStart,
            RangeEnd = request.RangeEnd,
            PageSize = request.PageSize,
            Status = BackfillStatus.Queued,
            RequestedBy = actorRef,
            LastChangedBy = actorRef,
            RequestedByReconciliationResultId = reconciliationResultId,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    private async Task ChangeStatusAsync(Guid id, string target, IReadOnlyCollection<string> allowed,
        string actorRef, CancellationToken cancellationToken)
    {
        var job = await JobAsync(id, cancellationToken);
        if (!allowed.Contains(job.Status))
            throw new BackfillRuleException("backfill_transition_invalid",
                $"A backfill in status '{job.Status}' cannot move to '{target}'.");
        job.Status = target;
        job.LastChangedBy = actorRef;
        job.LastFailure = null;
        if (target == BackfillStatus.Cancelled) job.CompletedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<BackfillCursor> JobAsync(Guid id, CancellationToken cancellationToken) =>
        await db.BackfillCursors.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.BackfillCursorId == id, cancellationToken)
        ?? throw new BackfillRuleException("backfill_not_found", "The backfill was not found.");

    private static InboundAcceptance AsBackfill(InboundAcceptance acceptance) => new(
        acceptance.Envelope with { CaptureMode = OpenEnum.Known("backfill") }, acceptance.MessageType,
        acceptance.CanonicalMessage, acceptance.RawPayload);

    private BackfillView View(BackfillProjection row)
    {
        decimal? progress = row.TotalMessages is > 0
            ? Math.Min(100, decimal.Round(row.ProcessedMessages * 100m / row.TotalMessages.Value, 2)) : null;
        TimeSpan? eta = null;
        if (row.Status == BackfillStatus.Running && row.StartedAt is { } started
            && row.TotalMessages is { } total && row.ProcessedMessages > 0 && total > row.ProcessedMessages)
        {
            var elapsed = clock.UtcNow - started;
            eta = TimeSpan.FromTicks((long)(elapsed.Ticks
                * ((total - row.ProcessedMessages) / (double)row.ProcessedMessages)));
        }
        return new(row.Id, row.ConnectionId, row.ConnectionName, row.Entity, row.RangeStart, row.RangeEnd,
            row.Status, row.ProcessedMessages, row.ProcessedPages, row.TotalMessages, progress, eta,
            row.CreatedAt, row.StartedAt, row.LastPageAt, row.AwaitingAt, row.CompletedAt,
            row.CompletionRunId, row.LastFailure);
    }

    private static string RequiredActor(string actorRef) => !string.IsNullOrWhiteSpace(actorRef)
        ? actorRef : throw new BackfillRuleException("audit_actor_required", "Backfill changes require an audit actor.");

    private static string Encode(Cursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"{cursor.CreatedAt.Ticks}|{cursor.Id:D}"));

    private static Cursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return parts.Length == 2 && long.TryParse(parts[0], out var ticks) && Guid.TryParse(parts[1], out var id)
                ? new(new DateTime(ticks, DateTimeKind.Utc), id) : null;
        }
        catch (FormatException) { return null; }
    }

    private sealed record Cursor(DateTime CreatedAt, Guid Id);

    private sealed class BackfillProjection
    {
        public Guid Id { get; init; }
        public Guid ConnectionId { get; init; }
        public required string ConnectionName { get; init; }
        public required string Entity { get; init; }
        public DateOnly RangeStart { get; init; }
        public DateOnly RangeEnd { get; init; }
        public required string Status { get; init; }
        public long ProcessedMessages { get; init; }
        public int ProcessedPages { get; init; }
        public long? TotalMessages { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? LastPageAt { get; init; }
        public DateTime? AwaitingAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public Guid? CompletionRunId { get; init; }
        public string? LastFailure { get; init; }
    }
}

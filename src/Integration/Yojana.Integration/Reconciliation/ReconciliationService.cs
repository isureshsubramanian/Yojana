using Yojana.Integration.Connections;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connectors;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Reconciliation;

public sealed partial class ReconciliationService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IConnectorAdapterResolver adapters,
    ISerialisedOnHandQuery serialisedOnHand,
    IItemNamePort itemNames,
    ILocationNamePort locationNames,
    ReconciliationSettings settings,
    ReconciliationBusinessDay businessDay,
    IReconciliationBackfillScheduler backfills,
    DeadLetterService deadLetters,
    IUserDirectory people)
{
    public async Task<IReadOnlyList<Guid>> RunScheduledAsync(CancellationToken cancellationToken)
    {
        var connections = await db.Connections.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.Status == ConnectionStatus.Active)
            .OrderBy(row => row.ConnectionId).Select(row => row.ConnectionId).ToListAsync(cancellationToken);
        var runs = new List<Guid>(connections.Count);
        foreach (var connectionId in connections)
        {
            var days = await settings.WindowDaysAsync(connectionId, cancellationToken);
            var end = await businessDay.TodayAsync(connectionId, cancellationToken);
            runs.Add(await RunAsync(connectionId, end.AddDays(-(days - 1)), end,
                ReconciliationTrigger.Scheduled, includeEntityDays: true, requestedBy: null, cancellationToken));
        }
        return runs;
    }

    public async Task<Guid> RunLocationChecksAsync(Guid connectionId, string requestedBy,
        CancellationToken cancellationToken)
    {
        var day = await businessDay.TodayAsync(connectionId, cancellationToken);
        return await RunAsync(connectionId, day, day, ReconciliationTrigger.OnDemand,
            includeEntityDays: false, requestedBy, cancellationToken);
    }

    public async Task<Guid> RunAsync(Guid connectionId, DateOnly rangeStart, DateOnly rangeEnd,
        string trigger, bool includeEntityDays, string? requestedBy, CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty)
            throw new ReconciliationRuleException("connection_required", "A connection is required.");
        if (rangeEnd < rangeStart)
            throw new ReconciliationRuleException("reconciliation_range_invalid",
                "The reconciliation end date must not precede its start date.");
        if (rangeEnd > await businessDay.TodayAsync(connectionId, cancellationToken))
            throw new ReconciliationRuleException("reconciliation_range_in_future",
                "The reconciliation end date must not be after today in the connection's time zone.");
        var windowDays = await settings.WindowDaysAsync(connectionId, cancellationToken);
        if (rangeEnd.DayNumber - rangeStart.DayNumber + 1 > windowDays)
            throw new ReconciliationRuleException("reconciliation_range_too_wide",
                $"The reconciliation range must not exceed this connection's {windowDays}-day window.");
        await using var activity = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, connectionId, cancellationToken);
        var connectorCode = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && row.Status == ConnectionStatus.Active)
            .Select(row => row.ConnectorCode).SingleOrDefaultAsync(cancellationToken);
        if (connectorCode is null)
            throw new ReconciliationRuleException("connection_not_found", "The active connection was not found.");
        var source = adapters.ReconciliationSource(connectorCode);

        var run = new ReconciliationRun
        {
            TenantId = tenant.TenantId,
            ReconciliationRunId = TimeOrderedId.New(clock.UtcNow),
            ConnectionId = connectionId,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd,
            Trigger = trigger,
            Status = ReconciliationRunStatus.Running,
            StartedAt = clock.UtcNow,
            RequestedBy = requestedBy,
        };
        db.ReconciliationRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var entityResults = includeEntityDays
                ? await CompareEntityDaysAsync(run, source, cancellationToken)
                : [];
            var locationResults = await CompareLocationsAsync(run, source, cancellationToken);
            run.MismatchCount = entityResults.Count(row => row.Status == ReconciliationResultStatus.Mismatch);
            run.LocationMismatchCount = locationResults.Count(row => row.Status == ReconciliationResultStatus.Mismatch);
            run.Status = run.MismatchCount + run.LocationMismatchCount == 0
                ? ReconciliationRunStatus.Clean : ReconciliationRunStatus.Mismatch;
            run.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            if (includeEntityDays)
            {
                var cleanEntities = entityResults.GroupBy(row => row.Entity, StringComparer.Ordinal)
                    .Where(group => group.All(row => row.Status == ReconciliationResultStatus.Clean))
                    .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
                await backfills.ConfirmCleanAsync(connectionId, run.ReconciliationRunId,
                    rangeStart, rangeEnd, cleanEntities, cancellationToken);
            }
            return run.ReconciliationRunId;
        }
        catch (Exception ex)
        {
            run.Status = ReconciliationRunStatus.Failed;
            run.Failure = ex.Message;
            run.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ReconciliationRunPage> ListAsync(ReconciliationRunListRequest request,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(request.Take, 1, 200);
        var query = db.ReconciliationRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId);
        if (request.ConnectionId is { } connectionId)
            query = query.Where(row => row.ConnectionId == connectionId);
        if (!string.IsNullOrWhiteSpace(request.Status))
            query = query.Where(row => row.Status == request.Status);
        if (Decode(request.Cursor) is { } cursor)
            query = query.Where(row => row.StartedAt < cursor.StartedAt
                || (row.StartedAt == cursor.StartedAt
                    && row.ReconciliationRunId.CompareTo(cursor.RunId) < 0));
        var rows = await query.Join(db.Connections.AsNoTracking(), run =>
                new { run.TenantId, run.ConnectionId }, connection =>
                new { connection.TenantId, connection.ConnectionId }, (run, connection) =>
                new RunProjection
                {
                    RunId = run.ReconciliationRunId,
                    ConnectionId = run.ConnectionId,
                    ConnectionName = connection.DisplayLabel,
                    RangeStart = run.RangeStart,
                    RangeEnd = run.RangeEnd,
                    Trigger = run.Trigger,
                    Status = run.Status,
                    StartedAt = run.StartedAt,
                    CompletedAt = run.CompletedAt,
                    MismatchCount = run.MismatchCount,
                    LocationMismatchCount = run.LocationMismatchCount,
                    RequestedBy = run.RequestedBy,
                }).OrderByDescending(row => row.StartedAt).ThenByDescending(row => row.RunId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var more = rows.Count > take;
        var names = await people.DisplayNamesAsync(rows.Take(take).Where(row => row.RequestedBy is not null)
            .Select(row => row.RequestedBy!).Distinct().ToList(), cancellationToken);
        var page = rows.Take(take).Select(row => View(row, names)).ToList();
        return new(page, more && page.Count != 0
            ? Encode(new(page[^1].StartedAt, page[^1].RunId)) : null);
    }

    public async Task<ReconciliationRunDetail?> DetailAsync(Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await db.ReconciliationRuns.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ReconciliationRunId == runId, cancellationToken);
        if (run is null) return null;
        var connectionName = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == run.ConnectionId).Select(row => row.DisplayLabel)
            .SingleAsync(cancellationToken);
        var entityRows = await db.ReconciliationEntityDayResults.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ReconciliationRunId == runId)
            .OrderBy(row => row.BusinessDate).ThenBy(row => row.Entity).ToListAsync(cancellationToken);
        var locations = await db.ReconciliationLocationResults.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ReconciliationRunId == runId)
            .OrderBy(row => row.LocationCode).ThenBy(row => row.ItemCode).ToListAsync(cancellationToken);
        var names = run.RequestedBy is null ? new Dictionary<string, string>()
            : await people.DisplayNamesAsync([run.RequestedBy], cancellationToken);
        return new(new(run.ReconciliationRunId, run.ConnectionId, connectionName, run.RangeStart,
                run.RangeEnd, run.Trigger, run.Status, run.StartedAt, run.CompletedAt,
                run.MismatchCount, run.LocationMismatchCount, RequestedByName(run.Trigger, run.RequestedBy, names)), run.Failure,
            entityRows.Select(row => new ReconciliationEntityDayView(row.ReconciliationEntityDayResultId,
                row.Entity, row.BusinessDate, row.SourceCount, row.ModuleCount, row.SourceQuantity,
                row.ModuleQuantity, row.SourceDistinctSerials, row.ModuleDistinctSerials,
                row.SourceKeyFieldHash, row.ModuleKeyFieldHash, row.Status, row.RangedReadSupported,
                row.BackfillJobId, row.DeadLetterItemId)).ToList(),
            locations.Select(row => new ReconciliationLocationView(row.ReconciliationLocationResultId,
                row.ItemCode, row.ItemName, row.LocationCode, row.LocationName, row.SourceOnHand,
                row.ModuleUnitCount, row.Status)).ToList());
    }

    private async Task<List<ReconciliationEntityDayResult>> CompareEntityDaysAsync(
        ReconciliationRun run, IConnectorReconciliationSource source,
        CancellationToken cancellationToken)
    {
        var sourceRows = await source.EntityDayDigestsAsync(run.ConnectionId, run.RangeStart,
            run.RangeEnd, cancellationToken);
        var moduleRows = await ModuleDigestsAsync(run.ConnectionId, run.RangeStart,
            run.RangeEnd, cancellationToken);
        var sourceByKey = sourceRows.ToDictionary(row => (row.Entity, row.BusinessDate));
        var moduleByKey = moduleRows.ToDictionary(row => (row.Entity, row.BusinessDate));
        var keys = sourceByKey.Keys.Union(moduleByKey.Keys)
            .OrderBy(key => key.BusinessDate).ThenBy(key => key.Entity, StringComparer.Ordinal).ToList();
        var results = new List<ReconciliationEntityDayResult>(keys.Count);
        foreach (var key in keys)
        {
            var sourceRow = sourceByKey.GetValueOrDefault(key) ?? Empty(key.Entity, key.BusinessDate);
            var moduleRow = moduleByKey.GetValueOrDefault(key) ?? Empty(key.Entity, key.BusinessDate);
            var clean = Equal(sourceRow, moduleRow);
            var ranged = !clean && await source.SupportsRangedReadsAsync(run.ConnectionId,
                key.Entity, cancellationToken);
            var result = new ReconciliationEntityDayResult
            {
                TenantId = tenant.TenantId,
                ReconciliationEntityDayResultId = TimeOrderedId.New(clock.UtcNow),
                ReconciliationRunId = run.ReconciliationRunId,
                ConnectionId = run.ConnectionId,
                Entity = key.Entity,
                BusinessDate = key.BusinessDate,
                SourceCount = sourceRow.Count,
                ModuleCount = moduleRow.Count,
                SourceQuantity = sourceRow.Quantity,
                ModuleQuantity = moduleRow.Quantity,
                SourceDistinctSerials = sourceRow.DistinctSerials,
                ModuleDistinctSerials = moduleRow.DistinctSerials,
                SourceKeyFieldHash = sourceRow.KeyFieldHash,
                ModuleKeyFieldHash = moduleRow.KeyFieldHash,
                Status = clean ? ReconciliationResultStatus.Clean : ReconciliationResultStatus.Mismatch,
                RangedReadSupported = ranged,
            };
            db.ReconciliationEntityDayResults.Add(result);
            results.Add(result);
            await db.SaveChangesAsync(cancellationToken);
            if (clean) continue;
            if (ranged)
            {
                var id = await backfills.ScheduleAsync(run.ConnectionId, key.Entity,
                    key.BusinessDate, key.BusinessDate, result.ReconciliationEntityDayResultId,
                    cancellationToken);
                result.BackfillJobId = id == Guid.Empty ? null : id;
            }
            else
            {
                var comparison = JsonSerializer.Serialize(new { source = sourceRow, module = moduleRow });
                result.DeadLetterItemId = await deadLetters.ParkReconciliationAsync(
                    result.ReconciliationEntityDayResultId, run.ConnectionId, key.Entity, comparison,
                    "The connector cannot read this mismatch as a range; operator attention is required.",
                    cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return results;
    }

    private async Task<List<ReconciliationLocationResult>> CompareLocationsAsync(
        ReconciliationRun run, IConnectorReconciliationSource source,
        CancellationToken cancellationToken)
    {
        var mappingJson = await db.ConnectionGatewaySettings.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ConnectionId == run.ConnectionId)
            .Select(row => row.OnHandStatusMappingJson).SingleAsync(cancellationToken);
        var statusMapping = JsonSerializer.Deserialize<Dictionary<string, bool>>(mappingJson)
            ?? throw new InvalidOperationException("The connection on-hand status mapping must be a JSON object.");
        var sourceRows = await source.OnHandAsync(new(run.ConnectionId, statusMapping), cancellationToken);
        var moduleRows = await serialisedOnHand.CountsAsync(cancellationToken);
        var sourceByKey = sourceRows.ToDictionary(row => (row.ItemId, row.LocationId));
        var moduleByKey = moduleRows.ToDictionary(row => (row.ItemId, row.LocationId));
        var keys = sourceByKey.Keys.Union(moduleByKey.Keys).ToList();
        var missingItemIds = keys.Select(key => key.ItemId).Distinct().ToList();
        var missingLocationIds = keys.Select(key => key.LocationId).Distinct().ToList();
        var itemLabels = await itemNames.NamesAsync(missingItemIds, cancellationToken);
        var locationLabels = await locationNames.NamesAsync(missingLocationIds, cancellationToken);
        var results = new List<ReconciliationLocationResult>(keys.Count);
        foreach (var key in keys)
        {
            var sourceRow = sourceByKey.GetValueOrDefault(key);
            var moduleCount = moduleByKey.GetValueOrDefault(key)?.UnitCount ?? 0;
            var item = itemLabels.GetValueOrDefault(key.ItemId);
            var location = locationLabels.GetValueOrDefault(key.LocationId);
            var sourceOnHand = sourceRow?.OnHand ?? 0;
            var result = new ReconciliationLocationResult
            {
                TenantId = tenant.TenantId,
                ReconciliationLocationResultId = TimeOrderedId.New(clock.UtcNow),
                ReconciliationRunId = run.ReconciliationRunId,
                ConnectionId = run.ConnectionId,
                ItemId = key.ItemId,
                LocationId = key.LocationId,
                ItemCode = sourceRow?.ItemCode ?? item?.Code ?? "UNRESOLVED ITEM",
                ItemName = sourceRow?.ItemName ?? item?.Name,
                LocationCode = sourceRow?.LocationCode ?? location?.Code ?? "UNRESOLVED LOCATION",
                LocationName = sourceRow?.LocationName ?? location?.Name,
                SourceOnHand = sourceOnHand,
                ModuleUnitCount = moduleCount,
                Status = sourceOnHand == moduleCount
                    ? ReconciliationResultStatus.Clean : ReconciliationResultStatus.Mismatch,
            };
            db.ReconciliationLocationResults.Add(result);
            results.Add(result);
        }
        return results;
    }

    private async Task<List<ConnectorEntityDayDigest>> ModuleDigestsAsync(Guid connectionId,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken)
    {
        // COUPLED to each connector's OccurredAt mapping (CX01: Cx01Mapper.Map): connectors carry the ERP's business date
        // as that calendar date at 00:00 UTC, and the source digests bucket by the same UTC date, so both sides agree
        // without a zone. Converting OccurredAt to a local zone here would move every date one day back west of UTC.
        // A connector that ever reports true instants must bucket them itself the same way on both sides.
        var start = rangeStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endExclusive = rangeEnd.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rows = await db.InboundMessages.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && row.IsLatest && row.OccurredAt >= start
                && row.OccurredAt < endExclusive)
            .Select(row => new MessageProjection
            {
                Entity = row.ErpEntity,
                ExternalId = row.ErpExternalId,
                CanonicalMessage = row.CanonicalMessage,
                OccurredAt = row.OccurredAt,
            }).ToListAsync(cancellationToken);
        return rows.GroupBy(row => new { row.Entity, Day = DateOnly.FromDateTime(row.OccurredAt) })
            .Select(group =>
            {
                var metrics = ReconciliationHash.Metrics(group.Select(row => row.CanonicalMessage));
                return new ConnectorEntityDayDigest(group.Key.Entity, group.Key.Day, group.LongCount(),
                    metrics.Quantity, metrics.DistinctSerials, ReconciliationHash.KeyFields(group.Select(row =>
                        (row.ExternalId, row.CanonicalMessage))));
            }).ToList();
    }

    private static ConnectorEntityDayDigest Empty(string entity, DateOnly day) =>
        new(entity, day, 0, 0, 0, ReconciliationHash.KeyFields([]));

    private static bool Equal(ConnectorEntityDayDigest left, ConnectorEntityDayDigest right) =>
        left.Count == right.Count && left.Quantity == right.Quantity
        && left.DistinctSerials == right.DistinctSerials
        && StringComparer.OrdinalIgnoreCase.Equals(left.KeyFieldHash, right.KeyFieldHash);

    private static ReconciliationRunRow View(RunProjection row, IReadOnlyDictionary<string, string> names) => new(
        row.RunId, row.ConnectionId, row.ConnectionName, row.RangeStart, row.RangeEnd, row.Trigger, row.Status,
        row.StartedAt, row.CompletedAt, row.MismatchCount, row.LocationMismatchCount,
        RequestedByName(row.Trigger, row.RequestedBy, names));

    // Never a raw user reference on screen: a name, "Scheduled", or "Not recorded" for runs from before the column.
    private static string RequestedByName(string trigger, string? requestedBy, IReadOnlyDictionary<string, string> names) =>
        requestedBy is null
            ? trigger == ReconciliationTrigger.Scheduled ? "Scheduled" : "Not recorded"
            : names.GetValueOrDefault(requestedBy) ?? "Unknown user";

    private static string Encode(Cursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"{cursor.StartedAt.Ticks}|{cursor.RunId:D}"));

    private static Cursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return parts.Length == 2 && long.TryParse(parts[0], out var ticks)
                && Guid.TryParse(parts[1], out var id) ? new(new DateTime(ticks, DateTimeKind.Utc), id) : null;
        }
        catch (FormatException) { return null; }
    }

    private sealed record Cursor(DateTime StartedAt, Guid RunId);

    private sealed class MessageProjection
    {
        public required string Entity { get; init; }
        public required string ExternalId { get; init; }
        public required string CanonicalMessage { get; init; }
        public DateTime OccurredAt { get; init; }
    }

    private sealed class RunProjection
    {
        public Guid RunId { get; init; }
        public Guid ConnectionId { get; init; }
        public required string ConnectionName { get; init; }
        public DateOnly RangeStart { get; init; }
        public DateOnly RangeEnd { get; init; }
        public required string Trigger { get; init; }
        public required string Status { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public int MismatchCount { get; init; }
        public string? RequestedBy { get; init; }
        public int LocationMismatchCount { get; init; }
    }
}

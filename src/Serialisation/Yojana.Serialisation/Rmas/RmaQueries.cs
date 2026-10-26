using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Paging;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Rmas;

public sealed class RmaQueries(SerialisationDbContext db, IIdentifierPolicySource identifierPolicies,
    IRmaFollowUpQueryPort followUps, ITenantContext tenant)
{
    private static readonly SortedPage<Rma, Guid> Pages = new("requested", row => row.RmaId,
        new SortColumn<Rma, string>("number", row => row.RmaNumber ?? ""),
        new SortColumn<Rma, DateTime>("requested", row => row.RequestedAt),
        new SortColumn<Rma, string>("status", row => row.Stage));
    private const string RmaDocumentType = "rma";

    public async Task<RmaListResult> ListAsync(RmaListFilter filter, DateTime now, string? sort, string? direction, string? after, int take,
        CancellationToken cancellationToken)
    {
        if (filter.Stage is not null && !RmaStage.All.Contains(filter.Stage)) throw new ArgumentException($"Unknown RMA stage '{filter.Stage}'.", nameof(filter));
        if (filter.MinimumAgeDays is < 0 || filter.MaximumAgeDays is < 0 || filter.MinimumAgeDays > filter.MaximumAgeDays)
            throw new ArgumentException("RMA age bounds must be non-negative and minimum cannot exceed maximum.", nameof(filter));
        var priorityOrder = string.IsNullOrWhiteSpace(sort) || string.Equals(sort, "priority", StringComparison.Ordinal);
        if (priorityOrder && !string.IsNullOrWhiteSpace(direction) && !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase))
            throw new CommandRefusedException("sort_not_supported", $"Sort direction '{direction}' is not supported.");
        if (take > SortedPage<Rma, Guid>.MaximumTake)
            throw new CommandRefusedException("page_size_too_large", $"Page size cannot exceed {SortedPage<Rma, Guid>.MaximumTake}.");
        if (take < 1) throw new CommandRefusedException("page_size_invalid", "Page size must be at least 1.");
        var paging = priorityOrder ? null : Pages.Parse(sort, direction, take, after);
        var tenantId = tenant.TenantId;
        var query = db.Rmas.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (filter.Stage is not null) query = query.Where(r => r.Stage == filter.Stage);
        if (filter.MinimumAgeDays is { } minimum) { var before = now.AddDays(-minimum); query = query.Where(r => r.RequestedAt <= before); }
        if (filter.MaximumAgeDays is { } maximum) { var youngerThan = now.AddDays(-(maximum + 1)); query = query.Where(r => r.RequestedAt > youngerThan); }
        if (filter.ExceptionsOnly) query = query.Where(r => r.HasException);
        if (!string.IsNullOrWhiteSpace(filter.Find))
        {
            var find = filter.Find.Trim();
            var parsed = ScanParser.Parse(find);
            var normalised = IdentifierNormaliser.Normalise(parsed.Cleaned,
                identifierPolicies.For(IdentifierType.InternalSerial)).Value;
            var rmaNumber = find.ToUpperInvariant();
            // Free text deliberately has a bounded candidate phase: both prefixes seek their own indexes and no
            // request can turn a broad serial/RMA prefix into an unbounded RMA sort at benchmark volume.
            var unitIds = parsed.Format == ScanFormat.Url ? [] : await db.Identifiers.AsNoTracking()
                .Where(i => i.TenantId == tenantId && i.NormalisedValue.StartsWith(normalised))
                .OrderBy(i => i.NormalisedValue).Select(i => i.UnitId).Distinct().Take(201).ToListAsync(cancellationToken);
            foreach (var alternative in parsed.Alternatives)
            {
                if (unitIds.Count > 0) break;
                var alternativeNormalised = IdentifierNormaliser.Normalise(alternative,
                    identifierPolicies.For(IdentifierType.InternalSerial)).Value;
                unitIds = await db.Identifiers.AsNoTracking()
                    .Where(i => i.TenantId == tenantId && i.NormalisedValue.StartsWith(alternativeNormalised))
                    .OrderBy(i => i.NormalisedValue).Select(i => i.UnitId).Distinct().Take(201)
                    .ToListAsync(cancellationToken);
            }
            var numberIds = parsed.Format == ScanFormat.Url ? [] : await db.Rmas.AsNoTracking().Where(r => r.TenantId == tenantId && r.RmaNumber != null
                    && r.RmaNumber.StartsWith(rmaNumber))
                .OrderBy(r => r.RmaNumber).Select(r => r.RmaId).Take(201).ToListAsync(cancellationToken);
            var unitRmaIds = unitIds.Count == 0 ? [] : await db.RmaLines.AsNoTracking()
                .Where(line => line.TenantId == tenantId && unitIds.Contains(line.RequestedUnitId))
                .OrderBy(line => line.RequestedUnitId).ThenBy(line => line.RmaId)
                .Select(line => line.RmaId).Distinct().Take(201).ToListAsync(cancellationToken);
            var candidateIds = numberIds.Concat(unitRmaIds).Distinct().Take(400).ToList();
            query = query.Where(r => candidateIds.Contains(r.RmaId));
        }

        if (priorityOrder && DecodePriorityCursor(after) is { } priorityCursor)
            query = priorityCursor.HasException
                ? query.Where(r => !r.HasException
                    || r.HasException && (r.RequestedAt > priorityCursor.RequestedAt
                        || r.RequestedAt == priorityCursor.RequestedAt && r.RmaId.CompareTo(priorityCursor.RmaId) > 0))
                : query.Where(r => !r.HasException && (r.RequestedAt > priorityCursor.RequestedAt
                    || r.RequestedAt == priorityCursor.RequestedAt && r.RmaId.CompareTo(priorityCursor.RmaId) > 0));

        // The established queue order is a three-column indexed business priority. SortedPage deliberately owns
        // all user-selected two-column orders; this compatibility seam alone preserves exceptions-first ordering.
        var bounded = await (priorityOrder
                ? query.OrderByDescending(r => r.HasException).ThenBy(r => r.RequestedAt).ThenBy(r => r.RmaId)
                : paging!.Apply(query))
            .Take(take + 1).ToListAsync(cancellationToken);
        var hasMore = bounded.Count > take;
        var raw = bounded.Take(take).ToList();
        var pageIds = raw.Select(r => r.RmaId).ToList();
        var pageLines = await db.RmaLines.AsNoTracking().Where(l => l.TenantId == tenantId && pageIds.Contains(l.RmaId))
            .OrderBy(l => l.Sequence).Select(l => new
            {
                l.RmaId, l.RequestedUnitId, l.ReceivedUnitId, l.ProvisionalUnitId, l.ReceiptOutcome, l.ConditionGrade, l.CoverageSource,
            }).ToListAsync(cancellationToken);
        var primaryLines = pageLines.GroupBy(l => l.RmaId).ToDictionary(g => g.Key, g => g.First());
        var lineCounts = pageLines.GroupBy(line => line.RmaId).ToDictionary(group => group.Key, group => group.Count());
        var primaryUnitIds = primaryLines.Values.Select(l => l.ReceivedUnitId ?? l.RequestedUnitId).Distinct().ToList();
        var serials = await UnitSerials.DisplayAsync(db, tenantId, primaryUnitIds, cancellationToken);
        var itemIds = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && primaryUnitIds.Contains(row.UnitId))
            .ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
        var rows = raw.Select(r =>
        {
            primaryLines.TryGetValue(r.RmaId, out var line);
            var unitId = line?.ReceivedUnitId ?? line?.RequestedUnitId;
            var updatedAt = r.ClosedAt ?? r.DispositionedAt ?? r.ReceivedAt ?? r.InTransitAt ?? r.AuthorisedAt ?? r.RequestedAt;
            return new RmaListRow(r.RmaId, r.RmaNumber, r.Stage, r.RequestDate, updatedAt,
                Math.Max(0, DateOnly.FromDateTime(now).DayNumber - r.RequestDate.DayNumber), r.ReturningPartyRef,
                lineCounts.GetValueOrDefault(r.RmaId), r.ExceptionCount,
                unitId, unitId is { } id ? serials.GetValueOrDefault(id) : null,
                unitId is { } itemUnit && itemIds.TryGetValue(itemUnit, out var itemId) ? itemId : null,
                line?.ReceiptOutcome, line?.ConditionGrade, line?.CoverageSource, r.ReceivedAt, r.RequestedBy, r.LastActorRef);
        }).ToList();
        var summary = await SummaryAsync(now, cancellationToken);
        var continuation = hasMore && raw.Count > 0
            ? priorityOrder
                ? EncodePriorityCursor(raw[^1].HasException, raw[^1].RequestedAt, raw[^1].RmaId)
                : paging!.CursorFor(raw[^1])
            : null;
        return new(rows, summary, continuation, hasMore, take);
    }

    public Task<RmaListResult> ListAsync(RmaListFilter filter, DateTime now, string? after, int take,
        CancellationToken cancellationToken) => ListAsync(filter, now, null, null, after, take, cancellationToken);

    public async Task<IReadOnlyList<RmaStageCount>> SummaryAsync(DateTime now, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId.Value;
        var counts = await db.Database.SqlQuery<RmaStageSummaryProjection>($"""
            SELECT counters.stage AS Stage,
                   counters.document_count AS DocumentCount,
                   counters.exception_count AS ExceptionCount,
                   CASE
                       WHEN exception_oldest.requested_at IS NULL THEN regular_oldest.request_date
                       WHEN regular_oldest.requested_at IS NULL THEN exception_oldest.request_date
                       WHEN exception_oldest.requested_at < regular_oldest.requested_at THEN exception_oldest.request_date
                       WHEN exception_oldest.requested_at > regular_oldest.requested_at THEN regular_oldest.request_date
                       WHEN exception_oldest.rma_id < regular_oldest.rma_id THEN exception_oldest.request_date
                       ELSE regular_oldest.request_date
                   END AS OldestRequestDate
            FROM (
                SELECT stage, SUM(document_count) AS document_count, SUM(exception_count) AS exception_count
                FROM serialisation.rma_stage_counter
                WHERE tenant_id = {tenantId}
                GROUP BY stage
            ) counters
            OUTER APPLY (
                SELECT TOP (1) r.request_date, r.requested_at, r.rma_id
                FROM serialisation.rma r
                WHERE r.tenant_id = {tenantId} AND r.stage = counters.stage
                  AND r.has_exception = CAST(1 AS bit)
                ORDER BY r.requested_at, r.rma_id
            ) exception_oldest
            OUTER APPLY (
                SELECT TOP (1) r.request_date, r.requested_at, r.rma_id
                FROM serialisation.rma r
                WHERE r.tenant_id = {tenantId} AND r.stage = counters.stage
                  AND r.has_exception = CAST(0 AS bit)
                ORDER BY r.requested_at, r.rma_id
            ) regular_oldest
            """).ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(now);
        return RmaStage.All.Select(stage =>
        {
            var count = counts.FirstOrDefault(x => x.Stage == stage);
            return new RmaStageCount(stage, count?.DocumentCount ?? 0,
                count?.OldestRequestDate is { } oldest ? Math.Max(0, today.DayNumber - oldest.DayNumber) : null,
                count?.ExceptionCount ?? 0);
        }).ToList();
    }

    private static string EncodePriorityCursor(bool hasException, DateTime requestedAt, Guid rmaId)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sort = "priority",
            direction = "asc",
            value = $"{(hasException ? 1 : 0)}|{requestedAt.Ticks}",
            tie = rmaId,
        });
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static RmaCursor? DecodePriorityCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 512) throw InvalidCursor();
        try
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var root = document.RootElement;
            if (root.GetProperty("sort").GetString() != "priority" || root.GetProperty("direction").GetString() != "asc")
                throw new CommandRefusedException("sort_changed", "The paging cursor belongs to another sort.", "return to the first page");
            var parts = root.GetProperty("value").GetString()?.Split('|');
            if (parts is null || parts.Length != 2 || parts[0] is not ("0" or "1") || !long.TryParse(parts[1], out var ticks)
                || !root.GetProperty("tie").TryGetGuid(out var rmaId))
                throw new FormatException();
            return new(parts[0] == "1", new DateTime(ticks, DateTimeKind.Utc), rmaId);
        }
        catch (CommandRefusedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw InvalidCursor();
        }
    }

    private static CommandRefusedException InvalidCursor() =>
        new("cursor_invalid", "The paging cursor is not valid.", "return to the first page");

    private sealed record RmaCursor(bool HasException, DateTime RequestedAt, Guid RmaId);
    private sealed class RmaStageSummaryProjection
    {
        public string Stage { get; set; } = "";
        public int DocumentCount { get; set; }
        public int ExceptionCount { get; set; }
        public DateOnly? OldestRequestDate { get; set; }
    }

    public async Task<RmaDetail?> DetailAsync(Guid rmaId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rma = await db.Rmas.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RmaId == rmaId, cancellationToken);
        if (rma is null) return null;
        var lines = await db.RmaLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.RmaId == rmaId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        var receipts = await db.ReturnReceipts.AsNoTracking().Where(receipt => receipt.TenantId == tenantId && receipt.RmaId == rmaId)
            .ToDictionaryAsync(receipt => receipt.RmaLineId, cancellationToken);
        var unitIds = lines.SelectMany(line => new[] { (Guid?)line.RequestedUnitId, line.AuthorisedUnitId, line.ReceivedUnitId })
            .OfType<Guid>().Distinct().ToList();
        var serials = await UnitSerials.DisplayAsync(db, tenantId, unitIds, cancellationToken);
        var items = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId))
            .ToDictionaryAsync(row => row.UnitId, row => row.ItemId, cancellationToken);
        var dispatched = rma.InTransitAt is not { } dispatchedAt ? null : await db.Events.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.DocumentType == RmaDocumentType
                && row.DocumentId == rmaId && row.EventTime == dispatchedAt && row.EventType == RmaEventType.Dispatched)
            .Select(row => new { row.EventTime, row.ActorRef, row.Attributes })
            .FirstOrDefaultAsync(cancellationToken);
        var dispatch = Dispatch(dispatched?.EventTime, dispatched?.ActorRef, dispatched?.Attributes);
        var openFollowUps = rma.Stage == RmaStage.Dispositioned
            ? await followUps.OpenAsync(rmaId, cancellationToken)
            : [];
        return new RmaDetail(rma.RmaId, rma.RmaNumber, rma.Stage, rma.RequestDate, rma.ReturningPartyRef, rma.SettlementPartyRef,
            rma.ServiceEntityId, rma.ServiceCountry, rma.Currency, rma.RequestedAt, rma.AuthorisedAt, rma.InTransitAt,
            rma.ReceivedAt, rma.DispositionedAt, rma.ClosedAt, rma.RequestedBy, rma.LastActorRef, lines.Select(l =>
            {
                receipts.TryGetValue(l.RmaLineId, out var receipt);
                return new RmaDetailLine(l.RmaLineId, l.Sequence, l.RequestedUnitId, l.AuthorisedUnitId, l.OwnershipEpisodeId,
                    new DocumentRef(l.OriginalSaleDocumentType, l.OriginalSaleDocumentId, l.OriginalSaleDocumentLine), l.ParentUnitId,
                    l.PriceAllocation, l.ParentIncomplete, l.CoveragePct, l.ApprovedAmount, l.CoverageSource, l.EntitlementId,
                    l.DenialReason, l.ReceivedUnitId, l.ProvisionalUnitId, l.ReceiptOutcome, l.ConditionGrade, l.ValuationRule,
                    l.Disposition, l.ReplacementIntentId, l.EntitlementClosedReason, l.Description,
                    items.TryGetValue(l.RequestedUnitId, out var requestedItem) ? requestedItem : null, serials.GetValueOrDefault(l.RequestedUnitId),
                    l.AuthorisedUnitId is { } authorised ? serials.GetValueOrDefault(authorised) : null,
                    l.ReceivedUnitId is { } received && items.TryGetValue(received, out var receivedItem) ? receivedItem : null,
                    l.ReceivedUnitId is { } receivedId ? serials.GetValueOrDefault(receivedId) : null,
                    receipt?.BusinessTime, receipt?.PostingPeriod, receipt?.ReceivedBy, receipt?.ReceivingLocationId, receipt?.ExceptionNote);
            }).ToList(), dispatch, openFollowUps, DocumentVersion.Encode(rma.RowVersion));
    }

    private static RmaDispatchDetail? Dispatch(DateTime? at, string? actor, string? attributes)
    {
        if (at is null || actor is null) return null;
        if (attributes is null) return new(at.Value, null, null, actor);
        using var json = JsonDocument.Parse(attributes);
        var root = json.RootElement;
        return new(at.Value, Text(root, "carrier_name"), Text(root, "tracking_reference"), actor);
    }

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

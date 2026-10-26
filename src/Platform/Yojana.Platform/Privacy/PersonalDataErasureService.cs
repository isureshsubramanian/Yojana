using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Privacy;

public sealed record PreviewPersonalDataErasure(
    string SubjectKind,
    Guid SubjectId,
    string RequestReference,
    DateOnly RequestReceivedOn,
    string Reason,
    bool SubjectIsNaturalPerson);

public sealed record ConfirmPersonalDataErasure(
    string SubjectKind,
    Guid SubjectId,
    string RequestReference,
    DateOnly RequestReceivedOn,
    string Confirmation,
    string Reason,
    bool SubjectIsNaturalPerson,
    IReadOnlyList<PersonalDataReviewDecision> ReviewDecisions);

public sealed record PersonalDataErasurePreviewResponse(
    string SubjectKind,
    Guid SubjectId,
    string Token,
    IReadOnlyList<PersonalDataStorePreview> Counts,
    IReadOnlyList<PersonalDataReviewItem> ReviewItems,
    string AccuracyNote);

public sealed record PersonalDataStorePreview(string Store, string Kind, int Count, string Action);

public sealed record PersonalDataErasureRow(
    Guid ErasureId,
    string SubjectKind,
    Guid SubjectId,
    string Token,
    string RequestReference,
    string RequestedBy,
    DateOnly RequestReceivedOn,
    string Status,
    DateTime RequestedAt,
    DateTime? CompletedAt,
    string? FailureRule);

public sealed record PersonalDataErasureOldestOpen(
    DateOnly ReceivedOn,
    DateOnly DueOn,
    int AgeDays,
    string Warning);

public sealed record PersonalDataErasureSummary(
    int CompletedLastTwelveMonths,
    int InProgress,
    int Refused,
    PersonalDataErasureOldestOpen? OldestOpen);

public sealed record PersonalDataErasurePage(
    IReadOnlyList<PersonalDataErasureRow> Items,
    string? NextCursor,
    PersonalDataErasureSummary Summary);

public sealed record PersonalDataErasureDetail(
    Guid ErasureId,
    string SubjectKind,
    Guid SubjectId,
    string Token,
    string RequestReference,
    string Reason,
    string RequestedBy,
    DateOnly RequestReceivedOn,
    DateTime RequestedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? FailedAt,
    string Status,
    string RetentionBasis,
    IReadOnlyList<ErasureStoreCountRow> Counts,
    string? FailureRule,
    string? FailureDetail,
    IReadOnlyList<PersonalDataErasureBlocker> Blockers);

public sealed record ErasureStoreCountRow(string Store, string Kind, int PreviewCount, int ErasedCount,
    string Action, string Status, DateTime? CompletedAt);

public sealed record ExecutePersonalDataErasure(TenantId TenantId, Guid ErasureId, bool ReplayCompleted = false);
public sealed record ReplayCompletedPersonalDataErasures(TenantId TenantId);

public sealed class PersonalDataErasureService(
    PlatformDbContext db,
    ITenantContext tenant,
    IClock clock,
    IUserDirectory users,
    IEnumerable<IPersonalDataHolder> holders)
{
    public const string RetentionBasis = "Serial and warranty records are retained as pseudonymous references for legal obligations and legal claims.";

    public async Task<PersonalDataErasurePreviewResponse> PreviewAsync(PreviewPersonalDataErasure request,
        CancellationToken cancellationToken)
        => await PreviewCoreAsync(request, cancellationToken);

    public async Task<PersonalDataErasurePreviewResponse> PreviewAsync(PreviewPersonalDataErasure request,
        Guid actor, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        try
        {
            return await PreviewCoreAsync(request, cancellationToken);
        }
        catch (CommandRefusedException exception)
        {
            await SaveRefusalAsync(request, actor, exception, cancellationToken);
            throw;
        }
    }

    private async Task<PersonalDataErasurePreviewResponse> PreviewCoreAsync(PreviewPersonalDataErasure request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var kind = SubjectKind(request.SubjectKind);
        if (kind == ErasureSubjectKind.Party && !request.SubjectIsNaturalPerson)
            throw new CommandRefusedException("erasure_not_natural_person",
                "Party erasure requires confirmation that the subject is a natural person.",
                "confirm that the party is an individual");
        var subject = new ErasureSubject(kind, request.SubjectId, [], []);
        var previews = await PreviewHoldersAsync(subject, cancellationToken);
        var known = previews.SelectMany(x => x.Preview.KnownValues).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToList();
        if (known.Count > 0)
        {
            subject = subject with { KnownValues = known };
            previews = await PreviewHoldersAsync(subject, cancellationToken);
        }
        return new(kind.ToString(), subject.SubjectId, subject.Token,
            previews.SelectMany(x => x.Preview.Counts.Select(count => new PersonalDataStorePreview(
                x.Store, count.Kind, count.Count, count.Action))).ToList(),
            previews.SelectMany(x => x.Preview.ReviewItems).OrderBy(x => x.Store, StringComparer.Ordinal)
                .ThenBy(x => x.ItemKey, StringComparer.Ordinal).ToList(),
            "Exact-value matching is a floor, not a guarantee; review every linked free-text item.");
    }

    private static void ValidateRequest(PreviewPersonalDataErasure request)
    {
        ValidateText(request.RequestReference, 120, "request reference");
        ValidateText(request.Reason, 1000, "reason");
        if (request.SubjectId == Guid.Empty)
            throw new CommandRefusedException("erasure_subject_required", "Choose the customer or user to erase.");
        if (request.RequestReceivedOn == default)
            throw new CommandRefusedException("erasure_request_received_date_invalid",
                "The date the erasure request was received is required.");
        _ = SubjectKind(request.SubjectKind);
    }

    public async Task<PersonalDataErasureDetail> ConfirmAsync(ConfirmPersonalDataErasure request, Guid actor,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.RequestReference, request.Confirmation, StringComparison.Ordinal))
            throw new CommandRefusedException("erasure_confirmation_mismatch",
                "The typed confirmation must repeat the request reference exactly.",
                "type the request reference exactly as shown");
        var previewRequest = new PreviewPersonalDataErasure(request.SubjectKind, request.SubjectId,
            request.RequestReference, request.RequestReceivedOn, request.Reason, request.SubjectIsNaturalPerson);
        var preview = await PreviewAsync(previewRequest, actor, cancellationToken);
        var expected = preview.ReviewItems.Select(x => x.ItemKey).ToHashSet(StringComparer.Ordinal);
        var decisions = request.ReviewDecisions ?? [];
        var supplied = decisions.Select(x => x.ItemKey).ToList();
        if (supplied.Count != supplied.Distinct(StringComparer.Ordinal).Count()
            || !expected.SetEquals(supplied)
            || decisions.Any(x => !Enum.IsDefined(x.Action)))
            throw new CommandRefusedException("erasure_review_decisions_incomplete",
                "Every preview review item needs exactly one redact or keep choice.",
                "run the preview again and decide every listed item");

        var kind = SubjectKind(request.SubjectKind);
        var now = clock.UtcNow;
        var reference = request.RequestReference.Trim();
        var row = await db.ErasureRequests.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.RequestReference == reference, cancellationToken);
        if (row is not null && (row.Status != ErasureRequestStatus.Refused
            || row.SubjectKind != kind || row.SubjectId != request.SubjectId))
            throw new CommandRefusedException("erasure_request_reference_duplicate",
                "The request reference is already used by another erasure request.",
                "use the existing request or enter a different request reference");
        if (row is null)
        {
            row = new ErasureRequest
            {
                TenantId = tenant.TenantId,
                ErasureId = TimeOrderedId.New(now),
                SubjectKind = kind,
                SubjectId = request.SubjectId,
                RequestReference = reference,
                Reason = request.Reason.Trim(),
                RequestReceivedOn = request.RequestReceivedOn,
                RequestedBy = actor,
                RequestedAt = now,
                Status = ErasureRequestStatus.Pending,
                RetentionBasis = RetentionBasis,
                ReviewDecisionsJson = JsonSerializer.Serialize(decisions),
            };
            db.ErasureRequests.Add(row);
        }
        else
        {
            row.Reason = request.Reason.Trim();
            row.RequestReceivedOn = request.RequestReceivedOn;
            row.RequestedBy = actor;
            row.RequestedAt = now;
            row.StartedAt = null;
            row.CompletedAt = null;
            row.FailedAt = null;
            row.Status = ErasureRequestStatus.Pending;
            row.ReviewDecisionsJson = JsonSerializer.Serialize(decisions);
            row.FailureRule = null;
            row.FailureDetail = null;
            row.RefusalRecordsJson = "[]";
            var previousCounts = await db.ErasureStoreCounts.Where(x => x.TenantId == tenant.TenantId
                && x.ErasureId == row.ErasureId).ToListAsync(cancellationToken);
            db.ErasureStoreCounts.RemoveRange(previousCounts);
        }
        foreach (var group in preview.Counts.GroupBy(x => new { x.Store, x.Kind }))
        {
            db.ErasureStoreCounts.Add(new ErasureStoreCount
            {
                TenantId = tenant.TenantId, ErasureId = row.ErasureId, Store = group.Key.Store,
                Kind = group.Key.Kind, PreviewCount = group.Sum(x => x.Count), Action = group.First().Action,
                Status = ErasureStoreStatus.Previewed,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        // HIDDEN COUPLING (ADR-0017): this tombstone commit must precede the holder job. The integration gateway reads
        // it on every party upsert; publishing first or moving this save would allow a sync to restore personal data.
        await bus.PublishAsync(new ExecutePersonalDataErasure(tenant.TenantId, row.ErasureId),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        return await DetailAsync(row.ErasureId, cancellationToken)
            ?? throw new InvalidOperationException("The saved erasure request was not readable.");
    }

    public Task<PersonalDataErasurePage> ListAsync(string? cursor, int? take,
        CancellationToken cancellationToken) =>
        ListAsync(null, null, cursor, take, cancellationToken);

    public async Task<PersonalDataErasurePage> ListAsync(string? sort, string? direction, string? cursor, int? take,
        CancellationToken cancellationToken)
    {
        var paging = PrivacyEndpoints.Pages.Parse(sort, direction, take, cursor);
        var rows = await paging.Apply(db.ErasureRequests.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId))
            .Take(paging.Take + 1).ToListAsync(cancellationToken);
        var pageRows = rows.Take(paging.Take).ToList();
        var requesterReferences = pageRows.Select(x => x.RequestedBy.ToString("D")).Distinct().ToList();
        var requesterNames = await users.DisplayNamesAsync(requesterReferences, cancellationToken);
        var page = pageRows.Select(x =>
        {
            var subject = new ErasureSubject(x.SubjectKind, x.SubjectId, [], []);
            var requester = x.RequestedBy.ToString("D");
            return new PersonalDataErasureRow(x.ErasureId, x.SubjectKind.ToString(), x.SubjectId, subject.Token,
                x.RequestReference, requesterNames.GetValueOrDefault(requester) ?? "Unavailable user",
                x.RequestReceivedOn, x.Status, x.RequestedAt, x.CompletedAt, x.FailureRule);
        }).ToList();
        return new(page, rows.Count > paging.Take && pageRows.Count > 0
            ? paging.CursorFor(pageRows[^1]) : null,
            await SummaryAsync(cancellationToken));
    }

    public async Task<PersonalDataErasureDetail?> DetailAsync(Guid erasureId,
        CancellationToken cancellationToken)
    {
        var row = await db.ErasureRequests.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ErasureId == erasureId, cancellationToken);
        if (row is null) return null;
        var counts = await db.ErasureStoreCounts.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ErasureId == erasureId).OrderBy(x => x.Store).ThenBy(x => x.Kind)
            .Select(x => new ErasureStoreCountRow(x.Store, x.Kind, x.PreviewCount, x.ErasedCount, x.Action,
                x.Status, x.CompletedAt)).ToListAsync(cancellationToken);
        var requesterReference = row.RequestedBy.ToString("D");
        var requesterNames = await users.DisplayNamesAsync([requesterReference], cancellationToken);
        var subject = new ErasureSubject(row.SubjectKind, row.SubjectId, [], []);
        var blockers = JsonSerializer.Deserialize<List<PersonalDataErasureBlocker>>(row.RefusalRecordsJson) ?? [];
        return new(row.ErasureId, row.SubjectKind.ToString(), row.SubjectId, subject.Token,
            row.RequestReference, row.Reason,
            requesterNames.GetValueOrDefault(requesterReference) ?? "Unavailable user",
            row.RequestReceivedOn, row.RequestedAt, row.StartedAt, row.CompletedAt, row.FailedAt, row.Status,
            row.RetentionBasis, counts, row.FailureRule, row.FailureDetail, blockers);
    }

    public async Task ExportCsvAsync(Stream output, string? sort, string? direction,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        await WriteCsvRowAsync(writer,
            ["request_reference", "subject", "kind", "request_received_on", "recorded_at", "status", "refusal_rule"],
            cancellationToken);
        var paging = PrivacyEndpoints.Pages.Parse(sort, direction, 50);
        var rows = paging.Apply(db.ErasureRequests.AsNoTracking().Where(x => x.TenantId == tenant.TenantId))
            .Select(x => new
            {
                x.SubjectKind, x.SubjectId, x.RequestReference, x.RequestReceivedOn,
                x.RequestedAt, x.Status, x.FailureRule,
            }).AsAsyncEnumerable();
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            var token = new ErasureSubject(row.SubjectKind, row.SubjectId, [], []).Token;
            await WriteCsvRowAsync(writer,
                [row.RequestReference, token, row.SubjectKind.ToString(), row.RequestReceivedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    row.RequestedAt.ToString("O", CultureInfo.InvariantCulture), row.Status, row.FailureRule], cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }

    private async Task<PersonalDataErasureSummary> SummaryAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var since = clock.UtcNow.AddMonths(-12);
        var completed = await db.ErasureRequests.AsNoTracking().CountAsync(x => x.TenantId == tenantId
            && x.Status == ErasureRequestStatus.Completed && x.CompletedAt >= since, cancellationToken);
        var inProgress = await db.ErasureRequests.AsNoTracking().CountAsync(x => x.TenantId == tenantId
            && (x.Status == ErasureRequestStatus.Pending || x.Status == ErasureRequestStatus.Running), cancellationToken);
        var refused = await db.ErasureRequests.AsNoTracking().CountAsync(x => x.TenantId == tenantId
            && x.Status == ErasureRequestStatus.Refused, cancellationToken);
        var oldest = await db.ErasureRequests.AsNoTracking().Where(x => x.TenantId == tenantId
                && (x.Status == ErasureRequestStatus.Pending || x.Status == ErasureRequestStatus.Running
                    || x.Status == ErasureRequestStatus.Failed))
            .OrderBy(x => x.RequestReceivedOn).Select(x => (DateOnly?)x.RequestReceivedOn)
            .FirstOrDefaultAsync(cancellationToken);
        PersonalDataErasureOldestOpen? oldestOpen = null;
        if (oldest is { } received)
        {
            var today = DateOnly.FromDateTime(clock.UtcNow);
            var due = received.AddMonths(1);
            var warning = due < today ? "overdue" : due <= today.AddDays(7) ? "due_soon" : "none";
            oldestOpen = new(received, due, Math.Max(0, today.DayNumber - received.DayNumber), warning);
        }
        return new(completed, inProgress, refused, oldestOpen);
    }

    private async Task SaveRefusalAsync(PreviewPersonalDataErasure request, Guid actor,
        CommandRefusedException exception, CancellationToken cancellationToken)
    {
        var reference = request.RequestReference.Trim();
        var kind = SubjectKind(request.SubjectKind);
        var row = await db.ErasureRequests.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.RequestReference == reference, cancellationToken);
        if (row is not null && (row.Status != ErasureRequestStatus.Refused
            || row.SubjectKind != kind || row.SubjectId != request.SubjectId)) return;
        var now = clock.UtcNow;
        if (row is null)
        {
            row = new ErasureRequest
            {
                TenantId = tenant.TenantId, ErasureId = TimeOrderedId.New(now), SubjectKind = kind,
                SubjectId = request.SubjectId, RequestReference = reference, Reason = request.Reason.Trim(),
                RequestReceivedOn = request.RequestReceivedOn, RequestedBy = actor, RequestedAt = now,
                Status = ErasureRequestStatus.Refused, RetentionBasis = RetentionBasis,
            };
            db.ErasureRequests.Add(row);
        }
        else
        {
            row.Reason = request.Reason.Trim();
            row.RequestReceivedOn = request.RequestReceivedOn;
            row.RequestedBy = actor;
            row.RequestedAt = now;
        }
        row.Status = ErasureRequestStatus.Refused;
        row.FailureRule = exception.Rule;
        row.FailureDetail = exception.Message;
        row.RefusalRecordsJson = JsonSerializer.Serialize(
            exception is PersonalDataErasureRefusedException refusal ? refusal.Blockers : []);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task WriteCsvRowAsync(TextWriter writer, IReadOnlyList<string?> values,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0) await writer.WriteAsync(',');
            var value = values[index] ?? "";
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
                value = '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
            await writer.WriteAsync(value.AsMemory(), cancellationToken);
        }
        await writer.WriteAsync("\r\n".AsMemory(), cancellationToken);
    }

    private async Task<List<(string Store, PersonalDataPreview Preview)>> PreviewHoldersAsync(
        ErasureSubject subject, CancellationToken cancellationToken)
    {
        var results = new List<(string, PersonalDataPreview)>();
        foreach (var holder in holders.OrderBy(x => HolderOrder(x.Store)).ThenBy(x => x.Store, StringComparer.Ordinal))
            results.Add((holder.Store, await holder.PreviewAsync(subject, cancellationToken)));
        return results;
    }

    private static int HolderOrder(string store) => store switch
    {
        "knowledge" => 0,
        "serialisation" => 1,
        "integration" => 2,
        "platform" => 3,
        _ => 10,
    };

    private static ErasureSubjectKind SubjectKind(string value) => value?.ToUpperInvariant() switch
    {
        "PARTY" => ErasureSubjectKind.Party,
        "USER" => ErasureSubjectKind.User,
        _ => throw new CommandRefusedException("erasure_subject_kind_invalid", "Subject kind must be Party or User."),
    };

    private static void ValidateText(string value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum)
            throw new CommandRefusedException($"erasure_{label.Replace(' ', '_')}_invalid",
                $"The {label} is required and must be at most {maximum} characters.");
    }

}

using System.Text;
using System.Text.Json;
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
    string Reason,
    bool SubjectIsNaturalPerson);

public sealed record ConfirmPersonalDataErasure(
    string SubjectKind,
    Guid SubjectId,
    string RequestReference,
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
    string RequestReference,
    string Status,
    DateTime RequestedAt,
    DateTime? CompletedAt);

public sealed record PersonalDataErasurePage(IReadOnlyList<PersonalDataErasureRow> Items, string? NextCursor);

public sealed record PersonalDataErasureDetail(
    Guid ErasureId,
    string SubjectKind,
    Guid SubjectId,
    string RequestReference,
    string Reason,
    string RequestedBy,
    DateTime RequestedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? FailedAt,
    string Status,
    string RetentionBasis,
    IReadOnlyList<ErasureStoreCountRow> Counts,
    string? FailureRule,
    string? FailureDetail);

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
    {
        ValidateText(request.RequestReference, 120, "request reference");
        ValidateText(request.Reason, 1000, "reason");
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

    public async Task<PersonalDataErasureDetail> ConfirmAsync(ConfirmPersonalDataErasure request, Guid actor,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.RequestReference, request.Confirmation, StringComparison.Ordinal))
            throw new CommandRefusedException("erasure_confirmation_mismatch",
                "The typed confirmation must repeat the request reference exactly.",
                "type the request reference exactly as shown");
        var preview = await PreviewAsync(new(request.SubjectKind, request.SubjectId, request.RequestReference,
            request.Reason, request.SubjectIsNaturalPerson), cancellationToken);
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
        var row = new ErasureRequest
        {
            TenantId = tenant.TenantId,
            ErasureId = TimeOrderedId.New(now),
            SubjectKind = kind,
            SubjectId = request.SubjectId,
            RequestReference = request.RequestReference.Trim(),
            Reason = request.Reason.Trim(),
            RequestedBy = actor,
            RequestedAt = now,
            Status = ErasureRequestStatus.Pending,
            RetentionBasis = RetentionBasis,
            ReviewDecisionsJson = JsonSerializer.Serialize(decisions),
        };
        db.ErasureRequests.Add(row);
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

    public async Task<PersonalDataErasurePage> ListAsync(string? cursor, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        var after = DecodeCursor(cursor);
        var query = db.ErasureRequests.AsNoTracking().Where(x => x.TenantId == tenant.TenantId);
        if (after is not null)
            query = query.Where(x => x.RequestedAt < after.Value.At
                || (x.RequestedAt == after.Value.At && x.ErasureId.CompareTo(after.Value.Id) < 0));
        var rows = await query.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.ErasureId)
            .Take(take + 1).Select(x => new PersonalDataErasureRow(x.ErasureId, x.SubjectKind.ToString(),
                x.SubjectId, x.RequestReference, x.Status, x.RequestedAt, x.CompletedAt))
            .ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        return new(page, rows.Count > take && page.Count > 0
            ? EncodeCursor(page[^1].RequestedAt, page[^1].ErasureId) : null);
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
        return new(row.ErasureId, row.SubjectKind.ToString(), row.SubjectId, row.RequestReference, row.Reason,
            requesterNames.GetValueOrDefault(requesterReference) ?? "Unavailable user",
            row.RequestedAt, row.StartedAt, row.CompletedAt, row.FailedAt, row.Status,
            row.RetentionBasis, counts, row.FailureRule, row.FailureDetail);
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

    private static string EncodeCursor(DateTime at, Guid id) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes($"{at:O}\n{id:D}"));

    private static (DateTime At, Guid Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
            if (parts.Length != 2) throw new FormatException();
            return (DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind),
                Guid.Parse(parts[1]));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new CommandRefusedException("erasure_cursor_invalid", "The erasure cursor is invalid.");
        }
    }
}

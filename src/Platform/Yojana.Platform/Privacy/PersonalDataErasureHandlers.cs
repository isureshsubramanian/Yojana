using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Privacy;

public static class ExecutePersonalDataErasureHandler
{
    public static async Task Handle(ExecutePersonalDataErasure command, IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(command, scopes, cancellationToken);
        }
        catch (Exception exception)
        {
            await using var failedScope = scopes.CreateAsyncScope();
            failedScope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(command.TenantId);
            var failedDb = failedScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var failed = await failedDb.ErasureRequests.SingleOrDefaultAsync(x =>
                x.TenantId == command.TenantId && x.ErasureId == command.ErasureId, cancellationToken);
            if (failed is not null)
            {
                failed.Status = ErasureRequestStatus.Failed;
                failed.FailedAt = failedScope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                failed.FailureRule = exception is CommandRefusedException refusal ? refusal.Rule : "erasure_execution_failed";
                failed.FailureDetail = exception is CommandRefusedException
                    ? exception.Message
                    : "The holder step failed; see application diagnostics.";
                await failedDb.SaveChangesAsync(cancellationToken);
            }
            throw;
        }
    }

    private static async Task ExecuteAsync(ExecutePersonalDataErasure command, IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        ErasureSubject subject;
        await using (var startScope = scopes.CreateAsyncScope())
        {
            startScope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(command.TenantId);
            var db = startScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = await db.ErasureRequests.SingleOrDefaultAsync(x => x.TenantId == command.TenantId
                && x.ErasureId == command.ErasureId, cancellationToken);
            if (row is null || row.Status == ErasureRequestStatus.Completed && !command.ReplayCompleted) return;
            row.Status = ErasureRequestStatus.Running;
            row.StartedAt ??= startScope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            row.FailedAt = null;
            row.FailureRule = null;
            row.FailureDetail = null;
            var decisions = JsonSerializer.Deserialize<List<PersonalDataReviewDecision>>(row.ReviewDecisionsJson) ?? [];
            subject = new(row.SubjectKind, row.SubjectId, [], decisions);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Gather exact values before any holder runs. A second pass lets free-text holders use names and contacts
        // discovered by the authoritative Platform or Integration holder without either module reading the other.
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var holderName in HolderNames)
        {
            await using var previewScope = scopes.CreateAsyncScope();
            previewScope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(command.TenantId);
            var holder = previewScope.ServiceProvider.GetServices<IPersonalDataHolder>()
                .SingleOrDefault(x => x.Store == holderName);
            if (holder is null) continue;
            var preview = await holder.PreviewAsync(subject, cancellationToken);
            foreach (var value in preview.KnownValues.Where(x => !string.IsNullOrWhiteSpace(x))) known.Add(value);
        }
        subject = subject with { KnownValues = known.ToList() };

        foreach (var holderName in HolderNames)
        {
            // TIGHT COUPLING (ADR-0017 certificate redaction): the Serialisation holder sets
            // SESSION_CONTEXT(N'yojana.erasure_id') to this in-progress Platform row. A trigger exception is the only
            // safe seam through certificate immutability; renaming the key or running the holder before this row exists
            // makes redaction fail closed. The matching comment is in Erase01CertificateRedaction and the holder.
            await using var holderScope = scopes.CreateAsyncScope();
            holderScope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(command.TenantId);
            var db = holderScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var alreadyCompleted = !command.ReplayCompleted && await db.ErasureStoreCounts.AsNoTracking()
                .Where(x => x.TenantId == command.TenantId && x.ErasureId == command.ErasureId
                    && x.Store == holderName).AnyAsync(cancellationToken)
                && await db.ErasureStoreCounts.AsNoTracking().Where(x => x.TenantId == command.TenantId
                    && x.ErasureId == command.ErasureId && x.Store == holderName)
                    .AllAsync(x => x.Status == ErasureStoreStatus.Completed, cancellationToken);
            if (alreadyCompleted) continue;
            var holder = holderScope.ServiceProvider.GetServices<IPersonalDataHolder>()
                .SingleOrDefault(x => x.Store == holderName);
            if (holder is null) continue;
            var result = await holder.EraseAsync(subject, command.ErasureId, cancellationToken);
            var now = holderScope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var rows = await db.ErasureStoreCounts.Where(x => x.TenantId == command.TenantId
                && x.ErasureId == command.ErasureId && x.Store == holderName).ToListAsync(cancellationToken);
            foreach (var count in result.Counts)
            {
                var row = rows.SingleOrDefault(x => x.Kind == count.Kind);
                if (row is null)
                {
                    row = new ErasureStoreCount
                    {
                        TenantId = command.TenantId, ErasureId = command.ErasureId, Store = holderName,
                        Kind = count.Kind, PreviewCount = count.Count, Action = count.Action,
                        Status = ErasureStoreStatus.Previewed,
                    };
                    db.ErasureStoreCounts.Add(row);
                    rows.Add(row);
                }
                row.ErasedCount = count.Count;
                row.Status = ErasureStoreStatus.Completed;
                row.CompletedAt = now;
            }
            if (rows.Count == 0)
            {
                db.ErasureStoreCounts.Add(new ErasureStoreCount
                {
                    TenantId = command.TenantId, ErasureId = command.ErasureId, Store = holderName,
                    Kind = "none", PreviewCount = 0, ErasedCount = 0, Action = "no matching data",
                    Status = ErasureStoreStatus.Completed, CompletedAt = now,
                });
            }
            else
            {
                foreach (var row in rows.Where(x => x.Status != ErasureStoreStatus.Completed))
                {
                    row.Status = ErasureStoreStatus.Completed;
                    row.CompletedAt = now;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        await using var completeScope = scopes.CreateAsyncScope();
        completeScope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(command.TenantId);
        var completeDb = completeScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var completed = await completeDb.ErasureRequests.SingleAsync(x => x.TenantId == command.TenantId
            && x.ErasureId == command.ErasureId, cancellationToken);
        completed.Reason = ReplaceKnownValues(completed.Reason, known, subject.Token);
        completed.Status = ErasureRequestStatus.Completed;
        completed.CompletedAt = completeScope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        await completeDb.SaveChangesAsync(cancellationToken);
    }

    private static readonly string[] HolderNames = ["knowledge", "serialisation", "integration", "platform"];

    private static string ReplaceKnownValues(string value, IEnumerable<string> knownValues, string token)
    {
        foreach (var known in knownValues.Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal).OrderByDescending(x => x.Length))
            value = value.Replace(known, token, StringComparison.Ordinal);
        return value;
    }
}

public static class ReplayCompletedPersonalDataErasuresHandler
{
    public static async Task Handle(ReplayCompletedPersonalDataErasures command, IServiceScopeFactory scopes,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(command.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var ids = await db.ErasureRequests.AsNoTracking().Where(x => x.TenantId == command.TenantId
                && x.Status == ErasureRequestStatus.Completed).OrderBy(x => x.RequestedAt)
            .Select(x => x.ErasureId).ToListAsync(cancellationToken);
        foreach (var id in ids)
            await bus.PublishAsync(new ExecutePersonalDataErasure(command.TenantId, id, true),
                new DeliveryOptions { TenantId = command.TenantId.ToString() });
    }
}

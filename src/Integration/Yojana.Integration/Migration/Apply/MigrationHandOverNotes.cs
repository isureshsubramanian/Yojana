using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Apply;

/// <summary>
/// Answers Connection health's hand-over note (P9-T34, OD-60): the latest final load of the connection that still
/// stands and moved its watermarks. The caller shows it only while the connection is stopped.
/// </summary>
public sealed class MigrationHandOverNotes(IntegrationDbContext db, ITenantContext tenant) : IConnectionHandOverNotes
{
    public async Task<ConnectionHandOverNote?> ForAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var from = await (from run in db.Set<MigrationApplyRun>().AsNoTracking()
                          join batch in db.MigrationBatches.AsNoTracking()
                              on new { run.TenantId, run.BatchId } equals new { batch.TenantId, batch.BatchId }
                          where run.TenantId == tenant.TenantId && batch.ConnectionId == connectionId &&
                                run.Kind == ApplyRunKind.Initial && !run.Rehearsal &&
                                run.Status == ApplyRunStatus.Completed && run.WatermarkHandedOverFrom != null
                          orderby run.FinishedAt descending
                          select run.WatermarkHandedOverFrom).FirstOrDefaultAsync(cancellationToken);
        return from is not { } start ? null : new(start, string.Create(CultureInfo.InvariantCulture,
            $"The final migration load handed this connection over: when the sync is resumed it reads source changes from {start:d MMM yyyy HH:mm:ss} (source clock)."));
    }
}

using Quartz;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Serialisation.Projections;

/// <summary>Nightly replay-and-diff per tenant (ADR-0001 AI 8); repairs divergence, since the ledger is the truth.</summary>
[DisallowConcurrentExecution]
public sealed class UnitCurrentHealerJob(ITenantContextSetter setter, UnitCurrentHealer healer) : TenantJob(setter)
{
    public const string JobName = "serialisation.unit_current_healer";

    /// <summary>03:30 every day, after the partition lifecycle run.</summary>
    public const string Cron = "0 30 3 * * ?";

    public const int PageSize = 2000;

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        healer.RunAsync(repair: true, PageSize, cancellationToken);
}

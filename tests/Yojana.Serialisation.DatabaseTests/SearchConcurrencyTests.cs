using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Search;

namespace Yojana.Serialisation.DatabaseTests;

public sealed record SearchSqlRetryProbe(Guid ProbeId, TenantId TenantId, Guid FirstEntityId, Guid SecondEntityId, int ErrorNumber, int Failures);

public sealed record SearchSqlRetryProbeResult(int Attempts);

/// <summary>Exercises the production search retry policy with SQL Server errors raised by the database.</summary>
public static class SearchSqlRetryProbeHandler
{
    public static readonly ConcurrentDictionary<Guid, int> Attempts = new();

    public static void Configure(Wolverine.Runtime.Handlers.HandlerChain chain) => SearchProjectionRetry.Configure(chain);

    public static async Task<SearchSqlRetryProbeResult> Handle(SearchSqlRetryProbe probe, SerialisationDbContext db)
    {
        var attempt = Attempts.AddOrUpdate(probe.ProbeId, 1, (_, count) => count + 1);
        if (attempt <= probe.Failures)
        {
            if (probe.ErrorNumber == 1205)
            {
                await ForceDeadlockAsync(db.Database.GetConnectionString()!, probe);
            }
            else if (probe.ErrorNumber == 51001)
            {
                await db.Database.ExecuteSqlRawAsync("THROW 51001, 'search retry probe', 1;");
            }
            else throw new ArgumentOutOfRangeException(nameof(probe), probe.ErrorNumber, "unsupported SQL retry probe number");
        }

        return new SearchSqlRetryProbeResult(attempt);
    }

    private static async Task ForceDeadlockAsync(string connectionString, SearchSqlRetryProbe probe)
    {
        await using var first = new SqlConnection(connectionString);
        await using var second = new SqlConnection(connectionString);
        await Task.WhenAll(first.OpenAsync(), second.OpenAsync());
        await SetTenantAsync(first, probe.TenantId);
        await SetTenantAsync(second, probe.TenantId);
        await using var firstTransaction = (SqlTransaction)await first.BeginTransactionAsync();
        await using var secondTransaction = (SqlTransaction)await second.BeginTransactionAsync();
        await UpdateEntryAsync(first, firstTransaction, probe, probe.FirstEntityId, lowPriority: true);
        await UpdateEntryAsync(second, secondTransaction, probe, probe.SecondEntityId, lowPriority: false);

        var firstWait = UpdateEntryAsync(first, firstTransaction, probe, probe.SecondEntityId, lowPriority: true);
        await Task.Delay(50);
        var secondWait = UpdateEntryAsync(second, secondTransaction, probe, probe.FirstEntityId, lowPriority: false);
        await Task.WhenAll(firstWait, secondWait);
    }

    private static async Task SetTenantAsync(SqlConnection connection, TenantId tenantId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@tenant, @read_only=1;";
        command.Parameters.Add(new SqlParameter("@tenant", tenantId.Value));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateEntryAsync(SqlConnection connection, SqlTransaction transaction,
        SearchSqlRetryProbe probe, Guid entityId, bool lowPriority)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SET DEADLOCK_PRIORITY {(lowPriority ? "LOW" : "HIGH")}; UPDATE serialisation.search_entry SET type_weight=type_weight+1 WHERE tenant_id=@tenant AND entity_type=N'unit' AND entity_id=@entity; IF @@ROWCOUNT <> 1 THROW 51002, 'search retry probe entry missing', 1;";
        command.Parameters.Add(new SqlParameter("@tenant", probe.TenantId.Value));
        command.Parameters.Add(new SqlParameter("@entity", entityId));
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Regression coverage for asynchronous search projection contention under a published-event burst.</summary>
[Collection("serialisation host")]
public sealed class SearchConcurrencyTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Published_reindex_burst_leaves_no_dead_letters_and_every_unit_searchable()
    {
        var tenant = await fixture.NewTenantAsync("search-race");
        var item = Guid.NewGuid();
        var legalEntity = Guid.NewGuid();
        var location = Guid.NewGuid();
        var document = Guid.NewGuid();
        var marker = "RC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(
            InMemoryItemPorts.DefaultProfile(item) with { ItemCode = marker + "-ITEM" });

        const int unitCount = 1_200;
        var lines = Enumerable.Range(1, unitCount)
            .Select(i => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{marker}-{i:000000}")]))
            .ToList();
        CommissionUnitsResult? commissioned = null;
        var deadLettersBefore = await DeadLetterCountAsync();
        async Task CommissionAsync(IMessageContext _)
        {
            using var scope = fixture.ScopeFor(tenant);
            commissioned = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<CommissionUnitsResult>(
                new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased, lines, At,
                    "search-race", legalEntity, LocationId: location, LocationKind: LocationKind.Warehouse,
                    Document: new DocumentRef("receipt", document, 1)),
                new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
        }

        await fixture.Host.TrackActivity(TimeSpan.FromMinutes(2))
            .WaitForExecutionOf<SearchReindexRequested>(2)
            .ExecuteAndWaitAsync(CommissionAsync);
        var unitIds = commissioned!.Lines.Select(line => line.UnitId!.Value).ToArray();
        using (var deletionScope = fixture.ScopeFor(tenant))
        {
            var deletionDb = deletionScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await deletionDb.SearchTerms.Where(row => row.EntityType == SearchEntityType.Document && row.EntityId == document).ExecuteDeleteAsync();
            await deletionDb.SearchEntries.Where(row => row.EntityType == SearchEntityType.Document && row.EntityId == document).ExecuteDeleteAsync();
        }

        var requests = unitIds.Chunk(20)
            .Select(chunk => new SearchProjectionRequested(chunk, [new DocumentRef("receipt", document)], RecordedAt: At.AddDays(1)))
            .ToArray();
        async Task PublishBurstAsync(IMessageContext _)
        {
            using var scope = fixture.ScopeFor(tenant);
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await Task.WhenAll(requests.Select(request => bus.PublishAsync(request,
                new DeliveryOptions { TenantId = tenant.ToString() }).AsTask()));
        }
        await fixture.Host.TrackActivity(TimeSpan.FromMinutes(3))
            .WaitForExecutionOf<SearchProjectionRequested>(requests.Length)
            .ExecuteAndWaitAsync(PublishBurstAsync);

        Assert.Equal(unitCount, commissioned.Commissioned);
        Assert.Equal(deadLettersBefore, await DeadLetterCountAsync());
        using var finalScope = fixture.ScopeFor(tenant);
        var db = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(unitCount, await db.SearchEntries.CountAsync(row => row.EntityType == SearchEntityType.Unit));
        Assert.Single(await db.SearchEntries.Where(row => row.EntityType == SearchEntityType.Document).ToListAsync());
        Assert.Equal(unitCount, await db.SearchTerms.Where(row => row.EntityType == SearchEntityType.Unit
            && row.Kind == SearchTermKind.Identifier && row.Term.StartsWith(marker)).Select(row => row.EntityId).Distinct().CountAsync());
    }

    [DatabaseFact]
    public async Task Deadlock_is_retried_but_a_non_transient_sql_error_is_not()
    {
        var tenant = await fixture.NewTenantAsync("search-retry");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var firstEntityId = Guid.NewGuid();
        var secondEntityId = Guid.NewGuid();
        foreach (var entityId in new[] { firstEntityId, secondEntityId })
        {
            db.SearchEntries.Add(new SearchEntry
            {
                TenantId = tenant, EntityType = SearchEntityType.Unit, EntityId = entityId, EntryId = Guid.NewGuid(),
                Title = "search retry probe", SearchText = "probe", TypeWeight = 1, UpdatedAt = At,
                Metadata = "{}", Href = "/probe",
            });
        }
        await db.SaveChangesAsync();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var deadlock = new SearchSqlRetryProbe(Guid.NewGuid(), tenant, firstEntityId, secondEntityId, 1205, 1);

        var result = await bus.InvokeAsync<SearchSqlRetryProbeResult>(deadlock,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);

        Assert.Equal(2, result.Attempts);
        var permanent = new SearchSqlRetryProbe(Guid.NewGuid(), tenant, firstEntityId, secondEntityId, 51001, 1);
        var exception = await Assert.ThrowsAsync<SqlException>(() => bus.InvokeAsync<SearchSqlRetryProbeResult>(permanent,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None));
        Assert.Equal(51001, exception.Number);
        Assert.Equal(1, SearchSqlRetryProbeHandler.Attempts[permanent.ProbeId]);
    }

    [DatabaseFact]
    public async Task Healer_rebuilds_deliberately_skipped_unit_and_document_entries()
    {
        var tenant = await fixture.NewTenantAsync("search-heal");
        var item = Guid.NewGuid();
        var document = Guid.NewGuid();
        var marker = "HL" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(
            InMemoryItemPorts.DefaultProfile(item) with { ItemCode = marker + "-ITEM" });
        CommissionUnitsResult? result = null;
        async Task CommissionAsync(IMessageContext _)
        {
            using var scope = fixture.ScopeFor(tenant);
            result = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<CommissionUnitsResult>(
                new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
                    [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, marker + "-1")])],
                    At, "search-heal", Guid.NewGuid(), LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse,
                    Document: new DocumentRef("receipt", document, 1)),
                new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
        }

        await fixture.Host.TrackActivity(TimeSpan.FromSeconds(30))
            .WaitForExecutionOf<UnitCommissioned>(1)
            .WaitForExecutionOf<SearchProjectionRequested>(1)
            .ExecuteAndWaitAsync(CommissionAsync);
        var unitId = Assert.Single(result!.Lines).UnitId!.Value;
        using var healingScope = fixture.ScopeFor(tenant);
        var db = healingScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await db.SearchTerms.Where(row => row.EntityId == unitId || row.EntityId == document).ExecuteDeleteAsync();
        await db.SearchEntries.Where(row => row.EntityId == unitId || row.EntityId == document).ExecuteDeleteAsync();

        var healed = await healingScope.ServiceProvider.GetRequiredService<SearchIndexHealer>()
            .RunAsync(10, CancellationToken.None);

        Assert.Equal(new SearchIndexHealingResult(1, 1), healed);
        Assert.True(await db.SearchEntries.AnyAsync(row => row.EntityType == SearchEntityType.Unit && row.EntityId == unitId));
        Assert.True(await db.SearchEntries.AnyAsync(row => row.EntityType == SearchEntityType.Document && row.EntityId == document));
        Assert.Equal($"/units/{unitId}", (await healingScope.ServiceProvider.GetRequiredService<SearchQueries>()
            .SearchAsync(marker + "-1", null, SearchDisclosureTier.BackOffice, CancellationToken.None)).OpenDirectly);
        var scheduler = await fixture.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await fixture.Services.GetRequiredService<RecurringTenantJobScheduler>()
            .EnsureScheduledAsync(scheduler, [tenant], CancellationToken.None);
        var detail = await scheduler.GetJobDetail(TenantJob.KeyFor(SearchIndexHealerJob.JobName, tenant));
        Assert.NotNull(detail);
        Assert.Equal(typeof(SearchIndexHealerJob), detail.JobType);
    }

    private async Task<int> DeadLetterCountAsync()
    {
        using var scope = fixture.ScopeFor(null);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        return await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [wolverine].[wolverine_dead_letters]").SingleAsync();
    }

}

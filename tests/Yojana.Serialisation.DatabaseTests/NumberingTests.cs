using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Xunit.Abstractions;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>OD-26: each series chooses parallel sequence allocation or transactional gap-free allocation.</summary>
[Collection("serialisation host")]
public sealed class NumberingTests(SerialisationHostFixture fixture, ITestOutputHelper output)
{
    private static readonly DateTime At = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Sequence_creators_do_not_share_a_counter_row_or_wait_for_an_open_creator_transaction()
    {
        var tenant = await fixture.NewTenantAsync("numseq");
        var firstAllocated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(async () =>
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var number = await scope.ServiceProvider.GetRequiredService<DocumentNumbers>().NextAsync(DocumentNumbers.Claim, At, CancellationToken.None);
            firstAllocated.SetResult();
            await releaseFirst.Task;
            await transaction.CommitAsync();
            return number;
        });

        await firstAllocated.Task;
        var others = Enumerable.Range(0, 20).Select(_ => AllocateAsync(tenant, DocumentNumbers.Claim)).ToArray();
        var completed = await Task.WhenAny(Task.WhenAll(others), Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.NotEqual(TaskStatus.WaitingForActivation, completed.Status);
        Assert.True(others.All(task => task.IsCompletedSuccessfully), "sequence allocators waited for the first creator's open transaction");
        releaseFirst.SetResult();

        var numbers = (await Task.WhenAll(others)).Append(await first).ToList();
        Assert.Equal(numbers.Count, numbers.Distinct(StringComparer.Ordinal).Count());
        using var check = fixture.ScopeFor(tenant);
        Assert.False(await check.ServiceProvider.GetRequiredService<SerialisationDbContext>().NumberSeries.AnyAsync(n => n.Series == DocumentNumbers.Claim));
    }

    [DatabaseFact]
    public async Task Gap_free_creators_commit_one_consecutive_run_under_concurrency()
    {
        var tenant = await fixture.NewTenantAsync("numgap");
        await SetModeAsync(tenant, DocumentNumbers.Recovery, NumberingMode.GapFree);

        var numbers = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => AllocateAsync(tenant, DocumentNumbers.Recovery)));

        Assert.Equal(Enumerable.Range(1, 30).Select(n => DocumentNumbers.Format(DocumentNumbers.Recovery, 2026, n)), numbers.OrderBy(x => x, StringComparer.Ordinal));
    }

    [DatabaseFact]
    public async Task Gap_free_creators_queue_without_failure_at_peak_concurrency()
    {
        var passes = int.TryParse(Environment.GetEnvironmentVariable("YOJANA_NUMBERING_STRESS_PASSES"), out var configured)
            ? Math.Max(1, configured) : 1;
        var concurrency = int.TryParse(Environment.GetEnvironmentVariable("YOJANA_NUMBERING_STRESS_CONCURRENCY"), out configured)
            ? Math.Clamp(configured, 1, 120) : 120;
        var waits = new ConcurrentBag<TimeSpan>();

        for (var pass = 0; pass < passes; pass++)
        {
            // A fresh tenant exercises the empty counter-key range, where every first creator races to insert value 1.
            var tenant = await fixture.NewTenantAsync("numpeak", importJurisdictionStarter: false);
            await SetModeAsync(tenant, DocumentNumbers.Recovery, NumberingMode.GapFree);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allocations = Enumerable.Range(0, concurrency).Select(async _ =>
            {
                await start.Task;
                var timer = Stopwatch.StartNew();
                try { return await AllocateAsync(tenant, DocumentNumbers.Recovery); }
                finally { waits.Add(timer.Elapsed); }
            }).ToArray();
            start.SetResult();
            var numbers = await Task.WhenAll(allocations);
            Assert.Equal(Enumerable.Range(1, concurrency).Select(n => DocumentNumbers.Format(DocumentNumbers.Recovery, 2026, n)),
                numbers.OrderBy(x => x, StringComparer.Ordinal));
        }

        output.WriteLine("{0} passes at concurrency {1}; worst allocation wait {2:N0} ms", passes, concurrency,
            waits.Max().TotalMilliseconds);
    }

    [DatabaseFact]
    public async Task Switching_mode_mid_period_always_continues_above_the_highest_issued_number()
    {
        var tenant = await fixture.NewTenantAsync("numswitch");
        var sequence = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AllocateAsync(tenant, DocumentNumbers.Rma)));
        await SetModeAsync(tenant, DocumentNumbers.Rma, NumberingMode.GapFree);
        var gapFree = await AllocateAsync(tenant, DocumentNumbers.Rma);
        await SetModeAsync(tenant, DocumentNumbers.Rma, NumberingMode.Sequence);
        var sequenceAgain = await AllocateAsync(tenant, DocumentNumbers.Rma);

        Assert.True(Value(gapFree) > sequence.Max(Value));
        Assert.True(Value(sequenceAgain) > Value(gapFree));
        Assert.Equal(sequence.Append(gapFree).Append(sequenceAgain).Count(), sequence.Append(gapFree).Append(sequenceAgain).Distinct().Count());
    }

    private async Task<string> AllocateAsync(TenantId tenant, string series)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<DocumentNumbers>().NextAsync(series, At, CancellationToken.None);
    }

    private async Task SetModeAsync(TenantId tenant, string series, string mode)
    {
        using var scope = fixture.ScopeFor(tenant);
        var command = new SetNumberingMode(Guid.NewGuid(), series, mode, "numbering test");
        var result = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<SetNumberingModeResult>(command,
            new DeliveryOptions { TenantId = tenant.ToString() });
        Assert.Equal(mode, result.Setting.Mode);
    }

    private static int Value(string number) => int.Parse(number[(number.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
}

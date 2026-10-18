using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>Probe command: loses an optimistic-concurrency race a set number of times, then succeeds.</summary>
public sealed record LoseConcurrencyRace(Guid ProbeId, int Losses);

public sealed record LoseConcurrencyRaceResult(int Attempts);

public static class LoseConcurrencyRaceHandler
{
    public static readonly ConcurrentDictionary<Guid, int> Attempts = new();

    public static LoseConcurrencyRaceResult Handle(LoseConcurrencyRace command)
    {
        var attempt = Attempts.AddOrUpdate(command.ProbeId, 1, (_, n) => n + 1);
        if (attempt <= command.Losses) throw new DbUpdateConcurrencyException("lost race (probe)");
        return new LoseConcurrencyRaceResult(attempt);
    }
}

/// <summary>A command that loses a rowversion race is retried by the platform instead of failing the caller.</summary>
[Collection("serialisation host")]
public sealed class ConcurrencyRetryTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Invoked_command_that_loses_a_concurrency_race_is_retried_and_succeeds()
    {
        var probe = new LoseConcurrencyRace(Guid.NewGuid(), Losses: 2);
        using var scope = fixture.ScopeFor(null);

        var result = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<LoseConcurrencyRaceResult>(probe);

        Assert.Equal(3, result.Attempts);
    }

    [DatabaseFact]
    public async Task Command_that_keeps_losing_fails_after_the_retries_run_out()
    {
        var probe = new LoseConcurrencyRace(Guid.NewGuid(), Losses: 10);
        using var scope = fixture.ScopeFor(null);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<LoseConcurrencyRaceResult>(probe));
        Assert.Equal(4, LoseConcurrencyRaceHandler.Attempts[probe.ProbeId]);
    }
}

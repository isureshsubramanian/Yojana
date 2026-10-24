using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class ActingLegalEntityTests
{
    private static readonly Guid First = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("10000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Several_entities_without_a_default_refuse()
    {
        var resolver = Resolver(null, Legal(First, "LE-FIRST"), Legal(Second, "LE-SECOND"));

        var result = await resolver.ResolveAsync(null, choiceAllowed: false, default);

        Assert.False(result.Accepted);
        Assert.Equal("legal_entity_not_configured", result.RefusalRule);
    }

    [Fact]
    public async Task A_single_entity_is_used_automatically()
    {
        var resolver = Resolver(null, Legal(First, "LE-ONLY"));

        var result = await resolver.ResolveAsync(null, choiceAllowed: false, default);
        var options = await resolver.GetAsync(default);

        Assert.Equal(First, result.LegalEntityId);
        Assert.Equal(First, options.DefaultId);
    }

    [Fact]
    public async Task A_default_is_applied_when_the_command_omits_an_entity()
    {
        var resolver = Resolver(Second.ToString(), Legal(First, "LE-FIRST"), Legal(Second, "LE-SECOND"));

        var result = await resolver.ResolveAsync(null, choiceAllowed: false, default);

        Assert.Equal(Second, result.LegalEntityId);
    }

    [Fact]
    public async Task Unknown_and_foreign_party_ids_are_refused()
    {
        var customer = new PartyLabel(Second, "CUSTOMER", "Fictional Customer", "customer");
        var resolver = Resolver(First.ToString(), Legal(First, "LE-FIRST"), customer);

        var unknown = await resolver.ResolveAsync(Guid.NewGuid(), choiceAllowed: true, default);
        var foreign = await resolver.ResolveAsync(Second, choiceAllowed: true, default);

        Assert.Equal("legal_entity_unknown", unknown.RefusalRule);
        Assert.Equal("legal_entity_unknown", foreign.RefusalRule);
    }

    [Fact]
    public async Task A_stale_configured_default_is_not_reported_as_the_single_entity_default()
    {
        var resolver = Resolver(Second.ToString(), Legal(First, "LE-ONLY"));

        var options = await resolver.GetAsync(default);
        var result = await resolver.ResolveAsync(null, choiceAllowed: false, default);

        Assert.Null(options.DefaultId);
        Assert.Equal("legal_entity_unknown", result.RefusalRule);
    }

    [Fact]
    public async Task A_valid_choice_is_honoured_only_when_the_screen_allows_it()
    {
        var resolver = Resolver(First.ToString(), Legal(First, "LE-FIRST"), Legal(Second, "LE-SECOND"));

        Assert.Equal(Second, (await resolver.ResolveAsync(Second, choiceAllowed: true, default)).LegalEntityId);
        Assert.Equal(First, (await resolver.ResolveAsync(Second, choiceAllowed: false, default)).LegalEntityId);
    }

    private static ActingLegalEntity Resolver(string? value, params PartyLabel[] parties) =>
        new(new PartyPort(parties), new MemorySettings(value));

    private static PartyLabel Legal(Guid id, string code) => new(id, code, $"{code} Works", "legal_entity");

    private sealed class MemorySettings(string? value) : ITenantSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(value);

        public Task SetAsync(string key, string next, CancellationToken cancellationToken)
        {
            value = next;
            return Task.CompletedTask;
        }
    }

    private sealed class PartyPort(params PartyLabel[] parties) : IPartyNamePort
    {
        private readonly IReadOnlyList<PartyLabel> values = parties;

        public Task<IReadOnlyDictionary<Guid, PartyLabel>> NamesAsync(IReadOnlyCollection<Guid> partyRefs,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<Guid, PartyLabel>>(
            values.Where(row => partyRefs.Contains(row.PartyRef)).ToDictionary(row => row.PartyRef));

        public Task<IReadOnlyList<PartyLabel>> SearchAsync(string text, int take,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PartyLabel>>(values.Take(take).ToList());
    }
}

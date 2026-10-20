using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Outbox;

namespace Yojana.Integration.Tests;

public sealed class OutboundCompletenessTests
{
    [Theory]
    [InlineData("reference_only")]
    [InlineData("custom_mode")]
    [InlineData("documents")]
    public async Task Connector_owns_the_mode_decision_for_both_payloads_and_corrected_commands(string mode)
    {
        using var services = new ServiceCollection().AddKeyedSingleton<IOutboundCompletenessCheck>("FX01", new RequiresSource()).BuildServiceProvider();
        var check = new OutboundCompleteness(services);
        var command = new CreateServiceOrder(Guid.NewGuid(), "service/test", "test", "", new(Guid.NewGuid(), ""), "", "");
        var typed = await check.MissingAsync("FX01", Guid.NewGuid(), mode, command, default);
        var payload = await check.MissingPayloadAsync("FX01", Guid.NewGuid(), mode, nameof(CreateServiceOrder), JsonSerializer.Serialize(command, IntegrationContracts.Json), default);
        Assert.Equal(new[] { mode + ".customer_ref" }, typed);
        Assert.Equal(typed, payload);
    }

    private sealed class RequiresSource : IOutboundCompletenessCheck
    {
        public Task<IReadOnlyList<string>> MissingAsync(Guid connectionId, string outboundMode, IOutboundCommand command, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([outboundMode + ".customer_ref"]);
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;

namespace Yojana.Integration.Outbox;

/// <summary>Connector-specific source requirements; no remote writes or persistence.</summary>
public interface IOutboundCompletenessCheck
{
    Task<IReadOnlyList<string>> MissingAsync(Guid connectionId, string outboundMode, IOutboundCommand command, CancellationToken ct);
}

public sealed class OutboundCompleteness(IServiceProvider services)
{
    public const string MissingSourceField = "missing_source_field";

    public Task<IReadOnlyList<string>> MissingAsync(string connectorCode, Guid connectionId, string mode,
        IOutboundCommand command, CancellationToken ct) =>
        services.GetKeyedService<IOutboundCompletenessCheck>(connectorCode)?.MissingAsync(connectionId, mode, command, ct)
        ?? Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<IReadOnlyList<string>> MissingPayloadAsync(string connectorCode, Guid connectionId, string mode,
        string type, string payload, CancellationToken ct)
    {
        var check = services.GetKeyedService<IOutboundCompletenessCheck>(connectorCode);
        if (check is null) return [];
        try { return await check.MissingAsync(connectionId, mode, Read(type, payload), ct); }
        catch (JsonException) { return ["payload"]; }
    }

    public static IOutboundCommand Read(string type, string payload)
    {
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement.TryGetProperty("payload", out var nested) ? nested : json.RootElement;
        var contract = type switch
        {
            nameof(CreateReplacementDelivery) => typeof(CreateReplacementDelivery),
            nameof(CreateCreditMemo) => typeof(CreateCreditMemo),
            nameof(PostWarrantyExpense) => typeof(PostWarrantyExpense),
            nameof(ChangeStockStatusOrLocation) => typeof(ChangeStockStatusOrLocation),
            nameof(AttachDocument) => typeof(AttachDocument),
            nameof(CreateServiceOrder) => typeof(CreateServiceOrder),
            _ => throw new JsonException("Unknown outbound command type."),
        };
        return (IOutboundCommand)(root.Deserialize(contract, IntegrationContracts.Json) ?? throw new JsonException("Missing command."));
    }
}

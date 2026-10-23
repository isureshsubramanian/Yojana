using System.Text.Json;
using Yojana.Integration.Connections;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX01;

public sealed class Cx01WarrantyStatusHealth(ITenantSettings settings) : IConnectionHealthNoteSource
{
    public const string NoteCode = "native_warranty_status_fallback";

    public async Task RecordFallbackAsync(Guid connectionId, string cardStatus,
        CancellationToken cancellationToken) =>
        await ChangeAsync(connectionId, cardStatus, add: true, cancellationToken);

    public async Task RecordConfiguredAsync(Guid connectionId, string cardStatus,
        CancellationToken cancellationToken) =>
        await ChangeAsync(connectionId, cardStatus, add: false, cancellationToken);

    public async Task<ConnectionHealthNote?> ReadAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var missing = await ReadStatusesAsync(connectionId, cancellationToken);
        return missing.Count == 0 ? null : new(NoteCode,
            "The remote installed-base status code is not configured for "
            + string.Join(", ", missing.Order(StringComparer.Ordinal))
            + "; Active is being sent as the safe fallback.");
    }

    private async Task ChangeAsync(Guid connectionId, string cardStatus, bool add,
        CancellationToken cancellationToken)
    {
        var missing = await ReadStatusesAsync(connectionId, cancellationToken);
        var changed = add ? missing.Add(cardStatus) : missing.Remove(cardStatus);
        if (changed)
            await settings.SetAsync(Key(connectionId), JsonSerializer.Serialize(
                missing.Order(StringComparer.Ordinal), IntegrationContracts.Json), cancellationToken);
    }

    private async Task<HashSet<string>> ReadStatusesAsync(Guid connectionId,
        CancellationToken cancellationToken) =>
        await settings.GetAsync(Key(connectionId), cancellationToken) is { } json
            ? JsonSerializer.Deserialize<HashSet<string>>(json, IntegrationContracts.Json) ?? []
            : [];

    private static string Key(Guid connectionId) =>
        $"integration.cx01.native-warranty-status-health.{connectionId:N}";
}

using System.Text.Json;

namespace Yojana.Serialisation.MigrationLoads;

/// <summary>
/// The incumbent's delivery as recorded on a migrated <c>unit.ownership_opened</c> fact. The load writes these keys and
/// <see cref="Entitlements.BackfillEntitlementsHandler"/> reads them as start evidence, because a migrated ownership row
/// names no module document (P9-T09). Both sides use this class, so the keys cannot drift apart.
/// </summary>
public static class MigratedDeliveryEvidence
{
    public const string AtKey = "delivery_at";
    public const string LabelKey = "delivery_document_label";
    public const string NumberKey = "delivery_document_number";

    public static DateTime? ReadAt(string? attributes)
    {
        if (string.IsNullOrWhiteSpace(attributes)) return null;
        using var json = JsonDocument.Parse(attributes);
        return json.RootElement.TryGetProperty(AtKey, out var at) && at.ValueKind == JsonValueKind.String
            && at.TryGetDateTime(out var value) ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    }
}

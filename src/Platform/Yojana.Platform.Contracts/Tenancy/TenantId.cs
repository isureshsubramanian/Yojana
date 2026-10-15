using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>
/// The identity of a tenant. Leads every primary key and index of every tenant-scoped table
/// (MOD-01 invariant 1) and is the value placed in SQL Server <c>SESSION_CONTEXT('tenant_id')</c>.
/// </summary>
[JsonConverter(typeof(TenantIdJsonConverter))]
public readonly record struct TenantId(Guid Value)
{
    public static TenantId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out TenantId tenantId)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            tenantId = new TenantId(guid);
            return true;
        }

        tenantId = default;
        return false;
    }

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString("D");
}

/// <summary>Serialises as the plain GUID string, so APIs carry "tenantId": "1111-..." rather than an object.</summary>
public sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TenantId.Parse(reader.GetString() ?? throw new JsonException("tenant id expected"));

    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

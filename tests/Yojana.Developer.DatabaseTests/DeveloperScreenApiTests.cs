using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Developer.Contracts;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Developer.DatabaseTests;

[Collection("developer host")]
public sealed class DeveloperScreenApiTests(DeveloperHostFixture fixture)
{
    [DatabaseFact]
    public async Task Catalogue_contains_every_scope_and_event_with_a_schema_valid_fictional_sample()
    {
        var setup = await fixture.NewTenantAsync("dev-catalogue");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var catalogue = await client.GetFromJsonAsync<DeveloperCatalogue>("/api/developer/catalogue");
        Assert.NotNull(catalogue);
        Assert.Equal(PublicEventTypes.All, catalogue.Events.Select(entry => entry.Type));
        Assert.Equal(PublicApiScopes.All, catalogue.Scopes);
        Assert.All(catalogue.Events, entry =>
        {
            Assert.Equal(PublicEventTypes.CurrentVersion, entry.Version);
            Assert.Equal(entry.Type, entry.Schema.GetProperty("title").GetString());
            Validate(entry.Sample, entry.Schema, "$", []);
            if (entry.Type == PublicEventTypes.EndpointVerify)
                Assert.Equal("fictional-verification-challenge-0001",
                    entry.Sample.GetProperty("challenge").GetString());
            else
            {
                Assert.Equal("t_fictional_asterra_compute_works",
                    entry.Sample.GetProperty("tenant").GetString());
                Assert.Equal("2026-09-01T09:14:03Z",
                    entry.Sample.GetProperty("occurredAt").GetDateTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
                Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", entry.Sample.GetRawText(),
                    StringComparison.Ordinal);
            }
        });
    }

    [DatabaseFact]
    public async Task Retention_defaults_to_thirty_days_and_valid_change_is_tenant_scoped_and_audited()
    {
        var setup = await fixture.NewTenantAsync("dev-retention");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        Assert.Equal(30, (await client.GetFromJsonAsync<WebhookSettingsView>(
            "/api/developer/webhooks/settings"))!.RetentionDays);

        var response = await client.PutAsJsonAsync("/api/developer/webhooks/settings",
            new UpdateWebhookSettingsRequest(45));
        response.EnsureSuccessStatusCode();
        Assert.Equal(45, (await response.Content.ReadFromJsonAsync<WebhookSettingsView>())!.RetentionDays);

        using var scope = fixture.ScopeFor(setup.Tenant);
        Assert.Equal("45", await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
            .GetAsync(DeveloperWebhookSettings.RetentionDaysKey, default));
        var security = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents
            .SingleAsync(row => row.TenantId == setup.Tenant && row.Kind == SecurityEventKind.SettingsChanged
                && row.Reason == "developer.webhook_retention:30->45");
        Assert.Equal(setup.Administrator, security.ActorUserId);

        var other = await fixture.NewTenantAsync("dev-ret-other");
        using var otherClient = await fixture.ClientAsync(other.Tenant, other.Administrator);
        Assert.Equal(30, (await otherClient.GetFromJsonAsync<WebhookSettingsView>(
            "/api/developer/webhooks/settings"))!.RetentionDays);
    }

    [DatabaseFact]
    public async Task Retention_outside_supported_range_is_a_coded_refusal()
    {
        var setup = await fixture.NewTenantAsync("dev-ret-invalid");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var response = await client.PutAsJsonAsync("/api/developer/webhooks/settings",
            new UpdateWebhookSettingsRequest(3651));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("webhook_retention_out_of_range", problem.RootElement.GetProperty("code").GetString());
    }

    private static void Validate(JsonElement value, JsonElement schema, string path, List<string> failures)
    {
        if (schema.TryGetProperty("const", out var constant))
            Assert.Equal(constant.GetRawText(), value.GetRawText());
        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String) AssertType(value, type.GetString()!, path);
            else Assert.Contains(type.EnumerateArray().Select(item => item.GetString()),
                candidate => candidate is not null && IsType(value, candidate));
        }
        if (schema.TryGetProperty("minLength", out var minLength))
            Assert.True((value.GetString()?.Length ?? 0) >= minLength.GetInt32(), path);
        if (schema.TryGetProperty("minimum", out var minimum))
            Assert.True(value.GetInt64() >= minimum.GetInt64(), path);
        if (schema.TryGetProperty("format", out var format))
        {
            if (format.GetString() == "uuid") Assert.True(Guid.TryParse(value.GetString(), out _), path);
            if (format.GetString() == "date-time") Assert.True(DateTimeOffset.TryParse(value.GetString(), out _), path);
        }
        if (value.ValueKind != JsonValueKind.Object) return;
        var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;
        if (schema.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
                Assert.True(value.TryGetProperty(name, out _), $"{path}.{name} is required");
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(property.Name, out var propertySchema))
                Validate(property.Value, propertySchema, $"{path}.{property.Name}", failures);
            else if (schema.TryGetProperty("additionalProperties", out var additional)
                && additional.ValueKind == JsonValueKind.False)
                failures.Add($"{path}.{property.Name} is not declared");
        }
        Assert.Empty(failures);
    }

    private static void AssertType(JsonElement value, string type, string path) =>
        Assert.True(IsType(value, type), $"{path} must be {type}");

    private static bool IsType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };
}

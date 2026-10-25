using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Developer.Authentication;
using Yojana.Developer.Clients;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.DatabaseTests;

[Collection("developer host")]
public sealed class ApiClientAuthenticationTests(DeveloperHostFixture fixture)
{
    [DatabaseFact]
    public async Task Create_shows_secret_once_and_list_uses_names_not_actor_ids()
    {
        var setup = await fixture.NewTenantAsync("api-client");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional datacentre service", [PublicApiScopes.UnitsRead]);
        Assert.StartsWith($"yjc_{setup.Tenant.Value:N}_", created.Client.Id, StringComparison.Ordinal);
        Assert.Equal(43, created.Secret.Length);
        Assert.Equal(created.Secret[..8], created.Client.SecretHint);
        Assert.Equal("Fictional Administrator", created.Client.CreatedBy);

        var pageText = await admin.GetStringAsync("/api/developer/clients");
        Assert.DoesNotContain(created.Secret, pageText, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.Administrator.ToString("D"), pageText, StringComparison.OrdinalIgnoreCase);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var stored = await scope.ServiceProvider.GetRequiredService<DeveloperDbContext>().ApiClients.SingleAsync();
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(created.Secret), stored.SecretHash);
        Assert.Equal(32, stored.SecretHash.Length);
    }

    [DatabaseFact]
    public async Task Token_issue_rotation_expiry_and_revoke_apply_immediately_and_are_audited()
    {
        var setup = await fixture.NewTenantAsync("api-token");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional network operations", [PublicApiScopes.EventsRead]);

        using var anonymous = Anonymous();
        var wrong = await anonymous.PostAsJsonAsync("/api/public/v1/token",
            new TokenRequest("client_credentials", created.Client.Id, "not-the-secret"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_client", await CodeAsync(wrong));

        var first = await TokenAsync(anonymous, created.Client.Id, created.Secret);
        var rotationResponse = await admin.PostAsync($"/api/developer/clients/{created.Client.Id}/rotate-secret", null);
        rotationResponse.EnsureSuccessStatusCode();
        var rotated = await rotationResponse.Content.ReadFromJsonAsync<ApiClientSecretRotated>();
        Assert.NotNull(rotated);
        var overlap = await TokenAsync(anonymous, created.Client.Id, created.Secret);
        var current = await TokenAsync(anonymous, created.Client.Id, rotated.Secret);
        Assert.NotEqual(overlap.AccessToken, current.AccessToken);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            var hash = ApiClientService.Hash(first.AccessToken);
            var token = await db.AccessTokens.SingleAsync(row => row.TokenHash == hash);
            token.ExpiresAt = fixture.Clock.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        using var expiredRequest = new HttpRequestMessage(HttpMethod.Get, "/api/public/v1/events/00000000-0000-0000-0000-000000000001");
        expiredRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(expiredRequest)).StatusCode);

        var otherTenant = await fixture.NewTenantAsync("tok-other");
        var tokenParts = current.AccessToken.Split('_', 3);
        var mismatchedToken = $"yjt_{otherTenant.Tenant.Value:N}_{tokenParts[2]}";
        using var mismatchRequest = new HttpRequestMessage(HttpMethod.Get,
            "/api/public/v1/events/00000000-0000-0000-0000-000000000001");
        mismatchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mismatchedToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(mismatchRequest)).StatusCode);

        var revoked = await admin.PostAsync($"/api/developer/clients/{created.Client.Id}/revoke", null);
        revoked.EnsureSuccessStatusCode();
        using var revokedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/public/v1/events/00000000-0000-0000-0000-000000000001");
        revokedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(revokedRequest)).StatusCode);

        using var auditScope = fixture.ScopeFor(setup.Tenant);
        var events = await auditScope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents
            .Where(row => row.TenantId == setup.Tenant).ToListAsync();
        Assert.Contains(events, row => row.Reason == "invalid_client");
        Assert.Contains(events, row => row.Reason == "api_token_issued");
        Assert.Contains(events, row => row.Reason == "api_token_expired");
        Assert.Contains(events, row => row.Reason == "api_token_revoked");
        Assert.Contains(events, row => row.Reason == "api_client_secret_rotated");
        Assert.Contains(events, row => row.Reason == "api_client_revoked");
        Assert.All(events.Where(row => row.Reason.StartsWith("api_token_", StringComparison.Ordinal)
            || row.Reason == "invalid_client"), row => Assert.Contains(row.Kind,
                new[] { SecurityEventKind.ApiTokenIssued, SecurityEventKind.ApiTokenRefused }));
        var auditText = JsonSerializer.Serialize(events);
        Assert.DoesNotContain(created.Secret, auditText, StringComparison.Ordinal);
        Assert.DoesNotContain(rotated.Secret, auditText, StringComparison.Ordinal);
        Assert.DoesNotContain(first.AccessToken, auditText, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Token_endpoint_accepts_form_credentials_and_a_scope_subset()
    {
        var setup = await fixture.NewTenantAsync("api-form");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional server lifecycle",
            [PublicApiScopes.UnitsRead, PublicApiScopes.EventsRead]);
        using var anonymous = Anonymous();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = created.Client.Id,
            ["client_secret"] = created.Secret,
            ["scope"] = PublicApiScopes.UnitsRead,
        });
        var response = await anonymous.PostAsync("/api/public/v1/token", content);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        Assert.Equal(PublicApiScopes.UnitsRead, token.Scope);
        Assert.Equal(3600, token.ExpiresIn);
        Assert.Equal("Bearer", token.TokenType);
        using var scope = fixture.ScopeFor(setup.Tenant);
        Assert.Equal(PublicApiScopes.UnitsRead,
            (await scope.ServiceProvider.GetRequiredService<DeveloperDbContext>().AccessTokens.SingleAsync()).Scopes);
    }

    [DatabaseFact]
    public async Task Token_scope_is_the_issued_subset_intersected_with_the_clients_current_scopes()
    {
        var setup = await fixture.NewTenantAsync("api-scope");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional storage automation",
            [PublicApiScopes.UnitsRead, PublicApiScopes.EventsRead]);
        using var anonymous = Anonymous();
        var subset = await TokenAsync(anonymous, created.Client.Id, created.Secret, PublicApiScopes.UnitsRead);

        using var subsetEvent = PublicApiTestSupport.Bearer(HttpMethod.Get,
            "/api/public/v1/events/00000000-0000-0000-0000-000000000001", subset.AccessToken);
        var subsetRefusal = await anonymous.SendAsync(subsetEvent);
        Assert.Equal(HttpStatusCode.Forbidden, subsetRefusal.StatusCode);
        Assert.Equal("insufficient_scope", await CodeAsync(subsetRefusal));

        var full = await TokenAsync(anonymous, created.Client.Id, created.Secret);
        var changed = await admin.PutAsJsonAsync($"/api/developer/clients/{created.Client.Id}",
            new UpdateApiClientRequest([PublicApiScopes.UnitsRead], ApiClientService.DefaultRateLimitPerMinute));
        changed.EnsureSuccessStatusCode();
        using var narrowedEvent = PublicApiTestSupport.Bearer(HttpMethod.Get,
            "/api/public/v1/events/00000000-0000-0000-0000-000000000001", full.AccessToken);
        var narrowedRefusal = await anonymous.SendAsync(narrowedEvent);
        Assert.Equal(HttpStatusCode.Forbidden, narrowedRefusal.StatusCode);
        Assert.Equal("insufficient_scope", await CodeAsync(narrowedRefusal));
    }

    [DatabaseFact]
    public async Task Unknown_tenants_are_refused_without_foreign_key_failures_and_audited_without_a_tenant()
    {
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
        var unknownTenant = Guid.CreateVersion7();
        using var anonymous = Anonymous();
        var before = await SecurityEventIdsAsync();

        using var bearer = PublicApiTestSupport.Bearer(HttpMethod.Get,
            "/api/public/v1/events/00000000-0000-0000-0000-000000000001",
            $"yjt_{unknownTenant:N}_{new string('A', 43)}");
        var bearerResponse = await anonymous.SendAsync(bearer);
        Assert.Equal(HttpStatusCode.Unauthorized, bearerResponse.StatusCode);
        Assert.Equal("invalid_token", await CodeAsync(bearerResponse));
        var bearerEvent = Assert.Single(await SecurityEventsAfterAsync(before));
        Assert.Null(bearerEvent.TenantId);
        Assert.Equal(SecurityEventKind.ApiTokenRefused, bearerEvent.Kind);

        before = await SecurityEventIdsAsync();
        var tokenResponse = await anonymous.PostAsJsonAsync("/api/public/v1/token", new TokenRequest(
            "client_credentials", $"yjc_{Guid.CreateVersion7():N}_{Guid.CreateVersion7():N}", "wrong-secret"));
        Assert.Equal(HttpStatusCode.Unauthorized, tokenResponse.StatusCode);
        Assert.Equal("invalid_client", await CodeAsync(tokenResponse));
        var tokenEvent = Assert.Single(await SecurityEventsAfterAsync(before));
        Assert.Null(tokenEvent.TenantId);
        Assert.Equal(SecurityEventKind.ApiTokenRefused, tokenEvent.Kind);
    }

    [DatabaseFact]
    public async Task Refusal_budget_throttles_both_paths_before_audit_writes_and_successes_do_not_count()
    {
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
        var setup = await fixture.NewTenantAsync("reflimit");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional server monitor", [PublicApiScopes.EventsRead]);
        using var anonymous = Anonymous();

        for (var index = 0; index < 25; index++)
            _ = await TokenAsync(anonymous, created.Client.Id, created.Secret);

        var before = await SecurityEventCountAsync();
        for (var index = 0; index < 20; index++)
        {
            var refused = await anonymous.PostAsJsonAsync("/api/public/v1/token",
                new TokenRequest("client_credentials", created.Client.Id, "wrong-secret"));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        var throttled = await anonymous.PostAsJsonAsync("/api/public/v1/token",
            new TokenRequest("client_credentials", created.Client.Id, "wrong-secret"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("api_auth_throttled", await CodeAsync(throttled));
        Assert.NotNull(throttled.Headers.RetryAfter);
        Assert.Equal(20, (await SecurityEventsAfterAsync(before)).Count);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
        before = await SecurityEventCountAsync();
        for (var index = 0; index < 20; index++)
        {
            using var refused = PublicApiTestSupport.Bearer(HttpMethod.Get,
                "/api/public/v1/events/00000000-0000-0000-0000-000000000001",
                $"yjt_{setup.Tenant.Value:N}_{new string((char)('A' + index), 43)}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(refused)).StatusCode);
        }
        using var overBudget = PublicApiTestSupport.Bearer(HttpMethod.Get,
            "/api/public/v1/events/00000000-0000-0000-0000-000000000001",
            $"yjt_{setup.Tenant.Value:N}_{new string('Z', 43)}");
        var bearerThrottle = await anonymous.SendAsync(overBudget);
        Assert.Equal(HttpStatusCode.TooManyRequests, bearerThrottle.StatusCode);
        Assert.Equal("api_auth_throttled", await CodeAsync(bearerThrottle));
        Assert.NotNull(bearerThrottle.Headers.RetryAfter);
        Assert.Equal(20, (await SecurityEventsAfterAsync(before)).Count);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
    }

    [DatabaseFact]
    public async Task Previous_secret_is_valid_for_twenty_four_hours_and_then_refused()
    {
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
        var setup = await fixture.NewTenantAsync("secretover");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional network controller", [PublicApiScopes.EventsRead]);
        var rotationResponse = await admin.PostAsync($"/api/developer/clients/{created.Client.Id}/rotate-secret", null);
        rotationResponse.EnsureSuccessStatusCode();
        using var anonymous = Anonymous();
        _ = await TokenAsync(anonymous, created.Client.Id, created.Secret);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(24).AddSeconds(1);
        var refused = await anonymous.PostAsJsonAsync("/api/public/v1/token",
            new TokenRequest("client_credentials", created.Client.Id, created.Secret));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("invalid_client", await CodeAsync(refused));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
    }

    [DatabaseFact]
    public async Task Refused_api_token_does_not_change_staff_failed_sign_in_count()
    {
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
        var setup = await fixture.NewTenantAsync("api-summary");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional service console", [PublicApiScopes.EventsRead]);
        using var anonymous = Anonymous();
        var refused = await anonymous.PostAsJsonAsync("/api/public/v1/token",
            new TokenRequest("client_credentials", created.Client.Id, "wrong-secret"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var summary = await scope.ServiceProvider.GetRequiredService<SecurityEventQueries>().SummaryAsync(
            fixture.Clock.UtcNow.Date, fixture.Clock.UtcNow.Date.AddDays(-6), CancellationToken.None);
        Assert.Equal(0, summary.FailedSignIns);
        Assert.Single(await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents
            .Where(row => row.TenantId == setup.Tenant && row.Kind == SecurityEventKind.ApiTokenRefused)
            .ToListAsync());
    }

    [DatabaseFact]
    public async Task Public_and_internal_routes_refuse_the_other_authentication_scheme_and_enforce_scopes()
    {
        var setup = await fixture.NewTenantAsync("api-boundary");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional storage monitor", [PublicApiScopes.UnitsRead]);
        using var anonymous = Anonymous();
        var token = await TokenAsync(anonymous, created.Client.Id, created.Secret);

        var cookieOnPublic = await admin.GetAsync("/api/public/v1/events/00000000-0000-0000-0000-000000000001");
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnPublic.StatusCode);

        using var bearerOnInternal = new HttpRequestMessage(HttpMethod.Get, "/api/developer/clients");
        bearerOnInternal.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(bearerOnInternal)).StatusCode);

        using var missingScope = new HttpRequestMessage(HttpMethod.Get,
            "/api/public/v1/events/00000000-0000-0000-0000-000000000001");
        missingScope.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var refusal = await anonymous.SendAsync(missingScope);
        Assert.Equal(HttpStatusCode.Forbidden, refusal.StatusCode);
        Assert.Equal("insufficient_scope", await CodeAsync(refusal));
    }

    [DatabaseFact]
    public async Task Per_client_rate_limit_returns_retry_after()
    {
        var setup = await fixture.NewTenantAsync("api-rate");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional switch telemetry", [PublicApiScopes.EventsRead], 2);
        using var anonymous = Anonymous();
        var token = await TokenAsync(anonymous, created.Client.Id, created.Secret);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            db.PublicEvents.Add(new PublicEvent
            {
                TenantId = setup.Tenant, EventId = Guid.CreateVersion7(), Type = "unit.shipped", Version = "1",
                RecordKind = "unit", RecordId = Guid.CreateVersion7(), Sequence = 1,
                OccurredAt = fixture.Clock.UtcNow, RecordedAt = fixture.Clock.UtcNow,
                Body = "{\"id\":\"fictional-event\",\"type\":\"unit.shipped\"}",
            });
            await db.SaveChangesAsync();
        }
        Guid eventId;
        using (var scope = fixture.ScopeFor(setup.Tenant))
            eventId = await scope.ServiceProvider.GetRequiredService<DeveloperDbContext>().PublicEvents
                .Select(row => row.EventId).SingleAsync();
        HttpResponseMessage? last = null;
        for (var index = 0; index < 3; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/public/v1/events/{eventId:D}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            last = await anonymous.SendAsync(request);
        }
        Assert.NotNull(last);
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.True(last.Headers.RetryAfter is not null || last.Headers.Contains("Retry-After"));
        Assert.Equal("rate_limit_exceeded", await CodeAsync(last));
    }

    [DatabaseFact]
    public async Task Openapi_contains_only_public_paths_and_accepts_client_or_administrator()
    {
        var setup = await fixture.NewTenantAsync("api-openapi");
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(admin, "Fictional server tooling", [PublicApiScopes.UnitsRead]);
        var cookieDocument = await admin.GetAsync("/api/public/v1/openapi.json");
        cookieDocument.EnsureSuccessStatusCode();

        using var anonymous = Anonymous();
        var token = await TokenAsync(anonymous, created.Client.Id, created.Secret);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/public/v1/openapi.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var response = await anonymous.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/public/v1/units", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/developer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/settings", text, StringComparison.Ordinal);
        Assert.Contains("24 months", text, StringComparison.Ordinal);
        Assert.Contains("Idempotency-Key", text, StringComparison.Ordinal);
    }

    private async Task<ApiClientCreated> CreateAsync(HttpClient admin, string name, IReadOnlyList<string> scopes,
        int rate = ApiClientService.DefaultRateLimitPerMinute)
    {
        var response = await admin.PostAsJsonAsync("/api/developer/clients",
            new CreateApiClientRequest(name, scopes, PublicApiDisclosureTier.BackOffice,
                RateLimitPerMinute: rate));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiClientCreated>())!;
    }

    private static async Task<TokenResponse> TokenAsync(HttpClient client, string clientId, string secret,
        string? scope = null)
    {
        var response = await client.PostAsJsonAsync("/api/public/v1/token",
            new TokenRequest("client_credentials", clientId, secret, scope));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private HttpClient Anonymous() => new() { BaseAddress = new Uri(fixture.Host.Urls.Single()) };

    private async Task<int> SecurityEventCountAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents.CountAsync();
    }

    private async Task<HashSet<Guid>> SecurityEventIdsAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents.AsNoTracking()
            .Select(row => row.Id).ToHashSetAsync();
    }

    private async Task<List<Yojana.Platform.Data.Platform.Entities.SecurityEvent>> SecurityEventsAfterAsync(
        int before)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents.AsNoTracking()
            .OrderBy(row => row.OccurredAt).ThenBy(row => row.Id).Skip(before).ToListAsync();
    }

    private async Task<List<Yojana.Platform.Data.Platform.Entities.SecurityEvent>> SecurityEventsAfterAsync(
        HashSet<Guid> before)
    {
        using var scope = fixture.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents.AsNoTracking()
            .ToListAsync();
        return rows.Where(row => !before.Contains(row.Id)).ToList();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}

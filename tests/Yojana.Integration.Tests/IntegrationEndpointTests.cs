using Yojana.Integration.Coexistence;
using Yojana.Integration.Connections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Backfills;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Security;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.LinkOverrides;
using Yojana.Integration.LegacyMigration.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Yojana.Integration.Connectors.CX04;

namespace Yojana.Integration.Tests;

public sealed class IntegrationEndpointTests
{
    [Fact]
    public void Extension_status_read_check_and_guide_use_the_connection_operations_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<ExtensionStatusService>();
        var app = builder.Build();
        app.MapExtensionStatusEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();

        Assert.Equal(3, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Equal([YojanaPolicy.IntegrationOperations], endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>().Select(value => value.Policy).ToList()));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText?.EndsWith("/check",
            StringComparison.Ordinal) == true && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!
            .HttpMethods.Contains("POST"));
    }

    [Fact]
    public async Task Cx04_sign_in_actions_use_the_shared_administrator_policy_and_validation_errors_are_bad_requests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<Cx04Authorisation>();
        builder.Services.AddScoped<Cx04ReminderSettings>();
        var app = builder.Build();
        app.MapCx04AuthorisationEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        Assert.Equal(6, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Equal([YojanaPolicy.IntegrationOperations], endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>().Select(value => value.Policy).ToList()));

        foreach (var rule in new[] { "disable_confirmation_required", "disable_reason_invalid" })
        {
            var result = await Cx04AuthorisationEndpoints.Invoke<object>(() =>
                Task.FromException<object>(new Cx04AuthorisationException(rule, "Fictional validation failure.")));
            var problem = Assert.IsType<ProblemHttpResult>(result);
            Assert.Equal(400, problem.StatusCode);
            Assert.Equal(rule, problem.ProblemDetails.Extensions["rule"]);
        }
        Assert.DoesNotContain(typeof(Cx04DisableRequest).GetProperties(), property =>
            property.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(UpdateCx04ReminderSettings).GetProperties(), property =>
            property.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dead_letter_and_manual_task_endpoints_share_the_operator_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(YojanaPolicy.IntegrationOperations, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(YojanaPolicy.IntegrationIndicator, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(YojanaPolicy.TenantAdministrator, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddScoped<DeadLetterService>();
        builder.Services.AddScoped<ManualTaskDeskSettings>();
        builder.Services.AddScoped<ReconciliationService>();
        builder.Services.AddScoped<BackfillService>();
        var app = builder.Build();
        app.MapIntegrationOperationsEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/integration/",
                StringComparison.Ordinal) == true).ToList();
        Assert.Equal(36, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.IntegrationOperations));
        var fieldMapWrite = endpoints.Single(endpoint => endpoint.RoutePattern.RawText ==
            "/api/integration/connections/{connectionId:guid}/cx01/field-map"
            && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.Contains(fieldMapWrite.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.TenantAdministrator);
        var currencyWrite = endpoints.Single(endpoint => endpoint.RoutePattern.RawText ==
            "/api/integration/connections/{connectionId:guid}/cx01/currencies"
            && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.Contains(currencyWrite.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.TenantAdministrator);
        var approvalWrite = endpoints.Single(endpoint => endpoint.RoutePattern.RawText ==
            "/api/integration/connections/{connectionId:guid}/cx01/approval-handling"
            && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.Contains(approvalWrite.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.TenantAdministrator);
        var documentsWrite = endpoints.Single(endpoint => endpoint.RoutePattern.RawText ==
            "/api/integration/connections/{connectionId:guid}/cx01/documents"
            && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.Contains(documentsWrite.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.TenantAdministrator);
        var confirm = endpoints.Single(endpoint => endpoint.RoutePattern.RawText ==
            "/api/integration/connections/{connectionId:guid}/onboarding-checklist/{itemCode}/confirm");
        Assert.Contains(confirm.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.TenantAdministrator);
    }

    [Fact]
    public void Parallel_run_reads_admit_migration_leads_and_writes_keep_the_narrower_policies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<CoexistenceSettings>();
        builder.Services.AddScoped<CoexistenceDriftService>();
        builder.Services.AddScoped<CoexistenceConflicts>();
        builder.Services.AddScoped<SyncControl>();
        builder.Services.AddScoped<ParallelRun>();
        builder.Services.AddScoped<ParallelRunDashboardService>();
        builder.Services.AddScoped<WarrantyContractMaintenanceSettings>();
        var app = builder.Build();
        app.MapCoexistenceEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        IReadOnlyList<string?> Policies(string route, string method) => endpoints.Single(endpoint =>
                endpoint.RoutePattern.RawText == route
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method))
            .Metadata.GetOrderedMetadata<IAuthorizeData>().Select(x => x.Policy).ToList();

        foreach (var read in new[] { "/api/integration/connections/{connectionId:guid}/parallel-run/dashboard",
                     "/api/integration/go-live/records/{recordId:guid}", "/api/integration/go-live/records/{recordId:guid}/csv" })
            Assert.Equal([YojanaPolicy.ParallelRunRead], Policies(read, "GET"));
        foreach (var (route, method) in new[]
                 {
                     ("/api/integration/connections/{connectionId:guid}/parallel-run/month-end-close", "PUT"),
                     ("/api/integration/connections/{connectionId:guid}/parallel-run/handover/{kind}/plan", "PUT"),
                     ("/api/integration/connections/{connectionId:guid}/parallel-run/handover/{kind}/confirm", "POST"),
                     ("/api/integration/connections/{connectionId:guid}/go-live/record", "POST"),
                 })
            Assert.Equal([YojanaPolicy.IntegrationOperations, YojanaPolicy.TenantAdministrator], Policies(route, method));
        Assert.Equal([YojanaPolicy.IntegrationOperations],
            Policies("/api/integration/connections/{connectionId:guid}/go-live/{itemCode}/confirm", "POST"));
    }

    [Fact]
    public void Attention_indicator_keeps_its_broader_read_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(YojanaPolicy.IntegrationOperations, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(YojanaPolicy.IntegrationIndicator, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(YojanaPolicy.TenantAdministrator, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddScoped<DeadLetterService>();
        var app = builder.Build();
        app.MapIntegrationAttentionEndpoint();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(candidate => candidate.RoutePattern.RawText ==
                "/api/integration/attention-count");

        Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.IntegrationIndicator);
    }

    [Fact]
    public void Connection_status_is_read_only_and_operator_commands_use_the_narrower_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<ConnectionCommands>();
        builder.Services.AddScoped<ConnectionQueries>();
        var app = builder.Build();
        app.MapConnectionEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        Assert.Equal(14, endpoints.Count);
        var status = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/integration/status");
        Assert.Contains(status.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.IntegrationIndicator);
        Assert.All(endpoints.Where(endpoint => endpoint != status), endpoint =>
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.IntegrationOperations));
        var registration = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == "/api/integration/connections"
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        Assert.Contains(registration.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.TenantAdministrator);
        var smokeRead = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/smoke-read", StringComparison.Ordinal));
        Assert.DoesNotContain(smokeRead.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.TenantAdministrator);
        foreach (var route in new[] { "/onboarding", "/fetch-certificate", "/rebaseline" })
        {
            var matches = endpoints.Where(candidate => candidate.RoutePattern.RawText!.EndsWith(route,
                StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(matches);
            Assert.All(matches, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.TenantAdministrator));
        }
        foreach (var type in new[] { typeof(RegisterConnection), typeof(ArchiveConnection),
                     typeof(UpdateConnectionOnboarding), typeof(RebaselineConnection) })
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(TenantConnectionStatus).GetProperties(), property =>
            property.Name.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Operator_action_requests_do_not_accept_an_actor_from_the_browser()
    {
        foreach (var type in new[] { typeof(MapAndRetryDeadLetterRequest),
                     typeof(CreateProvisionalAndRetryDeadLetterRequest), typeof(DiscardDeadLetterRequest),
                     typeof(CompleteManualTaskRequest), typeof(StartBackfillRequest), typeof(RunReconciliationRequest) })
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Actor",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(YojanaRole.IsKnown(YojanaRole.IntegrationOperator));
    }

    [Fact]
    public async Task Migration_endpoints_split_administration_from_migration_review_and_never_expose_the_secret()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(YojanaPolicy.TenantAdministrator, policy => policy.RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId)
                .RequireClaim(YojanaClaim.Role, YojanaRole.TenantAdministrator));
            options.AddPolicy(YojanaPolicy.MigrationReview, policy => policy.RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId)
                .RequireClaim(YojanaClaim.Role, YojanaRole.TenantAdministrator, YojanaRole.MigrationLead));
        });
        builder.Services.AddScoped<MigrationSourceService>();
        builder.Services.AddScoped<MigrationBatchService>();
        builder.Services.AddScoped<TemplateMappingService>();
        builder.Services.AddScoped<TemplateKeywordRules>();
        builder.Services.AddScoped<MigrationProfileService>();
        builder.Services.AddScoped<MigrationProfileOutcomeSettings>();
        builder.Services.AddScoped<CanonicalisationService>();
        builder.Services.AddScoped<CanonicalFamilySettings>();
        builder.Services.AddScoped<ReconstructionService>();
        builder.Services.AddScoped<MappingService>();
        builder.Services.AddScoped<MappingSettings>();
        builder.Services.AddScoped<ReplacementLinkValueValidator>();
        builder.Services.AddScoped<LinkOverrideService>();
        builder.Services.AddScoped<ValidationService>();
        builder.Services.AddScoped<ValidationSettings>();
        builder.Services.AddScoped<Yojana.Integration.LegacyMigration.Apply.ApplyService>();
        var app = builder.Build();
        app.MapMigrationEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        Assert.Equal(73, endpoints.Count);
        var readPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/migration/batches", "/api/migration/batches/{batchId:guid}",
            "/api/migration/templates", "/api/migration/templates/summary",
            "/api/migration/templates/catalogue", "/api/migration/templates/{templateKey}",
            "/api/migration/batches/{batchId:guid}/profile",
            "/api/migration/batches/{batchId:guid}/profile/{classCode}/samples",
            "/api/migration/batches/{batchId:guid}/canonicalisation", "/api/migration/review",
            "/api/migration/review/export.csv", "/api/migration/canonicalisation/families/catalogue",
            "/api/migration/review/{itemId:guid}", "/api/migration/review/{itemId:guid}/decision",
            "/api/migration/batches/{batchId:guid}/reconstruction",
            "/api/migration/candidates/{candidateId:guid}/history",
            "/api/migration/batches/{batchId:guid}/map",
            "/api/migration/batches/{batchId:guid}/map/gaps",
            "/api/migration/plan/units/{planUnitId:guid}",
            "/api/migration/mapping/settings",
            "/api/migration/batches/{batchId:guid}/validation",
            "/api/migration/batches/{batchId:guid}/validation/export.csv",
            "/api/migration/batches/{batchId:guid}/validation/export.xlsx",
            "/api/migration/validation/checks/{checkId:guid}",
            "/api/migration/validation/checks/{checkId:guid}/differences",
            "/api/migration/validation/runs/{runId:guid}/sample",
            "/api/migration/signoff-records/{recordId:guid}",
            "/api/migration/signoff-records/{recordId:guid}/export.csv",
            "/api/migration/validation/settings",
            "/api/migration/batches/{batchId:guid}/apply", "/api/migration/loads/{loadId:guid}/refusals",
            "/api/migration/loads/{loadId:guid}/refusals/export.csv",
            "/api/migration/loads/{loadId:guid}/rollback/touched-units",
            "/api/migration/loads/{loadId:guid}/delta",
            "/api/migration/loads/{loadId:guid}/delta/export.csv",
            "/api/migration/loads/{loadId:guid}/delta/export.xlsx",
            "/api/migration/batches/{batchId:guid}/load-readiness", "/api/migration/loads",
            "/api/migration/loads/{loadId:guid}",
            "/api/migration/loads/{loadId:guid}/delta/export.csv",
            "/api/migration/loads/{loadId:guid}/delta/export.xlsx",
            "/api/migration/batches/{batchId:guid}/load-readiness", "/api/migration/loads",
            "/api/migration/loads/{loadId:guid}",
            "/api/migration/batches/{batchId:guid}/links/export.csv", "/api/migration/link-overrides",
        };
        Assert.All(endpoints, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == (IsReviewEndpoint(endpoint)
                ? YojanaPolicy.MigrationReview : YojanaPolicy.TenantAdministrator)));
        foreach (var type in new[] { typeof(CreateMigrationSourceRequest), typeof(UpdateMigrationSourceRequest),
                     typeof(UpdateMigrationLicenceRequest), typeof(StartMigrationBatchRequest) })
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Actor",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(MigrationSourceView).GetProperties(), property =>
            property.Name == nameof(CreateMigrationSourceRequest.ConnectionString));
        Assert.Contains(typeof(MigrationSourceView).GetProperties(), property =>
            property.Name == nameof(MigrationSourceView.HasConnectionString));
        foreach (var type in new[] { typeof(ProposeTemplatesRequest), typeof(UpdateTemplateMappingRequest),
                     typeof(ApprovePatternRequest), typeof(UpdateTemplateKeywordRulesRequest) })
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Actor",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/migration/templates"
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText ==
            "/api/migration/templates/{templateKey}/approve");
        foreach (var path in new[] { "/api/migration/templates/{templateKey}",
                     "/api/migration/templates/{templateKey}/approve", "/api/migration/templates/approve-pattern" })
            Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == path
                && endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(policy =>
                    policy.Policy == YojanaPolicy.MigrationReview));
        foreach (var path in new[] { "/api/migration/templates/propose",
                     "/api/migration/templates/keyword-rules" })
            Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == path
                && endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(policy =>
                    policy.Policy == YojanaPolicy.TenantAdministrator));
        Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText ==
            "/api/migration/batches/{batchId:guid}/profile/{classCode}/samples");
        Assert.Equal(2, endpoints.Count(endpoint => endpoint.RoutePattern.RawText ==
            "/api/migration/profiling/outcomes"));
        Assert.DoesNotContain(typeof(UpdateMigrationProfileOutcomeSettings).GetProperties(), property =>
            property.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(MigrationReviewDecisionRequest).GetProperties(), property =>
            property.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase));
        foreach (var type in new[] { typeof(StartValidationRequest), typeof(ExplainValidationCheckRequest),
                     typeof(SignValidationCheckRequest), typeof(TraceValidationSampleRequest),
                     typeof(UpdateValidationSettingsRequest) })
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Actor",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(YojanaRole.IsKnown(YojanaRole.MigrationLead));
        var migrationLead = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(YojanaClaim.TenantId, Guid.NewGuid().ToString("D")),
            new Claim(YojanaClaim.Role, YojanaRole.MigrationLead),
        ], "test"));
        var authorisation = app.Services.GetRequiredService<IAuthorizationService>();
        Assert.True((await authorisation.AuthorizeAsync(migrationLead, null, YojanaPolicy.MigrationReview)).Succeeded);
        Assert.False((await authorisation.AuthorizeAsync(migrationLead, null,
            YojanaPolicy.TenantAdministrator)).Succeeded);
        var tenantAdministrator = Principal(YojanaRole.TenantAdministrator);
        var otherRole = Principal(YojanaRole.IntegrationOperator);
        Assert.True((await authorisation.AuthorizeAsync(tenantAdministrator, null,
            YojanaPolicy.MigrationReview)).Succeeded);
        Assert.False((await authorisation.AuthorizeAsync(otherRole, null,
            YojanaPolicy.MigrationReview)).Succeeded);
        foreach (var (method, path) in new[]
                 {
                     ("PUT", "/api/migration/mapping/settings"),
                     ("POST", "/api/migration/link-overrides/upload"),
                     ("DELETE", "/api/migration/link-overrides"),
                 })
            Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == path &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method) &&
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(policy =>
                    policy.Policy == YojanaPolicy.MigrationReview));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText ==
            "/api/migration/batches/{batchId:guid}/canonicalise" &&
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(policy =>
                policy.Policy == YojanaPolicy.TenantAdministrator));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText ==
            "/api/migration/canonicalisation/families/catalogue" &&
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(policy =>
                policy.Policy == YojanaPolicy.MigrationReview));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText ==
            "/api/migration/canonicalisation/families" &&
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT") &&
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(policy =>
                policy.Policy == YojanaPolicy.TenantAdministrator));

        bool IsReviewEndpoint(RouteEndpoint endpoint)
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods;
            return (methods.Contains("GET") && readPaths.Contains(endpoint.RoutePattern.RawText!)) ||
                (methods.Contains("PUT") && endpoint.RoutePattern.RawText is
                    "/api/migration/templates/{templateKey}" or "/api/migration/mapping/settings") ||
                (methods.Contains("POST") && endpoint.RoutePattern.RawText is
                    "/api/migration/templates/{templateKey}/approve" or
                    "/api/migration/templates/approve-pattern") ||
                (methods.Contains("POST") && endpoint.RoutePattern.RawText ==
                    "/api/migration/review/{itemId:guid}/decision") ||
                (methods.Contains("POST") && endpoint.RoutePattern.RawText is
                    "/api/migration/validation/checks/{checkId:guid}/explain" or
                    "/api/migration/validation/checks/{checkId:guid}/sign" or
                    "/api/migration/validation/sample/{sampleId:guid}/trace" or
                    "/api/migration/link-overrides/upload") ||
                (methods.Contains("DELETE") && endpoint.RoutePattern.RawText == "/api/migration/link-overrides");
        }

        static ClaimsPrincipal Principal(string role) => new(new ClaimsIdentity(
        [
            new Claim(YojanaClaim.TenantId, Guid.NewGuid().ToString("D")),
            new Claim(YojanaClaim.Role, role),
        ], "test"));
    }
}

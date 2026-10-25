using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Yojana.Developer.PublicApi;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.OpenApi;

public static class PublicOpenApi
{
    public const string DocumentName = "public-v1";

    public static IServiceCollection AddPublicOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options =>
        {
            options.ShouldInclude = description => description.RelativePath?.StartsWith(
                DeveloperEndpoints.PublicPrefix.TrimStart('/') + "/", StringComparison.Ordinal) == true;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Yojana Public API";
                document.Info.Version = "1";
                document.Info.Description = "Opaque client-credentials API for serial and warranty records. "
                    + "A successor version is supported alongside this version for at least 24 months. "
                    + "List operations use opaque keyset cursors (after, limit; default 50, maximum 200). "
                    + "Writes require Idempotency-Key. Errors use RFC 9457 application/problem+json with code. "
                    + "Scopes: " + string.Join(", ", PublicApiScopes.All.Order(StringComparer.Ordinal)) + ".";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "opaque",
                    Description = "Token from POST /api/public/v1/token. The granted scopes are returned by that endpoint.",
                };
                var reference = new OpenApiSecuritySchemeReference("bearer", document, null);
                document.Security = [new OpenApiSecurityRequirement { [reference] = [] }];
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                if (context.Description.RelativePath?.EndsWith("/token", StringComparison.Ordinal) == true)
                    operation.Security = [];
                if (metadata.OfType<PublicApiScopeMetadata>().FirstOrDefault() is { } scope)
                    operation.Description = $"Required scope: {scope.Scope}. " + operation.Description;
                var method = context.Description.HttpMethod;
                if (method is "POST" or "PUT" && context.Description.RelativePath is not null
                    && !context.Description.RelativePath.EndsWith("/token", StringComparison.Ordinal))
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = IdempotencyService.Header,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "1–200 printable characters. Replays the first response for 24 hours.",
                    });
                }
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Invalid or missing bearer token (problem+json)." });
                operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Required scope is absent (problem+json)." });
                operation.Responses.TryAdd("422", new OpenApiResponse { Description = "Command refused with a stable code (problem+json)." });
                operation.Responses.TryAdd("429", new OpenApiResponse { Description = "Client rate limit exceeded; Retry-After is returned." });
                return Task.CompletedTask;
            });
        });

    public static async Task<IResult> DocumentAsync(
        [FromKeyedServices(DocumentName)] IOpenApiDocumentProvider provider,
        CancellationToken cancellationToken)
    {
        var document = await provider.GetOpenApiDocumentAsync(cancellationToken);
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken);
        return Results.Content(json, "application/json; charset=utf-8");
    }
}

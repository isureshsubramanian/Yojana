using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Connections;

public static class ConnectionEndpoints
{
    public static IEndpointRouteBuilder MapConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integration/connections").RequireAuthorization(YojanaPolicy.IntegrationOperations);
        group.MapGet("/registration-connectors", (string? cursor, int? take, ConnectionQueries queries, CancellationToken ct) =>
            Invoke(() => queries.RegistrationConnectorsAsync(cursor, take ?? 12, ct)));
        group.MapGet("", (string? cursor, int? take, bool? archived, ConnectionQueries queries, CancellationToken ct) =>
            Invoke(() => queries.ListAsync(cursor, take ?? 50, archived ?? false, ct)));
        group.MapGet("/{connectionId:guid}", (Guid connectionId, ConnectionQueries queries, CancellationToken ct) =>
            Invoke(() => queries.DetailAsync(connectionId, ct)));
        group.MapPost("", (RegisterConnection request, ClaimsPrincipal user, ConnectionCommands commands, CancellationToken ct) =>
            Invoke(() => commands.RegisterAsync(request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/fetch-certificate", (FetchServiceCertificate request,
            ConnectionCommands commands, CancellationToken ct) =>
            Invoke(() => commands.PreviewCertificateAsync(request, ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/{connectionId:guid}/smoke-read", (Guid connectionId, ConnectionCommands commands, CancellationToken ct) =>
            Invoke(() => commands.SmokeReadAsync(connectionId, ct)));
        group.MapPut("/{connectionId:guid}/onboarding", (Guid connectionId, UpdateConnectionOnboarding request,
            ClaimsPrincipal user, ConnectionCommands commands, CancellationToken ct) =>
            Invoke(() => commands.UpdateOnboardingAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapGet("/{connectionId:guid}/full-history-estimate", (Guid connectionId,
            ConnectionCommands commands, CancellationToken ct) =>
            Invoke(() => commands.EstimateFullHistoryAsync(connectionId, ct)));
        group.MapPost("/{connectionId:guid}/fetch-certificate", (Guid connectionId,
            ConnectionCommands commands, CancellationToken ct) =>
            Invoke(() => commands.FetchCertificateAsync(connectionId, ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/{connectionId:guid}/rebaseline", (Guid connectionId, RebaselineConnection request,
            ClaimsPrincipal user, ConnectionCommands commands, CancellationToken ct) =>
            Invoke(() => commands.RebaselineAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/{connectionId:guid}/archive", (Guid connectionId, ArchiveConnection request,
            ClaimsPrincipal user, ConnectionCommands commands, CancellationToken ct) => Invoke(() =>
                commands.ArchiveAsync(connectionId, request, Actor(user),
                    user.HasClaim(YojanaClaim.Role, YojanaRole.TenantAdministrator), ct)));
        group.MapGet("/{connectionId:guid}/health", (Guid connectionId, ConnectionQueries queries, CancellationToken ct) =>
            Invoke(() => queries.HealthAsync(connectionId, ct)));
        group.MapGet("/{connectionId:guid}/events", (Guid connectionId, string? cursor, int? take,
            ConnectionQueries queries, CancellationToken ct) => Invoke(() => queries.EventsAsync(connectionId, cursor, take ?? 50, ct)));
        app.MapGet("/api/integration/status", (string? cursor, int? take, ConnectionQueries queries, CancellationToken ct) =>
            Invoke(() => queries.StatusAsync(cursor, take ?? 50, ct))).RequireAuthorization(YojanaPolicy.IntegrationIndicator);
        return app;
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new ConnectionRuleException("audit_actor_required", "The authenticated user has no actor id.");
    private static async Task<IResult> Invoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ConnectionRuleException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message,
                statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404
                    : ex.Rule.EndsWith("forbidden", StringComparison.Ordinal) ? 403 : 422,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
        }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: 409,
                extensions: new Dictionary<string, object?>
                {
                    ["rule"] = ex.Rule,
                    ["unblockingStep"] = ex.UnblockingStep,
                });
        }
    }
}

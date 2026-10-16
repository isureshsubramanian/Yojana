using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Queries;
using Yojana.Serialisation.Search;
using Yojana.Serialisation.Selection;

namespace Yojana.Serialisation;

/// <summary>
/// HTTP surface of the P1 screens: read models directly, commands through the message bus (the tenant of the
/// signed-in session travels on the envelope; the handler's scope gets it from there). The client renders and
/// decides nothing; a refusal comes back as a 409 problem naming the rule and the unblocking step.
/// </summary>
public static class SerialisationEndpoints
{
    public static IEndpointRouteBuilder MapSerialisationEndpoints(this IEndpointRouteBuilder app)
    {
        var units = app.MapGroup("/api/units").RequireAuthorization(YojanaPolicy.TenantMember);
        units.MapGet("/lookup", async (string q, string? type, UnitQueries queries, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) ? Results.BadRequest(new { error = "q is required" }) : Results.Ok(await queries.LookupAsync(q.Trim(), type, ct)));
        units.MapGet("/search", async (string? q, string? type, Guid? item, string? status, Guid? owner, Guid? location, bool? superseded, string? sort, int? skip, int? take, string? document, UnitQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.SearchAsync(new UnitSearch(q, type, item, status, owner, location, superseded ?? true, sort ?? "identifier", Math.Max(0, skip ?? 0), take ?? 50, ParseDocument(document)), ct)));
        units.MapGet("/{unitId:guid}", async (Guid unitId, UnitQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(unitId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        units.MapGet("/{unitId:guid}/history", async (Guid unitId, int? skip, int? take, UnitQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.HistoryAsync(unitId, Math.Max(0, skip ?? 0), take ?? 50, ct)));
        units.MapPost("/scan", (ScanRequest request) => Results.Ok(ScanParser.Parse(request.Raw)));
        units.MapPost("/completeness", async (CompletenessQuery query, CompletenessQueries queries, CancellationToken ct) => Results.Ok(await queries.QueryAsync(query, ct)));
        units.MapPost("/commission", (CommissionUnits command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<CommissionUnitsResult>(bus, tenant, command, ct));
        units.MapPost("/assign-identifier", (AssignIdentifier command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<AssignIdentifierResult>(bus, tenant, command, ct));
        units.MapPost("/correct-identifier", (CorrectIdentifier command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<CorrectIdentifierResult>(bus, tenant, command, ct));
        units.MapPost("/release", (ReleaseUnit command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ReleaseUnitResult>(bus, tenant, command, ct));
        units.MapPost("/assemble", (RecordAssembly command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<RecordAssemblyResult>(bus, tenant, command, ct));
        units.MapPost("/disassemble", (RecordDisassembly command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<RecordDisassemblyResult>(bus, tenant, command, ct));
        units.MapPost("/flag", (SetUnitFlag command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<UnitFlagResult>(bus, tenant, command, ct));
        units.MapPost("/unflag", (ClearUnitFlag command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<UnitFlagResult>(bus, tenant, command, ct));

        var ranges = app.MapGroup("/api/serial-ranges").RequireAuthorization(YojanaPolicy.TenantMember);
        ranges.MapGet("/schemes", (SerialRangeQueries queries) => Results.Ok(queries.Schemes()));
        ranges.MapGet("/", async (string scheme, string? status, int? skip, int? take, SerialRangeQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListAsync(scheme, status, Math.Max(0, skip ?? 0), take ?? 50, ct)));
        ranges.MapPost("/", (AllocateSerialRange command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<AllocateSerialRangeResult>(bus, tenant, command, ct));

        // Global search (MOD-13): the palette's endpoint; the tier is back office until the portal (P7) adds its principal.
        app.MapGet("/api/search", async (string? q, string? scope, SearchQueries search, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) ? Results.Ok(new SearchResponse("", 0, [], [], null, "none")) : Results.Ok(await search.SearchAsync(q, scope, SearchDisclosureTier.Portal == scope ? SearchDisclosureTier.Portal : SearchDisclosureTier.BackOffice, ct)))
            .RequireAuthorization(YojanaPolicy.TenantMember);

        app.MapPost("/api/search/backfill", async (SearchBackfill backfill, CancellationToken ct) => Results.Ok(await backfill.BackfillUnitsAsync(ct)))
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));

        // Warranty policy editor (MOD-06, P2-T03): anyone in the tenant reads; the service manager writes.
        var policies = app.MapGroup("/api/warranty-policies").RequireAuthorization(YojanaPolicy.TenantMember);
        var editor = YojanaPolicy.ForRole(YojanaRole.ServiceManager);
        policies.MapGet("/", async (string? family, PolicyQueries queries, CancellationToken ct) => Results.Ok(await queries.ListAsync(family, ct)));
        policies.MapGet("/{policyCode}/versions", async (string policyCode, PolicyQueries queries, CancellationToken ct) => Results.Ok(await queries.VersionsAsync(policyCode, ct)));
        policies.MapGet("/versions/{policyVersionId:guid}", async (Guid policyVersionId, PolicyQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(policyVersionId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        policies.MapPost("/drafts", (CreatePolicyDraft command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command, ct)).RequireAuthorization(editor);
        policies.MapPut("/versions/{policyVersionId:guid}", (Guid policyVersionId, UpdatePolicyDraft command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { PolicyVersionId = policyVersionId }, ct)).RequireAuthorization(editor);
        policies.MapPost("/versions/{policyVersionId:guid}/validate", (Guid policyVersionId, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyValidation>(bus, tenant, new ValidatePolicyDraft(policyVersionId), ct));
        policies.MapPost("/versions/{policyVersionId:guid}/publish", (Guid policyVersionId, PublishPolicyVersion command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { PolicyVersionId = policyVersionId }, ct)).RequireAuthorization(editor);
        policies.MapPost("/versions/{policyVersionId:guid}/discard", (Guid policyVersionId, DiscardPolicyDraft command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { PolicyVersionId = policyVersionId }, ct)).RequireAuthorization(editor);
        var reference = app.MapGroup("/api/warranty-reference").RequireAuthorization(YojanaPolicy.TenantMember);
        reference.MapGet("/coverage-families", async (PolicyQueries queries, CancellationToken ct) => Results.Ok(await queries.FamiliesAsync(ct)));
        reference.MapPost("/coverage-families", (DefineCoverageFamily command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<CoverageFamilyRow>(bus, tenant, command, ct)).RequireAuthorization(editor);
        reference.MapGet("/replacement-policies", async (PolicyQueries queries, CancellationToken ct) => Results.Ok(await queries.ReplacementPoliciesAsync(ct)));
        reference.MapPost("/replacement-policies", (DefineReplacementPolicy command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ReplacementPolicyRow>(bus, tenant, command, ct)).RequireAuthorization(editor);

        var selections = app.MapGroup("/api/selections").RequireAuthorization(YojanaPolicy.TenantMember);
        selections.MapPost("/", (ResolveSelectionSet command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ResolveSelectionSetResult>(bus, tenant, command, ct));
        selections.MapPost("/{selectionId:guid}/preview", (Guid selectionId, PreviewBulkOperation command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<BulkPreviewResult>(bus, tenant, command with { SelectionId = selectionId }, ct));
        selections.MapPost("/{selectionId:guid}/execute", (Guid selectionId, ExecuteBulkOperation command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<BulkExecutionResult>(bus, tenant, command with { SelectionId = selectionId }, ct));
        selections.MapGet("/{selectionId:guid}/find", async (Guid selectionId, string q, int? skip, int? take, BulkExecutor executor, CancellationToken ct) =>
            Results.Ok(await executor.FindAsync(new FindInSelection(selectionId, q, Math.Max(0, skip ?? 0), take ?? 50), ct)));
        return app;
    }

    private static async Task<IResult> Invoke<TResult>(IMessageBus bus, ITenantContext tenant, object command, CancellationToken ct) where TResult : class
    {
        try
        {
            return Results.Ok(await bus.InvokeAsync<TResult>(command, new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, ct));
        }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
        }
    }

    /// <summary>"type:guid" as the search result href carries it; null when absent or malformed.</summary>
    private static DocumentRef? ParseDocument(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var colon = value.IndexOf(':');
        return colon > 0 && Guid.TryParse(value[(colon + 1)..], out var id) ? new DocumentRef(value[..colon], id) : null;
    }

    public sealed record ScanRequest(string Raw);
}

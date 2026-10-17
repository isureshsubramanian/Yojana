using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
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
        units.MapPost("/commission", (CommissionUnits command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<CommissionUnitsResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/assign-identifier", (AssignIdentifier command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<AssignIdentifierResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/correct-identifier", (CorrectIdentifier command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectIdentifierResult>(bus, tenant, command with { ActorRef = Actor(user), Correction = ApiCorrection(command, user) }, ct));
        units.MapPost("/release", (ReleaseUnit command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ReleaseUnitResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/assemble", (RecordAssembly command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<RecordAssemblyResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/disassemble", (RecordDisassembly command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<RecordDisassemblyResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/flag", (SetUnitFlag command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<UnitFlagResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/unflag", (ClearUnitFlag command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<UnitFlagResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));

        var corrections = app.MapGroup("/api/corrections").RequireAuthorization(YojanaPolicy.TenantMember);
        corrections.MapPost("/as-built", (CorrectAsBuilt command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        {
            var secured = Secure(command, user);
            return Invoke<CorrectAsBuiltResult>(bus, tenant, secured, ct);
        });
        corrections.MapPost("/as-built/preview", (PreviewCorrectAsBuilt preview, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        {
            var secured = preview with { Command = Secure(preview.Command, user) };
            return Invoke<CorrectionPreview>(bus, tenant, secured, ct);
        });

        var correctionReference = app.MapGroup("/api/correction-reference").RequireAuthorization(YojanaPolicy.TenantMember);
        correctionReference.MapGet("/approval", async (CorrectionApprovalSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        correctionReference.MapPut("/approval", async (CorrectionApprovalPolicy policy, CorrectionApprovalSettings settings, CancellationToken ct) =>
        {
            try
            {
                await settings.SetAsync(policy, ct);
                return Results.Ok(policy);
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        correctionReference.MapGet("/serial-reuse", async (SerialReuseSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        correctionReference.MapPut("/serial-reuse", async (SerialReusePolicy policy, SerialReuseSettings settings, CancellationToken ct) =>
        {
            try { await settings.SetAsync(policy, ct); return Results.Ok(policy); }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));

        var ranges = app.MapGroup("/api/serial-ranges").RequireAuthorization(YojanaPolicy.TenantMember);
        ranges.MapGet("/schemes", (SerialRangeQueries queries) => Results.Ok(queries.Schemes()));
        ranges.MapGet("/", async (string scheme, string? status, int? skip, int? take, SerialRangeQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListAsync(scheme, status, Math.Max(0, skip ?? 0), take ?? 50, ct)));
        ranges.MapPost("/", (AllocateSerialRange command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<AllocateSerialRangeResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));

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
        policies.MapPost("/drafts", (CreatePolicyDraft command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(editor);
        policies.MapPut("/versions/{policyVersionId:guid}", (Guid policyVersionId, UpdatePolicyDraft command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { ActorRef = Actor(user), PolicyVersionId = policyVersionId }, ct)).RequireAuthorization(editor);
        policies.MapPost("/versions/{policyVersionId:guid}/validate", (Guid policyVersionId, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyValidation>(bus, tenant, new ValidatePolicyDraft(policyVersionId), ct));
        policies.MapPost("/versions/{policyVersionId:guid}/publish", (Guid policyVersionId, PublishPolicyVersion command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { ActorRef = Actor(user), PolicyVersionId = policyVersionId }, ct)).RequireAuthorization(editor);
        policies.MapPost("/versions/{policyVersionId:guid}/discard", (Guid policyVersionId, DiscardPolicyDraft command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { ActorRef = Actor(user), PolicyVersionId = policyVersionId }, ct)).RequireAuthorization(editor);
        // Entitlements (P2-T05): the panel reads; the service desk and manager act. AmendEntitlement (P2-T08) is open to any
        // member: the handler checks the committer's roles against the tenant's approval policy for the reason and days.
        var entitlements = app.MapGroup("/api/entitlements").RequireAuthorization(YojanaPolicy.TenantMember);
        var desk = YojanaPolicy.ForRole(YojanaRole.ServiceDesk);
        units.MapGet("/{unitId:guid}/entitlements", async (Guid unitId, DateOnly? on, EntitlementQueries queries, IClock clock, CancellationToken ct) =>
            Results.Ok(await queries.ForUnitAsync(unitId, on ?? DateOnly.FromDateTime(clock.UtcNow), ct)));
        units.MapGet("/{unitId:guid}/entitlements/detail", async (Guid unitId, DateOnly? on, EntitlementQueries queries, IClock clock, CancellationToken ct) =>
            await queries.DetailForUnitAsync(unitId, on ?? DateOnly.FromDateTime(clock.UtcNow), ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        units.MapGet("/{unitId:guid}/warranty-check", async (Guid unitId, DateOnly? on, string? family, EntitlementQueries queries, IClock clock, CancellationToken ct) =>
            await queries.WarrantyCheckAsync(unitId, on ?? DateOnly.FromDateTime(clock.UtcNow), family, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        units.MapGet("/warranty-check", async (string identifier, string? type, DateOnly? on, string? family, UnitQueries unitQueries, EntitlementQueries entitlementQueries, IClock clock, CancellationToken ct) =>
        {
            var lookup = await unitQueries.LookupAsync(identifier, type, ct);
            var unitsFound = lookup.Hits.Select(h => h.UnitId).Distinct().ToList();
            if (unitsFound.Count == 0) return Results.NotFound();
            if (unitsFound.Count > 1) return Results.Conflict(new { reason = "ambiguous_identifier", lookup.Hits });
            return Results.Ok(await entitlementQueries.WarrantyCheckAsync(unitsFound[0], on ?? DateOnly.FromDateTime(clock.UtcNow), family, ct));
        });
        entitlements.MapPost("/", (CreateEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<CreateEntitlementResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        entitlements.MapPost("/extended", (AttachExtendedWarranty command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<AttachExtendedWarrantyResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        entitlements.MapPost("/backfill", (BackfillEntitlements command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BackfillEntitlementsResult>(bus, tenant, command with { ActorRef = Actor(user), CommittedBy = Committer(user) }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/rebase", (Guid entitlementId, RebaseEntitlementStart command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId }, ct)).RequireAuthorization(desk);
        entitlements.MapPost("/{entitlementId:guid}/suspend", (Guid entitlementId, SuspendEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/resume", (Guid entitlementId, ResumeEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/void", (Guid entitlementId, VoidEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/close", (Guid entitlementId, CloseEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/amend", (Guid entitlementId, AmendEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<AmendEntitlementResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId, CommittedBy = Committer(user) }, ct));
        entitlements.MapPost("/{entitlementId:guid}/supersede", (Guid entitlementId, SupersedeEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<SupersedeEntitlementResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId, CommittedBy = Committer(user) }, ct));
        entitlements.MapPost("/{entitlementId:guid}/transfer", (Guid entitlementId, TransferEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<TransferEntitlementResult>(bus, tenant, command with { ActorRef = Actor(user), SourceEntitlementId = entitlementId }, ct)).RequireAuthorization(editor);
        entitlements.MapGet("/requirements", async (string? status, string? documentType, Guid? document, int? skip, int? take, EntitlementRequirementQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.PageAsync(status, documentType, document, skip ?? 0, take ?? 50, ct)));
        entitlements.MapGet("/requirements/check", async (string documentType, Guid documentId, EntitlementRequirementQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.CheckAsync(documentType, documentId, ct)));
        entitlements.MapGet("/gap-report", async (string? items, DateOnly? saleFrom, DateOnly? saleTo, string? channels, string? dropShip,
            string? requirementStatus, string? sort, int? skip, int? take, EntitlementGapQueries queries, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        {
            try
            {
                var filters = EntitlementGapFilterParser.Parse(items, saleFrom, saleTo, channels, dropShip, requirementStatus, sort);
                await bus.InvokeAsync<EntitlementGapRunResult>(new RunEntitlementGapReport(Guid.NewGuid(), EntitlementGapTrigger.OnDemand, filters),
                    new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, ct);
                return Results.Ok(await queries.PageAsync(filters, skip ?? 0, take ?? 50, ct));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { reason = "filters_invalid", detail = ex.Message }); }
        });
        entitlements.MapGet("/gap-report/runs/latest", async (EntitlementGapQueries queries, CancellationToken ct) =>
            await queries.LastRunAsync(ct) is { } run ? Results.Ok(run) : Results.NotFound());
        entitlements.MapGet("/gap-report/export", async (string? items, DateOnly? saleFrom, DateOnly? saleTo, string? channels, string? dropShip,
            string? requirementStatus, string? sort, EntitlementGapQueries queries, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        {
            EntitlementGapFilters filters;
            try { filters = EntitlementGapFilterParser.Parse(items, saleFrom, saleTo, channels, dropShip, requirementStatus, sort); }
            catch (ArgumentException ex) { return Results.BadRequest(new { reason = "filters_invalid", detail = ex.Message }); }
            await bus.InvokeAsync<EntitlementGapRunResult>(new RunEntitlementGapReport(Guid.NewGuid(), EntitlementGapTrigger.OnDemand, filters),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, ct);
            return Results.Stream(async stream =>
            {
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(EntitlementGapCsv.Header.AsMemory(), ct);
                var offset = 0;
                while (true)
                {
                    var page = await queries.PageAsync(filters, offset, 500, ct);
                    foreach (var row in page.Rows) await writer.WriteAsync(EntitlementGapCsv.Row(row).AsMemory(), ct);
                    offset += page.Rows.Count;
                    if (offset >= page.Total || page.Rows.Count == 0) break;
                }
                await writer.FlushAsync(ct);
            }, "text/csv; charset=utf-8", "entitlement-gap-report.csv");
        });
        entitlements.MapPost("/requirements/{requirementId:guid}/waive", (Guid requirementId, WaiveEntitlementRequirement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<EntitlementRequirementRow>(bus, tenant, command with { ActorRef = Actor(user), RequirementId = requirementId }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/requirements/{requirementId:guid}/complete-transfer", (Guid requirementId, CompleteEntitlementTransfer command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<EntitlementRequirementRow>(bus, tenant, command with { ActorRef = Actor(user), RequirementId = requirementId }, ct)).RequireAuthorization(desk);
        entitlements.MapGet("/{entitlementId:guid}/disclosure", async (Guid entitlementId, EntitlementQueries queries, CancellationToken ct) =>
            await queries.DisclosureAsync(entitlementId, ct) is { } disclosure ? Results.Ok(disclosure) : Results.NotFound()).RequireAuthorization(YojanaPolicy.TenantMember);

        var reference = app.MapGroup("/api/warranty-reference").RequireAuthorization(YojanaPolicy.TenantMember);
        reference.MapGet("/coverage-families", async (PolicyQueries queries, CancellationToken ct) => Results.Ok(await queries.FamiliesAsync(ct)));
        reference.MapPost("/coverage-families", (DefineCoverageFamily command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<CoverageFamilyRow>(bus, tenant, command, ct)).RequireAuthorization(editor);
        reference.MapGet("/amendment-approval", async (AmendmentApprovalSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        reference.MapPut("/amendment-approval", async (AmendmentApprovalPolicy policy, AmendmentApprovalSettings settings, CancellationToken ct) =>
        {
            try
            {
                await settings.SetAsync(policy, ct);
                return Results.Ok(policy);
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
            }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        reference.MapGet("/refurbished-goods", async (RefurbishedGoodsSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        reference.MapPut("/refurbished-goods", async (RefurbishedGoodsPolicy policy, RefurbishedGoodsSettings settings, CancellationToken ct) =>
        {
            try
            {
                await settings.SetAsync(policy, ct);
                return Results.Ok(policy);
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
            }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        reference.MapGet("/service-contract-rules", async (WarrantyReferenceSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        reference.MapPut("/service-contract-rules", async (WarrantyReferencePolicy policy, WarrantyReferenceSettings settings, CancellationToken ct) =>
        {
            try { await settings.SetAsync(policy, ct); return Results.Ok(policy); }
            catch (CommandRefusedException ex) { return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        reference.MapGet("/replacement-policies", async (PolicyQueries queries, CancellationToken ct) => Results.Ok(await queries.ReplacementPoliciesAsync(ct)));
        reference.MapPost("/replacement-policies", (DefineReplacementPolicy command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ReplacementPolicyRow>(bus, tenant, command, ct)).RequireAuthorization(editor);

        var warrantyLines = app.MapGroup("/api/warranty-lines").RequireAuthorization(YojanaPolicy.TenantMember);
        warrantyLines.MapPost("/validate", async (WarrantyLineValidation request, WarrantyLineValidationService validation, CancellationToken ct) =>
            Results.Ok(await validation.ValidateAsync(request, ct)));
        warrantyLines.MapGet("/exceptions", async (string? status, int? skip, int? take, WarrantyLineExceptionQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.PageAsync(status, skip ?? 0, take ?? 50, ct)));
        warrantyLines.MapPost("/exceptions/{exceptionId:guid}/resolve", (Guid exceptionId, ResolveWarrantyLineException command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<WarrantyLineExceptionRow>(bus, tenant, command with { ActorRef = Actor(user), ExceptionId = exceptionId }, ct)).RequireAuthorization(desk);

        var selections = app.MapGroup("/api/selections").RequireAuthorization(YojanaPolicy.TenantMember);
        selections.MapPost("/", (ResolveSelectionSet command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ResolveSelectionSetResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        // Approver and roles come from the claims, never the body: the operations judge approval tiers on them.
        selections.MapPost("/{selectionId:guid}/preview", (Guid selectionId, PreviewBulkOperation command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkPreviewResult>(bus, tenant, command with { Arguments = command.Arguments with { ActorRef = Actor(user) }, SelectionId = selectionId, Approval = Approver(user, command.Approval) }, ct));
        selections.MapPost("/{selectionId:guid}/execute", (Guid selectionId, ExecuteBulkOperation command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkExecutionResult>(bus, tenant, command with { Arguments = command.Arguments with { ActorRef = Actor(user) }, SelectionId = selectionId, Approval = Approver(user, command.Approval)! }, ct));
        selections.MapGet("/{selectionId:guid}/find", async (Guid selectionId, string q, int? skip, int? take, BulkExecutor executor, CancellationToken ct) =>
            Results.Ok(await executor.FindAsync(new FindInSelection(selectionId, q, Math.Max(0, skip ?? 0), take ?? 50), ct)));
        // P2-T21 approval queue: requester, approver and their roles come from the claims, never the body.
        var approvals = app.MapGroup("/api/approval-requests").RequireAuthorization(YojanaPolicy.TenantMember);
        approvals.MapGet("/", async (string? status, int? skip, int? take, BulkApprovalRequestQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.PageAsync(status, skip ?? 0, take ?? 50, ct)));
        approvals.MapGet("/{requestId:guid}", async (Guid requestId, BulkApprovalRequestQueries queries, CancellationToken ct) =>
            await queries.GetAsync(requestId, ct) is { } row ? Results.Ok(row) : Results.NotFound());
        approvals.MapPost("/", (RequestBulkApproval command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkApprovalRequestRow>(bus, tenant, command with { Arguments = command.Arguments with { ActorRef = Actor(user) }, Requester = Approver(user, null) }, ct));
        approvals.MapPost("/{requestId:guid}/approve", (Guid requestId, ApproveBulkRequest command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkApprovalRequestRow>(bus, tenant, command with { RequestId = requestId, Approver = Approver(user, null) }, ct));
        approvals.MapPost("/{requestId:guid}/reject", (Guid requestId, RejectBulkRequest command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkApprovalRequestRow>(bus, tenant, command with { RequestId = requestId, Decider = Approver(user, null) }, ct));
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

    /// <summary>
    /// Who acts: the signed-in user's id, from the claims. Every command endpoint overwrites the body's ActorRef with it, so
    /// the ledger records who was signed in, never what a client claimed; screens show the name through IUserDirectory.
    /// </summary>
    private static string Actor(ClaimsPrincipal user) =>
        user.FindFirstValue(YojanaClaim.UserId) ?? throw new InvalidOperationException("A signed-in user carries a user id claim.");

    /// <summary>The signed-in user and their roles in the selected tenant, from the claims; never what a request body claims.</summary>
    private static AmendmentCommitter Committer(ClaimsPrincipal user) =>
        new(user.FindFirstValue(YojanaClaim.UserId) ?? throw new InvalidOperationException("A signed-in user carries a user id claim."), user.FindAll(YojanaClaim.Role).Select(c => c.Value).ToList());

    private static BulkApproval Approver(ClaimsPrincipal user, BulkApproval? given)
    {
        var committer = Committer(user);
        return new BulkApproval(committer.UserRef, given?.Required ?? false, committer.Roles);
    }

    private static CorrectionMetadata ApiCorrection(CorrectIdentifier command, ClaimsPrincipal user)
    {
        return ApiCorrection(command.CommandId, command.Reason, command.Note, command.Correction, user);
    }

    private static CorrectAsBuilt Secure(CorrectAsBuilt command, ClaimsPrincipal user) => command with
    {
        ActorRef = Actor(user),
        Correction = ApiCorrection(command.CommandId, command.Reason, command.Note, command.Correction, user),
        CommittedBy = CorrectionCommitter(user),
    };

    private static CorrectionCommitter CorrectionCommitter(ClaimsPrincipal user) =>
        new(Actor(user), user.FindAll(YojanaClaim.Role).Select(c => c.Value).ToList());

    private static CorrectionMetadata ApiCorrection(Guid commandId, string reason, string? note, CorrectionMetadata? supplied, ClaimsPrincipal user)
    {
        return new CorrectionMetadata(
            supplied?.CorrectionId ?? commandId,
            supplied?.ReasonCode ?? reason,
            supplied?.Note ?? note,
            supplied?.EvidenceRefs ?? [],
            supplied?.InitiatedVia ?? CorrectionInitiatedVia.Api,
            supplied?.CorrectsCorrectionId,
            InitiatingRole(user),
            // Approvers are never taken from the request body: a caller could name anyone, with any roles. The legacy
            // ApproverRef keeps its P1 meaning; server-recorded approvals arrive with the workbench approval flow (P3-T16).
            null);
    }

    private static string InitiatingRole(ClaimsPrincipal user)
    {
        var roles = user.FindAll(YojanaClaim.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        if (roles.Contains(YojanaRole.CustomerService)) return CorrectionInitiatingRole.CustomerService;
        if (roles.Contains(YojanaRole.WarehouseLead)) return CorrectionInitiatingRole.WarehouseLead;
        if (roles.Contains(YojanaRole.ProductionSupervisor)) return CorrectionInitiatingRole.ProductionSupervisor;
        if (roles.Contains(YojanaRole.Technician)) return CorrectionInitiatingRole.ServiceTechnician;
        return CorrectionInitiatingRole.SerialAdministrator;
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

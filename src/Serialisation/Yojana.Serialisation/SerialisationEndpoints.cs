using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.CustomerMail;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Claims.Risk;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Queries;
using Yojana.Serialisation.Search;
using Yojana.Serialisation.TimeZones;
using Yojana.Serialisation.Selection;
using Yojana.Serialisation.Workbench;
using Yojana.Serialisation.Campaigns;
using Yojana.Serialisation.Rmas;
using Yojana.Serialisation.Condition;
using Yojana.Serialisation.Refurbishments;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Labels;
using Yojana.Serialisation.Jurisdictions;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Imports;
using Yojana.Serialisation.Portal;
using Yojana.Serialisation.Reports;
using Yojana.Serialisation.Settings;
using CommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;

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
        app.MapGet("/api/public/portal/{tenantCode}/resolve", async (string tenantCode, string identifier,
            string? type, IPortalTenantResolver tenants, ITenantContextSetter tenantSetter,
            PublicPortalResolver resolver, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return Results.Problem(title: "identifier_required", detail: "An identifier is required.",
                    statusCode: StatusCodes.Status400BadRequest);
            var tenantId = await tenants.ResolveCodeAsync(tenantCode, ct);
            if (tenantId is null) return Results.NotFound();
            tenantSetter.Set(tenantId.Value);
            return Results.Ok(await resolver.ResolveAsync(tenantCode, identifier, type, ct));
        }).AllowAnonymous();

        var portalAdmin = app.MapGroup("/api/admin/portal").RequireAuthorization(YojanaPolicy.TenantAdministrator);
        portalAdmin.MapGet("/disclosure-policy", async (DisclosurePolicyService policy, CancellationToken ct) =>
            Results.Ok(await policy.GetAsync(ct)));
        portalAdmin.MapPut("/disclosure-policy/fields", async (UpdateDisclosureFields update, ClaimsPrincipal user,
            DisclosurePolicyService policy, CancellationToken ct) =>
        {
            try { return Results.Ok(await policy.SetFieldsAsync(update, Actor(user), ct)); }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
            }
        });
        portalAdmin.MapPut("/disclosure-policy/public-enabled", async (UpdatePublicPortalSetting update,
            DisclosurePolicyService policy, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(update.ExpectedVersion))
                return Results.Problem(title: "document_version_required",
                    detail: "ExpectedVersion is required for a public-portal setting edit.",
                    statusCode: StatusCodes.Status400BadRequest);
            try { return Results.Ok(await policy.SetPublicEnabledAsync(update, ct)); }
            catch (TenantSettingVersionConflictException ex)
            {
                return Results.Problem(title: "document_changed",
                    detail: "The public-portal setting changed after this view was loaded; reload and try again.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["rule"] = "document_changed", ["expectedVersion"] = ex.ExpectedVersion,
                        ["currentVersion"] = ex.CurrentVersion,
                    });
            }
        });
        portalAdmin.MapGet("/registration-settings", async (PortalRegistrationSettings settings,
            CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        portalAdmin.MapPut("/registration-settings", async (UpdatePortalRegistrationSettings update,
            PortalRegistrationSettings settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.SetAsync(update, ct)); }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
            }
            catch (TenantSettingVersionConflictException ex)
            {
                return Results.Problem(title: "document_changed",
                    detail: "The registration setting changed after this view was loaded; reload and try again.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["rule"] = "document_changed", ["expectedVersion"] = ex.ExpectedVersion,
                        ["currentVersion"] = ex.CurrentVersion,
                    });
            }
        });
        var portalReviews = app.MapGroup("/api/admin/portal/registrations")
            .RequireAuthorization(YojanaPolicy.ServiceDesk);
        portalReviews.MapGet("/", (string? status, string? cursor, int? take, PortalWriteService service,
            CancellationToken ct) => PortalWriteInvoke(() => service.ReviewsAsync(status, cursor, take ?? 50, ct)));
        portalReviews.MapPost("/{registrationId:guid}/approve", (Guid registrationId,
            ApprovePortalRegistrationRequest request, ClaimsPrincipal user, PortalWriteService service,
            CancellationToken ct) => PortalWriteInvoke(() => service.ApproveAsync(registrationId, request,
                Actor(user), ct)));

        // COUPLING (SQL portal RLS): TenantResolutionMiddleware recognises this policy metadata and sets the
        // read-only portal_party_ref session key before a DbContext opens. Changing this policy marker without
        // changing that middleware leaves these routes protected only by PortalOwnershipScope.
        var portal = app.MapGroup("/api/portal").RequireAuthorization(YojanaPolicy.CustomerPortal);
        portal.MapGet("/context", async (ClaimsPrincipal user, ITenantContext currentTenant,
            PortalQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ContextAsync(PortalActor(user, currentTenant), ct)));
        portal.MapGet("/lookup", async (string q, string? type, ClaimsPrincipal user, ITenantContext currentTenant,
            PortalQueries queries, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q)
                ? Results.Problem(title: "identifier_required", detail: "q is required.", statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(await queries.LookupAsync(PortalActor(user, currentTenant), q, type, ct)));
        portal.MapGet("/installed-base", async (bool? includeComponents, int? take, DateTime? afterDeliveryDate,
            Guid? afterUnitId, ClaimsPrincipal user, ITenantContext currentTenant, PortalQueries queries,
            CancellationToken ct) => Results.Ok(await queries.InstalledBaseAsync(PortalActor(user, currentTenant),
                includeComponents ?? false, take ?? 50, afterDeliveryDate, afterUnitId, ct)));
        portal.MapGet("/installed-base/export", async (string? format, bool? includeComponents, ClaimsPrincipal user,
            ITenantContext currentTenant, PortalQueries queries, ITabularExporter exporter, CancellationToken ct) =>
        {
            var requested = string.IsNullOrWhiteSpace(format) ? PortalExportFormat.Csv : format.ToLowerInvariant();
            if (requested is not PortalExportFormat.Csv and not PortalExportFormat.Xlsx)
                return Results.Problem(title: "export_format_unsupported", detail: $"Unsupported export format '{requested}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            var principal = PortalActor(user, currentTenant);
            return Results.Stream(stream => exporter.WriteAsync(requested, new TabularExport(
                ["serial", "item_code", "item_name", "customer_po_ref", "customer_item_ref", "delivery_date", "is_component", "coverage"],
                PortalInstalledBaseRows(queries.ExportInstalledBaseAsync(principal, includeComponents ?? false, ct), ct)),
                stream, ct), exporter.ContentType(requested), "installed-base" + exporter.Extension(requested));
        });
        portal.MapGet("/units/{unitId:guid}", async (Guid unitId, ClaimsPrincipal user,
            ITenantContext currentTenant, PortalQueries queries, CancellationToken ct) =>
            await queries.UnitAsync(PortalActor(user, currentTenant), unitId, ct) is { } unit
                ? Results.Ok(unit) : Results.NotFound());
        portal.MapGet("/units/{unitId:guid}/service-history", async (Guid unitId, int? take, DateTime? beforeAt,
            long? beforeSequence, ClaimsPrincipal user, ITenantContext currentTenant, PortalQueries queries,
            CancellationToken ct) => await queries.ServiceHistoryAsync(PortalActor(user, currentTenant), unitId,
                take ?? 50, beforeAt, beforeSequence, ct) is { } page ? Results.Ok(page) : Results.NotFound());
        portal.MapPost("/serial-list", async (PortalSerialListRequest request, ClaimsPrincipal user,
            ITenantContext currentTenant, PortalQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.PastedSerialsAsync(PortalActor(user, currentTenant), request, ct)); }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: "serial_list_too_large", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });
        portal.MapPost("/where-used", async (PortalSerialInput input, ClaimsPrincipal user,
            ITenantContext currentTenant, PortalQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.WhereUsedAsync(PortalActor(user, currentTenant), input, ct)));
        portal.MapGet("/units/{unitId:guid}/certificates", async (Guid unitId, int? take,
            DateTime? beforeIssuedAt, Guid? beforeCertificateId, ClaimsPrincipal user,
            ITenantContext currentTenant, PortalQueries queries, CancellationToken ct) =>
            await queries.CertificatesAsync(PortalActor(user, currentTenant), unitId, take ?? 50,
                beforeIssuedAt, beforeCertificateId, ct) is { } page ? Results.Ok(page) : Results.NotFound());
        portal.MapPost("/units/{unitId:guid}/registrations", async (Guid unitId,
            PortalRegistrationRequest request, ClaimsPrincipal user, ITenantContext currentTenant,
            PortalWriteService service, CancellationToken ct) =>
        {
            try
            {
                return await service.RegisterAsync(PortalActor(user, currentTenant), unitId, request,
                    Actor(user), ct) is { } result ? Results.Ok(result) : Results.NotFound();
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message,
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        });
        portal.MapPost("/units/{unitId:guid}/rmas", async (Guid unitId, PortalRmaRequest request,
            ClaimsPrincipal user, ITenantContext currentTenant, PortalWriteService service,
            CancellationToken ct) =>
        {
            try
            {
                return await service.RequestRmaAsync(PortalActor(user, currentTenant), unitId, request,
                    Actor(user), ct) is { } result ? Results.Ok(result) : Results.NotFound();
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message,
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        });

        app.MapGet("/api/admin/ledger-base-order-backfill", async (LedgerBaseOrderBackfill backfill, CancellationToken ct) =>
            Results.Ok(await backfill.GetProgressAsync(ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);

        var units = app.MapGroup("/api/units").RequireAuthorization(YojanaPolicy.TenantMember);
        units.MapGet("/lookup", async (string q, string? type, UnitQueries queries, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) ? ApiProblem("query_required", "q is required.") : Results.Ok(await queries.LookupAsync(q.Trim(), type, ct)));
        units.MapGet("/search", async (string? q, string? type, Guid? item, string? status, Guid? owner, Guid? location, bool? superseded, string? sort, int? skip, int? take, string? document, UnitQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.SearchAsync(new UnitSearch(q, type, item, status, owner, location, superseded ?? true, sort ?? "identifier", Math.Max(0, skip ?? 0), take ?? 50, ParseDocument(document)), ct)));
        units.MapGet("/search/export", (string? q, string? type, Guid? item, string? status, Guid? owner,
            Guid? location, bool? superseded, string? sort, string? document, string? format, HttpContext http,
            UnitQueries queries, ITabularExporter exporter, CancellationToken ct) => ListExport<UnitSearchRow>(format,
                "units", http, exporter, ExistingListExports.OffsetPages<UnitSearchRow>(async (skip, take, token) =>
                    (await queries.SearchAsync(new UnitSearch(q, type, item, status, owner, location,
                        superseded ?? true, sort ?? "identifier", skip, take, ParseDocument(document)), token)).Page.Items, ct)));
        units.MapGet("/{unitId:guid}", async (Guid unitId, UnitQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(unitId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        units.MapGet("/{unitId:guid}/history", async (Guid unitId, int? skip, int? take, UnitQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.HistoryAsync(unitId, Math.Max(0, skip ?? 0), take ?? 50, ct)));
        units.MapGet("/{unitId:guid}/coverage-decisions", async (Guid unitId, CoverageDecisionQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ForUnitAsync(unitId, ct)));
        units.MapPost("/scan", (ScanRequest request) => Results.Ok(ScanParser.Parse(request.Raw)));
        units.MapPost("/completeness", async (CompletenessQuery query, CompletenessQueries queries, CancellationToken ct) => Results.Ok(await queries.QueryAsync(query, ct)));
        units.MapPost("/commission", (CommissionUnits command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<CommissionUnitsResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/assign-identifier", (AssignIdentifier command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<AssignIdentifierResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/correct-identifier", (CorrectIdentifier command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectIdentifierResult>(bus, tenant, command with { ActorRef = Actor(user), Correction = ApiCorrection(command, user) }, ct));
        units.MapPost("/swap-identifiers/preview", (SwapIdentifiers command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectionPreview>(bus, tenant, new PreviewSwapIdentifiers(ApiSwap(command, user)), ct));
        units.MapPost("/swap-identifiers", (SwapIdentifiers command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<SwapIdentifiersResult>(bus, tenant, ApiSwap(command, user), ct));
        units.MapPost("/{unitId:guid}/correct-item/preview", (Guid unitId, CorrectItem command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        {
            var stamped = command with { UnitId = unitId, ActorRef = Actor(user), Correction = ApiCorrection(command, user), CommittedBy = CorrectionCommitter(user) };
            return Invoke<CorrectionPreview>(bus, tenant, new PreviewCorrectItem(stamped), ct);
        });
        units.MapPost("/{unitId:guid}/correct-item", (Guid unitId, CorrectItem command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectItemResult>(bus, tenant, command with { UnitId = unitId, ActorRef = Actor(user), Correction = ApiCorrection(command, user), CommittedBy = CorrectionCommitter(user) }, ct));
        units.MapPost("/{unitId:guid}/void/preview", (Guid unitId, VoidUnit command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        {
            var stamped = StampVoid(command with { UnitId = unitId }, user);
            return Invoke<CorrectionPreview>(bus, tenant, new PreviewVoidUnit(stamped), ct);
        });
        units.MapPost("/{unitId:guid}/void", (Guid unitId, VoidUnit command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<VoidUnitResult>(bus, tenant, StampVoid(command with { UnitId = unitId }, user), ct));
        units.MapPost("/merge/preview", (PreviewMergeUnits command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<MergeUnitsPreviewResult>(bus, tenant, command with { CommittedBy = CorrectionCommitter(user), Approvers = [] }, ct));
        units.MapPost("/merge", (MergeUnits command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<MergeUnitsResult>(bus, tenant, command with
            {
                ActorRef = Actor(user), CommittedBy = CorrectionCommitter(user),
                Correction = ApiCorrection(command.CommandId, command.Reason, command.Note, command.Correction, user),
            }, ct));
        units.MapPost("/release", (ReleaseUnit command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ReleaseUnitResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/assemble", (RecordAssembly command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<RecordAssemblyResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/disassemble", (RecordDisassembly command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<RecordDisassemblyResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/flag", (SetUnitFlag command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<UnitFlagResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapPost("/unflag", (ClearUnitFlag command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<UnitFlagResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        units.MapGet("/{unitId:guid}/condition", async (Guid unitId, ConditionQueries queries, CancellationToken ct) => Results.Ok(await queries.ConditionAsync(unitId, ct)));
        units.MapPost("/{unitId:guid}/condition", (Guid unitId, GradeUnitRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<GradeUnitResult>(bus, tenant, new GradeUnit(request.CommandId, unitId, request.Grade, request.ValuationRule, request.EventTime,
                Actor(user), request.LegalEntityId, request.FCode, request.CCode, request.Document), ct)).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.Quality));
        units.MapGet("/{unitId:guid}/sanitisation", async (Guid unitId, ConditionQueries queries, CancellationToken ct) => Results.Ok(await queries.SanitisationAsync(unitId, ct)));
        units.MapPost("/{unitId:guid}/sanitisation", (Guid unitId, RecordSanitisationRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RecordSanitisationResult>(bus, tenant, new RecordSanitisation(request.CommandId, unitId, request.Manufacturer, request.Model,
                request.MediaSerial, request.MediaType, request.Method, request.Tool, request.VerificationMethod, request.CertificateRef,
                request.EventTime, Actor(user), request.LegalEntityId, request.ToolVersion, request.SignatureRef, request.Document), ct)).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.Quality));
        units.MapGet("/{unitId:guid}/tests", async (Guid unitId, ConditionQueries queries, CancellationToken ct) => Results.Ok(await queries.TestsAsync(unitId, ct)));
        units.MapPost("/{unitId:guid}/tests", (Guid unitId, RecordTestRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RecordTestResult>(bus, tenant, new RecordTest(request.CommandId, unitId, request.TestKind, request.Result, request.MeasurementsJson,
                request.Equipment, request.EventTime, Actor(user), request.LegalEntityId, request.Document), ct)).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.Quality));
        units.MapGet("/{unitId:guid}/usage", async (Guid unitId, ConditionQueries queries, CancellationToken ct) => Results.Ok(await queries.UsageAsync(unitId, ct)));
        units.MapPost("/{unitId:guid}/usage", (Guid unitId, RecordUsageReadingRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RecordUsageReadingResult>(bus, tenant, new RecordUsageReading(request.CommandId, unitId, request.MeterKind, request.Value,
                request.Unit, request.ReadAt, Actor(user), request.LegalEntityId, request.MeterReplacement, request.Document), ct)).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.Technician));
        units.MapGet("/{unitId:guid}/attributes/history", async (Guid unitId, ConditionQueries queries, CancellationToken ct) => Results.Ok(await queries.AttributesAsync(unitId, ct)));
        units.MapPost("/{unitId:guid}/attributes/history", (Guid unitId, RecordUnitAttributesRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RecordUnitAttributesResult>(bus, tenant, new RecordUnitAttributes(request.CommandId, unitId, request.Attributes, request.EventTime,
                Actor(user), request.LegalEntityId, request.Document), ct)).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.Technician));

        var corrections = app.MapGroup("/api/corrections").RequireAuthorization(YojanaPolicy.TenantMember);
        corrections.MapPost("/document-serials/preview", (PreviewCorrectDocumentSerials command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectionPreview>(bus, tenant, command with
            {
                ActorRef = Actor(user), CommittedBy = CorrectionCommitter(user),
                Correction = ApiCorrection(Guid.Empty, command.Reason, null, command.Correction, user),
            }, ct));
        corrections.MapPost("/document-serials", (CorrectDocumentSerials command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectDocumentSerialsResult>(bus, tenant, command with
            {
                ActorRef = Actor(user), CommittedBy = CorrectionCommitter(user),
                Correction = ApiCorrection(command.CommandId, command.Reason, null, command.Correction, user),
            }, ct));
        corrections.MapPost("/source-document/preview", (PreviewCorrectSourceDocument command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectionPreview>(bus, tenant, command with
            {
                ActorRef = Actor(user),
                CommittedBy = CorrectionCommitter(user),
                Correction = ApiCorrection(Guid.Empty, command.Reason, command.Note, command.Correction, user),
            }, ct));
        corrections.MapPost("/source-document", (CorrectSourceDocument command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectSourceDocumentResult>(bus, tenant, command with
            {
                ActorRef = Actor(user),
                CommittedBy = CorrectionCommitter(user),
                Correction = ApiCorrection(command.CommandId, command.Reason, command.Note, command.Correction, user),
            }, ct));
        corrections.MapPost("/replacement-link/preview", (PreviewCorrectReplacementLink command, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectionPreview>(bus, tenant, command with
            {
                ActorRef = Actor(user), CommittedBy = CorrectionCommitter(user),
                Correction = ApiCorrection(Guid.Empty, command.Reason, command.Note, command.Correction, user),
            }, ct));
        corrections.MapPost("/replacement-link", (CorrectReplacementLink command, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectReplacementLinkResult>(bus, tenant, command with
            {
                ActorRef = Actor(user), CommittedBy = CorrectionCommitter(user),
                Correction = ApiCorrection(command.CommandId, command.Reason, command.Note, command.Correction, user),
            }, ct)).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.ServiceManager));
        corrections.MapPost("/reassign-allocation/preview", (ReassignAllocation command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ReassignAllocationResult>(bus, tenant, command with
            {
                ActorRef = Actor(user),
                Correction = ApiCorrection(command.CommandId, command.Reason, command.Note, command.Correction, user),
                CommittedBy = CorrectionCommitter(user),
                PreviewOnly = true,
            }, ct));
        corrections.MapPost("/reassign-allocation", (ReassignAllocation command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ReassignAllocationResult>(bus, tenant, command with
            {
                ActorRef = Actor(user),
                Correction = ApiCorrection(command.CommandId, command.Reason, command.Note, command.Correction, user),
                CommittedBy = CorrectionCommitter(user),
                PreviewOnly = false,
            }, ct));
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
        corrections.MapPost("/assign-identifiers/preview", (AssignIdentifiers command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<AssignIdentifiersPreview>(bus, tenant, new PreviewAssignIdentifiers(Secure(command, user)), ct));
        corrections.MapPost("/assign-identifiers", (AssignIdentifiers command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<AssignIdentifiersResult>(bus, tenant, Secure(command, user), ct));
        corrections.MapGet("/production-orders/{productionOrderId:guid}/awaiting-vendor-serial", async (Guid productionOrderId, AwaitingVendorSerialQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ByProductionOrderAsync(new DocumentRef("production_order", productionOrderId), ct)));
        corrections.MapGet("/production-orders/{productionOrderId:guid}/awaiting-vendor-serial/export",
            async (Guid productionOrderId, string? format, HttpContext http, AwaitingVendorSerialQueries queries,
                ITabularExporter exporter, CancellationToken ct) =>
            {
                var report = await queries.ByProductionOrderAsync(new DocumentRef("production_order", productionOrderId), ct);
                return ListExport<AwaitingVendorSerialUnit>(format, "awaiting-vendor-serial", http, exporter,
                    ExistingListExports.OnePage(Task.FromResult(report.Units), ct));
            });
        corrections.MapPost("/late-serialization", (LateSerialization command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<LateSerializationResult>(bus, tenant, Secure(command, user), ct));
        corrections.MapPost("/late-serialization/preview", (PreviewLateSerialization preview, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CorrectionPreview>(bus, tenant, preview with { Command = Secure(preview.Command, user) }, ct));
        corrections.MapGet("/kpi", async (DateOnly? from, DateOnly? to, string? breakdown, Guid? item, CorrectionKpiQueries queries, IClock clock, CancellationToken ct) =>
        {
            try
            {
                var end = to ?? DateOnly.FromDateTime(clock.UtcNow);
                return Results.Ok(await queries.QueryAsync(from ?? end.AddMonths(-11).AddDays(1 - end.Day), end, breakdown ?? CorrectionKpiBreakdown.RootCause, item, ct));
            }
            catch (ArgumentException ex) { return ApiProblem("kpi_query_invalid", ex.Message); }
        });
        corrections.MapGet("/kpi/export", async (DateOnly? from, DateOnly? to, string? breakdown, Guid? item, string? format,
            CorrectionKpiQueries queries, ITabularExporter exporter, IClock clock, CancellationToken ct) =>
        {
            CorrectionKpiReport report;
            try
            {
                var end = to ?? DateOnly.FromDateTime(clock.UtcNow);
                report = await queries.QueryAsync(from ?? end.AddMonths(-11).AddDays(1 - end.Day), end, breakdown ?? CorrectionKpiBreakdown.RootCause, item, ct);
            }
            catch (ArgumentException ex) { return ApiProblem("kpi_query_invalid", ex.Message); }
            var selected = (format ?? "csv").ToLowerInvariant();
            if (selected is not "csv" and not "xlsx") return ExportFormatProblem();
            return Results.Stream(stream => exporter.WriteAsync(selected, new TabularExport(
                ["month", "breakdown", "dimension", "corrections", "units_corrected", "units_shipped", "per_1000", "reversed_later", "target_per_1000"],
                CorrectionKpiRows(report.Breakdown, report.Rows, ct)), stream, ct), exporter.ContentType(selected), "correction-volume" + exporter.Extension(selected));
        });
        corrections.MapGet("/kpi/targets", async (CorrectionKpiTargetSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        corrections.MapPut("/kpi/targets", async (CorrectionKpiTargetPolicy policy, CorrectionKpiTargetSettings settings, CancellationToken ct) =>
        {
            try { await settings.SetAsync(policy, ct); return Results.Ok(policy); }
            catch (CommandRefusedException ex) { return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));

        var serialisationReferences = app.MapGroup("/api/serialisation").RequireAuthorization(YojanaPolicy.TenantMember);
        serialisationReferences.MapPost("/reference-names", async (ReferenceNamesRequest request, IItemNamePort items,
            IPartyNamePort parties, ILocationNamePort locations, IDocumentNumberPort documents, CancellationToken ct) =>
        {
            // Wire-contract cap mirrored by web/src/corrections/referenceStore.ts; change both sides together so its
            // batched requests cannot be refused after an otherwise independent client update.
            const int maximum = 200;
            if (request.Items.Count > maximum || request.Parties.Count > maximum || request.Documents.Count > maximum
                || (request.Locations?.Count ?? 0) > maximum)
            {
                return Results.Problem(title: "too_many_references",
                    detail: $"At most {maximum} item, partner, location and document references resolve in one call.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var itemLabels = await items.NamesAsync(request.Items.Distinct().ToList(), ct);
            var partyLabels = await parties.NamesAsync(request.Parties.Distinct().ToList(), ct);
            var locationLabels = await locations.NamesAsync((request.Locations ?? []).Distinct().ToList(), ct);
            var documentLabels = await documents.NumbersAsync(request.Documents
                .DistinctBy(document => new DocumentKey(document.Type, document.Id)).ToList(), ct);
            return Results.Ok(new ReferenceNamesResponse(
                itemLabels.ToDictionary(row => row.Key, row => new ItemReferenceView(row.Value.Code, row.Value.Name)),
                partyLabels.ToDictionary(row => row.Key, row => new PartyReferenceView(row.Value.Code, row.Value.Name, row.Value.PartyType)),
                documentLabels.ToDictionary(row => $"{row.Key.Type}:{row.Key.Id:D}",
                    row => new DocumentReferenceView(row.Value.Number, row.Value.Status, row.Value.ItemId)),
                locationLabels.ToDictionary(row => row.Key,
                    row => new LocationReferenceView(row.Value.Code, row.Value.Name, row.Value.Kind))));
        });
        serialisationReferences.MapGet("/item-families", async (IItemFamilyNamePort itemFamilies, CancellationToken ct) =>
            Results.Ok(await itemFamilies.ListAsync(ct)));
        serialisationReferences.MapGet("/reference-search", async (string kind, string? type, string? locationKind, string? q, int? take,
            IItemNamePort items, IPartyNamePort parties, ILocationNamePort locations, IDocumentNumberPort documents,
            UnitQueries unitQueries, CancellationToken ct) =>
        {
            var text = q?.Trim() ?? "";
            var count = Math.Clamp(take ?? 10, 1, 25);
            if (text.Length == 0) return Results.Ok(new ReferenceSearchResponse([]));
            switch (kind)
            {
                case "item":
                    return Results.Ok(new ReferenceSearchResponse((await items.SearchAsync(text, count, ct))
                        .Select(label => new ReferenceSearchResult("item", label.ItemId, Code: label.Code, Name: label.Name)).ToList()));
                case "party":
                    return Results.Ok(new ReferenceSearchResponse((await parties.SearchAsync(text, count, ct))
                        .Select(label => new ReferenceSearchResult("party", label.PartyRef, Code: label.Code, Name: label.Name, PartyType: label.PartyType)).ToList()));
                case "location":
                    return Results.Ok(new ReferenceSearchResponse((await locations.SearchAsync(text, count, locationKind, ct))
                        .Select(label => new ReferenceSearchResult("location", label.LocationId, Type: label.Kind,
                            Code: label.Code, Name: label.Name)).ToList()));
                case "unit":
                    // Units belong to this module, so the picker reads the unit search directly: live identifiers only,
                    // one result per unit, labelled with the identifier as printed.
                    var units = await unitQueries.SearchAsync(new UnitSearch(text, IncludeSuperseded: false, Take: count * 3), ct);
                    return Results.Ok(new ReferenceSearchResponse(units.Page.Items.DistinctBy(row => row.UnitId).Take(count)
                        .Select(row => new ReferenceSearchResult("unit", row.UnitId, Type: row.IdentifierType,
                            Number: row.RawValue ?? row.Identifier, Status: row.LifecycleStatus, ItemId: row.ItemId)).ToList()));
                case "document" when !string.IsNullOrWhiteSpace(type):
                    return Results.Ok(new ReferenceSearchResponse((await documents.FindAsync(type, text, count, ct))
                        .Select(label => new ReferenceSearchResult("document", label.Document.Id, Type: label.Document.Type,
                            Number: label.Number, Status: label.Status, ItemId: label.ItemId)).ToList()));
                case "document":
                    return Results.Problem(title: "document_type_required", detail: "type is required for document search.",
                        statusCode: StatusCodes.Status400BadRequest);
                default:
                    return Results.Problem(title: "reference_kind_invalid", detail: "kind must be item, party, location, document or unit.",
                        statusCode: StatusCodes.Status400BadRequest);
            }
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
        correctionReference.MapGet("/workbench-choices", async (WorkbenchChoiceSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.GetViewAsync(ct)))
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        correctionReference.MapPut("/workbench-choices", (UpdateWorkbenchChoices update, ClaimsPrincipal user, WorkbenchChoiceSettings settings, CancellationToken ct) =>
            WorkbenchInvoke(() => settings.SetAsync(update, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
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
        correctionReference.MapGet("/serial-confirmation", async (SerialConfirmationSettings settings, CancellationToken ct) =>
            Results.Ok(new SerialConfirmationSetting(await settings.GetAsync(ct))))
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        correctionReference.MapPut("/serial-confirmation", async (SerialConfirmationSetting setting, SerialConfirmationSettings settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.SetAsync(setting, ct)); }
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
        ranges.MapGet("/export", (string scheme, string? status, string? format, HttpContext http,
            SerialRangeQueries queries, ITabularExporter exporter, CancellationToken ct) => ListExport<SerialRangeRow>(
                format, "serial-ranges", http, exporter, ExistingListExports.OffsetPages<SerialRangeRow>(async (skip, take, token) =>
                    (await queries.ListAsync(scheme, status, skip, take, token)).Page.Items, ct)));
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
        policies.MapGet("/export", (string? family, string? format, HttpContext http, PolicyQueries queries,
            ITabularExporter exporter, CancellationToken ct) => ListExport<PolicySummary>(format, "warranty-policies",
                http, exporter, ExistingListExports.OnePage(queries.ListAsync(family, ct), ct)));
        policies.MapGet("/{policyCode}/versions", async (string policyCode, PolicyQueries queries, CancellationToken ct) => Results.Ok(await queries.VersionsAsync(policyCode, ct)));
        policies.MapGet("/{policyCode}/versions/export", (string policyCode, string? format, HttpContext http,
            PolicyQueries queries, ITabularExporter exporter, CancellationToken ct) => ListExport<PolicyVersionRow>(
                format, "warranty-policy-versions", http, exporter,
                ExistingListExports.OnePage(queries.VersionsAsync(policyCode, ct), ct)));
        policies.MapGet("/versions/{policyVersionId:guid}", async (Guid policyVersionId, PolicyQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(policyVersionId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        policies.MapPost("/drafts", (CreatePolicyDraft command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(editor);
        policies.MapPut("/versions/{policyVersionId:guid}", (Guid policyVersionId, UpdatePolicyDraft command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => InvokeEdit<PolicyDraftResult>(bus, tenant, command with { ActorRef = Actor(user), PolicyVersionId = policyVersionId }, ct)).RequireAuthorization(editor);
        policies.MapPost("/versions/{policyVersionId:guid}/validate", (Guid policyVersionId, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyValidation>(bus, tenant, new ValidatePolicyDraft(policyVersionId), ct));
        policies.MapPost("/versions/{policyVersionId:guid}/publish", (Guid policyVersionId, PublishPolicyVersion command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { ActorRef = Actor(user), PolicyVersionId = policyVersionId }, ct)).RequireAuthorization(editor);
        policies.MapPost("/versions/{policyVersionId:guid}/discard", (Guid policyVersionId, DiscardPolicyDraft command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<PolicyDraftResult>(bus, tenant, command with { ActorRef = Actor(user), PolicyVersionId = policyVersionId }, ct)).RequireAuthorization(editor);
        // Entitlements (P2-T05): the panel reads; the service desk and manager act. AmendEntitlement (P2-T08) is open to any
        // member: the handler checks the committer's roles against the tenant's approval policy for the reason and days.
        app.MapCoverDeviationEndpoints();
        app.MapCoverItemsEndpoints();
        app.MapCustomerMailEndpoints();
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
            if (unitsFound.Count > 1) return ApiProblem("ambiguous_identifier",
                "More than one unit matches this identifier.", StatusCodes.Status409Conflict,
                new Dictionary<string, object?> { ["hits"] = lookup.Hits });
            return Results.Ok(await entitlementQueries.WarrantyCheckAsync(unitsFound[0], on ?? DateOnly.FromDateTime(clock.UtcNow), family, ct));
        });
        entitlements.MapPost("/", (CreateEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<CreateEntitlementResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        entitlements.MapPost("/extended", (AttachExtendedWarranty command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<AttachExtendedWarrantyResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        entitlements.MapPost("/backfill", (BackfillEntitlements command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BackfillEntitlementsResult>(bus, tenant, command with { ActorRef = Actor(user), CommittedBy = Committer(user), Correction = ApiCorrection(command.CommandId, command.ReasonCode, null, command.Correction, user, command.EvidenceRefs) }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/rebase", (Guid entitlementId, RebaseEntitlementStart command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId }, ct)).RequireAuthorization(desk);
        entitlements.MapPost("/{entitlementId:guid}/suspend", (Guid entitlementId, SuspendEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/resume", (Guid entitlementId, ResumeEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/void", (Guid entitlementId, VoidEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId, Correction = ApiCorrection(command.CommandId, command.ClosedReason, command.Explanation, command.Correction, user, command.EvidenceRefs) }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/close", (Guid entitlementId, CloseEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntitlementChangeResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/{entitlementId:guid}/amend", (Guid entitlementId, AmendEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<AmendEntitlementResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId, CommittedBy = Committer(user), Correction = ApiCorrection(command.CommandId, command.ReasonCode, command.Note, command.Correction, user) }, ct));
        entitlements.MapPost("/{entitlementId:guid}/supersede", (Guid entitlementId, SupersedeEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<SupersedeEntitlementResult>(bus, tenant, command with { ActorRef = Actor(user), EntitlementId = entitlementId, CommittedBy = Committer(user), Correction = ApiCorrection(command.CommandId, command.ReasonCode, null, command.Correction, user) }, ct));
        entitlements.MapPost("/{entitlementId:guid}/transfer", (Guid entitlementId, TransferEntitlement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<TransferEntitlementResult>(bus, tenant, command with { ActorRef = Actor(user), SourceEntitlementId = entitlementId, Correction = ApiCorrection(command.CommandId, EntitlementTransferReason.Replacement, null, command.Correction, user) }, ct)).RequireAuthorization(editor);

        // Claims and supplier recovery (MOD-07, P4). Identity and reviewer roles come from the session; the endpoint
        // overwrites both values so a request body cannot forge the low-confidence review evidence.
        // P6 OD-26 numbering settings. Allocation stays behind DocumentNumbers; the API only changes its per-series contract.
        var numbering = app.MapGroup("/api/numbering").RequireAuthorization(YojanaPolicy.TenantAdministrator);
        numbering.MapGet("/settings", async (NumberingSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.GetAsync(ct)));
        numbering.MapPut("/settings/{series}", (string series, SetNumberingModeRequest request, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<SetNumberingModeResult>(bus, tenant,
                new SetNumberingMode(request.CommandId, series.ToUpperInvariant(), request.Mode, Actor(user)), ct));
        // End P6 OD-26 numbering settings.

        var claims = app.MapGroup("/api/claims").RequireAuthorization(YojanaPolicy.TenantMember);
        claims.MapGet("/", async (string? status, string? coverageSource, string? serviceCountry, string? recovery, string? q, Guid? campaignId, int? skip, int? take,
            ClaimQueries queries, UnitQueries units, IClock clock, CancellationToken ct) =>
        {
            IReadOnlyList<Guid>? unitIds = null;
            if (!string.IsNullOrWhiteSpace(q)) unitIds = (await units.LookupAsync(q.Trim(), null, ct)).Hits.Select(h => h.UnitId).Distinct().ToList();
            return Results.Ok(await queries.ListAsync(new ClaimListFilter(status ?? "open", coverageSource, serviceCountry, recovery, q, unitIds, campaignId),
                DateOnly.FromDateTime(clock.UtcNow), Math.Max(0, skip ?? 0), take ?? 50, ct));
        });
        claims.MapGet("/export", async (string? status, string? coverageSource, string? serviceCountry, string? recovery,
            string? q, Guid? campaignId, string? format, HttpContext http, ClaimQueries queries, UnitQueries units,
            IClock clock, ITabularExporter exporter, CancellationToken ct) =>
        {
            IReadOnlyList<Guid>? unitIds = null;
            if (!string.IsNullOrWhiteSpace(q)) unitIds = (await units.LookupAsync(q.Trim(), null, ct)).Hits
                .Select(hit => hit.UnitId).Distinct().ToList();
            var filter = new ClaimListFilter(status ?? "open", coverageSource, serviceCountry, recovery, q, unitIds, campaignId);
            return ListExport<ClaimListRow>(format, "claims", http, exporter,
                ExistingListExports.OffsetPages<ClaimListRow>(async (skip, take, token) =>
                    (await queries.ListAsync(filter, DateOnly.FromDateTime(clock.UtcNow), skip, take, token)).Items, ct));
        });
        claims.MapGet("/campaign-options", async (Guid unitId, DateOnly claimDate, ICampaignCoverPort campaigns, CancellationToken ct) =>
            Results.Ok((await campaigns.ListForUnitAsync(unitId, claimDate, ct)).Select(campaign => new ClaimCampaignOption(
                campaign.CampaignId, campaign.Code, campaign.Name, campaign.CoverageSource, campaign.ReserveTag, campaign.ReasonCode,
                campaign.RunsUntil, campaign.CoversOn(claimDate), campaign.NotCoveredReason(claimDate), campaign.PartsPct, campaign.LabourPct, campaign.TravelPct))));
        claims.MapGet("/summary", async (ClaimQueries queries, ClaimReviewSettings settings, IClock clock, CancellationToken ct) =>
            Results.Ok(await queries.SummaryAsync(DateOnly.FromDateTime(clock.UtcNow), (await settings.GetAsync(ct)).AdjudicationTargetWorkingDays, ct)));
        claims.MapGet("/risk-rules", async (ClaimRiskRulesSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.GetAsync(ct)));
        claims.MapPut("/risk-rules", async (ClaimRiskRulesPolicy policy, ClaimRiskRulesSettings settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.SetAsync(policy, ct)); }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        claims.MapGet("/risk-flags/report", async (DateOnly from, DateOnly to, ClaimRiskQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.ReportAsync(from, to, ct)); }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
        });
        claims.MapGet("/risk-flags", async (string? status, string? rule, DateOnly? from, DateOnly? to, string? cursor,
            int? take, ClaimRiskQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.ListAsync(status, rule, from, to, cursor, take ?? 50, ct)); }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
        });
        claims.MapGet("/risk-review", async (string? status, string? rule, DateOnly? from, DateOnly? to, string? cursor,
            int? take, ClaimRiskQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.ReviewAsync(status, rule, from, to, cursor, take ?? 50, ct)); }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
        });
        claims.MapGet("/risk-review/summary", async (DateOnly from, DateOnly to, ClaimRiskQueries queries,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.SummaryAsync(from, to, ct)); }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
        });
        claims.MapGet("/{claimId:guid}/risk-flags", async (Guid claimId, ClaimRiskQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ForClaimAsync(claimId, ct)));
        claims.MapPost("/{claimId:guid}/risk-flags/{flagId:guid}/decision", (Guid claimId, Guid flagId,
            ClaimRiskDecisionRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, IClock clock,
            CancellationToken ct) => Invoke<ClaimRiskFlagDecisionResult>(bus, tenant,
                new DecideClaimRiskFlag(claimId, flagId, request.Decision, request.Reason, clock.UtcNow, Actor(user), Roles(user)), ct));
        claims.MapGet("/{claimId:guid}/adjudication-preview", async (Guid claimId, ClaimAdjudicationPreviewQuery preview, ClaimReviewSettings settings, CancellationToken ct) =>
            await preview.PreviewAsync(claimId, (await settings.GetAsync(ct)).LowConfidenceReviewerRole, ct) is { } model ? Results.Ok(model) : Results.NotFound());
        claims.MapPost("/{claimId:guid}/request-evidence", (Guid claimId, RequestClaimEvidence command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            InvokeEdit<ClaimChangeResult>(bus, tenant, command with { ClaimId = claimId, ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        // Read by every claims screen (the recovery list's waiting rule); changed only by the tenant administrator.
        claims.MapGet("/recovery-policy", async (RecoverySettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        claims.MapPut("/recovery-policy", async (RecoveryPolicy policy, RecoverySettings settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.SetAsync(policy, ct)); }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        claims.MapGet("/review-policy", async (ClaimReviewSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)))
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        claims.MapPut("/review-policy", async (ClaimReviewPolicy policy, ClaimReviewSettings settings, CancellationToken ct) =>
        {
            try { await settings.SetAsync(policy, ct); return Results.Ok(policy); }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        claims.MapGet("/waiting-period-defaults", async (SerialisationDbContext db, ClaimWaitingPeriodSettings settings,
            ITenantContext tenant, CancellationToken ct) =>
        {
            var families = await db.CoverageFamilies.AsNoTracking().Where(f => f.TenantId == tenant.TenantId)
                .Select(f => f.FamilyCode).ToListAsync(ct);
            return Results.Ok(await settings.GetAsync(families, ct));
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        claims.MapPut("/waiting-period-defaults", async (ClaimWaitingPeriodDefaults policy, SerialisationDbContext db,
            ClaimWaitingPeriodSettings settings, ITenantContext tenant, CancellationToken ct) =>
        {
            try
            {
                var families = await db.CoverageFamilies.AsNoTracking().Where(f => f.TenantId == tenant.TenantId)
                    .Select(f => f.FamilyCode).ToListAsync(ct);
                return Results.Ok(await settings.SetAsync(policy, families, ct));
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        claims.MapGet("/{claimId:guid}", async (Guid claimId, ClaimQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(claimId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        claims.MapPost("/", (SubmitClaim command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<SubmitClaimResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        claims.MapPost("/{claimId:guid}/adjudicate", (Guid claimId, AdjudicateClaim command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            InvokeAdjudicate(bus, tenant, command with { ClaimId = claimId, ActorRef = Actor(user), ReviewedBy = ClaimReviewer(user) }, ct));
        claims.MapPost("/{claimId:guid}/settle", (Guid claimId, SettleClaim command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ClaimChangeResult>(bus, tenant, command with { ClaimId = claimId, ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        claims.MapPost("/{claimId:guid}/close", (Guid claimId, CloseClaim command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ClaimChangeResult>(bus, tenant, command with { ClaimId = claimId, ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);

        var rmas = app.MapGroup("/api/rmas").RequireAuthorization(YojanaPolicy.TenantMember);
        rmas.MapGet("/", async (string? stage, int? minimumAgeDays, int? maximumAgeDays, bool? exceptionsOnly, string? q, string? after, int? take,
            RmaQueries queries, IClock clock, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.ListAsync(new RmaListFilter(stage, minimumAgeDays, maximumAgeDays, q, exceptionsOnly ?? false), clock.UtcNow, after, take ?? 50, ct)); }
            catch (ArgumentException ex) { return ApiProblem("filters_invalid", ex.Message); }
        });
        rmas.MapGet("/export", (string? stage, int? minimumAgeDays, int? maximumAgeDays, bool? exceptionsOnly, string? q, string? format,
            HttpContext http, RmaQueries queries, IClock clock, ITabularExporter exporter, CancellationToken ct) =>
            ListExport<RmaListRow>(format, "rmas", http, exporter,
                ExistingListExports.CursorPages<RmaListRow>(async (after, take, token) =>
                {
                    var page = await queries.ListAsync(new RmaListFilter(stage, minimumAgeDays, maximumAgeDays, q, exceptionsOnly ?? false),
                        clock.UtcNow, after, take, token);
                    return (page.Rows, page.Continuation);
                }, ct)));
        rmas.MapPost("/stage-counters/rebuild", (RebuildRmaStageCounters command, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RebuildRmaStageCountersResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        rmas.MapGet("/{rmaId:guid}", async (Guid rmaId, RmaQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(rmaId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        rmas.MapPost("/request", (RequestRma command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RequestRmaResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        rmas.MapPost("/{rmaId:guid}/authorise", (Guid rmaId, AuthoriseRma command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<AuthoriseRmaResult>(bus, tenant, command with { RmaId = rmaId, ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.ServiceSupervisor);
        rmas.MapPost("/{rmaId:guid}/dispatch", (Guid rmaId, RecordReturnDispatched command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RecordReturnDispatchedResult>(bus, tenant, command with { RmaId = rmaId, ActorRef = Actor(user) }, ct))
            .RequireAuthorization(policy => policy.RequireClaim(YojanaClaim.Role, YojanaRole.ServiceDesk, YojanaRole.WarehouseLead));
        rmas.MapPost("/{rmaId:guid}/receive", (Guid rmaId, ReceiveReturn command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ReceiveReturnResult>(bus, tenant, command with { RmaId = rmaId, ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.WarehouseLead);
        rmas.MapPost("/{rmaId:guid}/disposition", (Guid rmaId, DispositionRma command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<DispositionRmaResult>(bus, tenant, command with { RmaId = rmaId, ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.ServiceSupervisor);
        rmas.MapPost("/{rmaId:guid}/close", (Guid rmaId, CloseRma command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            InvokeCloseRma(bus, tenant, command with { RmaId = rmaId, ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.ServiceSupervisor);

        // P5 refurbishment endpoints — kept as one block so sibling endpoint additions merge mechanically.
        var refurbishments = app.MapGroup("/api/refurbishments").RequireAuthorization(YojanaPolicy.TenantMember);
        refurbishments.MapGet("/", async (string? status, string? kind, Guid? unitId, int? skip, int? take,
            RefurbishmentQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.PageAsync(status, kind, unitId, Math.Max(0, skip ?? 0), take ?? 50, ct)));
        refurbishments.MapGet("/export", (string? status, string? kind, Guid? unitId, string? format,
            HttpContext http, RefurbishmentQueries queries, ITabularExporter exporter, CancellationToken ct) =>
            ListExport<RefurbishmentWorkOrderRow>(format, "refurbishments", http, exporter,
                ExistingListExports.OffsetPages<RefurbishmentWorkOrderRow>(async (skip, take, token) =>
                    (await queries.PageAsync(status, kind, unitId, skip, take, token)).Items, ct)));
        refurbishments.MapGet("/{workOrderId:guid}", async (Guid workOrderId, RefurbishmentQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(workOrderId, ct) is { } row ? Results.Ok(row) : Results.NotFound());
        refurbishments.MapPost("/", (OpenRefurbishmentRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<OpenRefurbishmentResult>(bus, tenant, new OpenRefurbishment(request.CommandId, request.TargetUnitId,
                request.Kind, request.OpenedAt, Actor(user), request.LegalEntityId, request.RepairLocationId,
                request.SourceRmaLineId, request.SourceDocument), ct)).RequireAuthorization(YojanaPolicy.ServiceSupervisor);
        refurbishments.MapPost("/rework", (OpenReworkRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<OpenReworkResult>(bus, tenant, new OpenRework(request.CommandId, request.TargetUnitIds,
                request.SelectionKind, request.SelectionReference, request.OpenedAt, Actor(user), request.LegalEntityId,
                request.RepairLocationId, request.SourceDocument), ct)).RequireAuthorization(YojanaPolicy.ProductionSupervisor);
        refurbishments.MapPost("/{workOrderId:guid}/operations", (Guid workOrderId, AddRefurbishmentOperationRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RefurbishmentOperationResult>(bus, tenant, new AddRefurbishmentOperation(request.CommandId,
                workOrderId, request.Sequence, request.Description, request.TechnicianRef, request.PlannedHours,
                request.EventTime, Actor(user), request.LegalEntityId), ct)).RequireAuthorization(YojanaPolicy.Technician);
        refurbishments.MapPost("/{workOrderId:guid}/operations/{operationId:guid}/complete", (Guid workOrderId,
            Guid operationId, CompleteRefurbishmentOperationRequest request, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) => Invoke<RefurbishmentOperationResult>(bus, tenant,
                new CompleteRefurbishmentOperation(request.CommandId, workOrderId, operationId, request.ActualHours,
                    request.CompletedAt, Actor(user), request.LegalEntityId), ct)).RequireAuthorization(YojanaPolicy.Technician);
        refurbishments.MapPost("/{workOrderId:guid}/parts/remove", (Guid workOrderId, RemoveRefurbishmentPartRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RefurbishmentPartResult>(bus, tenant, new RemoveRefurbishmentPart(request.CommandId, workOrderId,
                request.AssemblyLinkId, request.Destination, request.EventTime, Actor(user), request.LegalEntityId,
                request.DestinationLocationId, request.VendorPartyRef), ct)).RequireAuthorization(YojanaPolicy.Technician);
        refurbishments.MapPost("/{workOrderId:guid}/parts/install", (Guid workOrderId, InstallRefurbishmentPartRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RefurbishmentPartResult>(bus, tenant, new InstallRefurbishmentPart(request.CommandId, workOrderId,
                request.Part, request.UnitCost, request.Currency, request.EventTime, Actor(user), request.LegalEntityId,
                request.RepairLocationId), ct)).RequireAuthorization(YojanaPolicy.Technician);
        refurbishments.MapPost("/{workOrderId:guid}/labour", (Guid workOrderId, RecordRefurbishmentLabourRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RefurbishmentLabourResult>(bus, tenant, new RecordRefurbishmentLabour(request.CommandId, workOrderId,
                request.OperationId, request.Hours, request.HourlyRate, request.Currency, request.EventTime,
                Actor(user), request.LegalEntityId), ct)).RequireAuthorization(YojanaPolicy.Technician);
        refurbishments.MapPost("/{workOrderId:guid}/costs", (Guid workOrderId, RecordRefurbishmentCostRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RefurbishmentCostResult>(bus, tenant, new RecordRefurbishmentCost(request.CommandId, workOrderId,
                request.Kind, request.Amount, request.Currency, request.Description, request.EventTime,
                Actor(user), request.LegalEntityId), ct)).RequireAuthorization(YojanaPolicy.InventoryController);
        refurbishments.MapPost("/{workOrderId:guid}/release", (Guid workOrderId, ReleaseRefurbishmentRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ReleaseRefurbishmentResult>(bus, tenant, new ReleaseRefurbishment(request.CommandId, workOrderId,
                request.Grade, request.ValuationRule, request.ReleasedAt, Actor(user), request.LegalEntityId,
                request.WarehouseLocationId, request.NewSerial, request.FCode, request.CCode), ct)).RequireAuthorization(YojanaPolicy.ServiceSupervisor);
        refurbishments.MapPost("/{workOrderId:guid}/cancel", (Guid workOrderId, CancelRefurbishmentRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CancelRefurbishmentResult>(bus, tenant, new CancelRefurbishment(request.CommandId, workOrderId,
                request.Reason, request.CancelledAt, Actor(user), request.LegalEntityId, request.WarehouseLocationId), ct))
            .RequireAuthorization(YojanaPolicy.ServiceSupervisor);
        refurbishments.MapGet("/policy", async (RefurbishmentPolicySettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        refurbishments.MapPut("/policy", async (RefurbishmentPolicy policy, RefurbishmentPolicySettings settings, CancellationToken ct) =>
        {
            try { await settings.SetAsync(policy, ct); return Results.Ok(policy); }
            catch (CommandRefusedException ex) { return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        }).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        // End P5 refurbishment endpoints.

        // P6 labels block. Reads are tenant-wide; template lifecycle is administrative, sign-off is independent,
        // and operational roles print through the narrow LabelPrinter policy.
        var labels = app.MapGroup("/api/labels").RequireAuthorization(YojanaPolicy.TenantMember);
        labels.MapGet("/templates", async (string? name, string? appliesTo, string? status, int? skip, int? take,
            LabelQueries queries, CancellationToken ct) => Results.Ok(await queries.TemplatesAsync(name, appliesTo, status,
                skip ?? 0, take ?? 50, ct)));
        labels.MapGet("/templates/export", (string? name, string? appliesTo, string? status, string? format,
            HttpContext http, LabelQueries queries, ITabularExporter exporter, CancellationToken ct) =>
            ListExport<LabelTemplateCatalogueRow>(format, "label-templates", http, exporter,
                ExistingListExports.OffsetPages<LabelTemplateCatalogueRow>((skip, take, token) =>
                    queries.TemplatesAsync(name, appliesTo, status, skip, take, token), ct)));
        labels.MapGet("/templates/{templateId:guid}", async (Guid templateId, LabelQueries queries, CancellationToken ct) =>
            await queries.TemplateAsync(templateId, ct) is { } template ? Results.Ok(template) : Results.NotFound());
        labels.MapGet("/bindable-fields", () => Results.Ok(LabelBindableField.All.Select(field =>
            new LabelBindableFieldRow(field, field == LabelBindableField.BaseEntitlementEndDate))));
        labels.MapPost("/templates", (CreateLabelTemplateRequest request, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) => Invoke<LabelTemplateCreated>(bus, tenant,
                new CreateLabelTemplate(request.CommandId, request.Name, request.AppliesTo, request.AppliesToValue,
                    Actor(user)), ct)).RequireAuthorization(YojanaPolicy.LabelAdministrator);
        labels.MapPost("/templates/{templateId:guid}/versions/draft", (Guid templateId, SaveLabelTemplateDraftRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<LabelTemplateVersionSaved>(bus, tenant, new SaveLabelTemplateDraft(request.CommandId, templateId,
                request.TemplateVersionId, request.EffectiveFrom, request.EffectiveTo, request.LayoutKind, request.Layout,
                request.StaticText, request.Bindings, request.Symbology, request.PreviewSampleUnitId, request.Regulated,
                Actor(user)), ct))
            .RequireAuthorization(YojanaPolicy.LabelAdministrator);
        labels.MapPost("/versions/{versionId:guid}/preview", (Guid versionId, PreviewLabelTemplateRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<LabelTemplatePreview>(bus, tenant, new PreviewLabelTemplate(versionId, request.SampleUnitId,
                Actor(user)), ct))
            .RequireAuthorization(YojanaPolicy.LabelAdministrator);
        labels.MapPost("/versions/{versionId:guid}/legal-review/request", (Guid versionId, LabelVersionCommandRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<LabelTemplateVersionChanged>(bus, tenant, new RequestLabelLegalReview(request.CommandId, versionId,
                Actor(user)), ct))
            .RequireAuthorization(YojanaPolicy.LabelAdministrator);
        labels.MapPost("/versions/{versionId:guid}/legal-review/sign", (Guid versionId, LabelVersionCommandRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<LabelTemplateVersionChanged>(bus, tenant, new SignLabelLegalReview(request.CommandId, versionId,
                Actor(user)), ct))
            .RequireAuthorization(YojanaPolicy.LegalReviewer);
        labels.MapPost("/versions/{versionId:guid}/approve", (Guid versionId, LabelVersionCommandRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<LabelTemplateVersionChanged>(bus, tenant, new ApproveRegulatedLabel(request.CommandId, versionId,
                Actor(user)), ct))
            .RequireAuthorization(YojanaPolicy.LegalReviewer);
        labels.MapPost("/versions/{versionId:guid}/activate", (Guid versionId, ActivateLabelTemplateVersionRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<LabelTemplateVersionChanged>(bus, tenant, new ActivateLabelTemplateVersion(request.CommandId,
                versionId, request.ActivatedAt, Actor(user)), ct))
            .RequireAuthorization(YojanaPolicy.LabelAdministrator);
        labels.MapPost("/versions/{versionId:guid}/retire", (Guid versionId, RetireLabelTemplateVersionRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<LabelTemplateVersionChanged>(bus, tenant, new RetireLabelTemplateVersion(request.CommandId,
                versionId, request.RetiredAt, Actor(user)), ct))
            .RequireAuthorization(YojanaPolicy.LabelAdministrator);
        labels.MapGet("/settings", async (LabelSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        labels.MapPut("/settings", async (LabelPolicy policy, LabelSettings settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.SetAsync(policy, ct)); }
            catch (CommandRefusedException ex) { return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        }).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        labels.MapGet("/print-jobs", async (string? status, Guid? template, string? find, string? serial,
            string? printer, string? kind, DateTime? from, DateTime? to, DateTime? requestedFrom,
            DateTime? requestedTo, int? skip, int? take, LabelQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.JobsAsync(status, template, find ?? serial, printer, kind,
                from ?? requestedFrom, to ?? requestedTo, skip ?? 0, take ?? 50, ct)));
        labels.MapGet("/print-jobs/export", (string? status, Guid? template, string? find, string? serial,
            string? printer, string? kind, DateTime? from, DateTime? to, DateTime? requestedFrom,
            DateTime? requestedTo, string? format, HttpContext http, LabelQueries queries,
            ITabularExporter exporter, CancellationToken ct) => ListExport<LabelPrintJobRow>(format,
                "label-print-jobs", http, exporter, ExistingListExports.OffsetPages<LabelPrintJobRow>(async (skip, take, token) =>
                    (await queries.JobsAsync(status, template, find ?? serial, printer, kind,
                        from ?? requestedFrom, to ?? requestedTo, skip, take, token)).Rows, ct)));
        labels.MapGet("/print-jobs/summary", async (DateTime from, DateTime to, LabelQueries queries,
            CancellationToken ct) => Results.Ok(await queries.SummaryAsync(from, to, ct)));
        labels.MapGet("/print-jobs/printers", async (DateTime? from, DateTime? to, LabelQueries queries,
            CancellationToken ct) => Results.Ok(await queries.PrintersAsync(from, to, ct)));
        labels.MapGet("/print-jobs/{jobId:guid}", async (Guid jobId, LabelQueries queries, CancellationToken ct) =>
            await queries.JobAsync(jobId, ct) is { } job ? Results.Ok(job) : Results.NotFound());
        labels.MapPost("/print-jobs", (PrintLabelRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant,
            CancellationToken ct) => Invoke<LabelPrintResult>(bus, tenant, new PrintLabel(request.CommandId,
                request.UnitId, request.TemplateId, request.IdentifierId, request.PrinterRef, request.PrintAt,
                request.LegalEntityId, Actor(user)), ct))
            .RequireAuthorization(YojanaPolicy.LabelPrinter);
        labels.MapPost("/print-jobs/batch", (BatchPrintLabelRequest request, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, IClock clock, CancellationToken ct) => Invoke<LabelBatchPrintResult>(bus, tenant,
                new BatchPrintLabels(request.CommandId, request.TemplateVersionId, request.PrinterRef,
                    request.Serials, clock.UtcNow, Actor(user)), ct))
            .RequireAuthorization(YojanaPolicy.LabelPrinter);
        labels.MapPost("/print-jobs/{jobId:guid}/reprint", (Guid jobId, ReprintLabelRequest request, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<LabelPrintResult>(bus, tenant,
                new ReprintLabel(request.CommandId, jobId, request.PrinterRef, request.ReprintedAt,
                    request.LegalEntityId, Actor(user)), ct)).RequireAuthorization(YojanaPolicy.LabelPrinter);
        // End P6 labels block.

        // P6 jurisdiction catalogue block. Configuration is tenant data; application code consumes only the narrow
        // resolver ports. Actors are stamped here and edits require the version observed by the administrator.
        var jurisdictions = app.MapGroup("/api/settings/jurisdictions").RequireAuthorization(YojanaPolicy.TenantMember);
        jurisdictions.MapGet("/", (string? country, string? kind, JurisdictionCatalogueService service, CancellationToken ct) =>
            WorkbenchInvoke(() => service.CatalogueAsync(country, kind, ct)));
        jurisdictions.MapGet("/packs/{rulePackId:guid}/preview", async (Guid rulePackId,
            JurisdictionCatalogueService service, CancellationToken ct) =>
            await service.PreviewPackAsync(rulePackId, ct) is { } preview ? Results.Ok(preview) : Results.NotFound());
        jurisdictions.MapPost("/region-groups", (SaveRegionGroup command, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) => command.RegionGroupId is null
                ? Invoke<RegionGroupSaved>(bus, tenant, command with { ActorRef = Actor(user) }, ct)
                : InvokeEdit<RegionGroupSaved>(bus, tenant, command with { ActorRef = Actor(user) }, ct))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        jurisdictions.MapPost("/rules", (SaveJurisdictionRule command, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) => command.RuleId is null
                ? Invoke<JurisdictionRuleSaved>(bus, tenant, command with { ActorRef = Actor(user) }, ct)
                : InvokeEdit<JurisdictionRuleSaved>(bus, tenant, command with { ActorRef = Actor(user) }, ct))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        jurisdictions.MapPost("/rules/{ruleId:guid}/close", (Guid ruleId, CloseJurisdictionRule command,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            InvokeEdit<JurisdictionRuleClosed>(bus, tenant, command with { RuleId = ruleId, ActorRef = Actor(user) }, ct))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        jurisdictions.MapPost("/packs/{rulePackId:guid}/import", (Guid rulePackId, ImportRulePack command,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RulePackImported>(bus, tenant, command with { RulePackId = rulePackId, ActorRef = Actor(user) }, ct))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        jurisdictions.MapPost("/what-applies", (JurisdictionWhatAppliesRequest request,
            JurisdictionCatalogueService service, CancellationToken ct) => WorkbenchInvoke(async () =>
        {
            var kinds = string.IsNullOrWhiteSpace(request.Kind)
                ? (await service.CatalogueAsync(null, null, ct)).Kinds.Select(row => row.Kind)
                : [request.Kind];
            var rules = new List<JurisdictionRuleResolution>();
            foreach (var kind in kinds) rules.AddRange(await service.ResolveAsync(kind, request.Context, ct));
            return new JurisdictionWhatAppliesResult(rules);
        }));
        // End P6 jurisdiction catalogue block.

        var replacements = app.MapGroup("/api/warranty-replacements").RequireAuthorization(YojanaPolicy.TenantMember);
        replacements.MapGet("/", async (string? status, int? skip, int? take, Replacements.WarrantyReplacementQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListAsync(status, skip ?? 0, take ?? 50, ct)));
        replacements.MapGet("/export", (string? status, string? format, HttpContext http,
            Replacements.WarrantyReplacementQueries queries, ITabularExporter exporter, CancellationToken ct) =>
            ListExport<WarrantyReplacementRow>(format, "warranty-replacements", http, exporter,
                ExistingListExports.OffsetPages<WarrantyReplacementRow>(async (skip, take, token) =>
                    (await queries.ListAsync(status, skip, take, token)).Rows, ct)));
        replacements.MapGet("/pending", async (int? skip, int? take, Replacements.ReplacementLinkQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.PendingAsync(skip ?? 0, take ?? 50, ct)));
        replacements.MapGet("/pending/export", (string? format, HttpContext http,
            Replacements.ReplacementLinkQueries queries, ITabularExporter exporter, CancellationToken ct) =>
            ListExport<PendingWarrantyReplacementRow>(format, "pending-warranty-replacements", http, exporter,
                ExistingListExports.OffsetPages<PendingWarrantyReplacementRow>(async (skip, take, token) =>
                    (await queries.PendingAsync(skip, take, token)).Rows, ct)));
        replacements.MapGet("/{warrantyReplacementId:guid}", async (Guid warrantyReplacementId, Replacements.WarrantyReplacementQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(warrantyReplacementId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        replacements.MapPost("/", (CreateWarrantyReplacement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CreateWarrantyReplacementResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        replacements.MapPost("/{warrantyReplacementId:guid}/ship", (Guid warrantyReplacementId, ShipWarrantyReplacement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ShipWarrantyReplacementResult>(bus, tenant, command with { WarrantyReplacementId = warrantyReplacementId, ActorRef = Actor(user) }, ct)).RequireAuthorization(desk);
        replacements.MapPost("/convert-loan", (ConvertLoanToReplacement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ConvertLoanToReplacementResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.ServiceSupervisor);
        replacements.MapPost("/vendor-receipt", (ReceiveVendorReplacement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ReceiveVendorReplacementResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.WarehouseLead);

        // P5 advance-replacement block kept separate so sibling endpoint additions merge mechanically.
        var replacementLinks = app.MapGroup("/api/replacement-links").RequireAuthorization(YojanaPolicy.TenantMember);
        replacementLinks.MapGet("/", async (string? state, bool? overdue, int? dueWithinDays, string? q, int? skip, int? take, Replacements.ReplacementLinkQueries queries, IClock clock, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.PageAsync(state, overdue ?? false, dueWithinDays, q, DateOnly.FromDateTime(clock.UtcNow), skip ?? 0, take ?? 50, ct)); }
            catch (ArgumentException ex) { return ApiProblem("filters_invalid", ex.Message); }
        });
        replacementLinks.MapGet("/export", (string? state, bool? overdue, int? dueWithinDays, string? q,
            string? format, HttpContext http, Replacements.ReplacementLinkQueries queries, IClock clock,
            ITabularExporter exporter, CancellationToken ct) => ListExport<ReplacementLinkRow>(format,
                "replacement-links", http, exporter, ExistingListExports.OffsetPages<ReplacementLinkRow>(async (skip, take, token) =>
                    (await queries.PageAsync(state, overdue ?? false, dueWithinDays, q,
                        DateOnly.FromDateTime(clock.UtcNow), skip, take, token)).Rows, ct)));
        replacementLinks.MapPost("/{replacementLinkId:guid}/extend-return-due", (Guid replacementLinkId, ExtendReturnDue command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ExtendReturnDueResult>(bus, tenant, command with { ReplacementLinkId = replacementLinkId, ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.ServiceSupervisor);
        replacementLinks.MapPost("/{replacementLinkId:guid}/bill-non-return", (Guid replacementLinkId, BillNonReturn command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BillNonReturnResult>(bus, tenant, command with { ReplacementLinkId = replacementLinkId, ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.ServiceSupervisor);
        replacementLinks.MapPost("/holds/{partyRef:guid}/lift", (Guid partyRef, LiftReplacementHold command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<LiftReplacementHoldResult>(bus, tenant, command with { PartyRef = partyRef, ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.ServiceSupervisor);

        var recoveryClaims = app.MapGroup("/api/recovery-claims").RequireAuthorization(YojanaPolicy.TenantMember);
        recoveryClaims.MapGet("/", async (string? status, Guid? vendor, Guid? childItem, string? find, int? skip, int? take, RecoveryQueries queries, IPartyNamePort parties, IClock clock, CancellationToken ct) =>
        {
            try
            {
                var matchingVendors = string.IsNullOrWhiteSpace(find) ? [] : (await parties.SearchAsync(find, 25, ct)).Select(row => row.PartyRef).ToList();
                return Results.Ok(await queries.PageAsync(status, vendor, childItem, find, matchingVendors, DateOnly.FromDateTime(clock.UtcNow), skip ?? 0, take ?? 50, ct));
            }
            catch (ArgumentException ex) { return ApiProblem("filters_invalid", ex.Message); }
        });
        recoveryClaims.MapGet("/summary", async (RecoveryQueries queries, IClock clock, CancellationToken ct) =>
            Results.Ok(await queries.SummaryAsync(DateOnly.FromDateTime(clock.UtcNow), ct)));
        recoveryClaims.MapGet("/not-raised", async (int? days, RecoveryQueries queries, IClock clock, CancellationToken ct) =>
            Results.Ok(await queries.NotRaisedAsync(days ?? 30, DateOnly.FromDateTime(clock.UtcNow), ct)));
        recoveryClaims.MapGet("/export", async (string? status, Guid? vendor, Guid? childItem, string? find, string? format,
            HttpContext http, RecoveryQueries queries, IItemNamePort items, IPartyNamePort parties, IClock clock,
            ITabularExporter exporter, CancellationToken ct) =>
        {
            try { _ = RecoveryClaimFilter.Parse(status); }
            catch (ArgumentException ex) { return ApiProblem("filters_invalid", ex.Message); }
            var matchingVendors = string.IsNullOrWhiteSpace(find) ? [] : (await parties.SearchAsync(find, 25, ct)).Select(row => row.PartyRef).ToList();
            return DirectExport(format, "recovery-claims", http, exporter,
                ["recovery_number", "raised_at", "customer_claim", "claim_date", "child_serial", "child_item", "vendor",
                    "vendor_term_start", "vendor_term_end", "term_months", "claim_month_of_term", "requested", "approved",
                    "currency", "status", "vendor_reference", "vendor_note"],
                RecoveryClaimRows(status, vendor, childItem, find, matchingVendors, queries, items, parties,
                    DateOnly.FromDateTime(clock.UtcNow), ct));
        });
        recoveryClaims.MapPost("/{recoveryClaimId:guid}/approve", (Guid recoveryClaimId, ApproveRecoveryClaim command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RecoveryClaimChangeResult>(bus, tenant, command with { RecoveryClaimId = recoveryClaimId, ActorRef = Actor(user) }, ct)).RequireAuthorization(editor);
        entitlements.MapGet("/requirements", async (string? status, string? documentType, Guid? document, int? skip, int? take, EntitlementRequirementQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.PageAsync(status, documentType, document, skip ?? 0, take ?? 50, ct)));
        entitlements.MapGet("/requirements/export", (string? status, string? documentType, Guid? document,
            string? format, HttpContext http, EntitlementRequirementQueries queries, ITabularExporter exporter,
            CancellationToken ct) => ListExport<EntitlementRequirementRow>(format, "entitlement-requirements", http,
                exporter, ExistingListExports.OffsetPages<EntitlementRequirementRow>(async (skip, take, token) =>
                    (await queries.PageAsync(status, documentType, document, skip, take, token)).Items, ct)));
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
            catch (ArgumentException ex) { return ApiProblem("filters_invalid", ex.Message); }
        });
        entitlements.MapGet("/gap-report/runs/latest", async (EntitlementGapQueries queries, CancellationToken ct) =>
            await queries.LastRunAsync(ct) is { } run ? Results.Ok(run) : Results.NotFound());
        entitlements.MapGet("/gap-report/export", async (string? items, DateOnly? saleFrom, DateOnly? saleTo, string? channels, string? dropShip,
            string? requirementStatus, string? sort, string? format, HttpContext http, EntitlementGapQueries queries,
            IItemNamePort itemNames, IPartyNamePort partyNames, IMessageBus bus, ITenantContext tenant,
            ITabularExporter exporter, CancellationToken ct) =>
        {
            EntitlementGapFilters filters;
            try { filters = EntitlementGapFilterParser.Parse(items, saleFrom, saleTo, channels, dropShip, requirementStatus, sort); }
            catch (ArgumentException ex) { return ApiProblem("filters_invalid", ex.Message); }
            await bus.InvokeAsync<EntitlementGapRunResult>(new RunEntitlementGapReport(Guid.NewGuid(), EntitlementGapTrigger.OnDemand, filters),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, ct);
            return DirectExport(format, "entitlement-gap-report", http, exporter,
                ["primary_serial", "item", "sale_date", "customer", "channel", "drop_ship", "requirement_status", "missing_input"],
                EntitlementGapRows(filters, queries, itemNames, partyNames, ct));
        });
        entitlements.MapPost("/requirements/{requirementId:guid}/waive", (Guid requirementId, WaiveEntitlementRequirement command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<EntitlementRequirementRow>(bus, tenant, command with { ActorRef = Actor(user), RequirementId = requirementId }, ct)).RequireAuthorization(editor);
        entitlements.MapPost("/requirements/{requirementId:guid}/complete-transfer", (Guid requirementId, CompleteEntitlementTransfer command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<EntitlementRequirementRow>(bus, tenant, command with { ActorRef = Actor(user), RequirementId = requirementId }, ct)).RequireAuthorization(desk);
        entitlements.MapGet("/{entitlementId:guid}/disclosure", async (Guid entitlementId, EntitlementQueries queries, CancellationToken ct) =>
            await queries.DisclosureAsync(entitlementId, ct) is { } disclosure ? Results.Ok(disclosure) : Results.NotFound()).RequireAuthorization(YojanaPolicy.TenantMember);

        var reference = app.MapGroup("/api/warranty-reference").RequireAuthorization(YojanaPolicy.TenantMember);
        reference.MapGet("/coverage-families", async (PolicyQueries queries, CancellationToken ct) => Results.Ok(await queries.FamiliesAsync(ct)));
        reference.MapPost("/coverage-families", async (DefineCoverageFamily command, ClaimsPrincipal user, IMessageBus bus,
            SerialisationDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var stamped = command with { ActorRef = Actor(user) };
            return await db.CoverageFamilies.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
                    && row.FamilyCode == command.FamilyCode, ct)
                ? await InvokeEdit<CoverageFamilyRow>(bus, tenant, stamped, ct)
                : await Invoke<CoverageFamilyRow>(bus, tenant, stamped, ct);
        }).RequireAuthorization(editor);
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
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
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
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        reference.MapGet("/service-contract-rules", async (WarrantyReferenceSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        reference.MapPut("/service-contract-rules", async (WarrantyReferencePolicy policy, WarrantyReferenceSettings settings, CancellationToken ct) =>
        {
            try { await settings.SetAsync(policy, ct); return Results.Ok(policy); }
            catch (CommandRefusedException ex) { return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        }).RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        reference.MapGet("/replacement-policies", async (PolicyQueries queries, CancellationToken ct) => Results.Ok(await queries.ReplacementPoliciesAsync(ct)));
        reference.MapPost("/replacement-policies", async (DefineReplacementPolicy command, ClaimsPrincipal user, IMessageBus bus,
            SerialisationDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var stamped = command with { ActorRef = Actor(user) };
            return await db.ReplacementPolicies.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
                    && row.Code == command.Code, ct)
                ? await InvokeEdit<ReplacementPolicyRow>(bus, tenant, stamped, ct)
                : await Invoke<ReplacementPolicyRow>(bus, tenant, stamped, ct);
        }).RequireAuthorization(editor);

        var warrantyLines = app.MapGroup("/api/warranty-lines").RequireAuthorization(YojanaPolicy.TenantMember);
        warrantyLines.MapPost("/validate", async (WarrantyLineValidation request, WarrantyLineValidationService validation, CancellationToken ct) =>
            Results.Ok(await validation.ValidateAsync(request, ct)));
        warrantyLines.MapGet("/exceptions", async (string? status, int? skip, int? take, WarrantyLineExceptionQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.PageAsync(status, skip ?? 0, take ?? 50, ct)));
        warrantyLines.MapGet("/exceptions/export", (string? status, string? format, HttpContext http,
            WarrantyLineExceptionQueries queries, ITabularExporter exporter, CancellationToken ct) =>
            ListExport<WarrantyLineExceptionRow>(format, "warranty-line-exceptions", http, exporter,
                ExistingListExports.OffsetPages<WarrantyLineExceptionRow>(async (skip, take, token) =>
                    (await queries.PageAsync(status, skip, take, token)).Items, ct)));
        warrantyLines.MapPost("/exceptions/{exceptionId:guid}/resolve", (Guid exceptionId, ResolveWarrantyLineException command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<WarrantyLineExceptionRow>(bus, tenant, command with { ActorRef = Actor(user), ExceptionId = exceptionId }, ct)).RequireAuthorization(desk);

        // Campaign recipients are selected from current ownership and custody by the handler. The body can never name
        // the acting user; the authenticated claim is stamped at this boundary like every other command endpoint.
        var campaigns = app.MapGroup("/api/campaigns").RequireAuthorization(YojanaPolicy.TenantMember);
        campaigns.MapGet("/", async (string? status, int? skip, int? take, CampaignQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ListAsync(status, skip ?? 0, take ?? 50, ct)));
        campaigns.MapGet("/export", (string? status, string? format, HttpContext http, CampaignQueries queries,
            ITabularExporter exporter, CancellationToken ct) => ListExport<CampaignListRow>(format, "campaigns", http,
                exporter, ExistingListExports.OffsetPages<CampaignListRow>(async (skip, take, token) =>
                    (await queries.ListAsync(status, skip, take, token)).Items, ct)));
        campaigns.MapGet("/{campaignId:guid}", async (Guid campaignId, CampaignQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(campaignId, ct) is { } campaign ? Results.Ok(campaign) : Results.NotFound());
        campaigns.MapPost("/", (CreateCampaign command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CampaignResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(editor);
        campaigns.MapPut("/{campaignId:guid}", (Guid campaignId, UpdateCampaign command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            InvokeEdit<CampaignResult>(bus, tenant, command with { CampaignId = campaignId, ActorRef = Actor(user) }, ct)).RequireAuthorization(editor);
        campaigns.MapPost("/{campaignId:guid}/preview", (Guid campaignId, PreviewCampaign command, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CampaignPreview>(bus, tenant, command with { CampaignId = campaignId }, ct));
        campaigns.MapPost("/{campaignId:guid}/select", (Guid campaignId, SelectCampaignUnits command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<SelectCampaignUnitsResult>(bus, tenant, command with { CampaignId = campaignId, ActorRef = Actor(user) }, ct)).RequireAuthorization(editor);
        campaigns.MapGet("/{campaignId:guid}/funnel", async (Guid campaignId, CampaignQueries queries, CancellationToken ct) =>
            await queries.FunnelAsync(campaignId, ct) is { } funnel ? Results.Ok(funnel) : Results.NotFound());
        campaigns.MapGet("/{campaignId:guid}/units/{unitId:guid}/match", async (Guid campaignId, Guid unitId, CampaignQueries queries, CancellationToken ct) =>
            await queries.MatchAsync(campaignId, unitId, ct) is { } evidence ? Results.Ok(evidence) : Results.NotFound());
        campaigns.MapGet("/{campaignId:guid}/units", async (Guid campaignId, string? serial, Guid? owner, string? custody,
            bool? ownerDiffersInvoice, string? repair, int? skip, int? take, CampaignQueries queries, CancellationToken ct) =>
            await queries.UnitsAsync(campaignId, serial, owner, custody, ownerDiffersInvoice, repair, skip ?? 0, take ?? 50, ct) is { } page
                ? Results.Ok(page) : Results.NotFound());
        campaigns.MapGet("/{campaignId:guid}/units/summary", async (Guid campaignId, CampaignQueries queries, CancellationToken ct) =>
            await queries.UnitsSummaryAsync(campaignId, ct) is { } summary ? Results.Ok(summary) : Results.NotFound());
        campaigns.MapGet("/{campaignId:guid}/units/export", (Guid campaignId, string? format, HttpContext http,
            CampaignQueries queries, IPartyNamePort parties, ITabularExporter exporter, CancellationToken ct) =>
            DirectExport(format, $"campaign-{campaignId:N}-recipients", http, exporter,
                ["serial", "owner", "custodian", "selected_at"], CampaignRecipientRows(campaignId, queries, parties, ct)));

        // P6 certificate read and selection issue/reprint surface.
        var generalSettings = app.MapGroup("/api/settings").RequireAuthorization(YojanaPolicy.TenantMember);
        generalSettings.MapGet("/scanning", async (ScanningSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.GetAsync(ct)));
        generalSettings.MapPut("/scanning", async (ScanningPolicy policy, ScanningSettings settings,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.SetAsync(policy, ct)); }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
        }).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        generalSettings.MapGet("/items/{itemId:guid}/serialisation", async (Guid itemId,
            ItemSerialisationSettingsService settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.GetAsync(itemId, ct)); }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status404NotFound); }
        });
        generalSettings.MapPut("/items/{itemId:guid}/serialisation", async (Guid itemId,
            UpdateItemModelNumbers update, ItemSerialisationSettingsService settings, ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.SetAsync(itemId, update, Actor(user), ct)); }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
        }).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        generalSettings.MapGet("/time-zone", async (TenantTimeZoneSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.GetAsync(ct)));
        generalSettings.MapPut("/time-zone", async (UpdateTenantTimeZoneSettings update,
            TenantTimeZoneSettings settings, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(update.ExpectedVersion))
                return Results.Problem(title: "document_version_required",
                    detail: "ExpectedVersion is required for a settings edit.",
                    statusCode: StatusCodes.Status400BadRequest);
            try
            {
                return Results.Ok(await settings.SetAsync(update, Actor(user), ct));
            }
            catch (TenantSettingVersionConflictException ex)
            {
                return Results.Problem(title: "document_changed",
                    detail: "Time-zone settings changed after this screen was loaded; reload to see the current values.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["rule"] = "document_changed",
                        ["expectedVersion"] = ex.ExpectedVersion,
                        ["currentVersion"] = ex.CurrentVersion,
                        ["unblockingStep"] = "reload to see the current time-zone settings",
                    });
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
            }
        }).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        generalSettings.MapGet("/import-files", async (TenantImportFileSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.GetAsync(ct))).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        generalSettings.MapPut("/import-files", async (UpdateImportFileStorageSettings update,
            TenantImportFileSettings settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.SetAsync(update, ct)); }
            catch (TenantSettingVersionConflictException ex)
            {
                return Results.Problem(title: "document_changed", detail: "Import-file settings changed; reload before saving.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["rule"] = "document_changed", ["currentVersion"] = ex.CurrentVersion });
            }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
        }).RequireAuthorization(YojanaPolicy.TenantAdministrator);

        var certificates = app.MapGroup("/api/certificates").RequireAuthorization(YojanaPolicy.TenantMember);
        certificates.MapGet("/settings/release-record-issue-point", async (
            ReleaseRecordIssuePointSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        certificates.MapPut("/settings/release-record-issue-point", async (
            UpdateReleaseRecordIssuePointSettings update, ReleaseRecordIssuePointSettings settings,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(update.ExpectedVersion))
                return Results.Problem(title: "document_version_required",
                    detail: "ExpectedVersion is required for a settings edit.",
                    statusCode: StatusCodes.Status400BadRequest);
            try
            {
                return Results.Ok(await settings.SetAsync(update, ct));
            }
            catch (TenantSettingVersionConflictException ex)
            {
                return Results.Problem(title: "document_changed",
                    detail: "Certificate settings changed after this screen was loaded; reload to see the current values.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["rule"] = "document_changed",
                        ["expectedVersion"] = ex.ExpectedVersion,
                        ["currentVersion"] = ex.CurrentVersion,
                        ["unblockingStep"] = "reload to see the current settings",
                    });
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
            }
        }).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        certificates.MapGet("/settings/date-format", async (
            CertificateDateFormatSettings settings, CertificateTemplateService templates, CancellationToken ct) =>
        {
            await templates.EnsureStartersAsync(ct);
            return Results.Ok(await settings.GetAsync(ct));
        })
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        certificates.MapPut("/settings/date-format", async (
            UpdateCertificateDateFormatSettings update, CertificateDateFormatSettings settings,
            CertificateTemplateService templates,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(update.ExpectedVersion))
                return Results.Problem(title: "document_version_required",
                    detail: "ExpectedVersion is required for a settings edit.",
                    statusCode: StatusCodes.Status400BadRequest);
            try
            {
                await templates.EnsureStartersAsync(ct);
                return Results.Ok(await settings.SetAsync(update, ct));
            }
            catch (TenantSettingVersionConflictException ex)
            {
                return Results.Problem(title: "document_changed",
                    detail: "Certificate date and time settings changed after this screen was loaded; reload to see the current values.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["rule"] = "document_changed",
                        ["expectedVersion"] = ex.ExpectedVersion,
                        ["currentVersion"] = ex.CurrentVersion,
                        ["unblockingStep"] = "reload to see the current settings",
                    });
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
            }
        }).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        certificates.MapGet("/", async (string? kind, string? formType, string? status, string? unitSerial,
            DateTime? issuedFrom, DateTime? issuedTo, int? skip, int? take, CertificateQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.PageAsync(kind, formType, status, unitSerial, issuedFrom, issuedTo,
                skip ?? 0, take ?? 50, ct)));
        certificates.MapGet("/export", (string? kind, string? formType, string? status, string? unitSerial,
            DateTime? issuedFrom, DateTime? issuedTo, string? format, HttpContext http, CertificateQueries queries,
            ITabularExporter exporter, CancellationToken ct) => ListExport<CertificateListRow>(format, "certificates",
                http, exporter, ExistingListExports.OffsetPages<CertificateListRow>(async (skip, take, token) =>
                    (await queries.PageAsync(kind, formType, status, unitSerial, issuedFrom, issuedTo, skip, take, token)).Items, ct)));
        certificates.MapGet("/{certificateId:guid}", async (Guid certificateId, CertificateQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(certificateId, ct) is { } certificate ? Results.Ok(certificate) : Results.NotFound());
        certificates.MapGet("/{certificateId:guid}/rendered", async (Guid certificateId, CertificateQueries queries, CancellationToken ct) =>
            await queries.RenderAsync(certificateId, ct) is { } rendered
                ? Results.File(rendered.Bytes, rendered.MediaType) : Results.NotFound());
        certificates.MapGet("/units/{unitId:guid}/versions", async (Guid unitId, CertificateQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ChainAsync(unitId, ct)));
        certificates.MapGet("/units/{unitId:guid}/composite", async (Guid unitId, CertificateQueries queries, CancellationToken ct) =>
            await queries.RenderCompositeAsync(unitId, ct) is { } rendered
                ? Results.File(rendered.Bytes, rendered.MediaType)
                : Results.NotFound());
        // P6-T08 selection issue/reprint. Identity is always stamped from claims; one HTTP command handles the whole set.
        certificates.MapPost("/selection/preview", (CertificateSelectionPreviewRequest request,
            CertificateSelectionService service, CancellationToken ct) => TemplateInvoke(() => service.PreviewAsync(request, ct)));
        certificates.MapPost("/selection/issue", (IssueCertificate command, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) => Invoke<CertificateRunResult>(bus, tenant,
                command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.CertificateIssuer);
        certificates.MapPost("/selection/reprint", (ReprintCertificate command, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) => Invoke<CertificateRunResult>(bus, tenant,
                command with { ActorRef = Actor(user) }, ct)).RequireAuthorization(YojanaPolicy.CertificateIssuer);
        // End P6 certificate read surface.

        // P6 tenant-owned certificate layouts. Layout placeholders are validated only against immutable payload fields;
        // these endpoints never query live unit data except the explicit sample used for preview.
        var certificateTemplates = app.MapGroup("/api/certificate-templates").RequireAuthorization(YojanaPolicy.TenantMember);
        var templateEditor = YojanaPolicy.ForRole(YojanaRole.CertificateAdministrator);
        certificateTemplates.MapGet("/", async (CertificateTemplateService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct)));
        certificateTemplates.MapGet("/export", (string? format, HttpContext http, CertificateTemplateService service,
            ITabularExporter exporter, CancellationToken ct) => ListExport<CertificateTemplateSummary>(format,
                "certificate-templates", http, exporter, ExistingListExports.OnePage(service.ListAsync(ct), ct)));
        certificateTemplates.MapGet("/field-catalogue/{documentKind}", (string documentKind, CertificateTemplateService service) =>
            TemplateInvoke(() => Task.FromResult(service.Catalogue(documentKind))));
        certificateTemplates.MapGet("/{templateId:guid}", async (Guid templateId, CertificateTemplateService service, CancellationToken ct) =>
            await service.GetAsync(templateId, ct) is { } template ? Results.Ok(template) : Results.NotFound());
        certificateTemplates.MapPost("/", (CreateCertificateTemplateRequest request, ClaimsPrincipal user,
            CertificateTemplateService service, CancellationToken ct) =>
            TemplateInvoke(() => service.CreateAsync(request, Actor(user), ct))).RequireAuthorization(templateEditor);
        certificateTemplates.MapPost("/{templateId:guid}/versions/draft", async (Guid templateId,
            SaveCertificateTemplateDraftRequest request, ClaimsPrincipal user, CertificateTemplateService service, CancellationToken ct) =>
        {
            try
            {
                var saved = await service.SaveDraftAsync(templateId, request, Actor(user), ct);
                return saved.Version is null
                    ? ApiProblem("certificate_template_invalid", string.Join(" ", saved.Validation.Errors),
                        StatusCodes.Status422UnprocessableEntity,
                        new Dictionary<string, object?> { ["validation"] = saved.Validation })
                    : Results.Ok(saved);
            }
            catch (CommandRefusedException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        }).RequireAuthorization(templateEditor);
        certificateTemplates.MapPost("/{templateId:guid}/validate", (Guid templateId,
            SaveCertificateTemplateDraftRequest request, [FromServices] CertificateTemplateService service) =>
            TemplateInvoke(async () =>
            {
                var template = await service.GetAsync(templateId, CancellationToken.None)
                    ?? throw new CommandRefusedException("certificate_template_unknown", $"Certificate template {templateId} does not exist.");
                return service.Validate(template.DocumentKind, request.LayoutBody, request.HeaderBody, request.FooterBody, request.Fields);
            })).RequireAuthorization(templateEditor);
        certificateTemplates.MapPost("/{templateId:guid}/versions/{versionId:guid}/activate", (Guid templateId, Guid versionId,
            ClaimsPrincipal user, CertificateTemplateService service, CancellationToken ct) =>
            TemplateInvoke(() => service.ActivateAsync(templateId, versionId, Actor(user), ct))).RequireAuthorization(templateEditor);
        certificateTemplates.MapPost("/{templateId:guid}/versions/{versionId:guid}/retire", (Guid templateId, Guid versionId,
            ClaimsPrincipal user, CertificateTemplateService service, CancellationToken ct) =>
            TemplateInvoke(() => service.RetireAsync(templateId, versionId, Actor(user), ct))).RequireAuthorization(templateEditor);
        certificateTemplates.MapPost("/{templateId:guid}/versions/{versionId:guid}/preview", (Guid templateId, Guid versionId,
            CertificateTemplatePreviewRequest request, ClaimsPrincipal user, CertificateTemplateService service, CancellationToken ct) =>
            TemplateInvoke(() => service.PreviewAsync(templateId, versionId, request.SampleUnitId, Actor(user), ct)))
            .RequireAuthorization(templateEditor);
        // End P6 certificate-template surface.

        // P6 tenant image assets. Uploads are append-only; template versions hold the id and issued certificates also
        // hold the content hash, so replacing a tenant logo cannot change a prior rendering.
        var tenantAssets = app.MapGroup("/api/tenant-assets").RequireAuthorization(YojanaPolicy.TenantMember);
        tenantAssets.MapGet("/", (Guid? legalEntityId, TenantAssetService service, CancellationToken ct) =>
            service.ListLogosAsync(legalEntityId, ct));
        tenantAssets.MapGet("/export", (Guid? legalEntityId, string? format, HttpContext http,
            TenantAssetService service, ITabularExporter exporter, CancellationToken ct) => ListExport<TenantAssetRow>(
                format, "tenant-assets", http, exporter,
                ExistingListExports.OnePage(service.ListLogosAsync(legalEntityId, ct), ct)));
        tenantAssets.MapGet("/{assetId:guid}/content", async (Guid assetId, HttpContext http,
            TenantAssetService service, CancellationToken ct) =>
        {
            var asset = await service.ContentAsync(assetId, ct);
            if (asset is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            http.Response.Headers.ETag = $"\"{asset.Sha256}\"";
            return Results.File(asset.Bytes, asset.MediaType, lastModified: asset.UploadedAt);
        });
        tenantAssets.MapPost("/", async (HttpRequest request, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.Problem(title: "tenant_asset_multipart_required", detail: "Upload the logo as multipart form data.",
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.Problem(title: "tenant_asset_file_required", detail: "The multipart field 'file' is required.",
                    statusCode: StatusCodes.Status400BadRequest);
            if (!form.TryGetValue("commandId", out var commandValue) || !Guid.TryParse(commandValue, out var commandId))
                return Results.Problem(title: "tenant_asset_command_id_required", detail: "A valid upload command id is required.",
                    statusCode: StatusCodes.Status400BadRequest);
            Guid? legalEntityId = null;
            if (form.TryGetValue("legalEntityId", out var value) && !string.IsNullOrWhiteSpace(value))
            {
                if (!Guid.TryParse(value, out var parsed))
                    return Results.Problem(title: "tenant_asset_legal_entity_invalid", detail: "The legal entity is not a valid identifier.",
                        statusCode: StatusCodes.Status400BadRequest);
                legalEntityId = parsed;
            }
            try
            {
                if (file.Length > TenantAssetService.MaximumBytes)
                    throw new CommandRefusedException("tenant_asset_too_large", "Logos may be at most 1 MB.");
                await using var buffer = new MemoryStream((int)file.Length);
                await file.CopyToAsync(buffer, ct);
                var result = await bus.InvokeAsync<TenantAssetUploadResult>(
                    new UploadTenantLogo(commandId, legalEntityId, buffer.ToArray(), Actor(user), file.FileName),
                    new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, ct);
                return result.Replayed ? Results.Ok(result.Asset)
                    : Results.Created($"/api/tenant-assets/{result.Asset.AssetId}/content", result.Asset);
            }
            catch (CommandRefusedException ex)
            {
                var status = ex.Rule == "tenant_asset_too_large" ? StatusCodes.Status413PayloadTooLarge
                    : StatusCodes.Status422UnprocessableEntity;
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: status,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
            }
        }).RequireAuthorization(templateEditor);
        // End P6 tenant image assets.

        var importJobs = app.MapGroup("/api/imports").RequireAuthorization(policy => policy.RequireClaim(YojanaClaim.Role,
            YojanaRole.SerialAdministrator, YojanaRole.ProductionSupervisor, YojanaRole.Quality,
            YojanaRole.InventoryController, YojanaRole.WarehouseLead, YojanaRole.TenantAdministrator));
        importJobs.MapGet("/history", (DateTime? from, DateTime? to, string? find, string? status,
            string? uploader, string? fileName, string? hash, string? cursor, int? take, ClaimsPrincipal user,
            ImportQueries queries, CancellationToken ct) => ImportInvoke(async () =>
            {
                var page = await queries.HistoryAsync(new(from, to, status, uploader, find, fileName, hash), cursor, take ?? 50, ct);
                var actor = Actor(user);
                var roles = Roles(user);
                return page with
                {
                    Items = page.Items.Select(row => row with
                    {
                        MayDownload = ImportFileDownloadPolicy.MayDownload(row.UploaderReference, actor, roles),
                        ArchiveStoreName = roles.Contains(YojanaRole.TenantAdministrator, StringComparer.Ordinal)
                            ? row.ArchiveStoreName : null,
                    }).ToList(),
                };
            }));
        importJobs.MapGet("/history/export", (DateTime? from, DateTime? to, string? find, string? status,
            string? uploader, string? fileName, string? hash, HttpContext http, ImportQueries queries,
            ITabularExporter exporter, CancellationToken ct) =>
        {
            http.Response.Headers.ContentDisposition = "attachment; filename=import-file-history.csv";
            return Results.Stream(stream => exporter.WriteAsync("csv", new TabularExport(
                ["file_name", "format", "size", "original_hash", "converted_hash", "uploader", "uploaded_at", "source", "status", "applied", "skipped", "rejected", "storage_state"],
                queries.ExportHistoryAsync(new(from, to, status, uploader, find, fileName, hash), ct)), stream, ct),
                exporter.ContentType("csv"), "import-file-history.csv");
        });
        importJobs.MapGet("/history/feed-messages", (DateTime? from, DateTime? to, string? find, string? status,
            string? cursor, int? take, ImportQueries queries, CancellationToken ct) => ImportInvoke(() =>
                queries.FeedMessagesAsync(new(from, to, find, status), cursor, take ?? 50, ct)));
        importJobs.MapGet("/history/feed-messages/export", (DateTime? from, DateTime? to, string? find, string? status,
            HttpContext http, ImportQueries queries, ITabularExporter exporter, CancellationToken ct) =>
        {
            http.Response.Headers.ContentDisposition = "attachment; filename=partner-feed-message-history.csv";
            return Results.Stream(stream => exporter.WriteAsync("csv", new TabularExport(
                ["message_reference", "document_type", "document_id", "document_line", "received_at", "caller",
                    "status", "shipped", "asset_tags", "rejected", "payload_hash", "import_job_id"],
                queries.ExportFeedMessagesAsync(new(from, to, find, status), ct)), stream, ct),
                exporter.ContentType("csv"), "partner-feed-message-history.csv");
        });
        importJobs.MapGet("/history/comparisons", (DateTime? from, DateTime? to, string? find, string? status,
            string? cursor, int? take, ImportQueries queries, CancellationToken ct) => ImportInvoke(() =>
                queries.ComparisonsAsync(new(from, to, find, status), cursor, take ?? 50, ct)));
        importJobs.MapGet("/history/comparisons/export", (DateTime? from, DateTime? to, string? find, string? status,
            HttpContext http, ImportQueries queries, ITabularExporter exporter, CancellationToken ct) =>
        {
            http.Response.Headers.ContentDisposition = "attachment; filename=file-comparison-history.csv";
            return Results.Stream(stream => exporter.WriteAsync("csv", new TabularExport(
                ["first_file_id", "first_file_name", "first_file_hash", "second_file_id", "second_file_name",
                    "second_file_hash", "status", "applied", "skipped", "rejected", "ran_at", "import_job_id"],
                queries.ExportComparisonsAsync(new(from, to, find, status), ct)), stream, ct),
                exporter.ContentType("csv"), "file-comparison-history.csv");
        });
        // Downloads sit outside the import-role group: the administrators and the service manager may download any file
        // without running imports, and an uploader keeps access to their own file. ImportFileDownloadPolicy decides.
        app.MapGet("/api/imports/{jobId:guid}/files/{copyKind}", async (Guid jobId, string copyKind, ClaimsPrincipal user,
            HttpContext http, ImportFileDownloads downloads, CancellationToken ct) =>
        {
            try
            {
                var file = await downloads.OpenAsync(jobId, copyKind, Actor(user), Roles(user), ct);
                http.Response.ContentLength = file.ContentLength;
                return Results.Stream(file.Content, file.ContentType, file.FileName, enableRangeProcessing: false);
            }
            catch (CommandRefusedException ex) when (ex.Rule == "import_file_download_not_permitted")
            {
                return Refused(ex, StatusCodes.Status403Forbidden);
            }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status409Conflict); }
        }).RequireAuthorization(YojanaPolicy.TenantMember);
        importJobs.MapGet("/", (string? cursor, int? take, ImportQueries queries, CancellationToken ct) =>
            ImportInvoke(() => queries.PageAsync(cursor, take ?? 50, ct)));
        importJobs.MapGet("/export", (string? format, HttpContext http, ImportQueries queries,
            ITabularExporter exporter, CancellationToken ct) => ListExport<ImportJobSummary>(format, "imports", http,
                exporter, ExistingListExports.CursorPages<ImportJobSummary>(async (cursor, take, token) =>
                {
                    var page = await queries.PageAsync(cursor, take, token);
                    return (page.Items, page.NextCursor);
                }, ct)));
        importJobs.MapGet("/{jobId:guid}", async (Guid jobId, ImportQueries queries, CancellationToken ct) =>
            await queries.GetAsync(jobId, ct) is { } job ? Results.Ok(job) : Results.NotFound());
        importJobs.MapGet("/{jobId:guid}/rows", async (Guid jobId, string? cursor, int? take, ImportQueries queries, CancellationToken ct) =>
            await queries.RowsAsync(jobId, cursor, take ?? 100, ct) is { } page ? Results.Ok(page) : Results.NotFound());
        importJobs.MapPost("/run", (RunImport command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<RunImportResult>(bus, tenant, command with { ActorRef = Actor(user), CommittedBy = CorrectionCommitter(user) }, ct));
        importJobs.MapPost("/compare", (CompareImports command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<CompareImportsResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        importJobs.MapPost("/{jobId:guid}/review", (Guid jobId, ApplyImportReview command, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ApplyImportReviewResult>(bus, tenant,
                command with { JobId = jobId, ActorRef = Actor(user), CommittedBy = CorrectionCommitter(user) }, ct));
        importJobs.MapPost("/three-pl", (ReceiveThreePlFeed command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ReceiveThreePlFeedResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        importJobs.MapGet("/{jobId:guid}/rows/export", async (Guid jobId, string? format, ImportQueries queries,
            ITabularExporter exporter, CancellationToken ct) =>
        {
            var job = await queries.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound();
            var selected = (format ?? "csv").ToLowerInvariant();
            if (selected is not "csv" and not "xlsx") return ExportFormatProblem();
            return Results.Stream(stream => exporter.WriteAsync(selected, new TabularExport(
                ["row", "sheet", "raw_value", "normalised_value", "outcome", "rule", "order_number", "lot", "customer_asset_tag", "partition", "proposed_action", "reviewed_action", "collision_unit", "collision_owner"],
                queries.ExportRowsAsync(jobId, ct)), stream, ct), exporter.ContentType(selected), $"import-{job.JobId:N}{exporter.Extension(selected)}");
        });

        // Reconciliation exposes other-party names, so every route is internal-only. Verification repeats the role
        // check in its handler because message-bus callers do not pass through endpoint authorisation.
        var reconciliations = app.MapGroup("/api/customer-reconciliations")
            .RequireAuthorization(policy => policy.RequireClaim(YojanaClaim.Role,
                YojanaRole.SerialAdministrator, YojanaRole.OrderDeskSupervisor));
        reconciliations.MapPost("/", (ReconcileCustomerListRequest request, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) => Invoke<CustomerListReconciliationPage>(bus, tenant,
                new ReconcileCustomerList(request.CommandId, request.PartyRef, request.Source, request.AssertedAt,
                    Actor(user), request.Take, request.After), ct));
        reconciliations.MapGet("/{reconciliationId:guid}", (Guid reconciliationId, string? after, int? take,
            CustomerListReconciliationService service, CancellationToken ct) => ReportInvoke(() =>
                service.PageAsync(reconciliationId, after, take ?? 50, false, ct)));
        reconciliations.MapGet("/{reconciliationId:guid}/export", (Guid reconciliationId, string? format,
            ITabularExporter exporter, CustomerListReconciliationService service, CancellationToken ct) =>
        {
            var selected = ExportFormat(format);
            if (selected is null) return Results.BadRequest(new { rule = "export_format_unknown" });
            return Results.Stream(stream => exporter.WriteAsync(selected, new TabularExport(
            [
                "row", "input", "identifier_type", "normalised_identifier", "outcome", "validation_reasons",
                "unit", "item", "other_owner", "assertion_status", "customer_po_ref", "customer_item_ref",
            ], service.ExportAsync(reconciliationId, ct)), stream, ct), exporter.ContentType(selected),
                $"customer-reconciliation-{reconciliationId:N}{exporter.Extension(selected)}");
        });
        reconciliations.MapPost("/assertions/{assertionId:guid}/verify", (Guid assertionId,
            VerifyCustomerAssertionRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant,
            CancellationToken ct) => Invoke<CustomerAssertionDecision>(bus, tenant,
                new VerifyCustomerAssertion(request.CommandId, assertionId, request.EvidenceRef,
                    request.EvidenceDate, request.LegalEntityId, Actor(user), Roles(user),
                    request.UnitAtCustomerSite), ct));
        reconciliations.MapPost("/assertions/{assertionId:guid}/reject", (Guid assertionId,
            RejectCustomerAssertionRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant,
            CancellationToken ct) => Invoke<CustomerAssertionDecision>(bus, tenant,
                new RejectCustomerAssertion(request.CommandId, assertionId, request.Reason,
                    Actor(user), Roles(user)), ct));

        // P7 internal reports. Cost, internal notes and cross-customer ownership history are deliberately behind an
        // explicit back-office role policy; TenantMember alone is insufficient for this disclosure tier.
        var reports = app.MapGroup("/api/reports").RequireAuthorization(policy => policy.RequireClaim(YojanaClaim.Role,
            YojanaRole.SerialAdministrator, YojanaRole.ServiceManager, YojanaRole.InventoryController,
            YojanaRole.OrderDeskSupervisor, YojanaRole.FinanceReview, YojanaRole.TenantAdministrator));
        reports.MapGet("/identifier-cross-reference", (Guid selectionId, string? after, int? take,
            BackOfficeReportQueries queries, CancellationToken ct) => ReportInvoke(() =>
                queries.IdentifierCrossReferenceAsync(selectionId, after, take ?? 100, ct)));
        reports.MapGet("/identifier-cross-reference/export", (Guid selectionId, string? format, HttpContext http,
            BackOfficeReportQueries queries, ITabularExporter exporter, CancellationToken ct) =>
        {
            var selected = ExportFormat(format);
            if (selected is null) return ExportFormatProblem();
            http.Response.Headers["X-Export-Row-Limit"] = BackOfficeReportQueries.MaximumExportRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Stream(stream => exporter.WriteAsync(selected, new TabularExport(
                ["root_serial", "parent_serial", "depth", "slot", "internal_serial", "manufacturer_serial",
                    "vendor_serial", "customer_serial", "asset_tag", "item", "current_owner"],
                queries.ExportIdentifierCrossReferenceAsync(selectionId, ct)), stream, ct),
                exporter.ContentType(selected), "identifier-cross-reference" + exporter.Extension(selected));
        });
        reports.MapGet("/installed-base-stock", (string? item, string? holding, Guid? location, string? after, int? take,
            BackOfficeReportQueries queries, CancellationToken ct) => ReportInvoke(() => queries.InstalledBaseAndStockAsync(
                new InstalledBaseStockFilter(ParseIds(item), holding ?? BackOfficeHolding.All, location), after, take ?? 100, ct)));
        reports.MapGet("/installed-base-stock/export", (string? item, string? holding, Guid? location, string? format,
            HttpContext http, BackOfficeReportQueries queries, ITabularExporter exporter, CancellationToken ct) =>
        {
            var selected = ExportFormat(format);
            if (selected is null) return ExportFormatProblem();
            InstalledBaseStockFilter filter;
            try { filter = new InstalledBaseStockFilter(ParseIds(item), holding ?? BackOfficeHolding.All, location); }
            catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
            http.Response.Headers["X-Export-Row-Limit"] = BackOfficeReportQueries.MaximumExportRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Stream(stream => exporter.WriteAsync(selected, new TabularExport(
                ["serial", "item", "holding", "current_owner", "location", "location_kind", "lifecycle_status",
                    "disposition", "condition"], queries.ExportInstalledBaseAndStockAsync(filter, ct)), stream, ct),
                exporter.ContentType(selected), "installed-base-stock" + exporter.Extension(selected));
        });
        reports.MapGet("/as-built-completeness", (Guid selectionId, string? after, int? take, DateOnly? on,
            BackOfficeReportQueries queries, IClock clock, CancellationToken ct) => ReportInvoke(() =>
                queries.AsBuiltCompletenessAsync(selectionId, after, take ?? 100,
                    on ?? DateOnly.FromDateTime(clock.UtcNow), ct)));
        reports.MapGet("/as-built-completeness/export", (Guid selectionId, DateOnly? on, string? format, HttpContext http,
            BackOfficeReportQueries queries, ITabularExporter exporter, IClock clock, CancellationToken ct) =>
        {
            var selected = ExportFormat(format);
            if (selected is null) return ExportFormatProblem();
            http.Response.Headers["X-Export-Row-Limit"] = BackOfficeReportQueries.MaximumExportRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Stream(stream => exporter.WriteAsync(selected, new TabularExport(
                ["root_serial", "root_item", "customer_item_reference", "current_owner", "slot", "component_item",
                    "component_serial", "outcome"], queries.ExportAsBuiltCompletenessAsync(selectionId,
                    on ?? DateOnly.FromDateTime(clock.UtcNow), ct)), stream, ct), exporter.ContentType(selected),
                "as-built-completeness" + exporter.Extension(selected));
        });
        reports.MapGet("/internal-lookup", (string identifier, string? type, BackOfficeReportQueries queries,
            CancellationToken ct) => ReportResultInvoke(async () => await queries.InternalLookupAsync(identifier, type, ct)
                is { } result ? Results.Ok(result) : Results.NotFound()));
        reports.MapGet("/internal-lookup/export", (string identifier, string? type, string? format, HttpContext http,
            BackOfficeReportQueries queries, ITabularExporter exporter, CancellationToken ct) =>
        {
            var selected = ExportFormat(format);
            if (selected is null) return ExportFormatProblem();
            http.Response.Headers["X-Export-Row-Limit"] = BackOfficeReportQueries.MaximumExportRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Stream(stream => exporter.WriteAsync(selected, new TabularExport(
                ["section", "serial", "item", "owner", "from_or_recorded_at", "to", "order", "delivery",
                    "invoice_or_reference", "policy_or_kind", "internal_note", "cost", "currency"],
                queries.ExportInternalLookupAsync(identifier, type, ct)), stream, ct), exporter.ContentType(selected),
                "internal-unit-lookup" + exporter.Extension(selected));
        });

        var selections = app.MapGroup("/api/selections").RequireAuthorization(YojanaPolicy.TenantMember);
        selections.MapPost("/", (ResolveSelectionSet command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ResolveSelectionSetResult>(bus, tenant, command with { ActorRef = Actor(user) }, ct));
        // Approver and roles come from the claims, never the body: the operations judge approval tiers on them.
        selections.MapPost("/{selectionId:guid}/preview", (Guid selectionId, PreviewBulkOperation command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkPreviewResult>(bus, tenant, command with { Arguments = ApiBulkArguments(command.Arguments, user), SelectionId = selectionId, Approval = Approver(user, command.Approval) }, ct));
        selections.MapPost("/{selectionId:guid}/execute", (Guid selectionId, ExecuteBulkOperation command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkExecutionResult>(bus, tenant, command with { Arguments = ApiBulkArguments(command.Arguments, user), SelectionId = selectionId, Approval = Approver(user, command.Approval)! }, ct));
        selections.MapGet("/{selectionId:guid}/find", async (Guid selectionId, string q, int? skip, int? take, BulkExecutor executor, CancellationToken ct) =>
            Results.Ok(await executor.FindAsync(new FindInSelection(selectionId, q, Math.Max(0, skip ?? 0), take ?? 50), ct)));
        // P2-T21 approval queue: requester, approver and their roles come from the claims, never the body.
        var approvals = app.MapGroup("/api/approval-requests").RequireAuthorization(YojanaPolicy.TenantMember);
        approvals.MapGet("/", async (string? status, int? skip, int? take, BulkApprovalRequestQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.PageAsync(status, skip ?? 0, take ?? 50, ct)));
        approvals.MapGet("/export", (string? status, string? format, HttpContext http,
            BulkApprovalRequestQueries queries, ITabularExporter exporter, CancellationToken ct) =>
            ListExport<BulkApprovalRequestRow>(format, "approval-requests", http, exporter,
                ExistingListExports.OffsetPages<BulkApprovalRequestRow>(async (skip, take, token) =>
                    (await queries.PageAsync(status, skip, take, token)).Items, ct)));
        approvals.MapGet("/{requestId:guid}", async (Guid requestId, BulkApprovalRequestQueries queries, CancellationToken ct) =>
            await queries.GetAsync(requestId, ct) is { } row ? Results.Ok(row) : Results.NotFound());
        approvals.MapPost("/", (RequestBulkApproval command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkApprovalRequestRow>(bus, tenant, command with { Arguments = ApiBulkArguments(command.Arguments, user), Requester = Approver(user, null) }, ct));
        approvals.MapPost("/{requestId:guid}/approve", (Guid requestId, ApproveBulkRequest command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkApprovalRequestRow>(bus, tenant, command with { RequestId = requestId, Approver = Approver(user, null) }, ct));
        approvals.MapPost("/{requestId:guid}/reject", (Guid requestId, RejectBulkRequest command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<BulkApprovalRequestRow>(bus, tenant, command with { RequestId = requestId, Decider = Approver(user, null) }, ct));

        var workbench = app.MapGroup("/api/workbench").RequireAuthorization(YojanaPolicy.TenantMember);
        workbench.MapGet("/choices", async (IWorkbenchChoiceCatalog choices, CancellationToken ct) => Results.Ok(await choices.GetChoicesAsync(ct)));
        // The reason codes a route's command accepts, so the screen offers the server's vocabulary, never a copy of it.
        workbench.MapGet("/routes/{command}/reasons", (string command) =>
            WorkbenchInvoke(() => Task.FromResult(CorrectionRules.Reasons(WorkbenchChoiceRules.CorrectionCommandForRoute(command)))));
        workbench.MapGet("/picker", (Guid? unitId, string? q, WorkbenchPickerService picker, CancellationToken ct) =>
            WorkbenchInvoke(() => picker.QueryAsync(unitId, q, ct)));
        workbench.MapPost("/resolve", (ResolveWorkbenchEntry command, ClaimsPrincipal user, WorkbenchEntryResolver resolver, CancellationToken ct) =>
            WorkbenchInvoke(() => resolver.ResolveAsync(command with { ActorRef = Actor(user) }, ct)));
        workbench.MapGet("/units/{unitId:guid}/lineage", async (Guid unitId, WorkbenchLineageQueries queries, CancellationToken ct) =>
            await queries.ReadAsync(unitId, ct) is { } lineage ? Results.Ok(lineage) : Results.NotFound());
        workbench.MapPost("/preview", (WorkbenchPreviewRequest request, WorkbenchRequestService service, CancellationToken ct) =>
            WorkbenchInvoke(() => service.PreviewAsync(request, ct)));
        workbench.MapPost("/requests/{requestId:guid}", (Guid requestId, WorkbenchPreviewRequest request, ClaimsPrincipal user, WorkbenchRequestService service, CancellationToken ct) =>
            WorkbenchInvoke(() => service.InitiateAsync(requestId, request, WorkbenchActorFrom(user), ct)));
        workbench.MapPost("/requests/{requestId:guid}/unclassified", (Guid requestId, FileUnclassifiedWorkbenchRequest request, ClaimsPrincipal user, WorkbenchRequestService service, CancellationToken ct) =>
            WorkbenchInvoke(() => service.FileUnclassifiedAsync(requestId, request, WorkbenchActorFrom(user), ct)));
        workbench.MapPost("/requests/{requestId:guid}/refile", (Guid requestId, RefileWorkbenchRequest request, ClaimsPrincipal user, WorkbenchRequestService service, CancellationToken ct) =>
            WorkbenchInvoke(() => service.RefileAsync(requestId, request, WorkbenchActorFrom(user), ct)));
        workbench.MapPost("/requests/{requestId:guid}/no-command-fits", (Guid requestId, EscalateWorkbenchRequest request, ClaimsPrincipal user, WorkbenchRequestService service, CancellationToken ct) =>
            WorkbenchInvoke(() => service.EscalateAsync(requestId, WorkbenchActorFrom(user), request.Note, ct)));
        workbench.MapGet("/requests/{requestId:guid}", async (Guid requestId, WorkbenchRequestQueries queries, CancellationToken ct) =>
            await queries.GetAsync(requestId, ct) is { } request ? Results.Ok(request) : Results.NotFound());
        workbench.MapGet("/inbox", (int? skip, int? take, ClaimsPrincipal user, WorkbenchRequestQueries queries, CancellationToken ct) =>
            WorkbenchInvoke(() => queries.InboxAsync(Roles(user), Math.Max(0, skip ?? 0), take ?? 50, ct)));
        workbench.MapGet("/inbox/export", (string? format, HttpContext http, ClaimsPrincipal user,
            WorkbenchRequestQueries queries, ITabularExporter exporter, CancellationToken ct) =>
            ListExport<WorkbenchRequestRow>(format, "workbench-inbox", http, exporter,
                ExistingListExports.OffsetPages<WorkbenchRequestRow>(async (skip, take, token) =>
                    (await queries.InboxAsync(Roles(user), skip, take, token)).Items, ct)));
        workbench.MapPost("/requests/{requestId:guid}/approve", (Guid requestId, ApproveWorkbenchRequest command, ClaimsPrincipal user, WorkbenchRequestService service, CancellationToken ct) =>
            WorkbenchInvoke(() => service.ApproveAsync(requestId, WorkbenchActorFrom(user), command.Note, ct)));
        workbench.MapPost("/requests/{requestId:guid}/reject", (Guid requestId, RejectWorkbenchRequest command, ClaimsPrincipal user, WorkbenchRequestService service, CancellationToken ct) =>
            WorkbenchInvoke(() => service.RejectAsync(requestId, WorkbenchActorFrom(user), command.Note, ct)));
        workbench.MapPost("/requests/{requestId:guid}/commit", (Guid requestId, ClaimsPrincipal user, WorkbenchRequestService service, CancellationToken ct) =>
            WorkbenchInvoke(() => service.CommitAsync(requestId, WorkbenchActorFrom(user), ct)));
        return app;
    }

    private static async Task<IResult> WorkbenchInvoke<TResult>(Func<Task<TResult>> action)
    {
        try { return Results.Ok(await action()); }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
        }
    }

    private static async Task<IResult> PortalWriteInvoke<TResult>(Func<Task<TResult>> action)
    {
        try { return Results.Ok(await action()); }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
        }
    }

    private static PortalPrincipal PortalActor(ClaimsPrincipal user, ITenantContext tenant)
    {
        var value = user.FindFirst(YojanaClaim.CustomerPartyRef)?.Value;
        if (!Guid.TryParse(value, out var partyRef) || partyRef == Guid.Empty)
            throw new UnauthorizedAccessException("The customer portal identity has no valid party reference.");
        return new PortalPrincipal(tenant.TenantId, partyRef);
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static async Task<IResult> TemplateInvoke<TResult>(Func<Task<TResult>> action)
    {
        try { return Results.Ok(await action()); }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "certificate_template_invalid", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    internal static async Task<IResult> Invoke<TResult>(IMessageBus bus, ITenantContext tenant, object command, CancellationToken ct) where TResult : class
    {
        try
        {
            return Results.Ok(await bus.InvokeAsync<TResult>(command, new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, ct));
        }
        catch (DocumentChangedException ex)
        {
            return DocumentChanged(ex);
        }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(title: "document_changed", detail: "The document changed while this edit was being saved.",
                statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?>
                { ["rule"] = "document_changed", ["unblockingStep"] = DocumentVersionRules.Reload });
        }
    }

    private static async Task<IResult> InvokeAdjudicate(IMessageBus bus, ITenantContext tenant, AdjudicateClaim command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await bus.InvokeAsync<AdjudicateClaimResult>(command,
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, cancellationToken);
            return result.Refusal == "claim_risk_review_pending"
                ? Results.Problem(title: result.Refusal, detail: "Approval is held until every open claim risk flag has a second-review decision.",
                    statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?>
                    {
                        ["rule"] = result.Refusal,
                        ["unblockingStep"] = "decide every open claim risk flag",
                        ["openRules"] = result.OpenRiskRules,
                    })
                : Results.Ok(result);
        }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(title: "document_changed", detail: "The document changed while this edit was being saved.",
                statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?>
                { ["rule"] = "document_changed", ["unblockingStep"] = DocumentVersionRules.Reload });
        }
    }

    private static async Task<IResult> ImportInvoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
        }
    }

    private static async Task<IResult> ReportInvoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
    }

    private static async Task<IResult> ReportResultInvoke(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (CommandRefusedException ex) { return Refused(ex, StatusCodes.Status400BadRequest); }
    }

    private static IResult Refused(CommandRefusedException ex, int statusCode) => Results.Problem(
        title: ex.Rule, detail: ex.Message, statusCode: statusCode,
        extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });

    private static IResult ApiProblem(string rule, string detail, int statusCode = StatusCodes.Status400BadRequest,
        IDictionary<string, object?>? extra = null)
    {
        var extensions = extra is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(extra);
        extensions["rule"] = rule;
        return Results.Problem(title: rule, detail: detail, statusCode: statusCode, extensions: extensions);
    }

    private static IResult ExportFormatProblem() =>
        ApiProblem("export_format_unknown", "Choose CSV or XLSX.");

    private static string? ExportFormat(string? format)
    {
        var selected = (format ?? "csv").ToLowerInvariant();
        return selected is "csv" or "xlsx" ? selected : null;
    }

    private static IResult ListExport<T>(string? format, string fileName, HttpContext http,
        ITabularExporter exporter, IAsyncEnumerable<IReadOnlyList<string?>> rows)
        => DirectExport(format, fileName, http, exporter, ExistingListExports.Headers<T>(), rows);

    internal static IResult DirectExport(string? format, string fileName, HttpContext http,
        ITabularExporter exporter, IReadOnlyList<string> headers, IAsyncEnumerable<IReadOnlyList<string?>> rows)
    {
        var selected = ExportFormat(format);
        if (selected is null) return ExportFormatProblem();
        http.Response.Headers["X-Export-Row-Limit"] = BackOfficeReportQueries.MaximumExportRows
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Stream(stream => exporter.WriteAsync(selected,
                new TabularExport(headers, rows), stream, http.RequestAborted),
            exporter.ContentType(selected), fileName + exporter.Extension(selected));
    }

    private static async IAsyncEnumerable<IReadOnlyList<string?>> PortalInstalledBaseRows(
        IAsyncEnumerable<PortalInstalledBaseRow> rows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            var coverage = string.Join(';', row.Coverage.Select(item =>
                $"{item.CoverageFamily}:{item.Status}:{item.Expiry?.ToString("yyyy-MM-dd") ?? ""}"));
            yield return [row.Serial, row.ItemCode, row.ItemName, row.CustomerPoRef, row.CustomerItemRef,
                row.DeliveryDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                row.IsComponent ? "true" : "false", coverage];
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<string?>> RecoveryClaimRows(string? status, Guid? vendor,
        Guid? childItem, string? find, List<Guid> matchingVendors, RecoveryQueries queries, IItemNamePort items,
        IPartyNamePort parties, DateOnly asOf,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var offset = 0;
        var exported = 0;
        while (true)
        {
            var page = await queries.PageAsync(status, vendor, childItem, find, matchingVendors, asOf, offset,
                ExistingListExports.PageSize, cancellationToken);
            var itemNames = await items.NamesAsync(page.Rows.Select(row => row.ChildItemId).Distinct().ToList(), cancellationToken);
            var partyNames = await parties.NamesAsync(page.Rows.Select(row => row.VendorPartyRef).Distinct().ToList(), cancellationToken);
            foreach (var row in page.Rows)
            {
                EnsureEndpointExportCap(++exported);
                var item = itemNames.GetValueOrDefault(row.ChildItemId);
                yield return [row.RecoveryNumber, row.RaisedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    row.CustomerClaimNumber, row.ClaimDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    row.ChildSerial, item is null ? null : item.Name is null ? item.Code : $"{item.Code} · {item.Name}",
                    PartyName(row.VendorPartyRef, partyNames),
                    row.VendorTermStart?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    row.VendorTermEnd?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    row.VendorTermMonths?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    row.ClaimMonthOfTerm?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    row.RequestedAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    row.ApprovedAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Currency, row.Status,
                    row.VendorReference, row.VendorNote];
            }
            offset += page.Rows.Count;
            if (page.Rows.Count == 0 || offset >= page.Total) yield break;
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<string?>> EntitlementGapRows(EntitlementGapFilters filters,
        EntitlementGapQueries queries, IItemNamePort items, IPartyNamePort parties,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var offset = 0;
        var exported = 0;
        while (true)
        {
            var page = await queries.PageAsync(filters, offset, ExistingListExports.PageSize, cancellationToken);
            var itemNames = await items.NamesAsync(page.Rows.Select(row => row.ItemId).Distinct().ToList(), cancellationToken);
            var partyNames = await parties.NamesAsync(page.Rows.Select(row => row.CustomerPartyRef).Distinct().ToList(), cancellationToken);
            foreach (var row in page.Rows)
            {
                EnsureEndpointExportCap(++exported);
                var item = itemNames.GetValueOrDefault(row.ItemId);
                yield return [row.PrimarySerial,
                    item is null ? null : item.Name is null ? item.Code : $"{item.Code} · {item.Name}",
                    row.SaleDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    PartyName(row.CustomerPartyRef, partyNames), row.Channel, row.DropShip ? "true" : "false",
                    row.RequirementStatus, row.MissingInput];
            }
            offset += page.Rows.Count;
            if (page.Rows.Count == 0 || offset >= page.Total) yield break;
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<string?>> CampaignRecipientRows(Guid campaignId,
        CampaignQueries queries, IPartyNamePort parties,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var names = await parties.NamesAsync(await queries.RecipientPartyRefsAsync(campaignId, cancellationToken), cancellationToken);
        var exported = 0;
        await foreach (var row in queries.ExportUnitsAsync(campaignId, cancellationToken))
        {
            EnsureEndpointExportCap(++exported);
            yield return [row.Serial, PartyName(row.OwnerPartyRef, names),
                row.CustodianPartyRef == row.OwnerPartyRef ? null : PartyName(row.CustodianPartyRef, names),
                row.SelectedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)];
        }
    }

    private static void EnsureEndpointExportCap(int count)
    {
        if (count > BackOfficeReportQueries.MaximumExportRows)
            throw new CommandRefusedException("export_too_large",
                $"An export may contain at most {BackOfficeReportQueries.MaximumExportRows:N0} rows; narrow the list.");
    }

    private static List<Guid>? ParseIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var ids = new List<Guid>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(part, out var id))
                throw new CommandRefusedException("item_filter_invalid", $"'{part}' is not a valid item reference.");
            ids.Add(id);
        }
        return ids.Distinct().ToList();
    }

    private static async IAsyncEnumerable<IReadOnlyList<string?>> CorrectionKpiRows(string breakdown, IReadOnlyList<CorrectionKpiRow> rows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return [row.Bucket, breakdown, row.Dimension,
                row.Corrections.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.UnitsCorrected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.UnitsShipped.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.PerThousand?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                row.ReversedLater.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.TargetPerThousand?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)];
        }
        await Task.CompletedTask;
    }

    internal static Task<IResult> InvokeEdit<TResult>(IMessageBus bus, ITenantContext tenant, IExpectedDocumentVersion command, CancellationToken ct) where TResult : class
    {
        if (string.IsNullOrWhiteSpace(command.ExpectedVersion))
        {
            return Task.FromResult<IResult>(Results.Problem(title: "document_version_required", detail: "ExpectedVersion is required for an API edit.",
                statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["rule"] = "document_version_required" }));
        }

        return Invoke<TResult>(bus, tenant, command, ct);
    }

    private static IResult DocumentChanged(DocumentChangedException ex) => Results.Problem(title: ex.Rule, detail: ex.Message,
        statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?>
        {
            ["rule"] = ex.Rule,
            ["changedBy"] = ex.ChangedBy,
            ["changedAt"] = ex.ChangedAt,
            ["unblock"] = ex.UnblockingStep,
            ["unblockingStep"] = ex.UnblockingStep,
        });

    private static async Task<IResult> InvokeCloseRma(IMessageBus bus, ITenantContext tenant, CloseRma command, CancellationToken ct)
    {
        try
        {
            var result = await bus.InvokeAsync<CloseRmaResult>(command,
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, ct);
            return result.Closed ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status409Conflict);
        }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
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

    private static CorrectionCommitter CorrectionCommitter(ClaimsPrincipal user) =>
        new(user.FindFirstValue(YojanaClaim.UserId) ?? throw new InvalidOperationException("A signed-in user carries a user id claim."), user.FindAll(YojanaClaim.Role).Select(c => c.Value).ToList());

    private static ClaimReviewer ClaimReviewer(ClaimsPrincipal user) =>
        new(user.FindFirstValue(YojanaClaim.UserId) ?? throw new InvalidOperationException("A signed-in user carries a user id claim."), user.FindAll(YojanaClaim.Role).Select(c => c.Value).ToList());

    private static List<string> Roles(ClaimsPrincipal user) => user.FindAll(YojanaClaim.Role).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();

    private static string? PartyName(Guid? partyRef, IReadOnlyDictionary<Guid, PartyLabel> names)
    {
        if (partyRef is null || !names.TryGetValue(partyRef.Value, out var label)) return null;
        return string.IsNullOrWhiteSpace(label.Name) ? label.Code : $"{label.Name} ({label.Code})";
    }

    private static WorkbenchActor WorkbenchActorFrom(ClaimsPrincipal user) => new(Actor(user), Roles(user));

    private static BulkApproval Approver(ClaimsPrincipal user, BulkApproval? given)
    {
        var committer = Committer(user);
        return new BulkApproval(committer.UserRef, given?.Required ?? false, committer.Roles);
    }

    private static BulkOperationArguments ApiBulkArguments(BulkOperationArguments arguments, ClaimsPrincipal user) => arguments with
    {
        ActorRef = Actor(user),
        InitiatedVia = CorrectionInitiatedVia.Api,
        InitiatingRole = InitiatingRole(user),
    };

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

    private static VoidUnit StampVoid(VoidUnit command, ClaimsPrincipal user) => command with
    {
        ActorRef = Actor(user),
        CommittedBy = CorrectionCommitter(user),
        Correction = ApiCorrection(command.CommandId, command.Reason, command.Note, command.Correction, user),
    };

    private static LateSerialization Secure(LateSerialization command, ClaimsPrincipal user) => command with
    {
        ActorRef = Actor(user),
        CommittedBy = CorrectionCommitter(user),
        Correction = ApiCorrection(command.CommandId, command.Reason, command.Note, command.Correction, user),
    };

    private static CorrectionMetadata ApiCorrection(Guid commandId, string reason, string? note, CorrectionMetadata? supplied, ClaimsPrincipal user, IReadOnlyList<string>? evidenceRefs = null)
    {
        return new CorrectionMetadata(
            supplied?.CorrectionId ?? commandId,
            supplied?.ReasonCode ?? reason,
            supplied?.Note ?? note,
            supplied?.EvidenceRefs ?? evidenceRefs ?? [],
            supplied?.InitiatedVia ?? CorrectionInitiatedVia.Api,
            supplied?.CorrectsCorrectionId,
            InitiatingRole(user),
            // Approvers are never taken from the request body: a caller could name anyone, with any roles. The legacy
            // ApproverRef keeps its P1 meaning; server-recorded approvals arrive with the workbench approval flow (P3-T16).
            null);
    }

    private static SwapIdentifiers ApiSwap(SwapIdentifiers command, ClaimsPrincipal user)
    {
        var actor = Actor(user);
        var supplied = command.Correction;
        var correction = new CorrectionMetadata(
            supplied?.CorrectionId ?? command.CommandId,
            supplied?.ReasonCode ?? command.Reason,
            supplied?.Note ?? command.Note,
            supplied?.EvidenceRefs ?? [],
            supplied?.InitiatedVia ?? CorrectionInitiatedVia.Api,
            supplied?.CorrectsCorrectionId,
            InitiatingRole(user),
            // Direct API input can never manufacture an approval. P3-T16's approver flow supplies server-recorded
            // approvals after showing this same preview.
            null);
        return command with { ActorRef = actor, Correction = correction, CommittedBy = CorrectionCommitter(user) };
    }

    private static CorrectionMetadata ApiCorrection(CorrectItem command, ClaimsPrincipal user)
    {
        var supplied = command.Correction;
        var actor = Actor(user);
        var roles = user.FindAll(YojanaClaim.Role).Select(c => c.Value).ToList();
        return new CorrectionMetadata(
            supplied?.CorrectionId ?? command.CommandId,
            supplied?.ReasonCode ?? command.Reason,
            supplied?.Note,
            supplied?.EvidenceRefs ?? [],
            supplied?.InitiatedVia ?? CorrectionInitiatedVia.Api,
            supplied?.CorrectsCorrectionId,
            InitiatingRole(user),
            // The signed-in committer, from claims, is the only approval the API can attest; never the request body.
            [new CorrectionApprover(actor, roles)]);
    }

    private static AssignIdentifiers Secure(AssignIdentifiers command, ClaimsPrincipal user)
    {
        var supplied = command.Correction;
        var committer = Committer(user);
        return command with
        {
            ActorRef = committer.UserRef,
            CommittedBy = new CorrectionApprover(committer.UserRef, committer.Roles),
            Correction = new CorrectionMetadata(
                supplied?.CorrectionId ?? command.CommandId,
                supplied?.ReasonCode ?? command.Reason,
                supplied?.Note ?? command.Note,
                supplied?.EvidenceRefs ?? [],
                supplied?.InitiatedVia ?? CorrectionInitiatedVia.Api,
                supplied?.CorrectsCorrectionId,
                InitiatingRole(user),
                // Approvals are server records. Request bodies may name neither an approver nor their roles.
                null),
        };
    }

    private static string InitiatingRole(ClaimsPrincipal user)
    {
        return CorrectionRules.InitiatingRole(user.FindAll(YojanaClaim.Role).Select(c => c.Value).ToList());
    }

    /// <summary>"type:guid" as the search result href carries it; null when absent or malformed.</summary>
    private static DocumentRef? ParseDocument(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var colon = value.IndexOf(':');
        return colon > 0 && Guid.TryParse(value[(colon + 1)..], out var id) ? new DocumentRef(value[..colon], id) : null;
    }

    public sealed record ReferenceNamesRequest(
        IReadOnlyList<Guid> Items,
        IReadOnlyList<Guid> Parties,
        IReadOnlyList<DocumentRef> Documents,
        IReadOnlyList<Guid>? Locations = null);

    public sealed record ItemReferenceView(string Code, string? Name);

    public sealed record PartyReferenceView(string Code, string? Name, string? PartyType);

    public sealed record DocumentReferenceView(string Number, string? Status, Guid? ItemId);

    public sealed record LocationReferenceView(string Code, string? Name, string? Kind);

    public sealed record ReferenceNamesResponse(
        IReadOnlyDictionary<Guid, ItemReferenceView> Items,
        IReadOnlyDictionary<Guid, PartyReferenceView> Parties,
        IReadOnlyDictionary<string, DocumentReferenceView> Documents,
        IReadOnlyDictionary<Guid, LocationReferenceView> Locations);

    public sealed record ReferenceSearchResult(
        string Kind,
        Guid Id,
        string? Type = null,
        string? Code = null,
        string? Name = null,
        string? PartyType = null,
        string? Number = null,
        string? Status = null,
        Guid? ItemId = null);

    public sealed record ReferenceSearchResponse(IReadOnlyList<ReferenceSearchResult> Results);

    // P6 label HTTP inputs deliberately omit actor, reviewer and source-event fields. Those are server facts stamped
    // from claims or the consumed event before the internal command crosses the Wolverine boundary.
    public sealed record CreateLabelTemplateRequest(
        Guid CommandId,
        string Name,
        string AppliesTo,
        string AppliesToValue);

    public sealed record SaveLabelTemplateDraftRequest(
        Guid CommandId,
        Guid? TemplateVersionId,
        DateTime EffectiveFrom,
        DateTime? EffectiveTo,
        string LayoutKind,
        string Layout,
        IReadOnlyList<string> StaticText,
        IReadOnlyList<LabelBinding> Bindings,
        string Symbology,
        Guid? PreviewSampleUnitId,
        bool Regulated);

    public sealed record PreviewLabelTemplateRequest(Guid? SampleUnitId = null);

    public sealed record LabelVersionCommandRequest(Guid CommandId);

    public sealed record ActivateLabelTemplateVersionRequest(Guid CommandId, DateTime ActivatedAt);

    public sealed record RetireLabelTemplateVersionRequest(Guid CommandId, DateTime RetiredAt);

    public sealed record PrintLabelRequest(
        Guid CommandId,
        Guid UnitId,
        Guid? TemplateId,
        Guid? IdentifierId,
        string PrinterRef,
        DateTime PrintAt,
        Guid LegalEntityId);

    public sealed record ReprintLabelRequest(
        Guid CommandId,
        string? PrinterRef,
        DateTime ReprintedAt,
        Guid LegalEntityId);

    public sealed record BatchPrintLabelRequest(
        Guid CommandId,
        Guid? TemplateVersionId,
        string PrinterRef,
        IReadOnlyList<string> Serials);

    public sealed record ScanRequest(string Raw);
}

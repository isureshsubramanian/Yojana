using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Entries;
using Yojana.Knowledge.Questions;
using Yojana.Knowledge.Reports;
using Yojana.Knowledge.Training;
using Yojana.Knowledge.Data.Entities;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Files;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using CommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;

namespace Yojana.Knowledge;

public sealed record AskQuestionRequest(Guid RecordId, string RecordKind, string Title, string Body,
    Guid? DirectedToUser, string? DirectedToRole);
public sealed record QuestionBodyRequest(string Body);
public sealed record AcceptAnswerRequest(Guid ReplyId);
public sealed record CloseQuestionRequest(string Reason);
public sealed record EditEntryRequest(string Title, string Problem, string Answer, string Steps,
    IReadOnlyList<EntryTagInput> Tags, IReadOnlyList<EntryRecordLinkInput> RecordLinks,
    IReadOnlyList<WrittenAgainstInput> WrittenAgainst);
public sealed record EntryDecisionRequest(string? Note);
public sealed record RetireEntryRequest(string Note);
public sealed record MarkKnowledgeScreenChangedRequest(string ScreenReference, string Reason);
public sealed record SaveTrainingPathRequest(string Introduction, IReadOnlyList<Guid> EntryIds, int ExpectedVersion);
public sealed record RedactQuestionPersonalDataRequest(Guid PersonId, Guid RequestedByUserId,
    IReadOnlyList<Guid> QuestionIds, IReadOnlyList<Guid> ReplyIds, IReadOnlyList<Guid> AttachmentIds,
    string Reason);

public static class KnowledgeEndpoints
{
    internal static readonly SortedPage<Question, Guid> MyQuestionPages = new("asked", row => row.QuestionId,
        new SortColumn<Question, DateTime>("asked", row => row.AskedAt),
        new SortColumn<Question, string>("status", row => row.Status));
    internal static readonly SortedPage<KnowledgeEntry, Guid> EntryPages = new("published", row => row.EntryId,
        new SortColumn<KnowledgeEntry, DateTime>("published", row => row.PublishedAt!.Value,
            SortDirection.Descending),
        new SortColumn<KnowledgeEntry, DateTime>("relevance", row => row.PublishedAt!.Value,
            SortDirection.Descending));
    internal static readonly SortedPage<EntryReviewProjection, Guid> ReviewPages = new("activity",
        row => row.EntryId,
        new SortColumn<EntryReviewProjection, DateTime>("activity", row => row.ActivityAt));

    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var questions = app.MapGroup("/api/questions").RequireAuthorization(YojanaPolicy.StaffMember);
        questions.MapPost("/", (AskQuestionRequest request, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) => Invoke<AskQuestionResult>(bus, tenant,
                new AskQuestion(request.RecordId, request.RecordKind, request.Title, request.Body,
                    request.DirectedToUser, request.DirectedToRole, Actor(user)), ct));
        questions.MapPost("/{questionId:guid}/replies", (Guid questionId, QuestionBodyRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ReplyToQuestionResult>(bus, tenant, new ReplyToQuestion(questionId, request.Body, Actor(user)), ct));
        questions.MapPut("/{questionId:guid}", (Guid questionId, QuestionBodyRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<QuestionChangeResult>(bus, tenant, new EditQuestion(questionId, request.Body, Actor(user)), ct));
        questions.MapPut("/{questionId:guid}/replies/{replyId:guid}", (Guid questionId, Guid replyId,
            QuestionBodyRequest request, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant,
            CancellationToken ct) => Invoke<QuestionChangeResult>(bus, tenant,
                new EditReply(questionId, replyId, request.Body, Actor(user)), ct));
        questions.MapPost("/{questionId:guid}/accept", (Guid questionId, AcceptAnswerRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<QuestionChangeResult>(bus, tenant,
                new AcceptQuestionAnswer(questionId, request.ReplyId, Actor(user)), ct));
        questions.MapPost("/{questionId:guid}/close", (Guid questionId, CloseQuestionRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<QuestionChangeResult>(bus, tenant,
                new CloseQuestionWithoutAnswer(questionId, request.Reason, Actor(user)), ct));
        questions.MapPost("/{questionId:guid}/reopen", (Guid questionId, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<QuestionChangeResult>(bus, tenant,
                new ReopenQuestion(questionId, Actor(user)), ct));
        questions.MapPost("/{questionId:guid}/entry", (Guid questionId, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntryChangeResult>(bus, tenant,
                new CreateEntryFromAnswer(questionId, Actor(user)), ct));
        questions.MapPost("/{questionId:guid}/attachments", async (Guid questionId, HttpRequest request,
            ClaimsPrincipal user, QuestionAttachmentService attachments, CancellationToken ct) =>
            await UploadAttachment(questionId, null, request, user, attachments, ct))
            .DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute
            { MultipartBodyLengthLimit = 101L * 1024 * 1024 });
        questions.MapPost("/{questionId:guid}/replies/{replyId:guid}/attachments", async (Guid questionId,
            Guid replyId, HttpRequest request, ClaimsPrincipal user, QuestionAttachmentService attachments,
            CancellationToken ct) => await UploadAttachment(questionId, replyId, request, user, attachments, ct))
            .DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute
            { MultipartBodyLengthLimit = 101L * 1024 * 1024 });
        questions.MapGet("/{questionId:guid}/attachments/{fileId:guid}", async (Guid questionId, Guid fileId,
            ClaimsPrincipal user, QuestionAttachmentService attachments, CancellationToken ct) =>
        {
            try
            {
                var opened = await attachments.OpenAsync(questionId, fileId, Caller(user), ct);
                return opened is null ? Results.NotFound() : Results.Stream(opened.Content,
                    opened.Metadata.ContentType, opened.Metadata.FileName, enableRangeProcessing: false);
            }
            catch (TenantFileRuleException ex) { return FileProblem(ex); }
        });

        var privacy = app.MapGroup("/api/questions/personal-data")
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        privacy.MapPost("/redactions", async (RedactQuestionPersonalDataRequest request, ClaimsPrincipal user,
            QuestionPrivacyService service, CancellationToken ct) => await Read(async () => Results.Ok(
                await service.RedactAsync(new RedactQuestionPersonalData(request.PersonId,
                    request.RequestedByUserId, request.QuestionIds, request.ReplyIds, request.AttachmentIds,
                    request.Reason, Actor(user)), ct))));
        privacy.MapGet("/export", async (Guid personId, QuestionPrivacyService service,
            ITabularExporter exporter, HttpContext http, CancellationToken ct) =>
            Results.Stream(stream => exporter.WriteAsync("csv", new TabularExport(
            [
                "Type", "Written at", "Record kind", "Question status", "Question title", "Text", "Edited at",
            ], service.ExportAsync(personId, http.RequestAborted)), stream, http.RequestAborted),
                exporter.ContentType("csv"), "person-questions-and-replies.csv"));

        questions.MapGet("/addressees", async (string? search, string? cursor, int? take,
            QuestionQueries queries, CancellationToken ct) => await Read(async () =>
                Results.Ok(await queries.AddresseesAsync(search, cursor, take ?? 25, ct))));
        questions.MapGet("/notifications/bell", async (int? take, ClaimsPrincipal user,
            QuestionNotificationService notifications, CancellationToken ct) => await Read(async () =>
                Results.Ok(await notifications.BellAsync(Viewer(user), take ?? 5, ct))));
        questions.MapGet("/notifications/preference", async (ClaimsPrincipal user,
            QuestionNotificationService notifications, CancellationToken ct) => Results.Ok(
                await notifications.PreferenceAsync(Actor(user).UserId, ct)));
        questions.MapPut("/notifications/preference", async (UpdateQuestionNotificationPreference request,
            ClaimsPrincipal user, QuestionNotificationService notifications, CancellationToken ct) =>
            await Read(async () => Results.Ok(await notifications.UpdatePreferenceAsync(
                Actor(user).UserId, request.Mode, ct))));
        questions.MapGet("/mine", async (string? scope, string? status, string? recordKind, string? age,
            string? search, string? sort, string? dir, string? cursor, int? take,
            ClaimsPrincipal user, QuestionQueries queries,
            CancellationToken ct) => await Read(async () => Results.Ok(await queries.MyAsync(scope, status,
                recordKind, age, search, sort, dir, cursor, take, Viewer(user), ct))));
        questions.MapGet("/search", async (string query, bool? answeredOnly, string? cursor, int? take,
            ClaimsPrincipal user, QuestionQueries queries, CancellationToken ct) => await Read(async () =>
                Results.Ok(await queries.SearchAsync(query, answeredOnly ?? false, cursor, take ?? 10,
                    Viewer(user), ct))));
        questions.MapGet("/{questionId:guid}", async (Guid questionId, ClaimsPrincipal user,
            QuestionQueries queries, CancellationToken ct) => await Read(async () =>
                await queries.ThreadAsync(questionId, Viewer(user), ct) is { } result
                    ? Results.Ok(result) : Results.NotFound()));
        questions.MapGet("/", async (string recordKind, Guid recordId, string? status, string? cursor,
            int? take, ClaimsPrincipal user, QuestionQueries queries, CancellationToken ct) =>
            await Read(async () => Results.Ok(await queries.OnRecordAsync(recordKind, recordId, status,
                cursor, take ?? 50, Viewer(user), ct))));
        questions.MapGet("/{questionId:guid}/revisions", async (Guid questionId, ClaimsPrincipal user,
            QuestionQueries queries, CancellationToken ct) => await Read(async () =>
                await queries.RevisionsAsync(questionId, null, Viewer(user), ct) is { } result
                    ? Results.Ok(result) : Results.NotFound()));
        questions.MapGet("/{questionId:guid}/replies/{replyId:guid}/revisions", async (Guid questionId,
            Guid replyId, ClaimsPrincipal user, QuestionQueries queries, CancellationToken ct) => await Read(async () =>
                await queries.RevisionsAsync(questionId, replyId, Viewer(user), ct) is { } result
                    ? Results.Ok(result) : Results.NotFound()));

        var entries = app.MapGroup("/api/knowledge/entries").RequireAuthorization(YojanaPolicy.StaffMember);
        entries.MapGet("/tag-choices", async (KnowledgeEntryQueries queries, CancellationToken ct) =>
            await Read(async () => Results.Ok(await queries.TagChoicesAsync(ct))));
        entries.MapGet("/review", async (string? filter, string? sort, string? dir, string? cursor, int? take,
            ClaimsPrincipal user,
            KnowledgeEntryQueries queries, CancellationToken ct) => await Read(async () => Results.Ok(
                await queries.ReviewQueueAsync(filter, sort, dir, cursor, take, Viewer(user), ct))));
        entries.MapGet("/", async (string? query, string? role, string? task, string? sort, string? dir,
            string? cursor, int? take,
            KnowledgeEntryQueries queries, CancellationToken ct) => await Read(async () => Results.Ok(
                await queries.BrowseAsync(query, role, task, sort, dir, cursor, take, ct))));
        entries.MapGet("/{entryId:guid}", async (Guid entryId, ClaimsPrincipal user,
            KnowledgeEntryQueries queries, CancellationToken ct) => await Read(async () =>
                await queries.EntryAsync(entryId, Viewer(user), ct) is { } result
                    ? Results.Ok(result) : Results.NotFound()));
        entries.MapGet("/{entryId:guid}/changes-since-last-round", async (Guid entryId, ClaimsPrincipal user,
            KnowledgeEntryQueries queries, CancellationToken ct) => await Read(async () =>
                await queries.ChangesSinceLastRoundAsync(entryId, Viewer(user), ct) is { } result
                    ? Results.Ok(result) : Results.NotFound()));
        entries.MapPut("/{entryId:guid}", (Guid entryId, EditEntryRequest request, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntryChangeResult>(bus, tenant,
                new EditDraft(entryId, new EntryDraftInput(request.Title, request.Problem, request.Answer,
                    request.Steps, request.Tags, request.RecordLinks, request.WrittenAgainst), Actor(user)), ct));
        entries.MapPost("/{entryId:guid}/submit", (Guid entryId, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntryChangeResult>(bus, tenant,
                new SubmitEntryForReview(entryId, Actor(user)), ct));
        entries.MapPost("/{entryId:guid}/approve", (Guid entryId, EntryDecisionRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<EntryChangeResult>(bus, tenant, new ApproveEntry(entryId, request.Note, Actor(user)), ct));
        entries.MapPost("/{entryId:guid}/send-back", (Guid entryId, EntryDecisionRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<EntryChangeResult>(bus, tenant, new SendBackEntry(entryId, request.Note ?? "", Actor(user)), ct));
        entries.MapPost("/{entryId:guid}/retire", (Guid entryId, RetireEntryRequest request,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<EntryChangeResult>(bus, tenant, new RetireEntry(entryId, request.Note, Actor(user)), ct));
        entries.MapPost("/{entryId:guid}/still-correct", (Guid entryId, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<EntryChangeResult>(bus, tenant,
                new ConfirmKnowledgeEntryStillCorrect(entryId, Actor(user)), ct));

        var references = app.MapGroup("/api/knowledge/references")
            .RequireAuthorization(YojanaPolicy.StaffMember);
        references.MapGet("/policy-versions", async (string? search, string? cursor, int? take,
            IPolicyVersionPickerPort policies, CancellationToken ct) => await Read(async () => Results.Ok(
                await policies.SearchAsync(search, cursor, take ?? 25, ct))));
        references.MapGet("/screens", (string? search, string? cursor, int? take,
            ScreenReferenceCatalogue catalogue) => Read(() => Task.FromResult<IResult>(Results.Ok(
                catalogue.Search(search, cursor, take ?? 25)))));

        var training = app.MapGroup("/api/knowledge/training-paths")
            .RequireAuthorization(YojanaPolicy.StaffMember);
        training.MapGet("/", async (ClaimsPrincipal user, TrainingPathQueries queries, CancellationToken ct) =>
            await Read(async () => Results.Ok(await queries.ListAsync(Viewer(user), ct))));
        training.MapGet("/welcome", async (ClaimsPrincipal user, TrainingPathQueries queries,
            CancellationToken ct) => await Read(async () => await queries.WelcomeAsync(Viewer(user), ct) is { } card
                ? Results.Ok(card) : Results.NoContent()));
        training.MapPost("/welcome/{pathId:guid}/dismiss", (Guid pathId, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) => Invoke<TrainingPathChangeResult>(bus, tenant,
                new DismissTrainingWelcome(pathId, Actor(user)), ct));
        training.MapGet("/{role}", async (string role, ClaimsPrincipal user, TrainingPathQueries queries,
            CancellationToken ct) => await Read(async () => await queries.DetailAsync(role, Viewer(user), ct)
                is { } path ? Results.Ok(path) : Results.NotFound()));
        training.MapPut("/{role}", (string role, SaveTrainingPathRequest request, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<TrainingPathChangeResult>(
                bus, tenant, new SaveTrainingPath(role, request.Introduction, request.EntryIds,
                    request.ExpectedVersion, Actor(user)), ct));
        training.MapPost("/{role}/entries/{entryId:guid}/read", (string role, Guid entryId,
            ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<TrainingReadResult>(bus, tenant, new MarkTrainingEntryRead(role, entryId, Actor(user)), ct));
        training.MapGet("/{role}/team", async (string role, string? cursor, int? take, ClaimsPrincipal user,
            TrainingPathQueries queries, CancellationToken ct) => await Read(async () => Results.Ok(
                await queries.TeamAsync(role, cursor, take ?? 25, Viewer(user), ct))));

        var reports = app.MapGroup("/api/knowledge/reports")
            .RequireAuthorization(YojanaPolicy.StaffMember);
        reports.MapGet("/", async (string? period, string? directedRole, string? recordKind, string? topic,
            ClaimsPrincipal user, KnowledgeReportQueries queries, CancellationToken ct) => await Read(async () =>
                Results.Ok(await queries.ReportAsync(new KnowledgeReportFilters(
                    period ?? KnowledgeReportPeriod.NinetyDays, directedRole, recordKind, topic), Viewer(user), ct))));
        reports.MapGet("/export", async (string? period, string? directedRole, string? recordKind, string? topic,
            ClaimsPrincipal user, KnowledgeReportQueries queries, ITabularExporter exporter, HttpContext http,
            CancellationToken ct) => await Read(async () =>
        {
            var filters = new KnowledgeReportFilters(period ?? KnowledgeReportPeriod.NinetyDays,
                directedRole, recordKind, topic);
            await queries.EnsureAllowedAsync(Viewer(user), ct);
            return Results.Stream(stream => exporter.WriteAsync("csv", new TabularExport(
            [
                "Asked at", "Status", "Record kind", "Directed to", "Title", "Question text",
                "First reply at", "Accepted at", "Seconds to first reply", "Seconds to accepted answer", "Tags",
            ], queries.ExportAsync(filters, Viewer(user), http.RequestAborted)), stream, http.RequestAborted),
                exporter.ContentType("csv"), "knowledge-questions.csv");
        }));

        var screens = app.MapGroup("/api/knowledge/screens")
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        screens.MapGet("/", async (string? cursor, int? take, KnowledgeScreenQueries queries,
            CancellationToken ct) => await Read(async () =>
                Results.Ok(await queries.ListAsync(cursor, take ?? 50, ct))));
        screens.MapPost("/changed", (MarkKnowledgeScreenChangedRequest request, ClaimsPrincipal user,
            IMessageBus bus, ITenantContext tenant, CancellationToken ct) => Invoke<ScreenChangeResult>(bus, tenant,
                new MarkKnowledgeScreenChanged(request.ScreenReference, request.Reason, Actor(user)), ct));
        return app;
    }

    private static async Task<IResult> Invoke<TResult>(IMessageBus bus, ITenantContext tenant, object command,
        CancellationToken cancellationToken) where TResult : class
    {
        try
        {
            return Results.Ok(await bus.InvokeAsync<TResult>(command,
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, cancellationToken));
        }
        catch (QuestionRuleException ex)
        {
            return Problem(ex);
        }
    }

    private static async Task<IResult> Read(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (QuestionRuleException ex) { return Problem(ex); }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["rule"] = ex.Rule,
                    ["unblockingStep"] = ex.UnblockingStep,
                });
        }
        catch (QuestionSearchUnavailableException ex)
        {
            return Results.Problem(title: "question_search_unavailable", detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> UploadAttachment(Guid questionId, Guid? replyId, HttpRequest request,
        ClaimsPrincipal user, QuestionAttachmentService attachments, CancellationToken cancellationToken)
    {
        try
        {
            if (!request.HasFormContentType)
                return Results.Problem(title: "attachment_form_required",
                    detail: "Upload one file as multipart form data.", statusCode: StatusCodes.Status400BadRequest);
            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.Problem(title: "attachment_required", detail: "Choose a file to attach.",
                    statusCode: StatusCodes.Status400BadRequest);
            await using var content = file.OpenReadStream();
            return Results.Ok(await attachments.UploadAsync(questionId, replyId,
                new TenantFileUpload(file.FileName, file.ContentType, file.Length, content), Caller(user),
                cancellationToken));
        }
        catch (QuestionRuleException ex) { return Problem(ex); }
        catch (TenantFileRuleException ex) { return FileProblem(ex); }
    }

    private static IResult Problem(QuestionRuleException ex)
    {
        var status = ex.Rule switch
        {
            "record_not_visible" or "accept_answer_forbidden" or "close_question_forbidden" or
                "reopen_question_forbidden" or "question_edit_author_only" or "reply_edit_author_only" or
                "entry_edit_author_only" or "entry_submit_author_only" or "entry_self_review_forbidden" or
                "entry_review_role_required" or "entry_link_not_visible" or "screen_change_admin_required"
                or "training_path_reviewer_role_required" or "training_path_role_required"
                or "training_team_view_forbidden" or "knowledge_report_role_required"
                or "question_redaction_admin_required"
                => StatusCodes.Status403Forbidden,
            "record_not_found" or "question_not_found" or "reply_not_found" or "entry_not_found" or
                "entry_link_not_found" or "screen_reference_not_found" or "question_redaction_scope_not_found"
                => StatusCodes.Status404NotFound,
            "training_path_not_found" or "training_path_entry_not_found" or "training_member_not_found"
                => StatusCodes.Status404NotFound,
            "question_not_open" or "question_already_open" or "entry_already_exists" or "entry_not_draft" or
                "entry_not_in_review" or "entry_not_published" or "entry_not_needing_review"
                or "training_path_version_conflict"
                => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: status,
            extensions: new Dictionary<string, object?>
            {
                ["rule"] = ex.Rule,
                ["unblockingStep"] = ex.UnblockingStep,
            });
    }

    private static IResult FileProblem(TenantFileRuleException ex)
    {
        var status = ex.Rule is "file_portal_forbidden" or "file_record_not_visible"
            ? StatusCodes.Status403Forbidden
            : ex.Rule == "file_record_not_found" ? StatusCodes.Status404NotFound
            : StatusCodes.Status422UnprocessableEntity;
        return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: status,
            extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
    }

    private static QuestionActor Actor(ClaimsPrincipal user) => new(
        Guid.Parse(user.FindFirstValue(YojanaClaim.UserId)
            ?? throw new InvalidOperationException("A signed-in staff user carries a user id claim.")),
        user.FindAll(YojanaClaim.Role).Select(x => x.Value).Distinct(StringComparer.Ordinal).ToList());

    private static RecordReferenceViewer Viewer(ClaimsPrincipal user)
    {
        var actor = Actor(user);
        return new RecordReferenceViewer(actor.UserId, actor.Roles);
    }

    private static TenantFileCaller Caller(ClaimsPrincipal user)
    {
        var actor = Actor(user);
        return new TenantFileCaller(actor.UserId, actor.Roles,
            user.HasClaim(claim => claim.Type == YojanaClaim.CustomerPartyRef));
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge;

public sealed record AskQuestionRequest(Guid RecordId, string RecordKind, string Title, string Body,
    Guid? DirectedToUser, string? DirectedToRole);
public sealed record QuestionBodyRequest(string Body);
public sealed record AcceptAnswerRequest(Guid ReplyId);
public sealed record CloseQuestionRequest(string Reason);

public static class KnowledgeEndpoints
{
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
    }

    private static IResult Problem(QuestionRuleException ex)
    {
        var status = ex.Rule switch
        {
            "record_not_visible" or "accept_answer_forbidden" or "close_question_forbidden" or
                "reopen_question_forbidden" or "question_edit_author_only" or "reply_edit_author_only"
                => StatusCodes.Status403Forbidden,
            "record_not_found" or "question_not_found" or "reply_not_found" => StatusCodes.Status404NotFound,
            "question_not_open" or "question_already_open" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: status,
            extensions: new Dictionary<string, object?>
            {
                ["rule"] = ex.Rule,
                ["unblockingStep"] = ex.UnblockingStep,
            });
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
}

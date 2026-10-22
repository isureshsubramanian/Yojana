using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Knowledge.DatabaseTests;

[Collection("knowledge host")]
public sealed class QuestionDatabaseTests(KnowledgeHostFixture fixture)
{
    [DatabaseFact]
    public async Task Ask_supports_person_or_present_role_and_refuses_other_shapes()
    {
        var setup = await SetupAsync("ask");
        var person = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator, [YojanaRole.TenantAdministrator], setup.Staff, null);
        var role = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator, [YojanaRole.TenantAdministrator], null, YojanaRole.ServiceSupervisor);
        Assert.Equal(QuestionStatus.Open, person.Status);
        Assert.Equal(QuestionStatus.Open, role.Status);

        var both = new AskQuestion(setup.RecordId, RecordKind.ImportJob, "Storage server query", "Which bay owns this server?",
            setup.Staff, YojanaRole.ServiceSupervisor, Actor(setup.Administrator, YojanaRole.TenantAdministrator));
        await AssertRuleAsync("question_addressee_exactly_one", () => InvokeAsync<AskQuestionResult>(setup.Tenant, both));
        var absentRole = both with { DirectedToUser = null, DirectedToRole = YojanaRole.Quality };
        await AssertRuleAsync("directed_role_not_present", () => InvokeAsync<AskQuestionResult>(setup.Tenant, absentRole));
    }

    [DatabaseFact]
    public async Task Only_authors_edit_and_every_edit_keeps_the_earlier_text()
    {
        var setup = await SetupAsync("edits");
        var asked = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator, [YojanaRole.TenantAdministrator], setup.Staff, null);
        var reply = await InvokeAsync<ReplyToQuestionResult>(setup.Tenant,
            new ReplyToQuestion(asked.QuestionId, "Use the next-business-day service queue.", Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new EditQuestion(asked.QuestionId, "Which queue covers this storage server?", Actor(setup.Administrator, YojanaRole.TenantAdministrator)));
        await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new EditReply(asked.QuestionId, reply.ReplyId, "Use the next-business-day hardware service queue.", Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        await AssertRuleAsync("question_edit_author_only", () => InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new EditQuestion(asked.QuestionId, "Changed by somebody else", Actor(setup.Other, YojanaRole.Technician))));
        await AssertRuleAsync("reply_edit_author_only", () => InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new EditReply(asked.QuestionId, reply.ReplyId, "Changed by somebody else", Actor(setup.Other, YojanaRole.Technician))));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        var revisions = await db.Revisions.AsNoTracking().Where(x => x.QuestionId == asked.QuestionId).ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Contains(revisions, x => x.QuestionBody && x.Body == "Which queue covers this record?");
        Assert.Contains(revisions, x => x.ReplyId == reply.ReplyId && x.Body == "Use the next-business-day service queue.");
    }

    [DatabaseFact]
    public async Task Directed_role_can_accept_and_reopen_clears_the_accepted_reply()
    {
        var setup = await SetupAsync("accept");
        var asked = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator, [YojanaRole.TenantAdministrator], null, YojanaRole.ServiceSupervisor);
        var reply = await InvokeAsync<ReplyToQuestionResult>(setup.Tenant,
            new ReplyToQuestion(asked.QuestionId, "Retain media before replacing the array.", Actor(setup.Other, YojanaRole.Technician)));
        await AssertRuleAsync("accept_answer_forbidden", () => InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new AcceptQuestionAnswer(asked.QuestionId, reply.ReplyId, Actor(setup.Other, YojanaRole.Technician))));
        var answered = await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new AcceptQuestionAnswer(asked.QuestionId, reply.ReplyId, Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        Assert.Equal(QuestionStatus.Answered, answered.Status);
        var reopened = await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new ReopenQuestion(asked.QuestionId, Actor(setup.Administrator, YojanaRole.TenantAdministrator)));
        Assert.Equal(QuestionStatus.Open, reopened.Status);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var row = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Questions.SingleAsync(x => x.QuestionId == asked.QuestionId);
        Assert.Null(row.AcceptedReplyId);
        Assert.Equal(1, row.ReopenedCount);
    }

    [DatabaseFact]
    public async Task Supervisor_accepts_only_when_the_asker_is_no_longer_active_staff()
    {
        var setup = await SetupAsync("standin");
        var asked = await AskAsync(setup.Tenant, setup.RecordId, setup.Other, [YojanaRole.Technician], setup.Administrator, null);
        var reply = await InvokeAsync<ReplyToQuestionResult>(setup.Tenant,
            new ReplyToQuestion(asked.QuestionId, "Swap the controller under the advance-replacement terms.", Actor(setup.Administrator, YojanaRole.TenantAdministrator)));
        await AssertRuleAsync("accept_answer_forbidden", () => InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new AcceptQuestionAnswer(asked.QuestionId, reply.ReplyId, Actor(setup.Staff, YojanaRole.ServiceSupervisor))));

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var membership = await platform.Memberships.SingleAsync(m => m.TenantId == setup.Tenant && m.UserId == setup.Other);
            membership.DeactivatedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
            membership.DeactivatedByUserId = setup.Administrator;
            await platform.SaveChangesAsync();
        }

        var answered = await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new AcceptQuestionAnswer(asked.QuestionId, reply.ReplyId, Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        Assert.Equal(QuestionStatus.Answered, answered.Status);
    }

    [DatabaseFact]
    public async Task Close_needs_a_reason_and_only_asker_or_administrator_can_close_or_reopen()
    {
        var setup = await SetupAsync("close");
        var asked = await AskAsync(setup.Tenant, setup.RecordId, setup.Staff, [YojanaRole.ServiceSupervisor], setup.Other, null);
        await AssertRuleAsync("close_reason_required", () => InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new CloseQuestionWithoutAnswer(asked.QuestionId, " ", Actor(setup.Staff, YojanaRole.ServiceSupervisor))));
        await AssertRuleAsync("close_question_forbidden", () => InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new CloseQuestionWithoutAnswer(asked.QuestionId, "No longer needed", Actor(setup.Other, YojanaRole.Technician))));
        var closed = await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new CloseQuestionWithoutAnswer(asked.QuestionId, "The service request was cancelled.", Actor(setup.Administrator, YojanaRole.TenantAdministrator)));
        Assert.Equal(QuestionStatus.ClosedWithoutAnswer, closed.Status);
        await AssertRuleAsync("reopen_question_forbidden", () => InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new ReopenQuestion(asked.QuestionId, Actor(setup.Other, YojanaRole.Technician))));
        Assert.Equal(QuestionStatus.Open, (await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new ReopenQuestion(asked.QuestionId, Actor(setup.Staff, YojanaRole.ServiceSupervisor)))).Status);
    }

    [DatabaseFact]
    public async Task Asking_on_a_record_the_actor_cannot_view_is_refused()
    {
        var setup = await fixture.NewTenantAsync("hidden");
        var messageId = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.InboundMessages.Add(Inbound(setup.Tenant, messageId));
            await db.SaveChangesAsync();
        }
        await AssertRuleAsync("record_not_visible", () => InvokeAsync<AskQuestionResult>(setup.Tenant,
            new AskQuestion(messageId, RecordKind.IntegrationMessage, "Inbound server message", "Who should review this?",
                setup.Staff, null, Actor(setup.Other, YojanaRole.Technician))));
    }

    [DatabaseFact]
    public async Task Rls_hides_other_tenants_and_blocks_cross_tenant_insert()
    {
        var first = await SetupAsync("rlsa");
        var second = await fixture.NewTenantAsync("rlsb");
        var asked = await AskAsync(first.Tenant, first.RecordId, first.Administrator, [YojanaRole.TenantAdministrator], first.Staff, null);
        using (var scope = fixture.ScopeFor(second.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            Assert.False(await db.Questions.AnyAsync(x => x.QuestionId == asked.QuestionId));
        }
        using (var scope = fixture.ScopeFor(first.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            db.Questions.Add(new Question
            {
                TenantId = second.Tenant, QuestionId = Guid.CreateVersion7(), RecordKind = RecordKind.ImportJob,
                RecordId = first.RecordId, Title = "Cross tenant", Body = "Must be blocked", Status = QuestionStatus.Open,
                DirectedToUser = first.Staff, AskedBy = first.Administrator, AskedAt = fixture.Clock.UtcNow,
            });
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        }
    }

    [DatabaseFact]
    public async Task Customer_portal_principal_is_refused_even_if_it_carries_a_staff_role()
    {
        var setup = await SetupAsync("portal");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Staff, portal: true);
        using var response = await client.GetAsync($"/api/questions?recordKind={RecordKind.ImportJob}&recordId={setup.RecordId:D}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [DatabaseFact]
    public async Task Reads_return_names_role_labels_revision_counts_status_counts_and_keyset_pages()
    {
        var setup = await SetupAsync("reads");
        var first = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator, [YojanaRole.TenantAdministrator], null, YojanaRole.ServiceSupervisor);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMilliseconds(1);
        _ = await AskAsync(setup.Tenant, setup.RecordId, setup.Staff, [YojanaRole.ServiceSupervisor], setup.Other, null);
        var reply = await InvokeAsync<ReplyToQuestionResult>(setup.Tenant,
            new ReplyToQuestion(first.QuestionId, "Keep the failed media for retention.", Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new EditReply(first.QuestionId, reply.ReplyId, "Keep failed media for the retention period.", Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var queries = scope.ServiceProvider.GetRequiredService<QuestionQueries>();
        var viewer = new RecordReferenceViewer(setup.Administrator, [YojanaRole.TenantAdministrator]);
        var page = await queries.OnRecordAsync(RecordKind.ImportJob, setup.RecordId, null, null, 1, viewer, default);
        Assert.Single(page.Items);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(2, page.Counts.Open);
        var next = await queries.OnRecordAsync(RecordKind.ImportJob, setup.RecordId, null, page.NextCursor, 1, viewer, default);
        Assert.Single(next.Items);
        var thread = await queries.ThreadAsync(first.QuestionId, viewer, default);
        Assert.NotNull(thread);
        Assert.Equal("Fictional Administrator", thread.AskedByName);
        Assert.Equal("Service Supervisor", thread.DirectedToRoleLabel);
        Assert.Equal(1, thread.Replies.Single().RevisionCount);
        Assert.Single((await queries.RevisionsAsync(first.QuestionId, reply.ReplyId, viewer, default))!);
    }

    [DatabaseFact]
    public async Task Schema_contains_all_tables_with_tenant_first_keys_and_rls()
    {
        var setup = await fixture.NewTenantAsync("schema");
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        Assert.Empty(db.ValidateTenantKeyConventions());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables WHERE schema_id = SCHEMA_ID('knowledge') AND name <> '__migrations'").ToListAsync();
        Assert.Equal(new[] { "question", "question_participant", "question_reply", "question_reply_revision" }, tables.Order());
        var predicates = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.security_predicates p JOIN sys.tables t ON t.object_id=p.target_object_id WHERE t.schema_id=SCHEMA_ID('knowledge')").SingleAsync();
        Assert.Equal(12, predicates);
    }

    private async Task<(TenantId Tenant, Guid Administrator, Guid Staff, Guid Other, Guid RecordId)> SetupAsync(string prefix)
    {
        var setup = await fixture.NewTenantAsync(prefix);
        var recordId = Guid.CreateVersion7();
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.ImportJobs.Add(Import(setup.Tenant, recordId));
        await db.SaveChangesAsync();
        return (setup.Tenant, setup.Administrator, setup.Staff, setup.Other, recordId);
    }

    private Task<AskQuestionResult> AskAsync(TenantId tenant, Guid recordId, Guid actor,
        IReadOnlyList<string> roles, Guid? user, string? role) => InvokeAsync<AskQuestionResult>(tenant,
        new AskQuestion(recordId, RecordKind.ImportJob, "Storage server query", "Which queue covers this record?",
            user, role, new QuestionActor(actor, roles)));

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() });
    }

    private static async Task AssertRuleAsync(string rule, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<QuestionRuleException>(action);
        Assert.Equal(rule, exception.Rule);
    }

    private static QuestionActor Actor(Guid userId, params string[] roles) => new(userId, roles);

    private static ImportJob Import(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, JobId = id, CommandId = Guid.CreateVersion7(), Source = "service records",
        FileName = "fictional-storage-servers.csv", OriginalHash = new string('a', 64), Format = "csv",
        Mode = "apply", Status = "applied", ActorRef = "fixture", RecordedTime = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
    };

    private static InboundMessage Inbound(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, InboundMessageId = id, MessageId = Guid.CreateVersion7(), ConnectionId = Guid.CreateVersion7(),
        ConnectorCode = "FX01", ErpEntity = "service_order", ErpExternalId = "SO-1007", ErpChangeMarker = "1",
        MarkerKind = "sequence", ContractVersion = "1", MessageType = "ServiceOrderReceived", RawPayload = "{}",
        CanonicalMessage = "{}", OccurredAt = new DateTime(2026, 9, 1, 7, 0, 0, DateTimeKind.Utc),
        ObservedAt = new DateTime(2026, 9, 1, 7, 0, 1, DateTimeKind.Utc), AcceptedAt = new DateTime(2026, 9, 1, 7, 0, 2, DateTimeKind.Utc),
        IsLatest = true, TranslationStatus = InboundTranslationStatus.NeedsAttention,
    };
}

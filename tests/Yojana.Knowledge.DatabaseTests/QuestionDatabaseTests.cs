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
    public async Task Thread_returns_server_decided_allowed_actions_for_each_viewer()
    {
        var setup = await SetupAsync("actions");
        var asked = await AskAsync(setup.Tenant, setup.RecordId, setup.Other, [YojanaRole.Technician],
            null, YojanaRole.ServiceSupervisor);
        var reply = await InvokeAsync<ReplyToQuestionResult>(setup.Tenant,
            new ReplyToQuestion(asked.QuestionId, "Replace the failed controller after retaining the media.",
                Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var queries = scope.ServiceProvider.GetRequiredService<QuestionQueries>();
        var asker = (await queries.ThreadAsync(asked.QuestionId,
            new RecordReferenceViewer(setup.Other, [YojanaRole.Technician]), default))!;
        Assert.Equal(new QuestionAllowedActions(true, true, true, true, false), asker.AllowedActions);
        Assert.False(asker.Replies.Single().CanEdit);
        var directedRole = (await queries.ThreadAsync(asked.QuestionId,
            new RecordReferenceViewer(setup.Staff, [YojanaRole.ServiceSupervisor]), default))!;
        Assert.True(directedRole.AllowedActions.CanAcceptAnswer);
        Assert.True(directedRole.Replies.Single().CanEdit);
        var unrelated = (await queries.ThreadAsync(asked.QuestionId,
            new RecordReferenceViewer(setup.Administrator, [YojanaRole.Technician]), default))!;
        Assert.Equal(new QuestionAllowedActions(false, true, false, false, false), unrelated.AllowedActions);
        var administrator = (await queries.ThreadAsync(asked.QuestionId,
            new RecordReferenceViewer(setup.Administrator, [YojanaRole.TenantAdministrator]), default))!;
        Assert.Equal(new QuestionAllowedActions(false, true, true, true, false), administrator.AllowedActions);
        await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new AcceptQuestionAnswer(asked.QuestionId, reply.ReplyId, Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        var answered = (await queries.ThreadAsync(asked.QuestionId,
            new RecordReferenceViewer(setup.Other, [YojanaRole.Technician]), default))!;
        Assert.Equal(new QuestionAllowedActions(true, false, false, false, true, true), answered.AllowedActions);
    }

    [DatabaseFact]
    public async Task Addressee_lookup_is_names_only_server_paged_and_lists_only_present_roles()
    {
        var setup = await SetupAsync("picker");
        using var scope = fixture.ScopeFor(setup.Tenant);
        var queries = scope.ServiceProvider.GetRequiredService<QuestionQueries>();
        var first = await queries.AddresseesAsync("Fictional", null, 2, default);
        Assert.Equal(2, first.People.Items.Count);
        Assert.NotNull(first.People.NextCursor);
        Assert.DoesNotContain(first.People.Items, person => string.IsNullOrWhiteSpace(person.Name));
        Assert.Contains(first.Roles, role => role.Role == YojanaRole.ServiceSupervisor && role.People == 1);
        Assert.DoesNotContain(first.Roles, role => role.Role == YojanaRole.Quality);
        var second = await queries.AddresseesAsync("Fictional", first.People.NextCursor, 2, default);
        Assert.Single(second.People.Items);
        Assert.Empty(first.People.Items.Select(x => x.UserId).Intersect(second.People.Items.Select(x => x.UserId)));
    }

    [DatabaseFact]
    public async Task My_questions_filters_waiting_order_and_keyset_pages_on_the_server()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("mine");
        var oldest = await AskAsync(setup.Tenant, setup.RecordId, setup.Other, [YojanaRole.Technician],
            null, YojanaRole.ServiceSupervisor);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(2);
        _ = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator, [YojanaRole.TenantAdministrator],
            setup.Staff, null);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(2);
        _ = await AskAsync(setup.Tenant, setup.RecordId, setup.Staff, [YojanaRole.ServiceSupervisor],
            setup.Other, null);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var queries = scope.ServiceProvider.GetRequiredService<QuestionQueries>();
        var viewer = new RecordReferenceViewer(setup.Staff, [YojanaRole.ServiceSupervisor]);
        var first = await queries.MyAsync("all", QuestionStatus.Open, RecordKind.ImportJob, null,
            null, null, 1, viewer, default);
        Assert.Equal(oldest.QuestionId, first.Items.Single().QuestionId);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(3, first.Summary.WaitingByRecordKind.Single().Count);
        var second = await queries.MyAsync("all", QuestionStatus.Open, RecordKind.ImportJob, null,
            null, first.NextCursor, 1, viewer, default);
        Assert.Single(second.Items);
        Assert.NotEqual(oldest.QuestionId, second.Items.Single().QuestionId);
        var directed = await queries.MyAsync("directed_to_me", null, null, null, null, null, 10, viewer, default);
        Assert.Single(directed.Items);
        var roles = await queries.MyAsync("directed_to_roles", null, null, "four_to_seven_days",
            null, null, 10, viewer, default);
        Assert.Single(roles.Items);
        var searched = await queries.MyAsync("all", QuestionStatus.Open, null, null,
            "Storage", null, 10, viewer, default);
        Assert.Equal(3, searched.Items.Count);
        Assert.Empty((await queries.MyAsync("all", QuestionStatus.Open, null, null,
            "nonmatchingterm", null, 10, viewer, default)).Items);
    }

    [DatabaseFact]
    public async Task Search_ranks_accepted_answers_first_and_filters_record_visibility_tenant_and_portal()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("search");
        var answered = await InvokeAsync<AskQuestionResult>(setup.Tenant,
            new AskQuestion(setup.RecordId, RecordKind.ImportJob, "Controller replacement", "Which part is covered?",
                null, YojanaRole.ServiceSupervisor, Actor(setup.Administrator, YojanaRole.TenantAdministrator)));
        var accepted = await InvokeAsync<ReplyToQuestionResult>(setup.Tenant,
            new ReplyToQuestion(answered.QuestionId, "Retain the media before replacing the controller.",
                Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        await InvokeAsync<QuestionChangeResult>(setup.Tenant, new AcceptQuestionAnswer(answered.QuestionId,
            accepted.ReplyId, Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        _ = await InvokeAsync<AskQuestionResult>(setup.Tenant,
            new AskQuestion(setup.RecordId, RecordKind.ImportJob, "Media retention controller", "Still investigating.",
                setup.Staff, null, Actor(setup.Other, YojanaRole.Technician)));

        var hiddenId = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            integration.InboundMessages.Add(Inbound(setup.Tenant, hiddenId));
            await integration.SaveChangesAsync();
        }
        var hidden = await InvokeAsync<AskQuestionResult>(setup.Tenant,
            new AskQuestion(hiddenId, RecordKind.IntegrationMessage, "Controller message", "Hidden controller detail.",
                setup.Staff, null, Actor(setup.Administrator, YojanaRole.TenantAdministrator)));

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var queries = scope.ServiceProvider.GetRequiredService<QuestionQueries>();
            var results = await queries.SearchAsync("controller", false, null, 10,
                new RecordReferenceViewer(setup.Other, [YojanaRole.Technician]), default);
            Assert.Equal(answered.QuestionId, results.Questions[0].QuestionId);
            Assert.DoesNotContain(results.Questions, row => row.QuestionId == hidden.QuestionId);
            Assert.Empty(results.ReviewedEntries);
            var first = await queries.SearchAsync("controller", false, null, 1,
                new RecordReferenceViewer(setup.Other, [YojanaRole.Technician]), default);
            Assert.Equal(answered.QuestionId, first.Questions.Single().QuestionId);
            Assert.NotNull(first.NextCursor);
            var second = await queries.SearchAsync("controller", false, first.NextCursor, 1,
                new RecordReferenceViewer(setup.Other, [YojanaRole.Technician]), default);
            Assert.Single(second.Questions);
            Assert.NotEqual(answered.QuestionId, second.Questions.Single().QuestionId);
        }

        var other = await SetupAsync("searchother");
        using (var scope = fixture.ScopeFor(other.Tenant))
        {
            var results = await scope.ServiceProvider.GetRequiredService<QuestionQueries>().SearchAsync(
                "controller", false, null, 10,
                new RecordReferenceViewer(other.Staff, [YojanaRole.ServiceSupervisor]), default);
            Assert.Empty(results.Questions);
        }
        using var portal = await fixture.ClientAsync(setup.Tenant, setup.Other, portal: true);
        using var response = await portal.GetAsync("/api/questions/search?query=controller");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [DatabaseFact]
    public async Task Bell_counts_person_role_and_unread_reply_but_excludes_hidden_other_tenant_and_read_threads()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("bell");
        var person = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator,
            [YojanaRole.TenantAdministrator], setup.Staff, null);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        _ = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator,
            [YojanaRole.TenantAdministrator], null, YojanaRole.ServiceSupervisor);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        var mine = await AskAsync(setup.Tenant, setup.RecordId, setup.Staff,
            [YojanaRole.ServiceSupervisor], setup.Other, null);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        await InvokeAsync<ReplyToQuestionResult>(setup.Tenant,
            new ReplyToQuestion(mine.QuestionId, "Retain the failed media before replacement.",
                Actor(setup.Other, YojanaRole.Technician)));

        var hiddenId = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            integration.InboundMessages.Add(Inbound(setup.Tenant, hiddenId));
            await integration.SaveChangesAsync();
        }
        _ = await InvokeAsync<AskQuestionResult>(setup.Tenant, new AskQuestion(hiddenId,
            RecordKind.IntegrationMessage, "Hidden inbound message", "Which queue owns it?", setup.Staff, null,
            Actor(setup.Administrator, YojanaRole.TenantAdministrator)));
        var otherTenant = await SetupAsync("bellother");
        _ = await AskAsync(otherTenant.Tenant, otherTenant.RecordId, otherTenant.Administrator,
            [YojanaRole.TenantAdministrator], otherTenant.Staff, null);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var service = scope.ServiceProvider.GetRequiredService<QuestionNotificationService>();
            var viewer = new RecordReferenceViewer(setup.Staff, [YojanaRole.ServiceSupervisor]);
            var bell = await service.BellAsync(viewer, 10, default);
            Assert.Equal(3, bell.Count);
            Assert.Contains(bell.Items, x => x.QuestionId == person.QuestionId && x.Reason == "Waiting for your reply");
            Assert.Contains(bell.Items, x => x.QuestionId == mine.QuestionId && x.Reason == "New reply");
            Assert.DoesNotContain(bell.Items, x => x.RecordKind == RecordKind.IntegrationMessage);
            await scope.ServiceProvider.GetRequiredService<QuestionQueries>().ThreadAsync(mine.QuestionId, viewer, default);
            Assert.Equal(2, (await service.BellAsync(viewer, 10, default)).Count);
        }

        await InvokeAsync<ReplyToQuestionResult>(setup.Tenant, new ReplyToQuestion(person.QuestionId,
            "Use the next-business-day service queue.", Actor(setup.Staff, YojanaRole.ServiceSupervisor)));
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var bell = await scope.ServiceProvider.GetRequiredService<QuestionNotificationService>().BellAsync(
                new RecordReferenceViewer(setup.Staff, [YojanaRole.ServiceSupervisor]), 10, default);
            Assert.DoesNotContain(bell.Items, x => x.QuestionId == person.QuestionId);
        }

        using var portal = await fixture.ClientAsync(setup.Tenant, setup.Staff, portal: true);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await portal.GetAsync("/api/questions/notifications/bell")).StatusCode);
    }

    [DatabaseFact]
    public async Task Notification_preferences_use_split_tenant_defaults_and_a_person_override()
    {
        var setup = await SetupAsync("preference");
        using var scope = fixture.ScopeFor(setup.Tenant);
        var service = scope.ServiceProvider.GetRequiredService<QuestionNotificationService>();
        var initial = await service.PreferenceAsync(setup.Staff, default);
        Assert.Equal(QuestionNotificationMode.Immediately, initial.PersonalDefault);
        Assert.Equal(QuestionNotificationMode.DailyDigest, initial.RoleDefault);
        Assert.Null(initial.Choice);
        var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
        await settings.SetAsync(QuestionNotificationSettings.PersonalDefaultKey,
            QuestionNotificationMode.DailyDigest, default);
        await settings.SetAsync(QuestionNotificationSettings.RoleDefaultKey, QuestionNotificationMode.Off, default);
        var tenantDefaults = await service.PreferenceAsync(setup.Staff, default);
        Assert.Equal(QuestionNotificationMode.DailyDigest, tenantDefaults.PersonalDefault);
        Assert.Equal(QuestionNotificationMode.Off, tenantDefaults.RoleDefault);
        Assert.Equal(QuestionNotificationMode.Immediately,
            (await service.UpdatePreferenceAsync(setup.Staff, QuestionNotificationMode.Immediately, default)).Choice);
        Assert.Null((await service.UpdatePreferenceAsync(setup.Staff, null, default)).Choice);
    }

    [DatabaseFact]
    public async Task Immediate_mail_is_enqueued_once_and_off_enqueues_nothing()
    {
        var setup = await SetupAsync("immediate");
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await scope.ServiceProvider.GetRequiredService<QuestionNotificationService>().UpdatePreferenceAsync(
                setup.Staff, QuestionNotificationMode.Immediately, default);
        var asked = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator,
            [YojanaRole.TenantAdministrator], setup.Staff, null);
        await WaitForNotificationsAsync(setup.Tenant, setup.Staff, 1);
        var duplicate = new QuestionAsked(asked.QuestionId, RecordKind.ImportJob, setup.RecordId,
            setup.Administrator, setup.Staff, null, fixture.Clock.UtcNow);
        await InvokeAsync(setup.Tenant, duplicate);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var rows = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Notifications.AsNoTracking()
                .Where(x => x.RecipientUserId == setup.Staff && x.QuestionId == asked.QuestionId).ToListAsync();
            Assert.Single(rows);
            Assert.NotNull(rows[0].MailId);
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
            await scope.ServiceProvider.GetRequiredService<QuestionNotificationService>().UpdatePreferenceAsync(
                setup.Other, QuestionNotificationMode.Off, default);
        var off = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator,
            [YojanaRole.TenantAdministrator], setup.Other, null);
        await Task.Delay(250);
        using (var scope = fixture.ScopeFor(setup.Tenant))
            Assert.False(await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Notifications.AsNoTracking()
                .AnyAsync(x => x.RecipientUserId == setup.Other && x.QuestionId == off.QuestionId));
    }

    [DatabaseFact]
    public async Task Daily_digest_is_idempotent_per_person_and_day()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("digest");
        _ = await AskAsync(setup.Tenant, setup.RecordId, setup.Administrator,
            [YojanaRole.TenantAdministrator], null, YojanaRole.ServiceSupervisor);
        await WaitForNotificationsAsync(setup.Tenant, setup.Staff, 1);
        fixture.Clock.UtcNow = new DateTime(2026, 9, 2, 7, 0, 0, DateTimeKind.Utc);
        var run = new RunQuestionDailyDigest(new DateOnly(2026, 9, 2), fixture.Clock.UtcNow);
        await InvokeAsync(setup.Tenant, run);
        await InvokeAsync(setup.Tenant, run);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        Assert.Single(await db.Digests.AsNoTracking().Where(x => x.UserId == setup.Staff
            && x.DigestDate == run.Date).ToListAsync());
        Assert.All(await db.Notifications.AsNoTracking().Where(x => x.RecipientUserId == setup.Staff).ToListAsync(),
            row => Assert.Equal(run.Date, row.DigestDate));
    }

    [DatabaseFact]
    public async Task Schema_contains_all_tables_with_tenant_first_keys_and_rls()
    {
        var setup = await fixture.NewTenantAsync("schema");
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        Assert.Empty(db.ValidateTenantKeyConventions());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables WHERE schema_id = SCHEMA_ID('knowledge') AND name <> '__migrations'").ToListAsync();
        Assert.Equal(new[] { "knowledge_entry", "knowledge_entry_record_link", "knowledge_entry_review_reason",
            "knowledge_entry_review_round", "knowledge_entry_tag", "knowledge_entry_version",
            "knowledge_entry_written_against", "knowledge_screen_change", "question", "question_digest",
            "question_notification", "question_notification_preference", "question_participant", "question_reply",
            "question_reply_revision" }, tables.Order());
        var predicates = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.security_predicates p JOIN sys.tables t ON t.object_id=p.target_object_id WHERE t.schema_id=SCHEMA_ID('knowledge')").SingleAsync();
        Assert.Equal(45, predicates);
        var searchKeys = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name IN ('ux_question_question_id', 'ux_question_reply_reply_id', 'ux_knowledge_entry_entry_id')").SingleAsync();
        Assert.Equal(3, searchKeys);
        var fullTextInstalled = await db.Database.SqlQueryRaw<int>("SELECT CONVERT(int, FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')) AS [Value]").SingleAsync();
        var fullTextIndexes = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.fulltext_indexes WHERE object_id IN (OBJECT_ID('knowledge.question'), OBJECT_ID('knowledge.question_reply'), OBJECT_ID('knowledge.knowledge_entry'))").SingleAsync();
        Assert.Equal(fullTextInstalled == 1 ? 3 : 0, fullTextIndexes);
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

    private async Task InvokeAsync(TenantId tenant, object command)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(command,
            new DeliveryOptions { TenantId = tenant.ToString() });
    }

    private async Task WaitForNotificationsAsync(TenantId tenant, Guid recipient, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var scope = fixture.ScopeFor(tenant);
            if (await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Notifications.AsNoTracking()
                .CountAsync(x => x.RecipientUserId == recipient) >= count) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Question notifications were not handled within five seconds.");
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

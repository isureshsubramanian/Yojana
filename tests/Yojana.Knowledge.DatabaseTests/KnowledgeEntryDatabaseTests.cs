using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Entries;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Knowledge.DatabaseTests;

[Collection("knowledge host")]
public sealed class KnowledgeEntryDatabaseTests(KnowledgeHostFixture fixture)
{
    [DatabaseFact]
    public async Task Create_requires_an_answered_question_with_an_accepted_reply_and_prefills_version_one()
    {
        var setup = await SetupAsync("entrycreate");
        await AssertRuleAsync("entry_requires_accepted_answer", () => InvokeAsync<EntryChangeResult>(setup.Tenant,
            new CreateEntryFromAnswer(setup.QuestionId, Actor(setup.Author, YojanaRole.TenantAdministrator))));
        await AnswerAsync(setup);
        var created = await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new CreateEntryFromAnswer(setup.QuestionId, Actor(setup.Author, YojanaRole.TenantAdministrator)));
        Assert.Equal(KnowledgeEntryStatus.Draft, created.Status);
        Assert.Equal(1, created.CurrentVersion);
        await AssertRuleAsync("entry_already_exists", () => InvokeAsync<EntryChangeResult>(setup.Tenant,
            new CreateEntryFromAnswer(setup.QuestionId, Actor(setup.Author, YojanaRole.TenantAdministrator))));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        var entry = await db.Entries.SingleAsync(x => x.EntryId == created.EntryId);
        var version = await db.EntryVersions.SingleAsync(x => x.EntryId == created.EntryId);
        Assert.Equal("Which service covers this storage array?", entry.Title);
        Assert.Equal("The array lost a controller after a power event.", entry.Problem);
        Assert.Equal("Use accidental damage cover and retain the failed media.", entry.Answer);
        Assert.Equal(entry.Answer, version.Answer);
        Assert.Single(await db.EntryRecordLinks.Where(x => x.EntryId == created.EntryId).ToListAsync());
    }

    [DatabaseFact]
    public async Task Edit_keeps_every_version_and_send_back_requires_a_note()
    {
        var setup = await PublishedSetupAsync("versions", publish: false);
        var edited = await InvokeAsync<EntryChangeResult>(setup.Tenant, new EditDraft(setup.EntryId,
            Draft(setup.RecordId, "Replace the controller only after the retention copy is complete."),
            Actor(setup.Author, YojanaRole.TenantAdministrator)));
        Assert.Equal(3, edited.CurrentVersion);
        await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new SubmitEntryForReview(setup.EntryId, Actor(setup.Author, YojanaRole.TenantAdministrator)));
        await AssertRuleAsync("entry_send_back_note_required", () => InvokeAsync<EntryChangeResult>(setup.Tenant,
            new SendBackEntry(setup.EntryId, " ", Actor(setup.Reviewer, YojanaRole.ServiceSupervisor))));
        var sentBack = await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new SendBackEntry(setup.EntryId, "Name the next-business-day service step.",
                Actor(setup.Reviewer, YojanaRole.ServiceSupervisor)));
        Assert.Equal(KnowledgeEntryStatus.Draft, sentBack.Status);
        await InvokeAsync<EntryChangeResult>(setup.Tenant, new EditDraft(setup.EntryId,
            Draft(setup.RecordId, "Retain the media, then book next-business-day controller service."),
            Actor(setup.Author, YojanaRole.TenantAdministrator)));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        Assert.Equal(new[] { 1, 2, 3, 4 }, await db.EntryVersions.Where(x => x.EntryId == setup.EntryId)
            .OrderBy(x => x.Version).Select(x => x.Version).ToArrayAsync());
        Assert.Equal("send_back", (await db.EntryReviewRounds.SingleAsync(x => x.EntryId == setup.EntryId)).Decision);
    }

    [DatabaseFact]
    public async Task Author_can_never_review_and_reviewer_roles_come_from_the_tenant_setting()
    {
        var setup = await PublishedSetupAsync("reviewroles", publish: false);
        await SetReviewerRolesAsync(setup.Tenant, YojanaRole.TenantAdministrator, YojanaRole.Technician);
        await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new SubmitEntryForReview(setup.EntryId, Actor(setup.Author, YojanaRole.TenantAdministrator)));
        await AssertRuleAsync("entry_self_review_forbidden", () => InvokeAsync<EntryChangeResult>(setup.Tenant,
            new ApproveEntry(setup.EntryId, null, Actor(setup.Author, YojanaRole.TenantAdministrator))));
        await AssertRuleAsync("entry_review_role_required", () => InvokeAsync<EntryChangeResult>(setup.Tenant,
            new ApproveEntry(setup.EntryId, null, Actor(setup.Reviewer, YojanaRole.ServiceSupervisor))));
        var approved = await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new ApproveEntry(setup.EntryId, "Checked against the service procedure.",
                Actor(setup.Reader, YojanaRole.Technician)));
        Assert.Equal(KnowledgeEntryStatus.Published, approved.Status);
    }

    [DatabaseFact]
    public async Task Linked_records_the_reader_cannot_open_are_counted_without_names_or_ids()
    {
        var setup = await PublishedSetupAsync("hiddenlinks", publish: false);
        var messageId = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            integration.InboundMessages.Add(Inbound(setup.Tenant, messageId));
            await integration.SaveChangesAsync();
        }
        await InvokeAsync<EntryChangeResult>(setup.Tenant, new EditDraft(setup.EntryId,
            Draft(setup.RecordId, "Keep the source message private.",
                [new(RecordKind.ImportJob, setup.RecordId), new(RecordKind.IntegrationMessage, messageId)]),
            Actor(setup.Author, YojanaRole.TenantAdministrator)));
        await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new SubmitEntryForReview(setup.EntryId, Actor(setup.Author, YojanaRole.TenantAdministrator)));
        await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new ApproveEntry(setup.EntryId, null, Actor(setup.Reviewer, YojanaRole.ServiceSupervisor)));

        using var readScope = fixture.ScopeFor(setup.Tenant);
        var view = await readScope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>().EntryAsync(setup.EntryId,
            new RecordReferenceViewer(setup.Reader, [YojanaRole.Technician]), default);
        Assert.NotNull(view);
        Assert.Equal(1, view.RecordLinks.HiddenCount);
        Assert.Single(view.RecordLinks.Visible);
        var json = JsonSerializer.Serialize(view.RecordLinks);
        Assert.DoesNotContain(messageId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceOrderReceived", json, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Other_tenants_and_portal_users_cannot_read_entries()
    {
        var setup = await PublishedSetupAsync("entrytenant");
        var other = await fixture.NewTenantAsync("entryother");
        using (var scope = fixture.ScopeFor(other.Tenant))
        {
            var view = await scope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>().EntryAsync(setup.EntryId,
                new RecordReferenceViewer(other.Other, [YojanaRole.Technician]), default);
            Assert.Null(view);
        }
        using var portal = await fixture.ClientAsync(setup.Tenant, setup.Reader, portal: true);
        using var response = await portal.GetAsync($"/api/knowledge/entries/{setup.EntryId:D}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [DatabaseFact]
    public async Task Published_entries_rank_in_the_reviewed_slot_before_matching_questions()
    {
        var setup = await PublishedSetupAsync("entrysearch", answer: "Retain the controller media before replacement.");
        _ = await InvokeAsync<AskQuestionResult>(setup.Tenant, new AskQuestion(setup.RecordId, RecordKind.ImportJob,
            "Controller media query", "Is the controller media retained?", setup.Reviewer, null,
            Actor(setup.Reader, YojanaRole.Technician)));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var results = await scope.ServiceProvider.GetRequiredService<QuestionQueries>().SearchAsync("controller", false,
            null, 10, new RecordReferenceViewer(setup.Reader, [YojanaRole.Technician]), default);
        Assert.Equal(setup.EntryId, results.ReviewedEntries.Single().EntryId);
        Assert.NotEmpty(results.Questions);
    }

    [DatabaseFact]
    public async Task Browse_filters_by_role_and_task_and_retire_removes_the_entry_from_knowledge()
    {
        var setup = await PublishedSetupAsync("browse");
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var queries = scope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>();
            var page = await queries.BrowseAsync(null, YojanaRole.ServiceDesk, "media_retention", null, 10, default);
            Assert.Equal(setup.EntryId, page.Items.Single().EntryId);
            Assert.Contains(page.Roles, x => x.Value == YojanaRole.ServiceDesk);
            Assert.Contains(page.Tasks, x => x.Value == "media_retention");
        }
        var retired = await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new RetireEntry(setup.EntryId, "The controller procedure was replaced.",
                Actor(setup.Reviewer, YojanaRole.ServiceSupervisor)));
        Assert.Equal(KnowledgeEntryStatus.Retired, retired.Status);
        using var afterScope = fixture.ScopeFor(setup.Tenant);
        Assert.Empty((await afterScope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>()
            .BrowseAsync(null, null, null, null, 10, default)).Items);
    }

    [DatabaseFact]
    public async Task Changes_since_the_last_round_compare_saved_versions()
    {
        var setup = await PublishedSetupAsync("changes", publish: false);
        await InvokeAsync<EntryChangeResult>(setup.Tenant, new EditDraft(setup.EntryId,
            Draft(setup.RecordId, "Version two answer."), Actor(setup.Author, YojanaRole.TenantAdministrator)));
        await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new SubmitEntryForReview(setup.EntryId, Actor(setup.Author, YojanaRole.TenantAdministrator)));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var changes = await scope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>()
            .ChangesSinceLastRoundAsync(setup.EntryId,
                new RecordReferenceViewer(setup.Reviewer, [YojanaRole.ServiceSupervisor]), default);
        Assert.Contains(changes!, x => x.Field == "Answer" && x.After == "Version two answer.");
    }

    private async Task<(TenantId Tenant, Guid Author, Guid Reviewer, Guid Reader, Guid RecordId, Guid QuestionId)> SetupAsync(string prefix)
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var people = await fixture.NewTenantAsync(prefix);
        var recordId = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            serialisation.ImportJobs.Add(Import(people.Tenant, recordId));
            await serialisation.SaveChangesAsync();
        }
        var question = await InvokeAsync<AskQuestionResult>(people.Tenant, new AskQuestion(recordId,
            RecordKind.ImportJob, "Which service covers this storage array?",
            "The array lost a controller after a power event.", people.Staff, null,
            Actor(people.Administrator, YojanaRole.TenantAdministrator)));
        return (people.Tenant, people.Administrator, people.Staff, people.Other, recordId, question.QuestionId);
    }

    private async Task<(TenantId Tenant, Guid Author, Guid Reviewer, Guid Reader, Guid RecordId, Guid EntryId)> PublishedSetupAsync(
        string prefix, bool publish = true, string? answer = null)
    {
        var setup = await SetupAsync(prefix);
        await AnswerAsync(setup, answer);
        var created = await InvokeAsync<EntryChangeResult>(setup.Tenant,
            new CreateEntryFromAnswer(setup.QuestionId, Actor(setup.Author, YojanaRole.TenantAdministrator)));
        await InvokeAsync<EntryChangeResult>(setup.Tenant, new EditDraft(created.EntryId,
            Draft(setup.RecordId, answer ?? "Retain the media before replacing the storage controller."),
            Actor(setup.Author, YojanaRole.TenantAdministrator)));
        if (publish)
        {
            await InvokeAsync<EntryChangeResult>(setup.Tenant,
                new SubmitEntryForReview(created.EntryId, Actor(setup.Author, YojanaRole.TenantAdministrator)));
            await InvokeAsync<EntryChangeResult>(setup.Tenant,
                new ApproveEntry(created.EntryId, null, Actor(setup.Reviewer, YojanaRole.ServiceSupervisor)));
        }
        return (setup.Tenant, setup.Author, setup.Reviewer, setup.Reader, setup.RecordId, created.EntryId);
    }

    private async Task AnswerAsync(
        (TenantId Tenant, Guid Author, Guid Reviewer, Guid Reader, Guid RecordId, Guid QuestionId) setup,
        string? answer = null)
    {
        var reply = await InvokeAsync<ReplyToQuestionResult>(setup.Tenant,
            new ReplyToQuestion(setup.QuestionId, answer ?? "Use accidental damage cover and retain the failed media.",
                Actor(setup.Reviewer, YojanaRole.ServiceSupervisor)));
        await InvokeAsync<QuestionChangeResult>(setup.Tenant,
            new AcceptQuestionAnswer(setup.QuestionId, reply.ReplyId,
                Actor(setup.Author, YojanaRole.TenantAdministrator)));
    }

    private static EntryDraftInput Draft(Guid recordId, string answer,
        IReadOnlyList<EntryRecordLinkInput>? links = null) => new(
        "Storage controller replacement and media retention",
        "A rack storage array loses one controller after a power event.", answer,
        "Copy the retained media.\nBook next-business-day service.",
        [new(KnowledgeTagKind.Role, YojanaRole.ServiceDesk), new(KnowledgeTagKind.Task, "media_retention"),
            new(KnowledgeTagKind.ClaimCause, "POWER_SURGE")],
        links ?? [new(RecordKind.ImportJob, recordId)],
        [new(WrittenAgainstKind.Screen, "claims.intake", "New claim")]);

    private async Task SetReviewerRolesAsync(TenantId tenant, params string[] roles)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(EntryReviewerSettings.Key,
            JsonSerializer.Serialize(roles), default);
    }

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
        FileName = "fictional-storage-arrays.csv", OriginalHash = new string('b', 64), Format = "csv",
        Mode = "apply", Status = "applied", ActorRef = "fixture",
        RecordedTime = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
    };

    private static InboundMessage Inbound(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, InboundMessageId = id, MessageId = Guid.CreateVersion7(), ConnectionId = Guid.CreateVersion7(),
        ConnectorCode = "FX02", ErpEntity = "service_order", ErpExternalId = "SO-2042", ErpChangeMarker = "1",
        MarkerKind = "sequence", ContractVersion = "1", MessageType = "ServiceOrderReceived", RawPayload = "{}",
        CanonicalMessage = "{}", OccurredAt = new DateTime(2026, 9, 1, 7, 0, 0, DateTimeKind.Utc),
        ObservedAt = new DateTime(2026, 9, 1, 7, 0, 1, DateTimeKind.Utc),
        AcceptedAt = new DateTime(2026, 9, 1, 7, 0, 2, DateTimeKind.Utc), IsLatest = true,
        TranslationStatus = InboundTranslationStatus.NeedsAttention,
    };
}

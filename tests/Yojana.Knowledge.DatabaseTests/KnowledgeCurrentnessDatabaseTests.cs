using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Entries;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.TimeZones;

namespace Yojana.Knowledge.DatabaseTests;

[Collection("knowledge host")]
public sealed class KnowledgeCurrentnessDatabaseTests(KnowledgeHostFixture fixture)
{
    [DatabaseFact]
    public async Task Publishing_a_replacement_policy_flags_only_published_dependants_and_notifies_once()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var people = await fixture.NewTenantAsync("curpol");
        var v1 = await PublishedPolicyAsync(people.Tenant, "STORAGE-NBD", new DateOnly(2026, 1, 1));
        var published = await EntryAsync(people, "policy published", true,
            new(WrittenAgainstKind.PolicyVersion, v1.PolicyVersionId.ToString("D"), "Storage NBD v1"));
        var draft = await EntryAsync(people, "policy draft", false,
            new(WrittenAgainstKind.PolicyVersion, v1.PolicyVersionId.ToString("D"), "Storage NBD v1"));
        var otherPeople = await fixture.NewTenantAsync("curpoth");
        var other = await EntryAsync(otherPeople, "policy other", true,
            new(WrittenAgainstKind.PolicyVersion, v1.PolicyVersionId.ToString("D"), "Storage NBD v1"));
        using (var scope = fixture.ScopeFor(people.Tenant))
            await scope.ServiceProvider.GetRequiredService<QuestionNotificationService>()
                .UpdatePreferenceAsync(people.Staff, QuestionNotificationMode.Immediately, default);

        fixture.Clock.UtcNow = new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
        var v2 = await InvokeAsync<PolicyDraftResult>(people.Tenant, new CreatePolicyDraft(Guid.CreateVersion7(),
            "STORAGE-NBD", "Storage next-business-day service", "base_warranty", EntitlementType.Manufacturer,
            new DateOnly(2027, 1, 1), null, "fixture"));
        await InvokeAsync<PolicyDraftResult>(people.Tenant, new PublishPolicyVersion(v2.PolicyVersionId, "fixture"));
        await WaitForAsync(people.Tenant, async db => await db.Entries.AnyAsync(x =>
            x.EntryId == published.EntryId && x.NeedsReview));

        Guid reasonId;
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            Assert.True((await db.Entries.SingleAsync(x => x.EntryId == published.EntryId)).NeedsReview);
            Assert.False((await db.Entries.SingleAsync(x => x.EntryId == draft.EntryId)).NeedsReview);
            var reason = await db.EntryReviewReasons.SingleAsync(x => x.EntryId == published.EntryId);
            Assert.Equal(KnowledgeReviewReasonKind.PolicyVersionSuperseded, reason.Kind);
            Assert.Contains("v1 was superseded by v2", reason.Detail, StringComparison.Ordinal);
            reasonId = reason.ReasonId;
        }
        using (var scope = fixture.ScopeFor(otherPeople.Tenant))
            Assert.False((await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Entries
                .SingleAsync(x => x.EntryId == other.EntryId)).NeedsReview);

        var duplicate = new KnowledgeEntryNeedsReview(published.EntryId, reasonId,
            KnowledgeReviewReasonKind.PolicyVersionSuperseded, "Storage NBD v1", fixture.Clock.UtcNow);
        await InvokeAsync(people.Tenant, duplicate);
        await InvokeAsync(people.Tenant, duplicate);
        await WaitForAsync(people.Tenant, async db => await db.Notifications.CountAsync(x =>
            x.EntryId == published.EntryId && x.EventKind == "entry_needs_review") == 1);

        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var viewer = new RecordReferenceViewer(people.Other, [YojanaRole.Technician]);
            var view = await scope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>()
                .EntryAsync(published.EntryId, viewer, default);
            Assert.NotNull(view);
            Assert.True(view.NeedsReview);
            Assert.Single(view.ReviewReasons);
            var browse = await scope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>()
                .BrowseAsync(null, null, null, null, 10, default);
            Assert.True(browse.Items.Single(x => x.EntryId == published.EntryId).NeedsReview);
            var search = await scope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>()
                .BrowseAsync("Original", null, null, null, 10, default);
            Assert.True(search.Items.Single(x => x.EntryId == published.EntryId).NeedsReview);
            var reviewQueue = await scope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>()
                .ReviewQueueAsync("needs_review", null, 10,
                    new RecordReferenceViewer(people.Staff, [YojanaRole.ServiceSupervisor]), default);
            var reviewItem = Assert.Single(reviewQueue.Items, x => x.EntryId == published.EntryId);
            Assert.True(reviewItem.CanConfirmStillCorrect);
            Assert.Single(reviewItem.ReviewReasons);
            var bell = await scope.ServiceProvider.GetRequiredService<QuestionNotificationService>().BellAsync(
                new RecordReferenceViewer(people.Staff, [YojanaRole.ServiceSupervisor]), 10, default);
            Assert.Contains(bell.Items, x => x.EntryId == published.EntryId && x.Reason == "May be out of date");
        }

        await AssertRuleAsync("entry_self_review_forbidden", () => InvokeAsync<EntryChangeResult>(people.Tenant,
            new ConfirmKnowledgeEntryStillCorrect(published.EntryId,
                Actor(people.Administrator, YojanaRole.ServiceSupervisor, YojanaRole.TenantAdministrator))));
        await InvokeAsync<EntryChangeResult>(people.Tenant, new ConfirmKnowledgeEntryStillCorrect(published.EntryId,
            Actor(people.Staff, YojanaRole.ServiceSupervisor)));
        using var clearedScope = fixture.ScopeFor(people.Tenant);
        var clearedDb = clearedScope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        Assert.False((await clearedDb.Entries.SingleAsync(x => x.EntryId == published.EntryId)).NeedsReview);
        Assert.Equal("still_correct", (await clearedDb.EntryReviewReasons.SingleAsync(x =>
            x.EntryId == published.EntryId)).Clearance);
    }

    [DatabaseFact]
    public async Task Screen_change_is_audited_and_an_edited_entry_stays_readable_until_approval()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        var people = await fixture.NewTenantAsync("curscr");
        var published = await EntryAsync(people, "screen published", true,
            new(WrittenAgainstKind.Screen, "claims.intake", "New claim"));
        var draft = await EntryAsync(people, "screen draft", false,
            new(WrittenAgainstKind.Screen, "claims.intake", "New claim"));

        var changed = await InvokeAsync<ScreenChangeResult>(people.Tenant, new MarkKnowledgeScreenChanged(
            "claims.intake", "The coverage evidence panel now asks for the service level.",
            Actor(people.Administrator, YojanaRole.TenantAdministrator)));
        Assert.Equal(1, changed.EntriesFlagged);
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            Assert.True((await db.Entries.SingleAsync(x => x.EntryId == published.EntryId)).NeedsReview);
            Assert.False((await db.Entries.SingleAsync(x => x.EntryId == draft.EntryId)).NeedsReview);
            var audit = await db.ScreenChanges.SingleAsync(x => x.ChangeId == changed.ChangeId);
            Assert.Equal(people.Administrator, audit.ChangedBy);
            Assert.Equal("New claim", audit.ScreenLabel);
            var screens = await scope.ServiceProvider.GetRequiredService<KnowledgeScreenQueries>()
                .ListAsync(null, 10, default);
            var referenced = Assert.Single(screens.Items, x => x.Reference == "claims.intake");
            Assert.Equal("New claim", referenced.Label);
            Assert.Equal(1, referenced.PublishedEntries);
            Assert.False(string.IsNullOrWhiteSpace(referenced.LastChangedByName));
        }

        await InvokeAsync<EntryChangeResult>(people.Tenant, new EditDraft(published.EntryId,
            Draft(published.RecordId, "Updated reviewed answer.",
                new(WrittenAgainstKind.Screen, "claims.intake", "New claim")),
            Actor(people.Administrator, YojanaRole.TenantAdministrator)));
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var publicView = await scope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>().EntryAsync(
                published.EntryId, new RecordReferenceViewer(people.Other, [YojanaRole.Technician]), default);
            Assert.NotNull(publicView);
            Assert.Equal("Original reviewed answer.", publicView.Answer);
            Assert.True(publicView.NeedsReview);
        }
        await InvokeAsync<EntryChangeResult>(people.Tenant, new SubmitEntryForReview(published.EntryId,
            Actor(people.Administrator, YojanaRole.TenantAdministrator)));
        await InvokeAsync<EntryChangeResult>(people.Tenant, new ApproveEntry(published.EntryId, null,
            Actor(people.Staff, YojanaRole.ServiceSupervisor)));
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var view = await scope.ServiceProvider.GetRequiredService<KnowledgeEntryQueries>().EntryAsync(
                published.EntryId, new RecordReferenceViewer(people.Other, [YojanaRole.Technician]), default);
            Assert.NotNull(view);
            Assert.Equal("Updated reviewed answer.", view.Answer);
            Assert.False(view.NeedsReview);
        }

        using var portal = await fixture.ClientAsync(people.Tenant, people.Other, portal: true);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await portal.GetAsync("/api/knowledge/screens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await portal.PostAsJsonAsync("/api/knowledge/screens/changed",
            new { screenReference = "claims.intake", reason = "Portal attempt" })).StatusCode);
    }

    [DatabaseFact]
    public async Task Digest_schedule_uses_seven_in_each_tenant_zone_and_tenant_local_dates()
    {
        var india = await fixture.NewTenantAsync("dgin");
        var newYork = await fixture.NewTenantAsync("dgny");
        await SetTimeZoneAsync(india.Tenant, "Asia/Kolkata", india.Administrator);
        await SetTimeZoneAsync(newYork.Tenant, "America/New_York", newYork.Administrator);
        var boundary = new DateTime(2026, 9, 1, 23, 30, 0, DateTimeKind.Utc);

        using (var scope = fixture.ScopeFor(india.Tenant))
        {
            var schedule = scope.ServiceProvider.GetRequiredService<QuestionDailyDigestSchedule>();
            Assert.Equal(new DateOnly(2026, 9, 2), await schedule.LocalDateAsync(boundary, default));
            var due = await schedule.DueAsync(new DateTime(2026, 9, 2, 1, 30, 0, DateTimeKind.Utc), default);
            Assert.NotNull(due);
            Assert.Equal(new DateOnly(2026, 9, 2), due.Date);
            Assert.Null(await schedule.DueAsync(new DateTime(2026, 9, 2, 7, 0, 0, DateTimeKind.Utc), default));
        }
        using (var scope = fixture.ScopeFor(newYork.Tenant))
        {
            var schedule = scope.ServiceProvider.GetRequiredService<QuestionDailyDigestSchedule>();
            Assert.Equal(new DateOnly(2026, 9, 1), await schedule.LocalDateAsync(boundary, default));
            var due = await schedule.DueAsync(new DateTime(2026, 9, 2, 11, 0, 0, DateTimeKind.Utc), default);
            Assert.NotNull(due);
            Assert.Equal(new DateOnly(2026, 9, 2), due.Date);
            Assert.Null(await schedule.DueAsync(new DateTime(2026, 9, 2, 7, 0, 0, DateTimeKind.Utc), default));
        }
    }

    private async Task<PolicyDraftResult> PublishedPolicyAsync(TenantId tenant, string code, DateOnly from)
    {
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty",
            false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90",
            ReplacementRule.MaxRemainingOrMinDays, 90, null, "longer of remaining and 90 days"));
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.CreateVersion7(), code,
            "Storage next-business-day service", "base_warranty", EntitlementType.Manufacturer, from,
            StandardTerms(), "fixture"));
        return await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "fixture"));
    }

    private async Task<EntrySetup> EntryAsync(
        (TenantId Tenant, Guid Administrator, Guid Staff, Guid Other) people, string title, bool publish,
        WrittenAgainstInput writtenAgainst)
    {
        var recordId = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.ImportJobs.Add(new ImportJob
            {
                TenantId = people.Tenant, JobId = recordId, CommandId = Guid.CreateVersion7(),
                Source = "service records", FileName = $"fictional-{title.Replace(' ', '-')}.csv",
                OriginalHash = string.Concat(recordId.ToString("N"), recordId.ToString("N")), Format = "csv",
                Mode = "apply", Status = "applied",
                ActorRef = "fixture", RecordedTime = fixture.Clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var asked = await InvokeAsync<AskQuestionResult>(people.Tenant, new AskQuestion(recordId,
            RecordKind.ImportJob, title, "Which service path applies?", people.Staff, null,
            Actor(people.Administrator, YojanaRole.TenantAdministrator)));
        var reply = await InvokeAsync<ReplyToQuestionResult>(people.Tenant, new ReplyToQuestion(asked.QuestionId,
            "Original reviewed answer.", Actor(people.Staff, YojanaRole.ServiceSupervisor)));
        await InvokeAsync<QuestionChangeResult>(people.Tenant, new AcceptQuestionAnswer(asked.QuestionId,
            reply.ReplyId, Actor(people.Administrator, YojanaRole.TenantAdministrator)));
        var created = await InvokeAsync<EntryChangeResult>(people.Tenant, new CreateEntryFromAnswer(asked.QuestionId,
            Actor(people.Administrator, YojanaRole.TenantAdministrator)));
        await InvokeAsync<EntryChangeResult>(people.Tenant, new EditDraft(created.EntryId,
            Draft(recordId, "Original reviewed answer.", writtenAgainst),
            Actor(people.Administrator, YojanaRole.TenantAdministrator)));
        if (publish)
        {
            await InvokeAsync<EntryChangeResult>(people.Tenant, new SubmitEntryForReview(created.EntryId,
                Actor(people.Administrator, YojanaRole.TenantAdministrator)));
            await InvokeAsync<EntryChangeResult>(people.Tenant, new ApproveEntry(created.EntryId, null,
                Actor(people.Staff, YojanaRole.ServiceSupervisor)));
        }
        return new EntrySetup(created.EntryId, recordId);
    }

    private static EntryDraftInput Draft(Guid recordId, string answer, WrittenAgainstInput against) => new(
        "Storage controller service", "A storage server reports a failed controller.", answer,
        "Retain the media.\nBook next-business-day service.", [],
        [new EntryRecordLinkInput(RecordKind.ImportJob, recordId)], [against]);

    private static PolicyTerms StandardTerms() => new(
        new DurationRule(24, DurationRule.Months), DurationBasis.Time, [],
        new StartRule(StartTrigger.Deliver, [new StartFallback(StartTrigger.Invoice, 14)], 90),
        new CoverageTerms(100, 100, 0, "return_to_base"), ["misuse"], ["GB"],
        Transferability.Yes, "full", "STD-90",
        [new RefurbishedTerm(null, "A", "direct", "GB", 12, null)], 30,
        AssuranceOrService.Assurance, PolicyTerms.TimeZoneCustomerSite);

    private async Task SetTimeZoneAsync(TenantId tenant, string zone, Guid actor)
    {
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<TenantTimeZoneSettings>();
        var current = await settings.GetAsync(default);
        await settings.SetAsync(new UpdateTenantTimeZoneSettings(zone, [], current.Version),
            actor.ToString("D"), default);
    }

    private async Task WaitForAsync(TenantId tenant, Func<KnowledgeDbContext, Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var scope = fixture.ScopeFor(tenant);
            if (await condition(scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>())) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Knowledge currentness work was not handled within five seconds.");
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() });
    }

    private async Task InvokeAsync(TenantId tenant, object message)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message,
            new DeliveryOptions { TenantId = tenant.ToString() });
    }

    private static async Task AssertRuleAsync(string rule, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<QuestionRuleException>(action);
        Assert.Equal(rule, exception.Rule);
    }

    private static QuestionActor Actor(Guid userId, params string[] roles) => new(userId, roles);
    private sealed record EntrySetup(Guid EntryId, Guid RecordId);
}

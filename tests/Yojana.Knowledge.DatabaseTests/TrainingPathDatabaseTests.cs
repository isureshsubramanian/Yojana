using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Entries;
using Yojana.Knowledge.Questions;
using Yojana.Knowledge.Training;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Knowledge.DatabaseTests;

[Collection("knowledge host")]
public sealed class TrainingPathDatabaseTests(KnowledgeHostFixture fixture)
{
    [DatabaseFact]
    public async Task Only_reviewer_roles_maintain_paths_and_every_ordered_version_is_kept()
    {
        fixture.Clock.UtcNow = Utc(2026, 9, 1, 9);
        var people = await fixture.NewTenantAsync("trainvers");
        var first = await PublishedEntryAsync(people.Tenant, people.Administrator, people.Staff,
            "Reading server cover");
        var second = await PublishedEntryAsync(people.Tenant, people.Administrator, people.Staff,
            "Retaining storage media");
        await AssertRuleAsync("training_path_reviewer_role_required", () => InvokeAsync<TrainingPathChangeResult>(
            people.Tenant, new SaveTrainingPath(YojanaRole.Technician, "Read these in order.", [first], 0,
                Actor(people.Administrator, YojanaRole.TenantAdministrator))));
        var created = await InvokeAsync<TrainingPathChangeResult>(people.Tenant,
            new SaveTrainingPath(YojanaRole.Technician, "Read these in order.", [first, second], 0,
                Actor(people.Staff, YojanaRole.ServiceSupervisor)));
        var changed = await InvokeAsync<TrainingPathChangeResult>(people.Tenant,
            new SaveTrainingPath(YojanaRole.Technician, "Start with media retention.", [second, first], 1,
                Actor(people.Staff, YojanaRole.ServiceSupervisor)));
        Assert.Equal(2, changed.Version);
        using var scope = fixture.ScopeFor(people.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        var versions = await db.TrainingPathVersions.Where(x => x.PathId == created.PathId)
            .OrderBy(x => x.Version).Select(x => x.Version).ToArrayAsync();
        var versionOne = await db.TrainingPathEntries.Where(x => x.PathId == created.PathId
                && x.Version == 1).OrderBy(x => x.Position).Select(x => x.EntryId).ToArrayAsync();
        var versionTwo = await db.TrainingPathEntries.Where(x => x.PathId == created.PathId
                && x.Version == 2).OrderBy(x => x.Position).Select(x => x.EntryId).ToArrayAsync();
        Assert.Equal(new[] { 1, 2 }, versions);
        Assert.Equal(new[] { first, second }, versionOne);
        Assert.Equal(new[] { second, first }, versionTwo);
    }

    [DatabaseFact]
    public async Task Progress_is_per_person_and_new_member_welcome_uses_the_tenant_setting()
    {
        fixture.Clock.UtcNow = Utc(2026, 9, 1, 9);
        var people = await fixture.NewTenantAsync("trainread");
        await AddRoleAsync(people.Tenant, people.Administrator, YojanaRole.Technician, Utc(2026, 9, 1));
        var entryId = await PublishedEntryAsync(people.Tenant, people.Administrator, people.Staff,
            "Accidental damage on a storage server", needsReview: true);
        var path = await InvokeAsync<TrainingPathChangeResult>(people.Tenant,
            new SaveTrainingPath(YojanaRole.Technician, "Read the cover rules before handling a claim.", [entryId], 0,
                Actor(people.Staff, YojanaRole.ServiceSupervisor)));

        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var queries = scope.ServiceProvider.GetRequiredService<TrainingPathQueries>();
            var welcome = await queries.WelcomeAsync(Viewer(people.Other, YojanaRole.Technician), default);
            Assert.NotNull(welcome); Assert.Equal(path.PathId, welcome.PathId);
            var view = await queries.DetailAsync(YojanaRole.Technician,
                Viewer(people.Other, YojanaRole.Technician), default);
            Assert.NotNull(view); Assert.True(view.NewMember);
            var item = Assert.Single(view.Entries); Assert.True(item.NeedsReview);
            Assert.Equal("This entry may be out of date.", item.Warning);
        }
        await InvokeAsync<TrainingReadResult>(people.Tenant,
            new MarkTrainingEntryRead(YojanaRole.Technician, entryId,
                Actor(people.Other, YojanaRole.Technician)));
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var queries = scope.ServiceProvider.GetRequiredService<TrainingPathQueries>();
            Assert.True((await queries.DetailAsync(YojanaRole.Technician,
                Viewer(people.Other, YojanaRole.Technician), default))!.Finished);
            Assert.False((await queries.DetailAsync(YojanaRole.Technician,
                Viewer(people.Administrator, YojanaRole.Technician), default))!.Finished);
            await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
                .SetAsync(TrainingSettings.NewMemberDaysKey, "1", default);
        }
        fixture.Clock.UtcNow = Utc(2026, 9, 3, 9);
        using (var scope = fixture.ScopeFor(people.Tenant))
            Assert.Null(await scope.ServiceProvider.GetRequiredService<TrainingPathQueries>()
                .WelcomeAsync(Viewer(people.Administrator, YojanaRole.Technician), default));

        var other = await fixture.NewTenantAsync("trainother");
        using (var scope = fixture.ScopeFor(other.Tenant))
            Assert.Null(await scope.ServiceProvider.GetRequiredService<TrainingPathQueries>()
                .DetailAsync(YojanaRole.Technician, Viewer(other.Other, YojanaRole.Technician), default));
        using var portal = await fixture.ClientAsync(people.Tenant, people.Other, portal: true);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await portal.GetAsync($"/api/knowledge/training-paths/{YojanaRole.Technician}")).StatusCode);
    }

    [DatabaseFact]
    public async Task Team_view_returns_per_person_reading_but_only_one_team_question_total()
    {
        fixture.Clock.UtcNow = Utc(2026, 9, 10, 9);
        var people = await fixture.NewTenantAsync("trainteam");
        await AddRoleAsync(people.Tenant, people.Administrator, YojanaRole.Technician, Utc(2026, 9, 1));
        var entryId = await PublishedEntryAsync(people.Tenant, people.Administrator, people.Staff,
            "Next-business-day service");
        await InvokeAsync<TrainingPathChangeResult>(people.Tenant,
            new SaveTrainingPath(YojanaRole.Technician, "Read before taking service calls.", [entryId], 0,
                Actor(people.Staff, YojanaRole.ServiceSupervisor)));
        await InvokeAsync<TrainingReadResult>(people.Tenant, new MarkTrainingEntryRead(YojanaRole.Technician,
            entryId, Actor(people.Other, YojanaRole.Technician)));
        await AddQuestionAsync(people.Tenant, people.Other, people.Staff, Utc(2026, 9, 2));
        await AddQuestionAsync(people.Tenant, people.Administrator, people.Staff, Utc(2026, 9, 3));
        await AddQuestionAsync(people.Tenant, people.Other, people.Staff, Utc(2026, 10, 5));

        using var scope = fixture.ScopeFor(people.Tenant);
        var team = await scope.ServiceProvider.GetRequiredService<TrainingPathQueries>().TeamAsync(
            YojanaRole.Technician, null, 25, Viewer(people.Staff, YojanaRole.ServiceSupervisor), default);
        Assert.Equal(3, team.QuestionsAskedInFirstThirtyDays); // the entry's source question plus two in-window questions
        Assert.Equal(2, team.People.Count);
        Assert.Single(team.People, person => person.Name == "Fictional Technician" && person.Finished);
        Assert.Single(team.People, person => person.Name == "Fictional Administrator" && !person.Finished);
        var peopleJson = JsonSerializer.Serialize(team.People);
        Assert.DoesNotContain("question", peopleJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", peopleJson, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> PublishedEntryAsync(TenantId tenant, Guid author, Guid reviewer, string title,
        bool needsReview = false)
    {
        var questionId = Guid.CreateVersion7(); var entryId = Guid.CreateVersion7(); var now = fixture.Clock.UtcNow;
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        db.Questions.Add(Question(tenant, questionId, author, reviewer, now));
        db.Entries.Add(new KnowledgeEntry
        {
            TenantId = tenant, EntryId = entryId, Title = title, Problem = "A fictional server needs service.",
            Answer = "Check the reviewed cover and retain failed media.", Steps = "Check cover.",
            WrittenAgainstJson = "[]", Status = KnowledgeEntryStatus.Published, SourceQuestionId = questionId,
            CurrentVersion = 1, PublishedVersion = 1, NeedsReview = needsReview,
            NeedsReviewSince = needsReview ? now : null, AuthorId = author, CreatedAt = now, LastSavedBy = author,
            LastSavedAt = now, PublishedBy = reviewer, PublishedAt = now,
        });
        db.EntryVersions.Add(new KnowledgeEntryVersion
        {
            TenantId = tenant, EntryId = entryId, Version = 1, Title = title,
            Problem = "A fictional server needs service.", Answer = "Check the reviewed cover and retain failed media.",
            Steps = "Check cover.", WrittenAgainstJson = "[]", SavedBy = author, SavedAt = now,
        });
        db.EntryTags.Add(new KnowledgeEntryTag
        {
            TenantId = tenant, EntryId = entryId, Version = 1, Kind = KnowledgeTagKind.Task,
            Value = "check_cover", Label = "Check cover",
        });
        await db.SaveChangesAsync();
        return entryId;
    }

    private async Task AddQuestionAsync(TenantId tenant, Guid askedBy, Guid directedTo, DateTime askedAt)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        db.Questions.Add(Question(tenant, Guid.CreateVersion7(), askedBy, directedTo, askedAt));
        await db.SaveChangesAsync();
    }

    private static Question Question(TenantId tenant, Guid id, Guid askedBy, Guid directedTo, DateTime at) => new()
    {
        TenantId = tenant, QuestionId = id, RecordKind = RecordKind.Unit, RecordId = Guid.CreateVersion7(),
        Title = "Fictional server service", Body = "Which cover applies?", Status = QuestionStatus.Open,
        DirectedToUser = directedTo, AskedBy = askedBy, AskedAt = at, UpdatedAt = at,
    };

    private async Task AddRoleAsync(TenantId tenant, Guid userId, string role, DateTime joinedAt)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var membership = await db.Memberships.SingleAsync(x => x.TenantId == tenant && x.UserId == userId);
        membership.Roles = membership.Roles.Append(role).Distinct(StringComparer.Ordinal).ToList();
        membership.CreatedAt = joinedAt;
        await db.SaveChangesAsync();
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
    private static RecordReferenceViewer Viewer(Guid userId, params string[] roles) => new(userId, roles);
    private static DateTime Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);
}

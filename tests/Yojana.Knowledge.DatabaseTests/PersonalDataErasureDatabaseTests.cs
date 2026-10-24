using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wolverine;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Privacy;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Privacy;

namespace Yojana.Knowledge.DatabaseTests;

[Collection("knowledge host")]
public sealed class PersonalDataErasureDatabaseTests(KnowledgeHostFixture fixture)
{
    [DatabaseFact]
    public async Task Confirm_runs_all_holders_and_restore_replay_erases_returned_data()
    {
        var (tenant, administrator, _, _) = await fixture.NewTenantAsync("erreplay");
        var party = Guid.CreateVersion7();
        const string restoredName = "Fictional Restored Customer";
        using (var seed = fixture.ScopeFor(tenant))
        {
            var integration = seed.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            integration.ReferenceProjections.Add(new ReferenceProjection
            {
                TenantId = tenant, EntityType = "party", ModuleId = party, Code = "RESTORED-9601",
                DisplayName = restoredName, AttributesJson = "{}", UpdatedAt = fixture.Clock.UtcNow,
            });
            await integration.SaveChangesAsync();
        }
        using var client = await fixture.ClientAsync(tenant, administrator);
        var request = new
        {
            subjectKind = "Party", subjectId = party, requestReference = "DSR-2026-9601",
            reason = $"Verified request for {restoredName}", subjectIsNaturalPerson = true,
        };
        using (var preview = await client.PostAsJsonAsync("/api/privacy/erasures/preview", request))
        {
            preview.EnsureSuccessStatusCode();
            using var scope = fixture.ScopeFor(tenant);
            Assert.False(await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
                .ErasureRequests.AnyAsync(x => x.SubjectId == party));
        }
        using var confirm = await client.PostAsJsonAsync("/api/privacy/erasures/confirm", new
        {
            request.subjectKind, request.subjectId, request.requestReference,
            confirmation = request.requestReference, request.reason, request.subjectIsNaturalPerson,
            reviewDecisions = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
        var accepted = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        var erasureId = accepted.GetProperty("erasureId").GetGuid();
        await WaitForAsync(async () =>
        {
            using var scope = fixture.ScopeFor(tenant);
            return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().ErasureRequests
                .AnyAsync(x => x.ErasureId == erasureId && x.Status == ErasureRequestStatus.Completed);
        });
        using (var firstVerify = fixture.ScopeFor(tenant))
        {
            var firstReason = await firstVerify.ServiceProvider.GetRequiredService<PlatformDbContext>()
                .ErasureRequests.Where(x => x.ErasureId == erasureId).Select(x => x.Reason).SingleAsync();
            Assert.DoesNotContain(restoredName, firstReason, StringComparison.Ordinal);
        }

        using (var restored = fixture.ScopeFor(tenant))
        {
            var integration = restored.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var projection = await integration.ReferenceProjections.SingleAsync(x => x.ModuleId == party);
            projection.Code = "RESTORED-9601";
            projection.DisplayName = restoredName;
            await integration.SaveChangesAsync();
            var platform = restored.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var erasure = await platform.ErasureRequests.SingleAsync(x => x.ErasureId == erasureId);
            erasure.Reason = $"Verified request for {restoredName}";
            await platform.SaveChangesAsync();
            var knowledge = restored.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            knowledge.Questions.Add(new Question
            {
                TenantId = tenant, QuestionId = Guid.CreateVersion7(), RecordKind = "unit",
                RecordId = Guid.CreateVersion7(), Title = restoredName, Body = $"Call {restoredName}",
                Status = "open", DirectedToRole = YojanaRole.ServiceSupervisor,
                AskedBy = administrator, AskedAt = fixture.Clock.UtcNow, UpdatedAt = fixture.Clock.UtcNow,
            });
            await knowledge.SaveChangesAsync();
            await restored.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(
                new ReplayCompletedPersonalDataErasures(tenant), new DeliveryOptions { TenantId = tenant.ToString() });
        }

        await WaitForAsync(async () =>
        {
            using var scope = fixture.ScopeFor(tenant);
            var projection = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>()
                .ReferenceProjections.AsNoTracking().SingleAsync(x => x.ModuleId == party);
            var reason = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
                .ErasureRequests.Where(x => x.ErasureId == erasureId).Select(x => x.Reason).SingleAsync();
            var questionRestored = await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>()
                .Questions.AsNoTracking().AnyAsync(x => x.Title.Contains(restoredName) || x.Body.Contains(restoredName));
            return projection.DisplayName != restoredName
                && !reason.Contains(restoredName, StringComparison.Ordinal) && !questionRestored;
        });
        using var verify = fixture.ScopeFor(tenant);
        Assert.All(await verify.ServiceProvider.GetRequiredService<KnowledgeDbContext>()
            .Questions.AsNoTracking().Select(x => x.Title + x.Body).ToListAsync(),
            text => Assert.DoesNotContain(restoredName, text, StringComparison.Ordinal));
        var replayedReason = await verify.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .ErasureRequests.Where(x => x.ErasureId == erasureId).Select(x => x.Reason).SingleAsync();
        Assert.DoesNotContain(restoredName, replayedReason, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Non_administrator_cannot_use_the_erasure_api()
    {
        var (tenant, _, staff, _) = await fixture.NewTenantAsync("erauth");
        using var client = await fixture.ClientAsync(tenant, staff);
        using var response = await client.PostAsJsonAsync("/api/privacy/erasures/preview", new
        {
            subjectKind = "Party", subjectId = Guid.CreateVersion7(), requestReference = "DSR-2026-9701",
            reason = "verified request", subjectIsNaturalPerson = true,
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [DatabaseFact]
    public async Task User_erasure_covers_question_revisions_and_every_knowledge_entry_version()
    {
        var (tenant, _, author, _) = await fixture.NewTenantAsync("erknow");
        var questionId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var entryId = Guid.CreateVersion7();
        const string name = "Fictional Service Lead";
        using (var seed = fixture.ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            db.Questions.Add(new Question
            {
                TenantId = tenant, QuestionId = questionId, RecordKind = "unit",
                RecordId = Guid.CreateVersion7(), Title = $"Question from {name}", Body = $"Contact {name}",
                Status = "open", DirectedToRole = YojanaRole.ServiceSupervisor, AskedBy = author,
                AskedAt = fixture.Clock.UtcNow, UpdatedAt = fixture.Clock.UtcNow,
            });
            db.Revisions.Add(new QuestionReplyRevision
            {
                TenantId = tenant, RevisionId = revisionId, QuestionId = questionId, QuestionBody = true,
                Body = $"Earlier text by {name}", RevisedBy = author, RevisedAt = fixture.Clock.UtcNow,
            });
            db.Entries.Add(new KnowledgeEntry
            {
                TenantId = tenant, EntryId = entryId, Title = $"Guide by {name}",
                Problem = $"Ask {name}", Answer = $"Follow {name}'s steps", Steps = "Restart the storage server",
                WrittenAgainstJson = "[]", Status = "draft", SourceQuestionId = questionId, CurrentVersion = 1,
                AuthorId = author, CreatedAt = fixture.Clock.UtcNow, LastSavedBy = author,
                LastSavedAt = fixture.Clock.UtcNow,
            });
            db.EntryVersions.Add(new KnowledgeEntryVersion
            {
                TenantId = tenant, EntryId = entryId, Version = 1, Title = $"Guide by {name}",
                Problem = $"Ask {name}", Answer = $"Private note about {name}", Steps = "Restart the storage server",
                WrittenAgainstJson = "[]", SavedBy = author, SavedAt = fixture.Clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var subject = new ErasureSubject(ErasureSubjectKind.User, author, [name], []);
        using (var previewScope = fixture.ScopeFor(tenant))
        {
            var holder = previewScope.ServiceProvider.GetRequiredService<KnowledgePersonalDataHolder>();
            var preview = await holder.PreviewAsync(subject, default);
            var decisions = preview.ReviewItems.Select(item => new PersonalDataReviewDecision(item.ItemKey,
                item.Store == "knowledge_entry_version" && item.Field == "answer"
                    ? PersonalDataReviewAction.Redact : PersonalDataReviewAction.Keep)).ToList();
            await holder.EraseAsync(subject with { ReviewDecisions = decisions }, Guid.CreateVersion7(), default);
        }

        using var verify = fixture.ScopeFor(tenant);
        var dbVerify = verify.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        var question = await dbVerify.Questions.AsNoTracking().SingleAsync(x => x.QuestionId == questionId);
        Assert.Equal(Guid.Empty, question.AskedBy);
        Assert.DoesNotContain(name, question.Title, StringComparison.Ordinal);
        var revision = await dbVerify.Revisions.AsNoTracking().SingleAsync(x => x.RevisionId == revisionId);
        Assert.Equal(Guid.Empty, revision.RevisedBy);
        Assert.DoesNotContain(name, revision.Body, StringComparison.Ordinal);
        var version = await dbVerify.EntryVersions.AsNoTracking().SingleAsync(x => x.EntryId == entryId);
        Assert.Equal(Guid.Empty, version.SavedBy);
        Assert.Equal("[Personal data redacted]", version.Answer);
        Assert.DoesNotContain(name, version.Title, StringComparison.Ordinal);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.True(await condition(), "The background erasure did not reach the expected state.");
    }
}

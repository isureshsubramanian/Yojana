using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Files;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Knowledge.DatabaseTests;

[Collection("knowledge host")]
public sealed class RetentionAndAttachmentDatabaseTests(KnowledgeHostFixture fixture)
{
    [DatabaseFact]
    public async Task Retention_is_off_by_default_then_purges_only_old_closed_questions_and_records_the_run()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("retention");
        var oldClosed = Question(setup.Tenant, setup.RecordId, setup.Administrator,
            QuestionStatus.Open, new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var open = Question(setup.Tenant, setup.RecordId, setup.Administrator, QuestionStatus.Open,
            new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var recentClosed = Question(setup.Tenant, setup.RecordId, setup.Administrator,
            QuestionStatus.ClosedWithoutAnswer, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var entrySource = Question(setup.Tenant, setup.RecordId, setup.Administrator,
            QuestionStatus.Answered, new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var acceptedReply = Guid.CreateVersion7();
        entrySource.AcceptedReplyId = acceptedReply;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            db.Questions.AddRange(oldClosed, open, recentClosed, entrySource);
            db.Replies.AddRange(
                new QuestionReply
                {
                    TenantId = setup.Tenant, ReplyId = Guid.CreateVersion7(), QuestionId = oldClosed.QuestionId,
                    Body = "Old reply to purge.", Author = setup.Staff, At = oldClosed.AskedAt,
                },
                new QuestionReply
                {
                    TenantId = setup.Tenant, ReplyId = acceptedReply, QuestionId = entrySource.QuestionId,
                    Body = "Keep the media-retention add-on.", Author = setup.Staff, At = entrySource.AskedAt,
                });
            db.Revisions.Add(new QuestionReplyRevision
            {
                TenantId = setup.Tenant, RevisionId = Guid.CreateVersion7(), QuestionId = oldClosed.QuestionId,
                QuestionBody = true, Body = "Old question text to purge.", RevisedBy = setup.Administrator,
                RevisedAt = oldClosed.AskedAt,
            });
            db.Entries.Add(new KnowledgeEntry
            {
                TenantId = setup.Tenant, EntryId = Guid.CreateVersion7(), Title = "Media retention",
                Problem = "Storage server replacement", Answer = "Retain the media.", Steps = "Record custody.",
                WrittenAgainstJson = "[]", Status = KnowledgeEntryStatus.Draft,
                SourceQuestionId = entrySource.QuestionId, CurrentVersion = 1, AuthorId = setup.Administrator,
                CreatedAt = entrySource.AskedAt, LastSavedBy = setup.Administrator, LastSavedAt = entrySource.AskedAt,
            });
            await db.SaveChangesAsync();
            var attachment = scope.ServiceProvider.GetRequiredService<QuestionAttachmentService>();
            await attachment.UploadAsync(oldClosed.QuestionId, null, Upload("evidence.pdf", Pdf()),
                Caller(setup.Administrator, YojanaRole.TenantAdministrator), default);
            oldClosed.Status = QuestionStatus.ClosedWithoutAnswer;
            oldClosed.ClosedReason = "No longer needed";
            oldClosed.ClosedBy = setup.Administrator;
            oldClosed.ClosedAt = oldClosed.AskedAt;
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var result = await scope.ServiceProvider.GetRequiredService<QuestionRetentionService>()
                .RunBatchAsync(default);
            Assert.False(result.Enabled);
            Assert.Equal(4, await scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>().Questions.CountAsync());
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
            await settings.SetAsync(QuestionRetentionSettings.EnabledKey, "true", default);
            await settings.SetAsync(QuestionRetentionSettings.YearsKey, "5", default);
        }
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var result = await scope.ServiceProvider.GetRequiredService<QuestionRetentionService>()
                .RunBatchAsync(default);
            Assert.True(result.Enabled);
            Assert.Equal(1, result.Questions);
            Assert.Equal(1, result.Replies);
            Assert.Equal(1, result.Revisions);
            Assert.Equal(1, result.Attachments);
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            var remaining = await db.Questions.Select(x => x.QuestionId).ToListAsync();
            Assert.DoesNotContain(oldClosed.QuestionId, remaining);
            Assert.Contains(open.QuestionId, remaining);
            Assert.Contains(recentClosed.QuestionId, remaining);
            Assert.Contains(entrySource.QuestionId, remaining);
            var run = await db.PurgeRuns.SingleAsync(x => x.RunId == result.RunId);
            Assert.Equal(QuestionRetentionJob.JobName, run.TriggeredByLabel);
            Assert.Equal(1, run.QuestionsPurged);
            Assert.Equal(1, run.RepliesPurged);
            Assert.Equal(1, run.RevisionsPurged);
            Assert.Equal(1, run.AttachmentsPurged);
            Assert.Empty(await db.PendingFilePurges.ToListAsync());
        }
    }

    [DatabaseFact]
    public async Task Redaction_neutralises_names_and_marked_text_keeps_the_thread_readable_and_records_no_removed_text()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var setup = await SetupAsync("redaction");
        var question = Question(setup.Tenant, setup.RecordId, setup.Staff, QuestionStatus.Open, fixture.Clock.UtcNow);
        var replyId = Guid.CreateVersion7();
        var otherReplyId = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            db.Questions.Add(question);
            db.Replies.AddRange(
                new QuestionReply
                {
                    TenantId = setup.Tenant, ReplyId = replyId, QuestionId = question.QuestionId,
                    Body = "My private reply about the server.", Author = setup.Staff, At = fixture.Clock.UtcNow,
                },
                new QuestionReply
                {
                    TenantId = setup.Tenant, ReplyId = otherReplyId, QuestionId = question.QuestionId,
                    Body = "Keep this other person's reply.", Author = setup.Other,
                    At = fixture.Clock.UtcNow.AddMinutes(1),
                });
            db.Revisions.AddRange(
                new QuestionReplyRevision
                {
                    TenantId = setup.Tenant, RevisionId = Guid.CreateVersion7(), QuestionId = question.QuestionId,
                    QuestionBody = true, Body = "My earlier private question.", RevisedBy = setup.Staff,
                    RevisedAt = fixture.Clock.UtcNow,
                },
                new QuestionReplyRevision
                {
                    TenantId = setup.Tenant, RevisionId = Guid.CreateVersion7(), QuestionId = question.QuestionId,
                    ReplyId = replyId, QuestionBody = false, Body = "My earlier private reply.",
                    RevisedBy = setup.Staff, RevisedAt = fixture.Clock.UtcNow,
                });
            await db.SaveChangesAsync();
            var attachment = await scope.ServiceProvider.GetRequiredService<QuestionAttachmentService>()
                .UploadAsync(question.QuestionId, null, Upload("details.txt", Text("private attachment")),
                    Caller(setup.Staff, YojanaRole.ServiceSupervisor), default);
            var otherAttachment = await scope.ServiceProvider.GetRequiredService<QuestionAttachmentService>()
                .UploadAsync(question.QuestionId, otherReplyId, Upload("other.txt", Text("keep this attachment")),
                    Caller(setup.Other, YojanaRole.ServiceSupervisor), default);
            var service = scope.ServiceProvider.GetRequiredService<QuestionPrivacyService>();
            await Assert.ThrowsAsync<QuestionRuleException>(() => service.RedactAsync(
                new(setup.Staff, setup.Staff, [question.QuestionId], [replyId], [attachment.FileId], "Requested",
                    new QuestionActor(setup.Staff, [YojanaRole.ServiceSupervisor])), default));
            var outsideScope = await Assert.ThrowsAsync<QuestionRuleException>(() => service.RedactAsync(new(
                setup.Staff, setup.Staff, [], [replyId], [otherAttachment.FileId], "Requested",
                new QuestionActor(setup.Administrator, [YojanaRole.TenantAdministrator])), default));
            Assert.Equal("question_redaction_attachment_outside_scope", outsideScope.Rule);
            var result = await service.RedactAsync(new(setup.Staff, setup.Staff, [question.QuestionId], [replyId],
                [attachment.FileId], "Person requested removal", new QuestionActor(setup.Administrator,
                    [YojanaRole.TenantAdministrator])), default);
            Assert.Equal((1, 1, 2, 1), (result.QuestionsRedacted, result.RepliesRedacted,
                result.RevisionsRedacted, result.AttachmentsPurged));
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            var audit = await db.PersonalDataRedactions.SingleAsync();
            Assert.DoesNotContain("private", audit.ScopeJson + audit.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(setup.Staff, audit.RequestedByUserId);
            Assert.Equal(setup.Administrator, audit.PerformedByUserId);
            Assert.Equal(otherReplyId, (await db.Attachments.SingleAsync()).ReplyId);
            var thread = await scope.ServiceProvider.GetRequiredService<QuestionQueries>().ThreadAsync(
                question.QuestionId, new(setup.Administrator, [YojanaRole.TenantAdministrator]), default);
            Assert.NotNull(thread);
            Assert.Equal(QuestionPrivacyService.NeutralAuthor, thread.AskedByName);
            Assert.Equal(QuestionPrivacyService.RedactedText, thread.Body);
            var redactedReply = Assert.Single(thread.Replies, item => item.ReplyId == replyId);
            Assert.Equal(QuestionPrivacyService.NeutralAuthor, redactedReply.AuthorName);
            Assert.Equal(QuestionPrivacyService.RedactedText, redactedReply.Body);
            Assert.Empty(thread.Attachments);
        }
    }

    [DatabaseFact]
    public async Task Person_export_contains_only_that_persons_questions_and_replies_and_requires_an_administrator()
    {
        var setup = await SetupAsync("export");
        var question = Question(setup.Tenant, setup.RecordId, setup.Staff, QuestionStatus.Open, fixture.Clock.UtcNow);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            db.Questions.Add(question);
            db.Replies.AddRange(
                new QuestionReply { TenantId = setup.Tenant, ReplyId = Guid.CreateVersion7(),
                    QuestionId = question.QuestionId, Body = "My reply", Author = setup.Staff, At = fixture.Clock.UtcNow },
                new QuestionReply { TenantId = setup.Tenant, ReplyId = Guid.CreateVersion7(),
                    QuestionId = question.QuestionId, Body = "Someone else's reply", Author = setup.Other,
                    At = fixture.Clock.UtcNow.AddMinutes(1) });
            await db.SaveChangesAsync();
        }
        using var staff = await fixture.ClientAsync(setup.Tenant, setup.Staff);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await staff.GetAsync($"/api/questions/personal-data/export?personId={setup.Staff:D}")).StatusCode);
        using var admin = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        using var response = await admin.GetAsync($"/api/questions/personal-data/export?personId={setup.Staff:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("My private question", csv);
        Assert.Contains("My reply", csv);
        Assert.DoesNotContain("Someone else's reply", csv);
    }

    [DatabaseFact]
    public async Task File_store_enforces_tenant_record_and_portal_rights_type_bytes_size_and_immutability()
    {
        var setup = await SetupAsync("files");
        Guid firstId;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var store = scope.ServiceProvider.GetRequiredService<ITenantFileStore>();
            var owner = new TenantFileOwner(RecordKind.ImportJob, setup.RecordId);
            var first = await store.PutAsync(owner, Upload("warranty.pdf", Pdf()),
                Caller(setup.Administrator, YojanaRole.TenantAdministrator), default);
            firstId = first.FileId;
            Assert.Equal(64, first.Sha256.Length);
            var second = await store.PutAsync(owner, Upload("warranty.pdf", Pdf()),
                Caller(setup.Administrator, YojanaRole.TenantAdministrator), default);
            Assert.NotEqual(first.FileId, second.FileId);
            await AssertFileRule("file_record_not_visible", () => store.OpenReadAsync(first.FileId,
                new TenantFileCaller(setup.Other, [], false), default));
            await AssertFileRule("file_portal_forbidden", () => store.OpenReadAsync(first.FileId,
                Caller(setup.Administrator, YojanaRole.TenantAdministrator) with { IsPortalUser = true }, default));
            await AssertFileRule("file_type_not_allowed", () => store.PutAsync(owner,
                Upload("run.exe", new byte[] { 0x4d, 0x5a, 1 }), Caller(setup.Administrator,
                    YojanaRole.TenantAdministrator), default));
            await AssertFileRule("file_content_type_mismatch", () => store.PutAsync(owner,
                Upload("fake.pdf", Text("not a PDF")), Caller(setup.Administrator,
                    YojanaRole.TenantAdministrator), default));
            var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
            await settings.SetAsync(Yojana.Platform.Files.TenantFileSettings.AllowedExtensionsKey, ".txt", default);
            await AssertFileRule("file_type_not_allowed", () => store.PutAsync(owner, Upload("excluded.pdf", Pdf()),
                Caller(setup.Administrator, YojanaRole.TenantAdministrator), default));
            await settings.SetAsync(Yojana.Platform.Files.TenantFileSettings.AllowedExtensionsKey, ".pdf", default);
            await settings.SetAsync(Yojana.Platform.Files.TenantFileSettings.MaximumBytesKey, "4", default);
            await AssertFileRule("file_too_large", () => store.PutAsync(owner, Upload("large.pdf", Pdf()),
                Caller(setup.Administrator, YojanaRole.TenantAdministrator), default));
        }
        var other = await fixture.NewTenantAsync("files-other");
        using (var scope = fixture.ScopeFor(other.Tenant))
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ITenantFileStore>().OpenReadAsync(firstId,
                Caller(other.Administrator, YojanaRole.TenantAdministrator), default));
        }
    }

    [DatabaseFact]
    public async Task Question_and_reply_attachments_upload_list_and_download_end_to_end()
    {
        var setup = await SetupAsync("attach-http");
        var asked = await InvokeAsync<AskQuestionResult>(setup.Tenant, new AskQuestion(setup.RecordId,
            RecordKind.ImportJob, "Controller service", "Which next-business-day service applies?", setup.Staff,
            null, new QuestionActor(setup.Administrator, [YojanaRole.TenantAdministrator])));
        var replied = await InvokeAsync<ReplyToQuestionResult>(setup.Tenant, new ReplyToQuestion(asked.QuestionId,
            "Use the accidental-damage add-on.", new QuestionActor(setup.Staff, [YojanaRole.ServiceSupervisor])));
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var questionUpload = await UploadHttpAsync(client, $"/api/questions/{asked.QuestionId:D}/attachments",
            "evidence.pdf", Pdf());
        questionUpload.EnsureSuccessStatusCode();
        var questionFile = await questionUpload.Content.ReadFromJsonAsync<QuestionAttachmentResult>();
        var replyUpload = await UploadHttpAsync(client,
            $"/api/questions/{asked.QuestionId:D}/replies/{replied.ReplyId:D}/attachments", "steps.txt",
            Text("Retain failed media."));
        replyUpload.EnsureSuccessStatusCode();
        var thread = await client.GetFromJsonAsync<QuestionThreadView>($"/api/questions/{asked.QuestionId:D}");
        Assert.NotNull(thread);
        Assert.Equal("evidence.pdf", thread.Attachments.Single().FileName);
        Assert.Equal("steps.txt", thread.Replies.Single().Attachments.Single().FileName);
        using var download = await client.GetAsync($"/api/questions/{asked.QuestionId:D}/attachments/{questionFile!.FileId:D}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf(), await download.Content.ReadAsByteArrayAsync());
        using var portal = await fixture.ClientAsync(setup.Tenant, setup.Administrator, portal: true);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await portal.GetAsync($"/api/questions/{asked.QuestionId:D}/attachments/{questionFile.FileId:D}")).StatusCode);
    }

    private async Task<(TenantId Tenant, Guid Administrator, Guid Staff, Guid Other, Guid RecordId)> SetupAsync(
        string prefix)
    {
        var setup = await fixture.NewTenantAsync(prefix);
        var recordId = Guid.CreateVersion7();
        using var scope = fixture.ScopeFor(setup.Tenant);
        scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ImportJobs.Add(new ImportJob
        {
            TenantId = setup.Tenant, JobId = recordId, CommandId = Guid.CreateVersion7(), Source = "network inventory",
            FileName = "fictional-server-list.csv", OriginalHash = new string('a', 64), Format = "csv",
            Mode = "apply", Status = "applied", ActorRef = "fixture", RecordedTime = fixture.Clock.UtcNow,
        });
        await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        return (setup.Tenant, setup.Administrator, setup.Staff, setup.Other, recordId);
    }

    private static Question Question(TenantId tenant, Guid recordId, Guid author, string status, DateTime at)
    {
        var closed = status == QuestionStatus.ClosedWithoutAnswer;
        var answered = status == QuestionStatus.Answered;
        return new Question
        {
            TenantId = tenant, QuestionId = Guid.CreateVersion7(), RecordKind = RecordKind.ImportJob,
            RecordId = recordId, Title = "My private question", Body = "My private question text.", Status = status,
            DirectedToRole = YojanaRole.ServiceSupervisor, AskedBy = author, AskedAt = at, UpdatedAt = at,
            AcceptedAt = answered ? at : null, ClosedReason = closed ? "No longer needed" : null,
            ClosedBy = closed ? author : null, ClosedAt = closed ? at : null,
        };
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>().InvokeAsync<T>(command,
            new Wolverine.DeliveryOptions { TenantId = tenant.ToString() });
    }

    private static TenantFileCaller Caller(Guid userId, params string[] roles) => new(userId, roles, false);
    private static TenantFileUpload Upload(string name, byte[] content) =>
        new(name, "application/octet-stream", content.LongLength, new MemoryStream(content, writable: false));
    private static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.7\nfictional hardware evidence");
    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    private static async Task<HttpResponseMessage> UploadHttpAsync(HttpClient client, string path, string fileName,
        byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", fileName);
        return await client.PostAsync(path, form);
    }

    private static async Task AssertFileRule<T>(string rule, Func<Task<T>> action)
    {
        var exception = await Assert.ThrowsAsync<TenantFileRuleException>(action);
        Assert.Equal(rule, exception.Rule);
    }
}

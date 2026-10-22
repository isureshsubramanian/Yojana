using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Reports;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.TimeZones;
using Xunit.Abstractions;

namespace Yojana.Knowledge.DatabaseTests;

[Collection("knowledge host")]
public sealed class KnowledgeReportDatabaseTests(KnowledgeHostFixture fixture, ITestOutputHelper output)
{
    [DatabaseFact]
    public async Task Fixed_fixture_returns_exact_metrics_local_boundaries_ranked_filters_recurrence_and_training()
    {
        fixture.Clock.UtcNow = Utc(2026, 9, 1, 9);
        var people = await fixture.NewTenantAsync("reports");
        var recordIds = Enumerable.Range(0, 7).Select(_ => Guid.CreateVersion7()).ToList();
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            await SetZoneAsync(scope, "Asia/Kolkata");
            var serial = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            serial.ImportJobs.AddRange(recordIds.Select((id, index) => Import(people.Tenant, id, index)));
            await serial.SaveChangesAsync();
            var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            integration.InboundMessages.Add(Inbound(people.Tenant, recordIds[4]));
            await integration.SaveChangesAsync();

            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            var source = Question(people.Tenant, recordIds[0], people.Administrator, YojanaRole.ServiceSupervisor,
                Utc(2026, 7, 1), "Published storage guidance", QuestionStatus.Answered, Utc(2026, 7, 1, 2));
            var one = Question(people.Tenant, recordIds[1], people.Administrator, YojanaRole.ServiceSupervisor,
                Utc(2026, 8, 31), "Controller reset", QuestionStatus.Answered, Utc(2026, 8, 31, 2));
            var two = Question(people.Tenant, recordIds[2], people.Administrator, YojanaRole.ServiceSupervisor,
                Utc(2026, 8, 30), "Media retention", QuestionStatus.Answered, Utc(2026, 8, 30, 6));
            var open = Question(people.Tenant, recordIds[3], people.Other, YojanaRole.ServiceDesk,
                Utc(2026, 8, 28), "Array warning", QuestionStatus.Open);
            var boundary = Question(people.Tenant, recordIds[4], people.Other, YojanaRole.Quality,
                new DateTime(2026, 8, 2, 18, 30, 0, DateTimeKind.Utc), "Boundary question",
                QuestionStatus.Answered, new DateTime(2026, 8, 3, 4, 30, 0, DateTimeKind.Utc),
                RecordKind.IntegrationMessage);
            var tagged = Question(people.Tenant, recordIds[5], people.Other, YojanaRole.ServiceSupervisor,
                Utc(2026, 8, 10), "Accidental damage on server", QuestionStatus.Answered, Utc(2026, 8, 10, 14));
            var before = Question(people.Tenant, recordIds[6], people.Other, YojanaRole.Quality,
                new DateTime(2026, 8, 2, 18, 29, 59, 999, DateTimeKind.Utc), "Outside boundary", QuestionStatus.Open);
            db.Questions.AddRange(source, one, two, open, boundary, tagged, before);
            AddReply(db, source, Utc(2026, 7, 1, 1), people.Staff, "Published reply");
            AddReply(db, one, Utc(2026, 8, 31, 1), people.Staff, "See the knowledge entry.");
            AddReply(db, two, Utc(2026, 8, 30, 3), people.Staff, "Retain the media.");
            AddReply(db, boundary, new DateTime(2026, 8, 2, 23, 30, 0, DateTimeKind.Utc), people.Staff, "Boundary reply");
            AddReply(db, tagged, Utc(2026, 8, 10, 7), people.Staff, "Accidental damage reply");

            var sourceEntry = Entry(people.Tenant, source.QuestionId, people.Administrator, people.Staff,
                "Storage controller recovery", Utc(2026, 8, 1));
            var taggedEntry = Entry(people.Tenant, tagged.QuestionId, people.Administrator, people.Staff,
                "Accidental damage for rack servers", Utc(2026, 8, 11));
            db.Entries.AddRange(sourceEntry, taggedEntry);
            db.EntryVersions.AddRange(Version(sourceEntry), Version(taggedEntry));
            db.EntryTags.Add(new KnowledgeEntryTag { TenantId = people.Tenant, EntryId = taggedEntry.EntryId,
                Version = 1, Kind = KnowledgeTagKind.Task, Value = "accidental-damage", Label = "Accidental damage" });
            db.SimilarEntries.Add(new QuestionSimilarEntry { TenantId = people.Tenant, QuestionId = one.QuestionId,
                EntryId = sourceEntry.EntryId, Rank = 600, MatchedAt = one.AskedAt });
            db.Replies.Local.Single(x => x.QuestionId == one.QuestionId).Body = $"See /knowledge/{sourceEntry.EntryId:D}";

            var pathId = Guid.CreateVersion7();
            db.TrainingPaths.Add(new TrainingPath { TenantId = people.Tenant, PathId = pathId,
                Role = YojanaRole.Technician, Introduction = "Read the hardware service guidance.", CurrentVersion = 1,
                CreatedBy = people.Staff, CreatedAt = Utc(2026, 8, 31), LastChangedBy = people.Staff,
                LastChangedAt = Utc(2026, 8, 31) });
            db.TrainingPathVersions.Add(new TrainingPathVersion { TenantId = people.Tenant, PathId = pathId,
                Version = 1, Role = YojanaRole.Technician, Introduction = "Read the hardware service guidance.",
                ChangedBy = people.Staff, ChangedAt = Utc(2026, 8, 31) });
            db.TrainingPathEntries.Add(new TrainingPathEntry { TenantId = people.Tenant, PathId = pathId,
                Version = 1, Position = 1, EntryId = taggedEntry.EntryId });
            db.TrainingProgress.Add(new TrainingProgress { TenantId = people.Tenant, PathId = pathId,
                EntryId = taggedEntry.EntryId, UserId = people.Other, ReadEntryVersion = 1,
                ReadAt = Utc(2026, 9, 1, 8) });
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var report = await scope.ServiceProvider.GetRequiredService<KnowledgeReportQueries>().ReportAsync(
                new KnowledgeReportFilters(KnowledgeReportPeriod.ThirtyDays),
                Viewer(people.Staff, YojanaRole.ServiceSupervisor, YojanaRole.TenantAdministrator), default);
            Assert.Equal(new DateOnly(2026, 8, 3), report.Window.From);
            Assert.Equal(new DateTime(2026, 8, 2, 18, 30, 0, DateTimeKind.Utc), report.Window.FromUtc);
            Assert.Equal(5, report.Questions);
            Assert.Equal(new KnowledgeReportTiming(14400, 23040, 4), report.FirstReply);
            Assert.Equal(new KnowledgeReportTiming(28800, 46080, 4), report.AcceptedAnswer);
            Assert.Equal(1, report.Unanswered);
            Assert.Equal(1, report.PointedToAnEntry);
            Assert.Equal(3, Assert.Single(report.ByDirectedRole, x => x.Key == YojanaRole.ServiceSupervisor).Count);
            Assert.Equal(1, Assert.Single(report.ByTopic, x => x.Label == "Accidental damage").Count);
            Assert.Equal(4, Assert.Single(report.ByTopic, x => x.Key == KnowledgeReportQueries.NoEntryTopic).Count);
            Assert.Equal(1, Assert.Single(report.AskedAgainDespiteEntry).AskedAgain);
            Assert.Equal(1, Assert.Single(report.UnansweredByAge, x => x.Key == "over_3_days").Count);
            Assert.Equal(1, report.ReadByNewTeamMembers.NewMembers);
            Assert.Equal(1, report.ReadByNewTeamMembers.Reads);

            var filtered = await scope.ServiceProvider.GetRequiredService<KnowledgeReportQueries>().ReportAsync(
                new KnowledgeReportFilters(KnowledgeReportPeriod.ThirtyDays, YojanaRole.ServiceSupervisor),
                Viewer(people.Staff, YojanaRole.ServiceSupervisor, YojanaRole.TenantAdministrator), default);
            Assert.Equal(3, filtered.Questions);
            Assert.All(filtered.ByDirectedRole, row => Assert.Equal(YojanaRole.ServiceSupervisor, row.Key));

            var byKind = await scope.ServiceProvider.GetRequiredService<KnowledgeReportQueries>().ReportAsync(
                new KnowledgeReportFilters(KnowledgeReportPeriod.ThirtyDays, RecordKind: RecordKind.IntegrationMessage),
                Viewer(people.Staff, YojanaRole.ServiceSupervisor, YojanaRole.TenantAdministrator), default);
            Assert.Equal(1, byKind.Questions);
            var byTopic = await scope.ServiceProvider.GetRequiredService<KnowledgeReportQueries>().ReportAsync(
                new KnowledgeReportFilters(KnowledgeReportPeriod.ThirtyDays, Topic: "task|accidental-damage"),
                Viewer(people.Staff, YojanaRole.ServiceSupervisor, YojanaRole.TenantAdministrator), default);
            Assert.Equal(1, byTopic.Questions);
        }
    }

    [DatabaseFact]
    public async Task Permission_setting_other_tenant_portal_and_export_text_masking_are_enforced()
    {
        fixture.Clock.UtcNow = Utc(2026, 9, 1, 9);
        var people = await fixture.NewTenantAsync("repsec");
        var otherTenant = await fixture.NewTenantAsync("repother");
        var importId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(
                KnowledgeReportSettings.AllowedRolesKey, JsonSerializer.Serialize(new[] { YojanaRole.Technician }), default);
            var serial = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            serial.ImportJobs.Add(Import(people.Tenant, importId, 1)); await serial.SaveChangesAsync();
            var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            integration.InboundMessages.Add(Inbound(people.Tenant, messageId)); await integration.SaveChangesAsync();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            db.Questions.AddRange(
                Question(people.Tenant, importId, people.Other, YojanaRole.Technician, Utc(2026, 8, 31),
                    "Visible title", QuestionStatus.Open, recordKind: RecordKind.ImportJob, body: "Visible question text"),
                Question(people.Tenant, messageId, people.Other, YojanaRole.Technician, Utc(2026, 8, 31),
                    "Secret integration title", QuestionStatus.Open, recordKind: RecordKind.IntegrationMessage,
                    body: "Secret integration question text"));
            await db.SaveChangesAsync();
        }
        using (var scope = fixture.ScopeFor(otherTenant.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            Assert.Empty(await db.Questions.ToListAsync());
        }
        using var client = await fixture.ClientAsync(people.Tenant, people.Other);
        var report = await client.GetFromJsonAsync<KnowledgeReportView>("/api/knowledge/reports?period=30_days");
        Assert.NotNull(report); Assert.Equal(1, report.Questions);
        var csv = await client.GetStringAsync("/api/knowledge/reports/export?period=30_days");
        Assert.StartsWith("Asked at,Status,Record kind,Directed to,Title,Question text,First reply at,Accepted at,Seconds to first reply,Seconds to accepted answer,Tags\r\n", csv);
        Assert.Contains("Visible title", csv); Assert.Contains("Visible question text", csv);
        Assert.DoesNotContain("Secret integration title", csv); Assert.DoesNotContain("Secret integration question text", csv);
        Assert.Equal(3, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        using var denied = await fixture.ClientAsync(people.Tenant, people.Staff);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await denied.GetAsync("/api/knowledge/reports?period=30_days")).StatusCode);
        using var portal = await fixture.ClientAsync(people.Tenant, people.Other, portal: true);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await portal.GetAsync("/api/knowledge/reports?period=30_days")).StatusCode);
    }

    [DatabaseFact]
    public async Task Twenty_thousand_question_report_stays_set_based_and_records_timing()
    {
        fixture.Clock.UtcNow = Utc(2026, 9, 1, 9);
        var people = await fixture.NewTenantAsync("repvolume");
        var recordId = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var serial = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            serial.ImportJobs.Add(Import(people.Tenant, recordId, 20)); await serial.SaveChangesAsync();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            const string sql = """
                INSERT INTO [knowledge].[question]
                    ([tenant_id],[question_id],[record_kind],[record_id],[title],[body],[status],
                     [directed_to_role],[asked_by],[asked_at],[updated_at],[reopened_count])
                SELECT TOP (20000) @tenant, NEWID(), N'import_job', @record, CONCAT(N'Server question ', ROW_NUMBER() OVER (ORDER BY a.object_id, b.object_id)),
                    N'Which service path covers this hardware?', N'open', N'service_supervisor', @actor,
                    DATEADD(second, -ROW_NUMBER() OVER (ORDER BY a.object_id, b.object_id), @asked),
                    DATEADD(second, -ROW_NUMBER() OVER (ORDER BY a.object_id, b.object_id), @asked), 0
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                """;
            await db.Database.ExecuteSqlRawAsync(sql, new SqlParameter("@tenant", people.Tenant.Value),
                new SqlParameter("@record", recordId), new SqlParameter("@actor", people.Other),
                new SqlParameter("@asked", Utc(2026, 8, 31)));
        }
        using (var scope = fixture.ScopeFor(people.Tenant))
        {
            var timer = Stopwatch.StartNew();
            var report = await scope.ServiceProvider.GetRequiredService<KnowledgeReportQueries>().ReportAsync(
                new KnowledgeReportFilters(KnowledgeReportPeriod.ThirtyDays),
                Viewer(people.Staff, YojanaRole.ServiceSupervisor), default);
            timer.Stop();
            output.WriteLine("20,000-question knowledge report: {0:N0} ms", timer.Elapsed.TotalMilliseconds);
            Assert.Equal(20000, report.Questions);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"Report took {timer.Elapsed}.");
        }
    }

    private static async Task SetZoneAsync(IServiceScope scope, string zone)
    {
        var settings = scope.ServiceProvider.GetRequiredService<TenantTimeZoneSettings>();
        var current = await settings.GetAsync(default);
        await settings.SetAsync(new Yojana.Serialisation.Contracts.UpdateTenantTimeZoneSettings(
            zone, [], current.Version), "fixture", default);
    }

    private static Question Question(TenantId tenant, Guid recordId, Guid asker, string role, DateTime askedAt,
        string title, string status, DateTime? acceptedAt = null, string recordKind = RecordKind.ImportJob,
        string body = "Hardware service question") => new()
    {
        TenantId = tenant, QuestionId = Guid.CreateVersion7(), RecordKind = recordKind, RecordId = recordId,
        Title = title, Body = body, Status = status, DirectedToRole = role, AskedBy = asker,
        AskedAt = askedAt, UpdatedAt = acceptedAt ?? askedAt, AcceptedAt = acceptedAt,
        AcceptedReplyId = acceptedAt is null ? null : Guid.CreateVersion7(),
    };

    private static void AddReply(KnowledgeDbContext db, Question question, DateTime at, Guid author, string body)
    {
        var replyId = question.AcceptedReplyId ?? Guid.CreateVersion7();
        db.Replies.Add(new QuestionReply { TenantId = question.TenantId, ReplyId = replyId,
            QuestionId = question.QuestionId, Body = body, Author = author, At = at });
    }

    private static KnowledgeEntry Entry(TenantId tenant, Guid questionId, Guid author, Guid reviewer,
        string title, DateTime publishedAt) => new()
    {
        TenantId = tenant, EntryId = Guid.CreateVersion7(), Title = title, Problem = "Server service problem",
        Answer = "Use the documented hardware service path.", Steps = "Confirm the system identifier.",
        WrittenAgainstJson = "[]", Status = KnowledgeEntryStatus.Published, SourceQuestionId = questionId,
        CurrentVersion = 1, PublishedVersion = 1, AuthorId = author, CreatedAt = publishedAt,
        LastSavedBy = author, LastSavedAt = publishedAt, PublishedBy = reviewer, PublishedAt = publishedAt,
    };

    private static KnowledgeEntryVersion Version(KnowledgeEntry entry) => new()
    {
        TenantId = entry.TenantId, EntryId = entry.EntryId, Version = 1, Title = entry.Title,
        Problem = entry.Problem, Answer = entry.Answer, Steps = entry.Steps,
        WrittenAgainstJson = "[]", SavedBy = entry.AuthorId, SavedAt = entry.CreatedAt,
    };

    private static ImportJob Import(TenantId tenant, Guid id, int number) => new()
    {
        TenantId = tenant, JobId = id, CommandId = Guid.CreateVersion7(), Source = "service records",
        FileName = $"fictional-server-{number}.csv", OriginalHash = number.ToString("x").PadLeft(64, 'a')[..64],
        Format = "csv", Mode = "apply", Status = "applied", ActorRef = "fixture", RecordedTime = Utc(2026, 8, 1),
    };

    private static InboundMessage Inbound(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, InboundMessageId = id, MessageId = Guid.CreateVersion7(), ConnectionId = Guid.CreateVersion7(),
        ConnectorCode = "FX01", ErpEntity = "service_order", ErpExternalId = "SO-2001", ErpChangeMarker = "1",
        MarkerKind = "sequence", ContractVersion = "1", MessageType = "ServiceOrderReceived", RawPayload = "{}",
        CanonicalMessage = "{}", OccurredAt = Utc(2026, 8, 31), ObservedAt = Utc(2026, 8, 31),
        AcceptedAt = Utc(2026, 8, 31), IsLatest = true, TranslationStatus = InboundTranslationStatus.NeedsAttention,
    };

    private static RecordReferenceViewer Viewer(Guid user, params string[] roles) => new(user, roles);
    private static DateTime Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);
}

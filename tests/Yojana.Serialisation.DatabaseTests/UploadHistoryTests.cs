using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Imports;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class UploadHistoryTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Three_history_queries_filter_page_and_remain_tenant_scoped()
    {
        var tenant = await fixture.NewTenantAsync("ui06hist");
        var other = await fixture.NewTenantAsync("ui06other");
        var seeded = await SeedAsync(tenant, "MAIN");
        await SeedAsync(other, "OTHER");

        using var scope = fixture.ScopeFor(tenant);
        var queries = scope.ServiceProvider.GetRequiredService<ImportQueries>();

        var firstFiles = await queries.HistoryAsync(new(), null, 1, CancellationToken.None);
        Assert.Single(firstFiles.Items);
        Assert.NotNull(firstFiles.NextCursor);
        var secondFiles = await queries.HistoryAsync(new(), firstFiles.NextCursor, 1, CancellationToken.None);
        Assert.Single(secondFiles.Items);
        Assert.Empty(firstFiles.Items.Select(row => row.JobId).Intersect(secondFiles.Items.Select(row => row.JobId)));
        Assert.Single((await queries.HistoryAsync(new(Find: seeded.FileBHash), null, 50, CancellationToken.None)).Items);
        Assert.Single((await queries.HistoryAsync(new(Status: ImportJobStatus.Rejected, Uploader: "uploader-b"), null, 50, CancellationToken.None)).Items);
        Assert.Single((await queries.HistoryAsync(new(From: At.AddMinutes(2), To: At.AddMinutes(3)), null, 50, CancellationToken.None)).Items);
        Assert.All((await queries.HistoryAsync(new(Find: "MAIN"), null, 50, CancellationToken.None)).Items,
            row => Assert.DoesNotContain("OTHER", row.FileName, StringComparison.Ordinal));

        var firstFeeds = await queries.FeedMessagesAsync(new(), null, 1, CancellationToken.None);
        Assert.Single(firstFeeds.Items);
        Assert.NotNull(firstFeeds.NextCursor);
        var secondFeeds = await queries.FeedMessagesAsync(new(), firstFeeds.NextCursor, 1, CancellationToken.None);
        Assert.Single(secondFeeds.Items);
        Assert.Empty(firstFeeds.Items.Select(row => row.FeedMessageId).Intersect(secondFeeds.Items.Select(row => row.FeedMessageId)));
        var feedByReference = Assert.Single((await queries.FeedMessagesAsync(new(Find: "MSG-MAIN-B"), null, 50, CancellationToken.None)).Items);
        Assert.Equal("Former user", feedByReference.CallerName);
        Assert.Single((await queries.FeedMessagesAsync(new(Find: seeded.FeedHash, Status: ImportJobStatus.Applied), null, 50, CancellationToken.None)).Items);
        Assert.Single((await queries.FeedMessagesAsync(new(From: At.AddMinutes(5), To: At.AddMinutes(6)), null, 50, CancellationToken.None)).Items);
        Assert.DoesNotContain(typeof(FeedMessageHistoryRow).GetProperties(), property =>
            property.Name.Contains("payload", StringComparison.OrdinalIgnoreCase) && property.Name != nameof(FeedMessageHistoryRow.PayloadHash));

        var firstComparisons = await queries.ComparisonsAsync(new(), null, 1, CancellationToken.None);
        Assert.Single(firstComparisons.Items);
        Assert.NotNull(firstComparisons.NextCursor);
        var secondComparisons = await queries.ComparisonsAsync(new(), firstComparisons.NextCursor, 1, CancellationToken.None);
        Assert.Single(secondComparisons.Items);
        Assert.Empty(firstComparisons.Items.Select(row => row.JobId).Intersect(secondComparisons.Items.Select(row => row.JobId)));
        var comparisonByName = Assert.Single((await queries.ComparisonsAsync(new(Find: "MAIN-delta"), null, 50, CancellationToken.None)).Items);
        Assert.Equal("MAIN-delta.csv", comparisonByName.SecondFile.Name);
        Assert.Single((await queries.ComparisonsAsync(new(Find: seeded.FileBHash, Status: ImportJobStatus.Compared), null, 50, CancellationToken.None)).Items);
        Assert.Single((await queries.ComparisonsAsync(new(From: At.AddMinutes(7), To: At.AddMinutes(8)), null, 50, CancellationToken.None)).Items);

        var files = await queries.HistoryAsync(new(), null, 50, CancellationToken.None);
        var feeds = await queries.FeedMessagesAsync(new(), null, 50, CancellationToken.None);
        var comparisons = await queries.ComparisonsAsync(new(), null, 50, CancellationToken.None);
        var imports = await queries.PageAsync(null, 50, CancellationToken.None);
        Assert.Equal(imports.Items.Count, files.Items.Count + feeds.Items.Count + comparisons.Items.Count);
        Assert.Equal(8, imports.Items.Count);
    }

    [DatabaseFact]
    public async Task A_comparison_that_cannot_be_traced_shows_its_file_unavailable_without_failing_the_card()
    {
        var tenant = await fixture.NewTenantAsync("ui06trace");
        var first = FileJob(tenant, "register.csv", Hash("register"), "uploader-a", At.AddMinutes(1));
        var earlierCopy = FileJob(tenant, "vendor.csv", Hash("vendor"), "uploader-a", At.AddMinutes(2));
        // The same file uploaded once to apply and once to compare: identical name and contents, two jobs.
        var laterCopy = FileJob(tenant, "vendor.csv", Hash("vendor"), "uploader-b", At.AddMinutes(3));
        laterCopy.Mode = ImportMode.Compare;
        var gone = FileJob(tenant, "gone.csv", Hash("gone"), "uploader-a", At.AddMinutes(4));
        gone.OriginalFileReference = null;
        var sameFileTwice = ComparisonJob(tenant, first, laterCopy, At.AddMinutes(5));
        var missingSecond = ComparisonJob(tenant, first, gone, At.AddMinutes(6));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.ImportJobs.AddRange(first, earlierCopy, laterCopy, gone, sameFileTwice, missingSecond);
        await db.SaveChangesAsync();

        var page = await scope.ServiceProvider.GetRequiredService<ImportQueries>()
            .ComparisonsAsync(new(), null, 50, CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        var duplicate = Assert.Single(page.Items, row => row.JobId == sameFileTwice.JobId);
        Assert.Equal(laterCopy.JobId, duplicate.SecondFile.Id);
        var untraced = Assert.Single(page.Items, row => row.JobId == missingSecond.JobId);
        Assert.Null(untraced.SecondFile.Id);
        Assert.Null(untraced.SecondFile.Hash);
        Assert.Equal("gone.csv", untraced.SecondFile.Name);
        Assert.Equal(first.JobId, untraced.FirstFile.Id);
    }

    [DatabaseFact]
    public async Task History_endpoints_keep_legacy_filters_and_stream_each_card_export()
    {
        var tenant = await fixture.NewTenantAsync("ui06http");
        var seeded = await SeedAsync(tenant, "HTTP");
        using var client = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.Quality);

        var byFileName = await client.GetFromJsonAsync<ImportFileHistoryPage>("/api/imports/history?fileName=HTTP-alpha");
        Assert.Single(byFileName!.Items);
        var byHash = await client.GetFromJsonAsync<ImportFileHistoryPage>($"/api/imports/history?hash={seeded.FileBHash}");
        Assert.Single(byHash!.Items);
        var byFind = await client.GetFromJsonAsync<ImportFileHistoryPage>("/api/imports/history?find=HTTP-beta");
        Assert.Single(byFind!.Items);
        var feed = await client.GetFromJsonAsync<FeedMessageHistoryPage>("/api/imports/history/feed-messages?find=MSG-HTTP-A&take=1");
        Assert.Single(feed!.Items);
        var comparison = await client.GetFromJsonAsync<ComparisonHistoryPage>("/api/imports/history/comparisons?find=HTTP-alpha&take=1");
        Assert.Single(comparison!.Items);

        foreach (var path in new[] { "/api/imports/history/export?find=HTTP", "/api/imports/history/feed-messages/export?find=HTTP",
                     "/api/imports/history/comparisons/export?find=HTTP" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
            Assert.Contains("HTTP", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    private async Task<Seeded> SeedAsync(TenantId tenant, string prefix)
    {
        var fileA = FileJob(tenant, $"{prefix}-alpha.csv", Hash($"{prefix}-alpha"), "uploader-a", At.AddMinutes(1));
        var fileB = FileJob(tenant, $"{prefix}-beta.csv", Hash($"{prefix}-beta"), "uploader-b", At.AddMinutes(2), ImportJobStatus.Rejected);
        var fileC = FileJob(tenant, $"{prefix}-gamma.csv", Hash($"{prefix}-gamma"), "uploader-a", At.AddMinutes(3));
        var fileD = FileJob(tenant, $"{prefix}-delta.csv", Hash($"{prefix}-delta"), "uploader-b", At.AddMinutes(4));
        var feedA = FeedJob(tenant, $"MSG-{prefix}-A.json", Hash($"{prefix}-feed-a"), At.AddMinutes(5));
        var feedB = FeedJob(tenant, $"MSG-{prefix}-B.json", Hash($"{prefix}-feed-b"), At.AddMinutes(6));
        var comparisonA = ComparisonJob(tenant, fileA, fileB, At.AddMinutes(7));
        var comparisonB = ComparisonJob(tenant, fileC, fileD, At.AddMinutes(8));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.ImportJobs.AddRange(fileA, fileB, fileC, fileD, feedA, feedB, comparisonA, comparisonB);
        db.ThreePlFeedMessages.AddRange(
            FeedMessage(tenant, feedA, $"MSG-{prefix}-A", At.AddMinutes(5)),
            FeedMessage(tenant, feedB, $"MSG-{prefix}-B", At.AddMinutes(6)));
        await db.SaveChangesAsync();
        return new(fileB.OriginalHash, feedA.OriginalHash);
    }

    private static ImportJob FileJob(TenantId tenant, string name, string hash, string actor, DateTime at,
        string status = ImportJobStatus.Applied)
    {
        var id = Guid.NewGuid();
        return new ImportJob
        {
            TenantId = tenant, JobId = id, CommandId = id, Source = ImportSource.UserUpload, FileName = name,
            OriginalHash = hash, Format = ImportFormat.Csv, Mode = ImportMode.Apply, Status = status,
            AppliedCount = 2, SkippedCount = 1, RejectedCount = status == ImportJobStatus.Rejected ? 1 : 0,
            OriginalFileReference = $"file:v1:{id:N}", OriginalLength = 128, ActorRef = actor, RecordedTime = at,
        };
    }

    private static ImportJob FeedJob(TenantId tenant, string name, string hash, DateTime at)
    {
        var id = Guid.NewGuid();
        return new ImportJob
        {
            TenantId = tenant, JobId = id, CommandId = id, Source = ImportSource.ThreePlFeed, FileName = name,
            OriginalHash = hash, Format = "json", Mode = ImportMode.Apply, Status = ImportJobStatus.Applied,
            AppliedCount = 2, ActorRef = "departed-caller", RecordedTime = at,
        };
    }

    private static ImportJob ComparisonJob(TenantId tenant, ImportJob first, ImportJob second, DateTime at)
    {
        var id = Guid.NewGuid();
        return new ImportJob
        {
            TenantId = tenant, JobId = id, CommandId = id, ParentJobId = first.JobId, Source = ImportSource.UserUpload,
            FileName = $"{first.FileName} versus {second.FileName}", OriginalHash = CompareHash(first.OriginalHash, second.OriginalHash),
            Format = ImportFormat.Csv, Mode = ImportMode.Compare, Status = ImportJobStatus.Compared,
            AppliedCount = 1, SkippedCount = 2, RejectedCount = 1, ActorRef = "reviewer", RecordedTime = at,
        };
    }

    private static ThreePlFeedMessage FeedMessage(TenantId tenant, ImportJob job, string reference, DateTime at) => new()
    {
        TenantId = tenant, FeedMessageId = job.JobId, CommandId = job.CommandId, MessageReference = reference,
        DocumentType = "shipment", DocumentId = Guid.NewGuid(), DocumentLine = 1, ImportJobId = job.JobId,
        PayloadHash = job.OriginalHash, Status = job.Status, ShippedCount = 2, AssetTagCount = 1,
        RejectedCount = 0, ActorRef = job.ActorRef, RecordedTime = at,
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string CompareHash(string first, string second) => Hash(first + ":" + second);
    private sealed record Seeded(string FileBHash, string FeedHash);
}

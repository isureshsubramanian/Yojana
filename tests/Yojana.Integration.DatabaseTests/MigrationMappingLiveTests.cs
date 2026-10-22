using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.LinkOverrides;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Validation;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationMappingLiveTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-live-mapping-lead";
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [MigrationLiveFact]
    [Trait("Category", "MigrationLive")]
    public async Task Live_demo_runs_the_complete_read_only_pipeline_into_a_mapping_plan()
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_MIGRATION_LIVE_SQL")!;
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var tenant = await fixture.NewTenantAsync("maplive");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(3));
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var source = await sources.CreateAsync(new("Owner demo read-only mapping source", "live-mapping",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, database,
            ConnectionString: connectionString), Actor, default);
        await sources.SetLicenceAsync(source.SourceId,
            new(true, "OWNER-DEMO-READ-ONLY-2026-10-22",
                "Owner confirmed read-only extraction from the isolated demo company."), Actor, default);

        var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
        var batch = await batches.StartAsync(new(source.SourceId), Actor, default);
        Assert.Equal(1, await batches.RunPendingAsync(default));
        var profiles = scope.ServiceProvider.GetRequiredService<MigrationProfileService>();
        Assert.Equal(1, await profiles.RunPendingAsync(default));
        Assert.False(await profiles.IsLoadBlocked(batch.BatchId, default));
        var profile = await profiles.GetAsync(batch.BatchId, default);
        Assert.NotNull(profile);
        var profileCounts = profile.Classes.Where(row => row.Count.HasValue)
            .ToDictionary(row => row.Code, row => row.Count!.Value, StringComparer.Ordinal);
        var canonical = scope.ServiceProvider.GetRequiredService<CanonicalisationService>();
        Assert.Equal(1, await canonical.EnqueueEligibleAsync(default));
        Assert.Equal(1, await canonical.RunPendingAsync(default));
        var reconstruction = scope.ServiceProvider.GetRequiredService<ReconstructionService>();
        Assert.Equal(1, await reconstruction.EnqueueEligibleAsync(default));
        Assert.Equal(1, await reconstruction.RunPendingAsync(default));

        var reviewCounts = await ResolveWaitingAsync(canonical, batch.BatchId);
        Assert.Empty((await canonical.ListAsync(batch.BatchId, null, null, MigrationReviewStatus.Waiting,
            null, 100, default)).Items);

        var mapping = scope.ServiceProvider.GetRequiredService<MappingService>();
        Assert.Equal(1, await mapping.EnqueueEligibleAsync(default));
        Assert.Equal(1, await mapping.RunPendingAsync(default));
        var view = await mapping.GetAsync(batch.BatchId, default);
        Assert.NotNull(view);
        Assert.Equal(MappingRunStatus.Completed, view.Status);
        Assert.True(view.UnitsByOrigin.Sum(row => row.Count) > 0);
        var linkCsv = await scope.ServiceProvider.GetRequiredService<LinkOverrideService>()
            .ExportAsync(batch.BatchId, default);
        var linkRows = MigrationCsvReader.Read(System.Text.Encoding.UTF8.GetString(linkCsv)).Count - 1;
        Assert.Equal(view.ReplacementLinks, linkRows);
        Console.WriteLine("LINK_EXPORT rows={0}", linkRows);

        Console.WriteLine("MAP run={0} cutover={1} status={2} excluded={3} waiting_left_out={4} replacements={5} expired={6} took={7}",
            view.RunId, view.CutoverDate, view.Status, view.ExcludedCandidates,
            view.WaitingCandidatesLeftOut, view.ReplacementLinks, view.ExpiredEntitlements,
            view.FinishedAt - view.StartedAt);
        Print("UNITS_ORIGIN", view.UnitsByOrigin);
        Print("UNITS_LOCATION", view.UnitsByLocationState);
        Print("IDENTIFIERS_ISSUER", view.IdentifiersByIssuer);
        Print("EVENTS_PROVENANCE", view.EventsByProvenance);
        Print("OWNERSHIP", view.Ownership);
        Print("ENTITLEMENTS_FAMILY", view.EntitlementsByFamily);
        Print("ENTITLEMENTS_DATE_SOURCE", view.EntitlementsByDateSource);
        Print("GAPS", view.GapsByKind);
        var storedGaps = await db.MigrationPlanGaps.AsNoTracking().Where(row => row.MapRunId == view.RunId)
            .GroupBy(row => row.Kind).Select(group => new { Kind = group.Key, Count = group.LongCount() })
            .OrderBy(row => row.Kind).ToListAsync();
        foreach (var gap in storedGaps) Console.WriteLine("GAP {0}: {1}", gap.Kind, gap.Count);

        var validation = scope.ServiceProvider.GetRequiredService<ValidationService>();
        var queuedValidation = await validation.EnqueueAsync(batch.BatchId, true, default);
        var validationStopwatch = Stopwatch.StartNew();
        Assert.Equal(1, await validation.RunPendingAsync(default));
        validationStopwatch.Stop();
        var validationView = await validation.GetAsync(batch.BatchId, default);
        Assert.NotNull(validationView);
        Assert.Equal(queuedValidation.RunId, validationView.RunId);
        Assert.Equal(ValidationRunStatus.Completed, validationView.Status);
        Assert.Equal(ValidationResult.Pass, validationView.Checks.Single(x =>
            x.Code == "historical_events").Result);
        Assert.Equal(ValidationResult.Fail, validationView.Checks.Single(x =>
            x.Code == "active_cover").Result);
        Assert.Equal(ValidationResult.Explained, validationView.Checks.Single(x =>
            x.Code == "open_service_calls").Result);
        Assert.Equal(2, validationView.Checks.Count(x => x.Result == ValidationResult.Report));
        foreach (var check in validationView.Checks)
            Console.WriteLine("VALIDATION {0}: source={1} target={2} difference={3} result={4}",
                check.Label, check.SourceTotal, check.TargetTotal, check.Difference, check.Result);
        Console.WriteLine("VALIDATION took={0}", validationStopwatch.Elapsed);

        // P9-T10: a rehearsal load of the live plan into the module, then its rollback. The connector's item sync is
        // simulated from the plan's item codes (serial-tracked); nothing is written to the source.
        var connectionId = (await db.MigrationBatches.AsNoTracking().SingleAsync(row => row.BatchId == batch.BatchId)).ConnectionId;
        if (!await db.ConnectionGatewaySettings.AnyAsync(row => row.ConnectionId == connectionId))
        {
            if (!await db.Connections.AnyAsync(row => row.ConnectionId == connectionId))
                db.Connections.Add(new Data.Entities.Connection { TenantId = tenant, ConnectionId = connectionId, ConnectorCode = "CX01",
                    DisplayLabel = "Owner demo", Status = Data.Entities.ConnectionStatus.Active, CreatedAt = fixture.Clock.UtcNow });
            db.ConnectionGatewaySettings.Add(new Data.Entities.ConnectionGatewaySetting { TenantId = tenant, ConnectionId = connectionId,
                LegalEntityId = Guid.NewGuid(), OutboundMode = "reference_only", NativeWarrantyObjectMode = "frozen", OrderingWaitMinutes = 15 });
        }
        foreach (var code in await db.MigrationPlanUnits.Where(row => row.MapRunId == view.RunId).Select(row => row.ItemCode).Distinct().ToListAsync())
        {
            var moduleId = Guid.CreateVersion7();
            db.ExternalIdMaps.Add(new Data.Entities.ExternalIdMap
            {
                TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(), ConnectionId = connectionId, EntityType = "item",
                ExternalId = code, ModuleId = moduleId, ValidFrom = fixture.Clock.UtcNow, NaturalKeyHash = code,
                MappingSource = Data.Entities.ExternalIdMappingSource.Auto, MappedBy = "fixture", MappedAt = fixture.Clock.UtcNow,
            });
            db.ReferenceProjections.Add(new Data.Entities.ReferenceProjection
            {
                TenantId = tenant, EntityType = "item", ModuleId = moduleId, Code = code, DisplayName = code,
                TrackingMode = "serial", Uom = "EA", AttributesJson = "{}", UpdatedAt = fixture.Clock.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        var apply = scope.ServiceProvider.GetRequiredService<LegacyMigration.Apply.ApplyService>();
        await apply.EnqueueAsync(batch.BatchId, new(true), Actor, default);
        var started = DateTime.UtcNow;
        Assert.Equal(1, await apply.RunPendingAsync(default));
        var load = (await apply.GetAsync(batch.BatchId, default))!;
        Console.WriteLine("APPLY status={0} plan={1} applied={2} refused={3} events={4} entitlements={5} links={6} certificates={7} took={8}",
            load.Status, load.UnitsInPlan, load.Applied, load.Refused, load.EventsWritten,
            load.EntitlementsWritten, load.ReplacementLinksWritten, load.CertificatesRecorded,
            DateTime.UtcNow - started);
        Console.WriteLine("GAP_REPORT after load: sold migrated units with no base cover={0}", load.CoverGaps);
        Assert.NotNull(load.GapReportRunId);
        foreach (var refusal in (await apply.RefusalsAsync(load.LoadId, null, 200, default)).Items
                     .GroupBy(row => string.Join(' ', row.Reasons)))
            Console.WriteLine("APPLY_REFUSED {0}: {1}", refusal.Key, refusal.Count());
        Assert.Equal(LegacyMigration.Apply.ApplyRunStatus.Completed, load.Status);
        Assert.Equal(load.UnitsInPlan, load.Applied + load.Refused);
        var rollback = await apply.RollBackAsync(load.LoadId, Actor, default);
        Console.WriteLine("ROLLBACK outcome={0} units={1} events={2} entitlements={3} links={4} certificates={5}",
            rollback.Outcome, rollback.UnitsRemoved, rollback.EventsRemoved, rollback.EntitlementsRemoved,
            rollback.ReplacementLinksRemoved, rollback.CertificatesRemoved);
        Assert.Equal(Yojana.Serialisation.Contracts.RollBackMigrationLoadResult.RolledBack, rollback.Outcome);
        await WriteEvidenceAsync(profileCounts, reviewCounts,
            view.GapsByKind.ToDictionary(row => row.Kind, row => row.Count, StringComparer.Ordinal),
            view.ReplacementLinks, load.Applied);
    }

    private static async Task<IReadOnlyDictionary<string, long>> ResolveWaitingAsync(
        CanonicalisationService canonical, Guid batchId)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        while (true)
        {
            var waiting = await canonical.ListAsync(batchId, null, null, MigrationReviewStatus.Waiting,
                null, 200, default);
            if (waiting.Items.Count == 0) break;
            foreach (var group in waiting.Items.GroupBy(row => row.Kind, StringComparer.Ordinal))
                counts[group.Key] = counts.GetValueOrDefault(group.Key) + group.LongCount();
            foreach (var item in waiting.Items)
            {
                var decision = item.Kind == MigrationReviewKind.HistoryNeedsReview
                    ? MigrationReviewDecisionKind.AcceptAsRecorded
                    : MigrationReviewDecisionKind.KeepApart;
                await canonical.DecideAsync(item.ItemId, new(decision, null,
                    "The isolated demo evidence is retained as separate recorded units for rehearsal."),
                    Actor, default);
            }
        }
        foreach (var count in counts.OrderBy(row => row.Key, StringComparer.Ordinal))
            Console.WriteLine("REVIEW {0}: {1}", count.Key, count.Value);
        return counts;
    }

    private static async Task WriteEvidenceAsync(IReadOnlyDictionary<string, long> profile,
        IReadOnlyDictionary<string, long> review, IReadOnlyDictionary<string, long> gaps,
        long replacementLinks, long unitsApplied)
    {
        var path = Environment.GetEnvironmentVariable("YOJANA_DEMO_PIPELINE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(path)) return;
        // Coupled to ManifestWriter's PipelineEvidence file contract. A project reference would make the
        // migration database tests depend on a non-shipped tool; changing these names or types breaks reports.
        var evidence = new
        {
            ProfilingAnomalies = profile,
            ReviewItems = review,
            PlanGaps = gaps,
            ReplacementLinks = replacementLinks,
            UnitsApplied = unitsApplied,
        };
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath,
            JsonSerializer.Serialize(evidence, EvidenceJson) + Environment.NewLine);
        Console.WriteLine("PIPELINE_EVIDENCE {0}", fullPath);
    }

    private static void Print(string heading, IReadOnlyList<MappingCount> counts)
    {
        foreach (var count in counts) Console.WriteLine("{0} {1}: {2}", heading, count.Key, count.Count);
    }

    private static void Print(string heading, IReadOnlyList<MigrationGapCount> counts)
    {
        foreach (var count in counts)
            Console.WriteLine("{0} {1} ({2}): {3}", heading, count.Kind, count.Label, count.Count);
    }
}

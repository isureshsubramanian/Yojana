using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yojana.Integration;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Apply;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Platform;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Wolverine;

namespace Yojana.Bench;

public sealed record MigrationRehearsalResult(
    Guid TenantId,
    int SourceSerials,
    long Loaded,
    long Refused,
    long Excluded,
    long DeltaNew,
    long DeltaCover,
    long DeltaOwner,
    long DeltaGone,
    long RemainingMigratedUnits,
    string PlanFingerprint,
    string MarkdownReport,
    string JsonReport);

public sealed class MigrationRehearsalRunner(BenchOptions options)
{
    private const string Actor = "rehearsal:migration-lead";
    private const string Reviewer = "rehearsal:tenant-administrator";
    private readonly RehearsalClock clock = new(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc));
    private MigrationRehearsalReport report = null!;
    private IHost host = null!;
    private TenantId tenant;
    private Guid? finalLoadId;
    private Guid? deltaLoadId;
    private bool finalUndone;
    private bool deltaUndone;
    private ActualPlanCapture? planCapture;
    private string planFingerprint = string.Empty;

    public async Task<MigrationRehearsalResult> RunAsync(CancellationToken cancellationToken)
    {
        var target = new SqlConnectionStringBuilder(options.TargetConnectionString!);
        target.ApplicationName = "yojana-migration-rehearsal";
        var source = new SqlConnectionStringBuilder(options.SourceConnectionString!);
        var sourceSerials = checked((int)await SourceSerialCountAsync(cancellationToken));
        host = BuildHost(target);
        await host.StartAsync(cancellationToken);
        tenant = await ResolveTenantAsync(cancellationToken);
        await EnsureCoverageFamiliesAsync(cancellationToken);
        report = new(options.OutDirectory, target.InitialCatalog, source.InitialCatalog, tenant.Value, sourceSerials);
        if (options.CapturePlans)
        {
            planCapture = new(target.ConnectionString,
                "yojana_migration_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..8],
                options.OutDirectory);
            await planCapture.StartAsync(cancellationToken);
        }

        MigrationApplyView? finalView = null;
        MigrationApplyView? deltaView = null;
        long excluded = 0;
        try
        {
            using var scope = Scope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<IntegrationDbContext>();
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
            var sourceService = services.GetRequiredService<MigrationSourceService>();
            var sourceView = await sourceService.CreateAsync(new(
                "Fictional first-customer rehearsal source", "rehearsal-source",
                MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, source.InitialCatalog,
                ConnectionString: options.SourceConnectionString), Actor, cancellationToken);
            await sourceService.SetLicenceAsync(sourceView.SourceId,
                new(true, "FICTIONAL-REHEARSAL-2026-09",
                    "Synthetic source generated for an isolated migration rehearsal."), Actor, cancellationToken);
            await ConfigureConnectionAsync(db, sourceView.SourceId, cancellationToken);

            var first = await RunPipelineAsync(services, sourceView.SourceId, "", cancellationToken);
            var map = first.Map;
            planFingerprint = await MigrationPlanFingerprint.CalculateAsync(options.TargetConnectionString!,
                tenant.Value, map.RunId, cancellationToken);
            report.SetPlanFingerprint(planFingerprint);
            excluded = Math.Max(0, sourceSerials - map.UnitsByOrigin.Sum(row => row.Count));
            await ConfigureItemReferencesAsync(db, sourceView.SourceId, map.RunId, cancellationToken);
            await RunFinalValidationAsync(services, first.BatchId, cancellationToken);

            var apply = services.GetRequiredService<ApplyService>();
            await MeasureAsync("final_load", map.UnitsByOrigin.Sum(row => row.Count), async () =>
            {
                var queued = await apply.EnqueueAsync(first.BatchId, new(false), Reviewer, cancellationToken);
                finalLoadId = queued.LoadId;
                var completed = await apply.RunPendingAsync(cancellationToken);
                if (completed != 1) throw new InvalidOperationException("The final load worker did not complete one load.");
                finalView = await apply.GetAsync(first.BatchId, cancellationToken)
                    ?? throw new InvalidOperationException("The final load disappeared after completion.");
                EnsureCompleted(finalView);
                return (finalView.UnitsInPlan, finalView.Applied,
                    $"refused={finalView.Refused:N0}; events={finalView.EventsWritten:N0}; entitlements={finalView.EntitlementsWritten:N0}; certificates={finalView.CertificatesRecorded:N0}");
            }, cancellationToken);
            var generator = new MigrationSourceGenerator(options.SourceConnectionString!, sourceSerials, options.Seed);
            await MeasureAsync("delta_mutation", sourceSerials, async () =>
            {
                var mutation = await generator.MutateForDeltaAsync(cancellationToken);
                return (sourceSerials, (long)mutation.NewSerials + mutation.CoverChanges + mutation.OwnerChanges + mutation.RemovedSerials,
                    $"new={mutation.NewSerials:N0}; cover={mutation.CoverChanges:N0}; owner={mutation.OwnerChanges:N0}; gone={mutation.RemovedSerials:N0}");
            }, cancellationToken);
            clock.Advance(TimeSpan.FromDays(1));
            var second = await RunPipelineAsync(services, sourceView.SourceId, "delta_", cancellationToken);
            await MeasureAsync("delta_load", second.Map.UnitsByOrigin.Sum(row => row.Count), async () =>
            {
                var queued = await apply.EnqueueDeltaAsync(second.BatchId, Reviewer, cancellationToken);
                deltaLoadId = queued.LoadId;
                var completed = await apply.RunPendingAsync(cancellationToken);
                if (completed != 1) throw new InvalidOperationException("The delta worker did not complete one load.");
                deltaView = await apply.GetAsync(second.BatchId, cancellationToken)
                    ?? throw new InvalidOperationException("The delta load disappeared after completion.");
                EnsureCompleted(deltaView);
                if (deltaView.Applied == 0 || deltaView.CoverReplaced == 0 || deltaView.OwnerDiffers == 0 ||
                    deltaView.GoneFromSource == 0)
                    throw new InvalidOperationException("The delta did not exercise every new, cover, owner and gone finding kind.");
                return (deltaView.UnitsInPlan, deltaView.Applied + deltaView.CoverReplaced + deltaView.OwnerDiffers + deltaView.GoneFromSource,
                    $"new={deltaView.Applied:N0}; cover={deltaView.CoverReplaced:N0}; cover refused={deltaView.CoverRefused:N0}; owner={deltaView.OwnerDiffers:N0}; gone={deltaView.GoneFromSource:N0}");
            }, cancellationToken);

            await MeasureAsync("undo_delta", 1, async () =>
            {
                var undone = await apply.RollBackAsync(deltaLoadId!.Value, Reviewer, cancellationToken);
                deltaUndone = undone.Outcome == Yojana.Serialisation.Contracts.RollBackMigrationLoadResult.RolledBack;
                if (!deltaUndone) throw new InvalidOperationException($"Delta undo returned {undone.Outcome}.");
                return (1, undone.UnitsRemoved,
                    $"events={undone.EventsRemoved:N0}; entitlements={undone.EntitlementsRemoved:N0}; cover reversals={undone.CoverReplacementsReversed:N0}");
            }, cancellationToken);
            await MeasureAsync("undo_final", finalView!.Applied, async () =>
            {
                var undone = await apply.RollBackAsync(finalLoadId!.Value, Reviewer, cancellationToken);
                finalUndone = undone.Outcome == Yojana.Serialisation.Contracts.RollBackMigrationLoadResult.RolledBack;
                if (!finalUndone) throw new InvalidOperationException($"Final undo returned {undone.Outcome}.");
                return (finalView.Applied, undone.UnitsRemoved,
                    $"events={undone.EventsRemoved:N0}; entitlements={undone.EntitlementsRemoved:N0}; certificates={undone.CertificatesRemoved:N0}");
            }, cancellationToken);

            var remaining = await RemainingMigratedUnitsAsync(services, cancellationToken);
            if (sourceSerials != finalView.Applied + finalView.Refused + excluded)
                throw new InvalidOperationException(
                    $"Reconciliation failed: source {sourceSerials}, loaded {finalView.Applied}, refused {finalView.Refused}, excluded {excluded}.");
            if (remaining != 0)
                throw new InvalidOperationException($"Both undos completed but {remaining} migrated unit registrations remain.");

            if (planCapture is not null)
            {
                foreach (var plan in await planCapture.StopAndWriteAsync(tenant.Value, cancellationToken))
                    report.AddPlan(plan);
                planCapture = null;
            }
            report.Complete();
            var paths = await report.WriteAsync(cancellationToken);
            return new(tenant.Value, sourceSerials, finalView.Applied, finalView.Refused, excluded,
                deltaView!.Applied, deltaView.CoverReplaced, deltaView.OwnerDiffers, deltaView.GoneFromSource,
                remaining, planFingerprint, paths.Markdown, paths.Json);
        }
        catch (Exception exception)
        {
            report.Fail(exception);
            await CleanUpLoadsAsync(cancellationToken);
            var paths = await report.WriteAsync(CancellationToken.None);
            throw new MigrationRehearsalException(exception.Message, paths.Markdown, paths.Json, exception);
        }
        finally
        {
            if (planCapture is not null) await planCapture.DisposeAsync();
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    private async Task<(Guid BatchId, MigrationMapView Map)> RunPipelineAsync(IServiceProvider services,
        Guid sourceId, string prefix, CancellationToken cancellationToken)
    {
        var batches = services.GetRequiredService<MigrationBatchService>();
        Guid batchId = Guid.Empty;
        MigrationBatchView? batchView = null;
        await MeasureAsync(prefix + "extraction", await SourceSerialCountAsync(cancellationToken), async () =>
        {
            var queued = await batches.StartAsync(new(sourceId), Actor, cancellationToken);
            batchId = queued.BatchId;
            if (await batches.RunPendingAsync(cancellationToken) != 1)
            {
                var detail = await batches.DetailAsync(batchId, cancellationToken);
                try
                {
                    var sourceService = services.GetRequiredService<MigrationSourceService>();
                    var source = await sourceService.SourceAsync(sourceId, cancellationToken);
                    await services.GetRequiredService<SourceExtractor>().ExtractFullAsync(source, batchId,
                        clock.UtcNow, cancellationToken);
                }
                catch (Exception diagnostic)
                {
                    throw new InvalidOperationException(
                        $"Extraction ended as {detail?.Status} ({detail?.Failure}); diagnostic replay: {diagnostic.Message}",
                        diagnostic);
                }
                throw new InvalidOperationException($"Extraction ended as {detail?.Status} ({detail?.Failure}).");
            }
            batchView = await batches.DetailAsync(batchId, cancellationToken)
                ?? throw new InvalidOperationException("The extraction batch disappeared after completion.");
            if (batchView.Status != MigrationBatchStatus.Succeeded)
                throw new InvalidOperationException($"Extraction ended as {batchView.Status}: {batchView.Failure}");
            var tables = batchView.Tables!;
            return (tables.Sum(row => row.RowsRead), tables.Sum(row => row.Inserted + row.Updated),
                $"unchanged={tables.Sum(row => row.Unchanged):N0}; absent={tables.Sum(row => row.Absent):N0}");
        }, cancellationToken);

        var profiles = services.GetRequiredService<MigrationProfileService>();
        await MeasureAsync(prefix + "profiling", batchView!.Tables!.Sum(row => row.RowsRead), async () =>
        {
            if (await profiles.RunPendingAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The profiling worker did not complete one run.");
            var view = await profiles.GetAsync(batchId, cancellationToken)
                ?? throw new InvalidOperationException("The profile disappeared after completion.");
            if (view.Status != MigrationProfileRunStatus.Completed)
                throw new InvalidOperationException($"Profiling ended as {view.Status}.");
            return (batchView.Tables!.Sum(row => row.RowsRead), view.Classes.Sum(row => row.Count ?? 0),
                string.Join("; ", view.Classes.Where(row => row.Count > 0).Select(row => $"{row.Code}={row.Count:N0}")));
        }, cancellationToken);

        var canonical = services.GetRequiredService<CanonicalisationService>();
        await MeasureAsync(prefix + "canonicalisation", batchView.Tables!.Single(row => row.Role == "serial_master").RowsRead,
            async () =>
            {
                if (await canonical.EnqueueEligibleAsync(cancellationToken) != 1 ||
                    await canonical.RunPendingAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Canonicalisation did not enqueue and complete one run.");
                var decisions = await ResolveReviewQueueAsync(canonical, batchId, cancellationToken);
                var view = await canonical.GetAsync(batchId, cancellationToken)
                    ?? throw new InvalidOperationException("The canonicalisation run disappeared after completion.");
                return (view.CandidateRowsTotal, view.CandidateRowsTotal - view.CandidateRowsMerged,
                    $"merged={view.CandidateRowsMerged:N0}; rehearsal decisions={decisions:N0}");
            }, cancellationToken);

        var reconstruction = services.GetRequiredService<ReconstructionService>();
        await MeasureAsync(prefix + "reconstruction", batchView.Tables!.Single(row => row.Role == "transaction_log_lines").RowsRead,
            async () =>
            {
                if (await reconstruction.EnqueueEligibleAsync(cancellationToken) != 1 ||
                    await reconstruction.RunPendingAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Reconstruction did not enqueue and complete one run.");
                var view = await reconstruction.GetAsync(batchId, cancellationToken)
                    ?? throw new InvalidOperationException("The reconstruction run disappeared after completion.");
                return (view.CandidatesReconstructed, view.EventsByMovementKind.Sum(row => row.Count),
                    $"skipped={view.CandidatesSkippedByDisposition:N0}; reviews={view.QueuedForReviewByReason.Sum(row => row.Count):N0}");
            }, cancellationToken);

        var templates = services.GetRequiredService<TemplateMappingService>();
        await MeasureAsync(prefix + "template_decomposition", MigrationSourceProfile.ContractTemplates, async () =>
        {
            if (prefix.Length != 0)
            {
                var reused = await templates.SummaryAsync(cancellationToken);
                return (reused.Templates, reused.Approved.Templates,
                    $"reused approved mappings={reused.Approved.Templates:N0}; source template definitions unchanged");
            }
            var proposed = await templates.ProposeAsync(new(), cancellationToken);
            var approved = await ApproveTemplatePatternsAsync(templates, cancellationToken);
            var published = await PublishDraftPoliciesAsync(services, cancellationToken);
            return (proposed.Requested, approved,
                $"proposed={proposed.Proposed:N0}; unchanged={proposed.Unchanged:N0}; policies published={published:N0}");
        }, cancellationToken);

        var fallbackPolicies = await EnsureFallbackPoliciesAsync(services, cancellationToken);
        var mappingSettings = services.GetRequiredService<MappingSettings>();
        var currentSettings = await mappingSettings.GetAsync(cancellationToken);
        await mappingSettings.SetAsync(new(true, new DateOnly(2026, 9, 1), currentSettings.ConditionMap,
            currentSettings.LinkDefaults, fallbackPolicies.Card, fallbackPolicies.SerialMaster,
            currentSettings.Version), cancellationToken);
        var mapping = services.GetRequiredService<MappingService>();
        MigrationMapView? map = null;
        await MeasureAsync(prefix + "mapping", batchView.Tables!.Single(row => row.Role == "serial_master").RowsRead,
            async () =>
            {
                await mapping.EnqueueAsync(batchId, cancellationToken);
                if (await mapping.RunPendingAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Mapping did not complete one run.");
                map = await mapping.GetAsync(batchId, cancellationToken)
                    ?? throw new InvalidOperationException("The map run disappeared after completion.");
                if (map.Status != MappingRunStatus.Completed)
                    throw new InvalidOperationException($"Mapping ended as {map.Status}.");
                return (batchView.Tables!.Single(row => row.Role == "serial_master").RowsRead,
                    map.UnitsByOrigin.Sum(row => row.Count),
                    $"events={map.EventsByProvenance.Sum(row => row.Count):N0}; entitlements={map.EntitlementsByFamily.Sum(row => row.Count):N0}; gaps={map.GapsByKind.Sum(row => row.Count):N0}");
            }, cancellationToken);
        return (batchId, map!);
    }

    private async Task RunFinalValidationAsync(IServiceProvider services, Guid batchId,
        CancellationToken cancellationToken)
    {
        var validation = services.GetRequiredService<ValidationService>();
        await MeasureAsync("validation_and_signoff", 1, async () =>
        {
            var queued = await validation.EnqueueAsync(batchId, false, cancellationToken);
            if (await validation.RunPendingAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Validation did not complete one run.");
            while (true)
            {
                var samples = await validation.SampleAsync(queued.RunId, null, 200, cancellationToken);
                var pending = samples.Items.Where(row => row.Outcome is null).ToList();
                if (pending.Count == 0) break;
                foreach (var sample in pending)
                    await validation.TraceAsync(sample.SampleId, "traced",
                        "Synthetic source and mapped trail agree under the rehearsal rule.", Actor, cancellationToken);
            }
            var view = await validation.GetAsync(batchId, cancellationToken)
                ?? throw new InvalidOperationException("The validation run disappeared after completion.");
            foreach (var check in view.Checks.Where(row => row.Result == ValidationResult.Fail))
                await validation.ExplainAsync(check.CheckId,
                    "Accepted only for the synthetic rehearsal; the generated source intentionally includes this measured difference.",
                    Reviewer, cancellationToken);
            view = (await validation.GetAsync(batchId, cancellationToken))!;
            if (view.HardFailed != 0)
                throw new InvalidOperationException($"Validation has {view.HardFailed} hard failures.");
            foreach (var check in view.Checks)
            {
                await validation.SignAsync(check.CheckId, ValidationSignatureRole.Preparer, Actor,
                    [YojanaRole.MigrationLead], cancellationToken);
                await validation.SignAsync(check.CheckId, ValidationSignatureRole.Reviewer, Reviewer,
                    [YojanaRole.TenantAdministrator], cancellationToken);
            }
            await validation.CreateSignoffAsync(queued.RunId, Reviewer, cancellationToken);
            var signed = (await validation.GetAsync(batchId, cancellationToken))!;
            return (signed.ChecksTotal, signed.SignedChecks,
                $"passed={signed.Passed:N0}; explained={signed.Explained:N0}; sample={signed.SampleTraced:N0}/{signed.SampleTotal:N0}");
        }, cancellationToken);
    }

    private async Task<long> ResolveReviewQueueAsync(CanonicalisationService canonical, Guid batchId,
        CancellationToken cancellationToken)
    {
        long decisions = 0;
        while (true)
        {
            var page = await canonical.ListAsync(batchId, null, null, MigrationReviewStatus.Waiting,
                null, 200, cancellationToken);
            if (page.Items.Count == 0) return decisions;
            foreach (var item in page.Items)
            {
                var (decision, keep, note) = item.Kind switch
                {
                    MigrationReviewKind.SameSerialOneItem when item.SuggestedKeepSourceKey is not null =>
                        (MigrationReviewDecisionKind.MergeInto, item.SuggestedKeepSourceKey, (string?)null),
                    MigrationReviewKind.HistoryNeedsReview =>
                        (MigrationReviewDecisionKind.AcceptAsRecorded, (string?)null, (string?)null),
                    MigrationReviewKind.OrphanEquipmentCard =>
                        (MigrationReviewDecisionKind.FixInIncumbent, (string?)null,
                            "Synthetic orphan retained as excluded rehearsal evidence."),
                    _ => (MigrationReviewDecisionKind.KeepApart, (string?)null, (string?)null),
                };
                await canonical.DecideAsync(item.ItemId, new(decision, keep,
                    "Deterministic rehearsal rule; never applied by the production workflow.", note), Actor,
                    cancellationToken);
                decisions++;
            }
        }
    }

    private static async Task<int> ApproveTemplatePatternsAsync(TemplateMappingService templates,
        CancellationToken cancellationToken)
    {
        var approved = 0;
        string? cursor = null;
        do
        {
            var page = await templates.ListAsync(null, null, cursor, 100, cancellationToken);
            foreach (var pattern in page.Items)
            {
                if (pattern.State == TemplateMappingState.NeedsPerson)
                    throw new InvalidOperationException($"Template pattern {pattern.PatternKey} needs a person: " +
                        string.Join("; ", pattern.Templates.Select(item => item.ReviewReason)
                            .Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct(StringComparer.Ordinal)));
                if (pattern.State == TemplateMappingState.Waiting)
                    approved += (await templates.ApprovePatternAsync(pattern.PatternKey, Actor, cancellationToken)).Approved;
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        return approved;
    }

    private async Task<int> PublishDraftPoliciesAsync(IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<SerialisationDbContext>();
        var drafts = await db.PolicyVersions.AsNoTracking().Where(row => row.Status == PolicyVersionStatus.Draft &&
                row.PolicyCode.StartsWith("MIG-"))
            .Select(row => row.PolicyVersionId).ToListAsync(cancellationToken);
        var bus = services.GetRequiredService<IMessageBus>();
        var delivery = new DeliveryOptions { TenantId = tenant.ToString() };
        foreach (var draft in drafts)
        {
            _ = await bus.InvokeAsync<PolicyValidation>(new ValidatePolicyDraft(draft), delivery, cancellationToken);
            _ = await bus.InvokeAsync<PolicyDraftResult>(new PublishPolicyVersion(draft, Actor), delivery,
                cancellationToken);
        }
        return drafts.Count;
    }

    private async Task<(Guid Card, Guid SerialMaster)> EnsureFallbackPoliciesAsync(IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var catalogue = await services.GetRequiredService<IPolicyCatalogueQuery>().ReadAsync(cancellationToken);
        var card = catalogue.Policies.FirstOrDefault(row => row.FamilyCode == CoverageFamilyCode.Base)?.PolicyId;
        var serialMaster = catalogue.Policies
            .FirstOrDefault(row => row.FamilyCode == CoverageFamilyCode.Supplier)?.PolicyId;
        if (card is not null && serialMaster is not null) return (card.Value, serialMaster.Value);

        var terms = new PolicyTerms(new(36, DurationRule.Months), DurationBasis.Time, [],
            new(StartTrigger.Deliver, [new StartFallback(StartTrigger.Invoice, 14)], 90),
            new(100, 100, 0, "return_to_base"), [], ["WORLD"], Transferability.Yes, "full", null,
            [], null, AssuranceOrService.Assurance, PolicyTerms.TimeZoneUtc);
        var bus = services.GetRequiredService<IMessageBus>();
        var delivery = new DeliveryOptions { TenantId = tenant.ToString() };
        if (card is null)
            card = await PublishFallbackPolicyAsync(bus, delivery, "MIG-CARD-36", "Rehearsal card cover",
                CoverageFamilyCode.Base, terms, cancellationToken);
        if (serialMaster is null)
            serialMaster = await PublishFallbackPolicyAsync(bus, delivery, "MIG-SUPPLIER-36",
                "Rehearsal serial-master cover", CoverageFamilyCode.Supplier,
                terms with { Start = new(StartTrigger.Receipt, [], null) }, cancellationToken);
        return (card.Value, serialMaster.Value);
    }

    private static async Task<Guid> PublishFallbackPolicyAsync(IMessageBus bus, DeliveryOptions delivery,
        string code, string name, string family, PolicyTerms terms, CancellationToken cancellationToken)
    {
        var draft = await bus.InvokeAsync<PolicyDraftResult>(new CreatePolicyDraft(Guid.CreateVersion7(), code,
            name, family, EntitlementType.Manufacturer, new DateOnly(2023, 1, 1), terms, Actor), delivery,
            cancellationToken);
        _ = await bus.InvokeAsync<PolicyValidation>(new ValidatePolicyDraft(draft.PolicyVersionId), delivery,
            cancellationToken);
        return (await bus.InvokeAsync<PolicyDraftResult>(new PublishPolicyVersion(draft.PolicyVersionId, Actor),
            delivery, cancellationToken)).PolicyVersionId;
    }

    private async Task ConfigureConnectionAsync(IntegrationDbContext db, Guid connectionId,
        CancellationToken cancellationToken)
    {
        db.Connections.Add(new Connection
        {
            TenantId = tenant, ConnectionId = connectionId, ConnectorCode = "CX01",
            DisplayLabel = "Fictional rehearsal source", Status = ConnectionStatus.Paused,
            CreatedAt = clock.UtcNow, RegistrationCompleted = true,
        });
        db.ConnectionGatewaySettings.Add(new ConnectionGatewaySetting
        {
            TenantId = tenant, ConnectionId = connectionId, LegalEntityId = Guid.CreateVersion7(clock.UtcNow),
            OutboundMode = "reference_only", NativeWarrantyObjectMode = "frozen", OrderingWaitMinutes = 15,
        });
        db.ConnectionPollWatermarks.Add(new ConnectionPollWatermark
        {
            TenantId = tenant, ConnectionId = connectionId, EntityType = "deliveries", OverlapSeconds = 300,
            IntervalSeconds = 120, NextPollAt = clock.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ConfigureItemReferencesAsync(IntegrationDbContext db, Guid connectionId, Guid mapRunId,
        CancellationToken cancellationToken)
    {
        var itemCodes = await db.MigrationPlanUnits.AsNoTracking().Where(row => row.MapRunId == mapRunId)
            .Select(row => row.ItemCode).Distinct().ToListAsync(cancellationToken);
        foreach (var code in itemCodes)
        {
            var moduleId = Guid.CreateVersion7(clock.UtcNow);
            db.ExternalIdMaps.Add(new ExternalIdMap
            {
                TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(clock.UtcNow), ConnectionId = connectionId,
                EntityType = "item", ExternalId = code, ModuleId = moduleId, ValidFrom = clock.UtcNow,
                NaturalKeyHash = code, MappingSource = ExternalIdMappingSource.Migration,
                MappedBy = Actor, MappedAt = clock.UtcNow,
            });
            db.ReferenceProjections.Add(new ReferenceProjection
            {
                TenantId = tenant, EntityType = "item", ModuleId = moduleId, Code = code, DisplayName = code,
                TrackingMode = "serial", Uom = "EA", AttributesJson = "{}", UpdatedAt = clock.UtcNow,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MeasureAsync(string phase, long rowsIn,
        Func<Task<(long RowsIn, long RowsOut, string? Notes)>> action, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        (long RowsIn, long RowsOut, string? Notes) result;
        int roundTrips;
        IReadOnlyList<string> slowest;
        using (var counter = SqlCommandCounter.Start())
        {
            result = await action();
            roundTrips = counter.Commands;
            slowest = counter.Slowest(2);
        }
        stopwatch.Stop();
        var sizes = await DatabaseSizesAsync(cancellationToken);
        report.Add(new(phase, stopwatch.Elapsed.TotalSeconds, result.RowsIn == 0 ? rowsIn : result.RowsIn,
            result.RowsOut, roundTrips, sizes.Target, sizes.Source, result.Notes, slowest));
        Console.WriteLine($"  {phase,-28} {stopwatch.Elapsed.TotalSeconds,9:0.000} s · {result.RowsOut,10:N0} rows out · {roundTrips,7:N0} SQL · {sizes.Target,8:0.0} MB target");
    }

    private IHost BuildHost(SqlConnectionStringBuilder target)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Platform:ConnectionString"] = options.TargetConnectionString;
        builder.Configuration["Platform:MigrateOnStartup"] = "true";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "true";
        builder.Configuration["Platform:StartScheduler"] = "false";
        builder.Configuration["Platform:ServiceName"] = "yojana-migration-rehearsal";
        builder.Configuration["Platform:SignIn:AuditHmacKey"] = "migration-rehearsal-only-hmac-key";
        builder.Configuration["Serialisation:Documents:ConfirmDeliverySeparately"] = "false";
        builder.Configuration["Serialisation:ImportFiles:DatabaseConnectionString"] =
            new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = target.InitialCatalog + "_files" }.ConnectionString;
        builder.Configuration["Serialisation:ImportFiles:FileRoot"] = Path.Combine(Path.GetTempPath(), "yojana-migration-rehearsal-files");
        builder.Configuration["Serialisation:ImportFiles:ArchiveStores:0:Name"] = "rehearsal";
        builder.Configuration["Serialisation:ImportFiles:ArchiveStores:0:Kind"] = "folder";
        builder.Configuration["Serialisation:ImportFiles:ArchiveStores:0:Path"] = Path.Combine(Path.GetTempPath(), "yojana-migration-rehearsal-archive");
        builder.Configuration["Integration:Security:KeyEncryption:Provider"] = "Local";
        builder.Configuration["Integration:Security:LocalKeyEncryption:Current:KeyId"] = "rehearsal";
        builder.Configuration["Integration:Security:LocalKeyEncryption:Current:KeyVersion"] = "1";
        builder.Configuration["Integration:Security:LocalKeyEncryption:Current:KeyBase64"] =
            Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        builder.Configuration["Integration:Security:LocalKeyEncryption:AllowInProduction"] = "true";
        builder.Configuration["Integration:Security:LocalKeyEncryption:ProductionOverrideReason"] = "isolated migration rehearsal";
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(configuration => configuration.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
        PlatformSetup.IncludeHandlers(typeof(IntegrationModule).Assembly);
        builder.AddYojanaPlatform();
        builder.AddSerialisationModule();
        builder.AddIntegrationModule();
        builder.Services.Replace(ServiceDescriptor.Singleton<IClock>(clock));
        return builder.Build();
    }

    private async Task<TenantId> ResolveTenantAsync(CancellationToken cancellationToken)
    {
        if (options.TenantSelection == "new")
        {
            var id = new TenantId(Guid.CreateVersion7(clock.UtcNow));
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Tenants.Add(new Tenant
                {
                    Id = id, Code = "REHEARSAL-" + id.Value.ToString("N")[^8..],
                    Name = "Fictional Migration Rehearsal", CreatedAt = clock.UtcNow,
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            using (var scope = host.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(id);
                await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
                    .SetAsync("serial_confirmation_mode", "copy_allowed", cancellationToken);
            }
            return id;
        }

        var selected = new TenantId(Guid.Parse(options.TenantSelection!));
        using var verify = host.Services.CreateScope();
        var platform = verify.ServiceProvider.GetRequiredService<PlatformDbContext>();
        if (!await platform.Tenants.AnyAsync(row => row.Id == selected, cancellationToken))
            throw new InvalidOperationException("The selected tenant does not exist in the target database.");
        return selected;
    }

    private async Task EnsureCoverageFamiliesAsync(CancellationToken cancellationToken)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var existing = await db.CoverageFamilies.AsNoTracking().Select(row => row.FamilyCode)
            .ToHashSetAsync(cancellationToken);
        var families = new[]
        {
            (CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance),
            (CoverageFamilyCode.Supplier, "Supplier warranty", false, AssuranceOrService.Assurance),
            ("base", "Base hardware cover", false, AssuranceOrService.Assurance),
            ("service_level", "Service level", true, AssuranceOrService.Service),
            ("accidental_damage", "Accidental damage", true, AssuranceOrService.Service),
            ("media_retention", "Media retention", true, AssuranceOrService.Service),
            ("extended_term", "Extended term", true, AssuranceOrService.Service),
        };
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        foreach (var family in families.Where(item => !existing.Contains(item.Item1)))
            _ = await bus.InvokeAsync<CoverageFamilyRow>(new DefineCoverageFamily(family.Item1, family.Item2,
                    family.Item3, family.Item4, ActorRef: Actor),
                new DeliveryOptions { TenantId = tenant.ToString() }, cancellationToken);
    }

    private IServiceScope Scope()
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }

    private async Task<long> SourceSerialCountAsync(CancellationToken cancellationToken)
    {
        var table = SourceProtocol.Tables.Single(row => row.RowType == typeof(SerialMasterStagingRow));
        await using var connection = new SqlConnection(options.SourceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(1) FROM [dbo].[{SourceProtocolDatabase.Escape(table.Reads[0].SourceTable)}];";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<(decimal Target, decimal Source)> DatabaseSizesAsync(CancellationToken cancellationToken) =>
        (await DatabaseSizeAsync(options.TargetConnectionString!, cancellationToken),
            await DatabaseSizeAsync(options.SourceConnectionString!, cancellationToken));

    private static async Task<decimal> DatabaseSizeAsync(string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(decimal(18,1), SUM(size) * 8.0 / 1024.0) FROM sys.database_files;";
        return (decimal)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<long> RemainingMigratedUnitsAsync(IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var integration = services.GetRequiredService<IntegrationDbContext>();
        var serialisation = services.GetRequiredService<SerialisationDbContext>();
        return await integration.Set<MigrationLoadedUnit>().LongCountAsync(cancellationToken) +
            await serialisation.MigrationLoadUnits.LongCountAsync(cancellationToken);
    }

    private async Task CleanUpLoadsAsync(CancellationToken cancellationToken)
    {
        if (host is null || tenant.Value == Guid.Empty) return;
        try
        {
            using var scope = Scope();
            var apply = scope.ServiceProvider.GetRequiredService<ApplyService>();
            if (deltaLoadId is { } delta && !deltaUndone)
                deltaUndone = (await apply.RollBackAsync(delta, Reviewer, cancellationToken)).Outcome ==
                    Yojana.Serialisation.Contracts.RollBackMigrationLoadResult.RolledBack;
            if (finalLoadId is { } final && !finalUndone)
                finalUndone = (await apply.RollBackAsync(final, Reviewer, cancellationToken)).Outcome ==
                    Yojana.Serialisation.Contracts.RollBackMigrationLoadResult.RolledBack;
        }
        catch (Exception cleanup)
        {
            Console.Error.WriteLine($"cleanup failed: {cleanup.GetType().Name}: {cleanup.Message}");
        }
    }

    private static void EnsureCompleted(MigrationApplyView view)
    {
        if (view.Status != ApplyRunStatus.Completed)
            throw new InvalidOperationException($"Load {view.LoadId:D} ended as {view.Status}: {view.Failure}");
    }

    private sealed class RehearsalClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; private set; } = utcNow;
        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }
}

public sealed class MigrationRehearsalException(string message, string markdownReport, string jsonReport,
    Exception innerException) : Exception(message, innerException)
{
    public string MarkdownReport { get; } = markdownReport;
    public string JsonReport { get; } = jsonReport;
}

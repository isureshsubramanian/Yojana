using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P6-T08: one selection preview and command, immutable Copy output, and an exact idempotent receipt.</summary>
[Collection("serialisation host")]
public sealed class CertificateSelectionTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("c8000000-0000-0000-0000-000000000001");

    [DatabaseFact]
    public async Task Selection_preview_issues_version_one_reprints_Copy_lists_refusals_and_replays_exactly()
    {
        var tenant = await fixture.NewTenantAsync("cert-selection");
        var ready = await UnitAsync(tenant, "SEL-READY", released: true, suppressPublication: true);
        var copy = await UnitAsync(tenant, "SEL-COPY", released: true, suppressPublication: false);
        var unreleased = await UnitAsync(tenant, "SEL-HOLD", released: false, suppressPublication: false);
        var missing = await UnitAsync(tenant, "SEL-MISSING", released: true, suppressPublication: true, missingRequired: true);
        await WaitForCertificateAsync(tenant, copy.UnitId);
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(),
            SelectionSource.ByPastedList([ready.Serial, copy.Serial, unreleased.Serial, missing.Serial]), "quality.user"));

        CertificateSelectionPreview preview;
        using (var scope = fixture.ScopeFor(tenant))
        {
            preview = await scope.ServiceProvider.GetRequiredService<CertificateSelectionService>().PreviewAsync(
                new CertificateSelectionPreviewRequest(selection.SelectionId, CertificateDocumentKind.ReleaseRecord,
                    CertificateFormType.CertificateOfConformity, Entity, "en", true, 0, 50), CancellationToken.None);
        }
        Assert.Equal((4, 1, 1, 2, 2), (preview.Kpis.InSelection, preview.Kpis.NewVersion1,
            preview.Kpis.ReprintAsCopy, preview.Kpis.NotIssuable, preview.Kpis.OutputDocuments));
        Assert.Equal(CertificateSelectionOutcome.IssueVersion1, preview.Units.Single(row => row.UnitId == ready.UnitId).Outcome);
        Assert.Equal(CertificateSelectionOutcome.ReprintCopy, preview.Units.Single(row => row.UnitId == copy.UnitId).Outcome);
        Assert.Equal("not_released", preview.Units.Single(row => row.UnitId == unreleased.UnitId).Reason);
        Assert.Equal("missing_required_component", preview.Units.Single(row => row.UnitId == missing.UnitId).Reason);

        var noTemplate = await PreviewAsync(tenant, selection.SelectionId, CertificateFormType.Custom);
        Assert.Equal("active_template_missing", noTemplate.Units.Single(row => row.UnitId == ready.UnitId).Reason);

        var commandId = Guid.NewGuid();
        var command = new IssueCertificate(commandId, selection.SelectionId, CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.CertificateOfConformity, Entity, "en", "Fictional delivery pack", true, "quality.user");
        var run = await InvokeAsync<CertificateRunResult>(tenant, command);
        Assert.False(run.Replayed);
        Assert.Equal(2, run.Documents.Count);
        Assert.Single(run.Documents, row => !row.Copy && row.UnitId == ready.UnitId);
        Assert.Single(run.Documents, row => row.Copy && row.UnitId == copy.UnitId);
        Assert.Equal(2, run.Skipped.Count);
        using (var archive = new ZipArchive(new MemoryStream(run.Archive), ZipArchiveMode.Read))
        {
            Assert.Contains(archive.Entries, entry => entry.Name == "bundle.html");
            Assert.Contains(archive.Entries, entry => entry.Name == "cover.html");
            Assert.Equal(2, archive.Entries.Count(entry => entry.Name.EndsWith(".html", StringComparison.Ordinal)
                && entry.Name is not "bundle.html" and not "cover.html"));
            var copyEntry = Assert.Single(archive.Entries, entry => entry.Name.Contains("-copy.html", StringComparison.Ordinal));
            using var reader = new StreamReader(copyEntry.Open(), Encoding.UTF8);
            Assert.Contains("COPY", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }

        var replay = await InvokeAsync<CertificateRunResult>(tenant, command);
        Assert.True(replay.Replayed);
        Assert.Equal(run.Documents.Select(row => row.CertificateId), replay.Documents.Select(row => row.CertificateId));
        Assert.Equal(run.Skipped.Select(row => row.UnitId), replay.Skipped.Select(row => row.UnitId));
        Assert.Equal(run.Archive, replay.Archive);
        var conflict = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CertificateRunResult>(tenant,
            command with { ReprintReason = "A different reason" }));
        Assert.Equal("command_id_conflict", conflict.Rule);

        using var finalScope = fixture.ScopeFor(tenant);
        var db = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var copiedCertificateId = run.Documents.Single(item => item.Copy).CertificateId;
        Assert.Equal(2, await db.CertificateInstances.CountAsync(row => row.UnitId == ready.UnitId || row.UnitId == copy.UnitId));
        Assert.Single(await db.ReprintLogs.Where(row => row.CertificateId == copiedCertificateId).ToListAsync());
        var copiedCertificate = await db.CertificateInstances.AsNoTracking().SingleAsync(row =>
            row.CertificateId == copiedCertificateId);
        Assert.Contains("\"profile\":\"default\"", copiedCertificate.BrandingSnapshot, StringComparison.Ordinal);
        Assert.Single(await db.CertificateRuns.Where(row => row.CommandId == commandId).ToListAsync());
        await AssertWholeUnitAsync(db, ready.UnitId, certificateEvents: 1, reprintEvents: 0);
        await AssertWholeUnitAsync(db, copy.UnitId, certificateEvents: 1, reprintEvents: 1);
        Assert.Empty(await db.CertificateInstances.Where(row => row.UnitId == unreleased.UnitId || row.UnitId == missing.UnitId).ToListAsync());
        var publication = Assert.Single(await PublishedEventSink.WaitForAsync<CertificateReprinted>(row =>
            row.Header.UnitId == copy.UnitId && row.Reason == "Fictional delivery pack", 1, TimeSpan.FromSeconds(20)));
        Assert.True(publication.Copy);
        Assert.Equal((CertificateReprinted.Schema, "1.0"), (publication.Header.Schema, publication.Header.Version));
    }

    [DatabaseFact]
    public async Task Selection_preview_endpoint_returns_kpis_over_http()
    {
        var tenant = await fixture.NewTenantAsync("cert-http");
        var ready = await UnitAsync(tenant, "HTTP-READY", released: true, suppressPublication: true);
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(),
            SelectionSource.ByPastedList([ready.Serial]), "quality.user"));
        using var client = await fixture.AuthenticatedClientAsync(tenant);

        using var response = await client.PostAsJsonAsync("/api/certificates/selection/preview", new
        {
            selectionId = selection.SelectionId,
            documentKind = CertificateDocumentKind.ReleaseRecord,
            formType = CertificateFormType.CertificateOfConformity,
            issuerEntityId = Entity,
            language = "en",
            includeBundle = false,
            skip = 0,
            take = 50,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<CertificateSelectionPreview>();
        Assert.NotNull(preview);
        Assert.Equal(1, preview.Kpis.InSelection);
        Assert.Equal(1, preview.Kpis.NewVersion1);
    }

    [DatabaseFact]
    public async Task Selection_larger_than_one_chunk_issues_every_unit_and_preserves_whole_unit_state()
    {
        var tenant = await fixture.NewTenantAsync("cert-chunks");
        var selection = await ReleasedSelectionAsync(tenant, 101, "CHUNK");
        var command = Command(Guid.NewGuid(), selection.SelectionId);

        var result = await InvokeAsync<CertificateRunResult>(tenant, command);

        Assert.False(result.Replayed);
        Assert.Equal(101, result.Documents.Count);
        Assert.Empty(result.Skipped);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(101, await db.CertificateRunUnits.CountAsync(row => row.CommandId == command.CommandId
            && row.Outcome == CertificateRunUnitOutcome.Issued));
        Assert.NotNull((await db.CertificateRuns.SingleAsync(row => row.CommandId == command.CommandId)).CompletedAt);
        await AssertWholeSelectionAsync(db, selection.UnitIds, expectedCertificateEvents: 101);
    }

    [DatabaseFact]
    public async Task Failure_before_second_chunk_keeps_first_committed_and_same_command_resumes_without_duplicates()
    {
        var tenant = await fixture.NewTenantAsync("cert-resume");
        var selection = await ReleasedSelectionAsync(tenant, 101, "RESUME");
        var command = Command(Guid.NewGuid(), selection.SelectionId);
        var observer = fixture.Services.GetRequiredService<TestCertificateRunChunkObserver>();
        observer.Reset();
        observer.FailCommandId = command.CommandId;
        observer.FailBeforeChunk = 2;
        try
        {
            await Assert.ThrowsAsync<SimulatedCertificateChunkFailureException>(() =>
                InvokeAsync<CertificateRunResult>(tenant, command));

            using (var failedScope = fixture.ScopeFor(tenant))
            {
                var failedDb = failedScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
                Assert.Equal(100, await failedDb.CertificateRunUnits.CountAsync(row => row.CommandId == command.CommandId));
                Assert.Equal(100, await failedDb.CertificateInstances.CountAsync(row => selection.UnitIds.Contains(row.UnitId)));
                Assert.Null((await failedDb.CertificateRuns.SingleAsync(row => row.CommandId == command.CommandId)).CompletedAt);
            }

            var resumed = await InvokeAsync<CertificateRunResult>(tenant, command);
            Assert.True(resumed.Replayed);
            Assert.Equal(101, resumed.Documents.Count);

            using var finalScope = fixture.ScopeFor(tenant);
            var db = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(101, await db.CertificateRunUnits.CountAsync(row => row.CommandId == command.CommandId));
            Assert.Equal(101, await db.CertificateInstances.CountAsync(row => selection.UnitIds.Contains(row.UnitId)));
            Assert.Equal(101, await db.CertificateTriggers.CountAsync(row => selection.UnitIds.Contains(row.UnitId)
                && row.Outcome == "issued"));
            await AssertWholeSelectionAsync(db, selection.UnitIds, expectedCertificateEvents: 101);
        }
        finally
        {
            observer.Reset();
        }
    }

    [DatabaseFact]
    public async Task Synchronous_run_refuses_a_selection_above_the_configured_limit_without_writes()
    {
        var tenant = await fixture.NewTenantAsync("cert-limit");
        var selection = await CommissionedSelectionAsync(tenant, 121, "LIMIT", release: false);
        var command = Command(Guid.NewGuid(), selection.SelectionId);

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<CertificateRunResult>(tenant, command));

        Assert.Equal("certificate_selection_too_large", refusal.Rule);
        Assert.Contains("120", refusal.Message, StringComparison.Ordinal);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.False(await db.CertificateRuns.AnyAsync(row => row.CommandId == command.CommandId));
        Assert.Empty(await db.CertificateInstances.Where(row => selection.UnitIds.Contains(row.UnitId)).ToListAsync());
    }

    private async Task<CertificateSelectionPreview> PreviewAsync(TenantId tenant, Guid selectionId, string form)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<CertificateSelectionService>().PreviewAsync(
            new CertificateSelectionPreviewRequest(selectionId, CertificateDocumentKind.ReleaseRecord, form,
                Entity, "en", true, 0, 50), CancellationToken.None);
    }

    private static IssueCertificate Command(Guid commandId, Guid selectionId) => new(commandId, selectionId,
        CertificateDocumentKind.ReleaseRecord, CertificateFormType.CertificateOfConformity, Entity, "en",
        "Fictional delivery pack", IncludeBundle: false, ActorRef: "quality.user");

    private Task<SelectionSetup> ReleasedSelectionAsync(TenantId tenant, int count, string prefix) =>
        CommissionedSelectionAsync(tenant, count, prefix, release: true);

    private async Task<SelectionSetup> CommissionedSelectionAsync(TenantId tenant, int count, string prefix, bool release)
    {
        var itemId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(itemId) with
        {
            ItemClass = "certificate-selection",
        });
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new ItemLabel(itemId,
            $"FIC-{prefix}", "Fictional modular controller"));
        var serials = Enumerable.Range(1, count).Select(index => $"{prefix}-{index:0000}").ToList();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), itemId,
            UnitOrigin.Manufactured, serials.Select(serial => new CommissionLine(
                [new IdentifierInput(IdentifierType.InternalSerial, serial)])).ToList(), At, "production.user", Entity,
            LocationId: locationId, LocationKind: LocationKind.Warehouse));
        var unitIds = commissioned.Lines.Select(line => line.UnitId!.Value).ToList();
        var selectionId = Guid.NewGuid();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (release)
        {
            scope.ServiceProvider.GetRequiredService<LedgerPublication>().Suppressed = true;
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            var guards = scope.ServiceProvider.GetServices<IReleaseGuard>();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            for (var index = 0; index < unitIds.Count; index++)
            {
                await ReleaseUnitHandler.Handle(new ReleaseUnit(Guid.NewGuid(), unitIds[index], At.AddHours(1),
                    "quality.user", Entity, $"SIG-{prefix}", Revision: "A"), db, writer, guards, tenantContext, clock,
                    CancellationToken.None);
            }
        }
        db.SelectionSets.Add(new SelectionSet
        {
            TenantId = tenant,
            SelectionId = selectionId,
            SourceKind = SelectionSource.Kinds.PastedList,
            SourceJson = "{\"kind\":\"pasted_list\"}",
            ResolvedCount = count,
            CreatedAt = At,
            CreatedBy = "quality.user",
        });
        db.SelectionSetMembers.AddRange(unitIds.Select((unitId, index) => new SelectionSetMember
        {
            TenantId = tenant,
            SelectionId = selectionId,
            UnitId = unitId,
            Serial = serials[index],
        }));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new SelectionSetup(selectionId, unitIds);
    }

    private async Task<Setup> UnitAsync(TenantId tenant, string serial, bool released, bool suppressPublication,
        bool missingRequired = false)
    {
        var itemId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var items = fixture.Services.GetRequiredService<InMemoryItemPorts>();
        items.Register(InMemoryItemPorts.DefaultProfile(itemId) with
        {
            ItemClass = "certificate-selection",
            AsBuiltCompleteness = missingRequired ? ItemSerialisationProfile.Completeness.Warn : ItemSerialisationProfile.Completeness.Block,
        });
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new ItemLabel(itemId,
            $"FIC-{serial}", "Fictional control assembly"));
        if (missingRequired)
        {
            items.RegisterBom(itemId, [new ExpectedComponent("controller-b", Guid.NewGuid(), "FIC-CTRL-B", 1)]);
            await fixture.EnableErpBomCompletenessAsync(tenant);
        }
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), itemId,
            UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            At, "production.user", Entity, LocationId: locationId, LocationKind: LocationKind.Warehouse));
        var unitId = Assert.Single(commissioned.Lines).UnitId!.Value;
        if (released)
        {
            var release = new ReleaseUnit(Guid.NewGuid(), unitId, At.AddHours(1), "quality.user", Entity, "SIG-FICTIONAL");
            if (suppressPublication)
            {
                using var scope = fixture.ScopeFor(tenant);
                scope.ServiceProvider.GetRequiredService<LedgerPublication>().Suppressed = true;
                var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync();
                await ReleaseUnitHandler.Handle(release, db,
                    scope.ServiceProvider.GetRequiredService<IUnitEventWriter>(),
                    scope.ServiceProvider.GetServices<IReleaseGuard>(),
                    scope.ServiceProvider.GetRequiredService<ITenantContext>(),
                    scope.ServiceProvider.GetRequiredService<IClock>(), CancellationToken.None);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            else
            {
                await InvokeAsync<ReleaseUnitResult>(tenant, release);
            }
        }
        return new Setup(unitId, serial, locationId);
    }

    private async Task WaitForCertificateAsync(TenantId tenant, Guid unitId)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            if (await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().CertificateInstances
                .AnyAsync(row => row.UnitId == unitId)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Automatic certificate issue timed out.");
    }

    private static async Task AssertWholeUnitAsync(SerialisationDbContext db, Guid unitId, int certificateEvents,
        int reprintEvents)
    {
        var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == unitId);
        Assert.Null(current.OwnerPartyRef);
        Assert.Equal(LocationKind.Warehouse, current.LocationKind);
        Assert.Equal("[]", current.LiveEntitlementIds);
        Assert.Single(await db.Episodes.Where(row => row.UnitId == unitId && row.Kind == EpisodeKind.Custody && row.ValidTo == null).ToListAsync());
        Assert.Empty(await db.Episodes.Where(row => row.UnitId == unitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null).ToListAsync());
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == unitId && row.ValidTo == null).ToListAsync());
        Assert.Equal(certificateEvents, await db.Events.CountAsync(row => row.UnitId == unitId && row.EventType == CertificateEventType.Issued));
        Assert.Equal(reprintEvents, await db.Events.CountAsync(row => row.UnitId == unitId && row.EventType == CertificateEventType.Reprinted));
    }

    private static async Task AssertWholeSelectionAsync(SerialisationDbContext db, IReadOnlyList<Guid> unitIds,
        int expectedCertificateEvents)
    {
        Assert.Equal(unitIds.Count, await db.Current.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.OwnerPartyRef == null && row.LocationKind == LocationKind.Warehouse
            && row.LiveEntitlementIds == "[]"));
        Assert.Equal(unitIds.Count, await db.Episodes.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null && row.LocationKind == LocationKind.Warehouse));
        Assert.Empty(await db.Episodes.Where(row => unitIds.Contains(row.UnitId)
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null).ToListAsync());
        Assert.Equal(unitIds.Count, await db.Identifiers.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.ValidTo == null));
        Assert.Empty(await db.Entitlements.Where(row => unitIds.Contains(row.UnitId)).ToListAsync());
        Assert.Equal(expectedCertificateEvents, await db.Events.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.EventType == CertificateEventType.Issued));
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private sealed record Setup(Guid UnitId, string Serial, Guid LocationId);
    private sealed record SelectionSetup(Guid SelectionId, IReadOnlyList<Guid> UnitIds);
}

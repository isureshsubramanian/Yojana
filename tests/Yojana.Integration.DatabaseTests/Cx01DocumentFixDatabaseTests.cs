using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Notifications;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.DatabaseTests;

public sealed partial class Cx01DocumentDatabaseTests
{
    [DatabaseFact]
    public async Task Collection_qualified_lookup_resolves_several_documents_for_one_module_record()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var module = Guid.NewGuid();
        foreach (var kind in new[] { "issue", "receipt", "return", "journal" })
            db.ExternalIdMaps.Add(Map(tenant, connection, "document", Cx01DocumentScenarios.Collection(kind) + ":81", module));
        await db.SaveChangesAsync();
        var facts = ActivatorUtilities.CreateInstance<Cx01OutboundFacts>(scope.ServiceProvider);
        foreach (var kind in new[] { "issue", "receipt", "return", "journal" })
            Assert.Equal(Cx01DocumentScenarios.Collection(kind) + ":81", await facts.ReferenceAsync(connection, "document",
                Cx01DocumentScenarios.Collection(kind) + ":" + module, default));
        Assert.Contains("ambiguous", (await Assert.ThrowsAsync<Cx01UnmappableException>(() => facts.ReferenceAsync(connection,
            "document", module.ToString(), default))).Message);
    }

    [DatabaseFact]
    public async Task Direct_issue_is_recognised_during_poll_while_receipt_waits_for_review()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        await PairSettingsAsync(scope, connection, true);
        var row = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("status", "PARTIAL-POLL"));
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).AwaitingReview);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(row).ReloadAsync();
        var map = await db.ExternalIdMaps.SingleAsync(x => x.ConnectionId == connection && x.EntityType == "document");
        var receipt = await db.Set<OutboundDocumentReceipt>().SingleAsync(x => x.ConnectionId == connection);
        Assert.NotNull(receipt.ChangeMarker);
        Assert.True(await scope.ServiceProvider.GetRequiredService<ICx01OwnWriteStore>().MatchesAsync(connection, map.ExternalId, receipt.ChangeMarker!, default));
        var poll = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>().PollAsync(new(tenant, connection,
            "inventory_adjustments", null, TimeSpan.FromMinutes(1), 100), default);
        Assert.Empty(poll.Events);
        Assert.False(await db.InboundMessages.AnyAsync(x => x.ConnectionId == connection));
        simulator.AddDraftAsDocument(7001, 9002);
        fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Completed);
        await db.Entry(row).ReloadAsync();
        Assert.True(OutboundAcknowledgement.Matches(row.ErpChangeMarker, map.ExternalId, receipt.ChangeMarker!));
        var reviewed = await db.Set<OutboundDocumentReceipt>().SingleAsync(x => x.ConnectionId == connection && x.ExternalId != map.ExternalId);
        Assert.Null(reviewed.ChangeMarker);
    }

    [DocumentDatabaseTheory]
    [InlineData("direct")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task Receipt_failure_keeps_posted_issue_mapped_and_names_it_in_the_manual_task(string failure)
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        await PairSettingsAsync(scope, connection, failure != "direct");
        if (failure == "direct") simulator.RejectNextDocumentCollection = Cx01DocumentScenarios.Collection("receipt");
        var row = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("status", "PAIR-FAIL"));
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        var result = await relay.RelayConnectionAsync(connection, default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        if (failure != "direct")
        {
            Assert.Equal(1, result.AwaitingReview);
            Cx01DocumentScenarios.EndReview(simulator, failure == "rejected");
            await db.Entry(row).ReloadAsync();
            fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
            result = await relay.RelayConnectionAsync(connection, default);
        }
        Assert.Equal(1, result.Parked);
        var map = await db.ExternalIdMaps.SingleAsync(x => x.ConnectionId == connection && x.EntityType == "document");
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.ConnectionId == connection);
        Assert.Contains("goods issue " + map.ExternalId.Split(':').Last() + " posted; receipt rejected:", task.Note);
        Assert.Contains("post the receipt by hand or reverse the issue", task.Note);
        Assert.NotNull(task.Draft);
    }

    [DatabaseFact]
    public async Task Omitted_read_only_preserves_value_and_registration_replay_keeps_acceptance_and_document_capabilities()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>();
        var view = await service.GetAsync(connection, default);
        await service.SetAsync(connection, new("documents", view.Settings, ReadOnly: true), "actor", default);
        await service.SetAsync(connection, new("documents", view.Settings), "actor", default);
        Assert.True((await service.GetAsync(connection, default)).ReadOnly);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        var configured = await settings.GetAsync(connection, default);
        var replay = configured with { DocumentsAcceptance = new("forged", fixture.Clock.UtcNow, "body") };
        var declaration = await ActivatorUtilities.CreateInstance<Cx01ConnectionLifecycleAdapter>(scope.ServiceProvider).ConfigureAsync(connection,
            Cx01Settings.ToJson(replay), default);
        Assert.Equal(view.Acceptance, (await settings.GetAsync(connection, default)).DocumentsAcceptance);
        Assert.Equal(CapabilitySupport.Native, declaration.Capabilities[nameof(CreateCreditMemo)]);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(2, await db.ConnectionEvents.CountAsync(x => x.ConnectionId == connection && x.EventType == "DocumentSettingsChanged"));
    }

    [DatabaseFact]
    public async Task Real_facts_with_real_staging_shapes_resolve_owner_and_park_missing_module_data_as_unsupported()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var units = new ModuleUnitFacts();
        db.ExternalIdMaps.Add(Map(tenant, connection, "party", "FIC-CUSTOMER", units.Owner));
        db.ExternalIdMaps.Add(Map(tenant, connection, "item", Cx01DocumentScenarios.Unit.ItemCode, units.Item));
        await db.SaveChangesAsync();
        var stage = ActivatorUtilities.CreateInstance<OutboundCommandStager>(scope.ServiceProvider, units);
        var header = new PublishedEventHeader("test", "1.0", Guid.NewGuid(), Cx01DocumentScenarios.UnitId,
            fixture.Clock.UtcNow, fixture.Clock.UtcNow, 1, Guid.NewGuid(), "actor", null);
        await PublishedEventOutboundHandler.Handle(new ReplacementReturnOverdue(header, Guid.NewGuid(), header.UnitId,
            Guid.NewGuid(), units.Owner, new(2026, 9, 1), "full", 2), stage, default);
        await PublishedEventOutboundHandler.Handle(new ClaimAdjudicated(header with { EventId = Guid.NewGuid() }, Guid.NewGuid(), null,
            "warranty", [new(Guid.NewGuid(), 10, 10, 100, "warranty", null)], Guid.NewGuid(), Guid.NewGuid(), units.Owner, units.Owner), stage, default);
        await PublishedEventOutboundHandler.Handle(new CertificateIssued(header with { EventId = Guid.NewGuid() }, Guid.NewGuid(),
            "release_record", "standard", 1, "CERT-1", "hash", Guid.NewGuid(), null, "system_generated", fixture.Clock.UtcNow), stage, default);
        var facts = ActivatorUtilities.CreateInstance<Cx01OutboundFacts>(scope.ServiceProvider, units);
        var documents = ActivatorUtilities.CreateInstance<Cx01OutboundDocuments>(scope.ServiceProvider, facts);
        var adapter = ActivatorUtilities.CreateInstance<Cx01Adapter>(scope.ServiceProvider, documents);
        var relay = ActivatorUtilities.CreateInstance<OutboundRelay>(scope.ServiceProvider, new DocumentResolver(adapter));
        Assert.Equal(3, (await relay.RelayConnectionAsync(connection, default)).Parked);
        var tasks = await db.ManualOutboundTasks.Where(x => x.ConnectionId == connection).ToListAsync();
        Assert.Equal(3, tasks.Count);
        Assert.True(tasks.Any(x => x.Note!.Contains("missing credit amount and currency in the module event", StringComparison.Ordinal)), string.Join("; ", tasks.Select(x => x.CommandType + ": " + x.Note)));
        Assert.Contains(tasks, x => x.Note!.Contains("missing journal currency and balancing account in the module event", StringComparison.Ordinal));
        Assert.Contains(tasks, x => x.Note!.Contains("missing attachment content reference in the module event", StringComparison.Ordinal));
        Assert.All(await db.OutboundCommands.Where(x => x.ConnectionId == connection).ToListAsync(), x => Assert.Equal(OutboundConnectorOutcome.Unsupported, x.Outcome));
        Assert.Equal("FIC-CUSTOMER", await facts.ReferenceAsync(connection, "party", units.Owner.ToString(), default));
        Assert.Empty(simulator.DocumentCreateCounts);
    }

    [DatabaseFact]
    public async Task Journal_business_rejection_keeps_the_remote_text_in_a_prefilled_task()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true,
            DocumentRejectionMessage = "Cannot perform transaction in controlling type account" };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        simulator.RejectNextDocumentCollection = Cx01DocumentScenarios.Collection("journal");
        var command = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("journal", "CONTROL"));
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Parked);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == command.OutboundCommandId);
        Assert.Contains(simulator.DocumentRejectionMessage, task.Note);
        Assert.Contains("journal_lines", task.Draft);
    }

    [DatabaseFact]
    public async Task Known_reference_length_rejects_without_posting_and_long_review_key_fits_the_column()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true, JournalReferenceLength = 50 };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        var current = await settings.GetAsync(connection, default);
        var collection = Cx01DocumentScenarios.Collection("journal");
        var field = Cx01DocumentCatalogue.ReferenceField(collection, current);
        Assert.Equal(50, await scope.ServiceProvider.GetRequiredService<ICx01SurveyReader>().FieldLengthAsync(connection, collection, field, default));
        var row = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("journal", new string('r', 57)));
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        Assert.Empty(simulator.DocumentCreateCounts);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(row).ReloadAsync();
        Assert.Equal(OutboundConnectorOutcome.Rejected, row.Outcome);
        Assert.Contains("never truncated", row.LastFailure);
        await PairSettingsAsync(scope, connection, true);
        current = await settings.GetAsync(connection, default);
        var longField = "U_" + new string('a', 62);
        await settings.SetAsync(connection, current with { FieldMap = new Dictionary<string, Cx01FieldSource>
            { ["outbound.receipt.reference"] = new(longField) } }, default);
        var pair = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("status", "LONG-FIELD"));
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).AwaitingReview);
        await db.Entry(pair).ReloadAsync();
        Assert.True(pair.PendingRemoteKey!.Length <= 100);
        Assert.Contains(longField, pair.PendingRemoteKey);
    }

    [DocumentDatabaseTheory]
    [InlineData("documents")]
    [InlineData("reference_only")]
    public async Task Annotation_query_before_create_adopts_the_reference_without_another_patch(string mode)
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        simulator.EnableDocumentPosts = false;
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>();
        var view = await service.GetAsync(connection, default);
        await service.SetAsync(connection, new(mode, view.Settings with { ServiceOrderAction = "annotate" }), "actor", default);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, Cx01DocumentScenarios.Annotate(await settings.GetAsync(connection, default)), default);
        var payload = (CreateServiceOrder)Cx01DocumentScenarios.Command("service", "ANNOTATE");
        payload = payload with { ModuleDocumentRef = Cx01DocumentScenarios.Collection("return") + ":5101" };
        var adapter = scope.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var lookup = new OutboundLookup(connection, nameof(CreateServiceOrder), payload.ErpReferenceText,
            JsonSerializer.Serialize(payload, IntegrationContracts.Json));
        Assert.Null(await adapter.FindByReferenceAsync(lookup, default));
        Assert.Equal(OutboundConnectorOutcome.Created, (await adapter.CreateAsync(new(connection, Guid.NewGuid(), nameof(CreateServiceOrder),
            payload.ErpReferenceText, lookup.Payload!, null), default)).Outcome);
        Assert.NotNull(await adapter.FindByReferenceAsync(lookup, default));
        Assert.Null(await adapter.FindByReferenceAsync(lookup with { ErpReferenceText = "ANOTHER" }, default));
        Assert.Equal(1, Cx01DocumentScenarios.AnnotationPatches(simulator));
        Assert.Empty(simulator.DocumentCreateCounts);
    }

    [DatabaseFact]
    public async Task Document_date_uses_the_tenant_calendar_across_utc_midnight()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var zones = scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.TimeZones.TenantTimeZoneSettings>();
        var view = await zones.GetAsync(default);
        await zones.SetAsync(new("Asia/Kolkata", [], view.Version), "actor", default);
        fixture.Clock.UtcNow = new(2026, 9, 14, 22, 0, 0, DateTimeKind.Utc);
        await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("delivery", "LOCAL-DATE"));
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Completed);
        Assert.Equal("2026-09-15", Cx01DocumentScenarios.PostedDate(simulator, "delivery"));
    }

    [DatabaseFact]
    public async Task Real_stager_carries_replacement_price_and_owner_and_real_facts_post_the_service_order()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var units = new ModuleUnitFacts();
        db.ExternalIdMaps.Add(Map(tenant, connection, "party", "FIC-CUSTOMER", units.Owner));
        db.ExternalIdMaps.Add(Map(tenant, connection, "item", Cx01DocumentScenarios.Unit.ItemCode, units.Item));
        await db.SaveChangesAsync();
        var stage = ActivatorUtilities.CreateInstance<OutboundCommandStager>(scope.ServiceProvider, units);
        var header = new PublishedEventHeader("test", "1.0", Guid.NewGuid(), Cx01DocumentScenarios.UnitId,
            fixture.Clock.UtcNow, fixture.Clock.UtcNow, 1, Guid.NewGuid(), "actor", null);
        await PublishedEventOutboundHandler.Handle(new ReplacementShipped(header, Guid.NewGuid(), "REP-1", Guid.NewGuid(),
            header.UnitId, Guid.NewGuid(), units.Item, units.Item, Guid.NewGuid(), Guid.NewGuid(), "claim", Guid.NewGuid(),
            "CLAIM-1", "warranty", "difference", 12.34m, "USD", "standard", new(2027, 9, 1), "new", false, 0), stage, default);
        await PublishedEventOutboundHandler.Handle(new RmaAuthorised(header with { EventId = Guid.NewGuid() }, Guid.NewGuid(), Guid.NewGuid(),
            "RMA-1", header.UnitId, Guid.NewGuid(), new("invoice", Guid.NewGuid()), 100, "warranty", null), stage, default);
        var replacement = await db.OutboundCommands.SingleAsync(x => x.ConnectionId == connection && x.CommandType == nameof(CreateReplacementDelivery));
        var command = JsonSerializer.Deserialize<CreateReplacementDelivery>(replacement.Payload, IntegrationContracts.Json)!;
        Assert.Equal("FIC-CUSTOMER", command.CustomerRef);
        Assert.Equal(new Money(12.34m, "USD"), command.Price);
        var facts = ActivatorUtilities.CreateInstance<Cx01OutboundFacts>(scope.ServiceProvider, units);
        var documents = ActivatorUtilities.CreateInstance<Cx01OutboundDocuments>(scope.ServiceProvider, facts);
        var adapter = ActivatorUtilities.CreateInstance<Cx01Adapter>(scope.ServiceProvider, documents);
        var relay = ActivatorUtilities.CreateInstance<OutboundRelay>(scope.ServiceProvider, new DocumentResolver(adapter));
        var result = await relay.RelayConnectionAsync(connection, default);
        Assert.Equal(1, result.Completed);
        Assert.Equal(2, result.Parked);
        Assert.Equal(1, simulator.DocumentCreateCounts.GetValueOrDefault(Cx01DocumentScenarios.Collection("service")));
        await db.Entry(replacement).ReloadAsync();
        Assert.Equal(OutboundConnectorOutcome.Unsupported, replacement.Outcome);
        Assert.Contains("missing ship-to address name in the module event", replacement.LastFailure);
    }

    [DatabaseFact]
    public async Task Local_currency_is_required_for_documents_and_company_survey_fills_it_when_available()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true, LocalCurrencyCode = "$" };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>();
        var view = await service.GetAsync(connection, default);
        var missing = view.Settings with { LocalCurrencyCode = null };
        Assert.Contains("local currency", (await Assert.ThrowsAsync<Cx01DocumentRuleException>(() =>
            service.SetAsync(connection, new("documents", missing), "actor", default))).Message);
        await service.SetAsync(connection, new("reference_only", missing), "actor", default);
        await scope.ServiceProvider.GetRequiredService<Cx01SurveyService>().RunAsync(connection, new(), default);
        view = await service.GetAsync(connection, default);
        Assert.Equal("$", view.Settings.LocalCurrencyCode);
        await service.SetAsync(connection, new("documents", view.Settings), "actor", default);
    }

    private static ExternalIdMap Map(TenantId tenant, Guid connection, string kind, string external, Guid module) => new()
    {
        TenantId = tenant, ExternalIdMapId = Guid.NewGuid(), ConnectionId = connection, EntityType = kind,
        ExternalId = external, ModuleId = module, ValidFrom = new(2026, 9, 1), MappingSource = ExternalIdMappingSource.Outbound,
        MappedAt = new(2026, 9, 1), MappedBy = "actor", NaturalKeyHash = new string('a', 64),
    };

    private static async Task PairSettingsAsync(IServiceScope scope, Guid connection, bool review)
    {
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        var current = await settings.GetAsync(connection, default);
        await settings.SetAsync(connection, current with
        {
            Documents = current.Documents! with { StatusPostingPair = new("6100", "2100") },
            ApprovalHandling = review ? new Dictionary<string, Cx01ApprovalHandling>
                { [Cx01DocumentScenarios.Collection("receipt")] = new(Cx01ApprovalModes.DraftForReview) } : null,
        }, default);
    }

    private sealed class DocumentResolver(IOutboundConnector connector) : IOutboundConnectorResolver
    {
        public IOutboundConnector Resolve(string code) => connector;
        public IOutboundReviewTracker? ResolveReviewTracker(string code) => null;
    }

    private sealed class ModuleUnitFacts : ICertificateUnitReadPort
    {
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Item { get; } = Guid.NewGuid();
        public Task<CertificateUnitFact?> ReadAsync(Guid id, CancellationToken ct) => Task.FromResult<CertificateUnitFact?>(new(id,
            "active", "purchased", "full", Item, null, new(2026, 9, 1), null, new(Guid.NewGuid(), Owner, "customer", new(2026, 9, 1)), [], []));
        public Task<IReadOnlyList<Guid>> FindUnitIdsBySerialAsync(string serial, CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([Cx01DocumentScenarios.UnitId]);
        public Task<IReadOnlyDictionary<Guid, string>> PrimarySerialsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(ids.ToDictionary(x => x, _ => Cx01DocumentScenarios.Unit.Serial));
        public Task<IReadOnlyDictionary<Guid, Guid>> CurrentItemIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(ids.ToDictionary(x => x, _ => Item));
    }
}

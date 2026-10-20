using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Gateway;
using Yojana.Integration.Outbox;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Integration.DatabaseTests;

public sealed partial class Cx01DocumentDatabaseTests
{
    [DocumentDatabaseTheory]
    [InlineData("delivery", "ship_to_ref")]
    [InlineData("credit", "lines[0].amount.currency")]
    [InlineData("journal", "journal_lines[0].currency")]
    [InlineData("transfer", "from_location_or_status")]
    [InlineData("attachment", "content_ref")]
    [InlineData("service", "customer_ref")]
    [InlineData("return", "customer_ref")]
    public async Task Missing_values_are_held_without_remote_calls_and_operator_values_resume(string scenario, string field)
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator, scenario == "return");
        using var scope = fixture.ScopeFor(tenant);
        var complete = Cx01DocumentScenarios.Command(scenario, "SOURCE-" + Guid.NewGuid().ToString("N"));
        IOutboundCommand missing = complete switch
        {
            CreateReplacementDelivery value => value with { ShipToRef = "" },
            CreateCreditMemo value => value with { Lines = [value.Lines[0] with { Amount = new(25, "") }] },
            PostWarrantyExpense value => value with { JournalLines = [value.JournalLines[0] with { Currency = "" }, value.JournalLines[1]] },
            ChangeStockStatusOrLocation value => value with { FromLocationOrStatus = null },
            AttachDocument value => value with { ContentRef = null },
            CreateServiceOrder value => value with { CustomerRef = "" }, _ => complete,
        };
        var row = await StageAsync(scope, tenant, connection, missing);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        Assert.Empty(simulator.DocumentCreateCounts);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var letter = await db.DeadLetterItems.SingleAsync(x => x.SourceId == row.OutboundCommandId);
        Assert.Equal("missing_source_field", letter.ReasonCode);
        Assert.Contains(field, letter.Failure);
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == row.OutboundCommandId);
        Assert.Equal(row.Payload, task.Draft);
        var desk = scope.ServiceProvider.GetRequiredService<DeadLetterService>();
        var invalid = JsonSerializer.SerializeToElement(missing, missing.GetType(), IntegrationContracts.Json);
        Assert.Equal("missing_source_field", (await Assert.ThrowsAsync<DeadLetterRuleException>(() => desk.CompleteManualTaskAsync(task.ManualOutboundTaskId,
            new(CorrectedDraft: invalid), "operator", default))).Rule);
        var draft = JsonSerializer.SerializeToElement(complete, complete.GetType(), IntegrationContracts.Json);
        if (complete is CreateReplacementDelivery delivery)
        {
            var changedIdentity = JsonSerializer.SerializeToElement(delivery with { IdempotencyKey = Guid.NewGuid() }, IntegrationContracts.Json);
            Assert.Equal("draft_identity_changed", (await Assert.ThrowsAsync<DeadLetterRuleException>(() => desk.CompleteManualTaskAsync(task.ManualOutboundTaskId,
                new(CorrectedDraft: changedIdentity), "operator", default))).Rule);
        }
        IOutboundCommand? retargeted = complete switch
        {
            CreateReplacementDelivery value => value with { ReplacementUnit = value.ReplacementUnit with { UnitId = Guid.NewGuid() } },
            CreateCreditMemo value => value with { Lines = [value.Lines[0] with { UnitId = Guid.NewGuid() }] },
            ChangeStockStatusOrLocation value => value with { Unit = value.Unit with { UnitId = Guid.NewGuid() } },
            CreateServiceOrder value => value with { Unit = value.Unit with { UnitId = Guid.NewGuid() } },
            _ => null,
        };
        if (retargeted is not null)
        {
            var changedUnit = JsonSerializer.SerializeToElement(retargeted, retargeted.GetType(), IntegrationContracts.Json);
            Assert.Equal("draft_identity_changed", (await Assert.ThrowsAsync<DeadLetterRuleException>(() => desk.CompleteManualTaskAsync(task.ManualOutboundTaskId,
                new(CorrectedDraft: changedUnit), "operator", default))).Rule);
            Assert.Equal(task.Draft, row.Payload);
        }
        var previousPayload = row.Payload;
        // Unknown fields and the optional transport wrapper must not enter stored command records or audit drafts.
        var normalised = System.Text.Json.Nodes.JsonNode.Parse(draft.GetRawText())!;
        normalised["unknown_field"] = "ignored";
        draft = JsonSerializer.SerializeToElement(new { payload = normalised }, IntegrationContracts.Json);
        await desk.CompleteManualTaskAsync(task.ManualOutboundTaskId, new(CorrectedDraft: draft), "operator", default);
        using var audit = JsonDocument.Parse((await db.DeadLetterEvents.SingleAsync(x => x.DeadLetterItemId == letter.DeadLetterItemId
            && x.Action == DeadLetterAction.MapAndRetry)).DetailsJson);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(previousPayload),
            System.Text.Json.Nodes.JsonNode.Parse(audit.RootElement.GetProperty("previous_payload").GetRawText())));
        Assert.False(audit.RootElement.GetProperty("corrected_draft").TryGetProperty("unknown_field", out _));
        Assert.Equal(JsonSerializer.Serialize(complete, complete.GetType(), IntegrationContracts.Json), row.Payload);
        Assert.Equal(row.Payload, task.Draft);
        Assert.Equal(row.Payload, letter.CanonicalAttempt);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Completed);
        await db.Entry(row).ReloadAsync();
        Assert.Equal(OutboundCommandStatus.Done, row.Status);
        Assert.Equal(1, simulator.DocumentCreateCounts[Cx01DocumentScenarios.Collection(scenario)]);
        Assert.Equal(0, (await relay.RelayConnectionAsync(connection, default)).Completed);
    }

    [DocumentDatabaseTheory]
    [InlineData("delivery")]
    [InlineData("credit")]
    [InlineData("journal")]
    [InlineData("transfer")]
    [InlineData("attachment")]
    [InlineData("service")]
    [InlineData("return")]
    public async Task Published_events_use_real_module_identity_custody_and_money_to_post_documents(string scenario)
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator, scenario == "return");
        using var scope = fixture.ScopeFor(tenant);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var at = fixture.Clock.UtcNow.AddDays(-1);
        var entity = Guid.NewGuid(); var customer = Guid.NewGuid();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var item = await scope.ServiceProvider.GetRequiredService<GatewayReferenceStore>().UpsertItemAsync(connection,
            new(Cx01DocumentScenarios.Unit.ItemCode, "Fictional source item", OpenEnum.Known("serial"), "EA", false, null), default);
        await db.SaveChangesAsync();
        var warehouse = Guid.NewGuid(); var site = Guid.NewGuid();
        async Task<T> Invoke<T>(object command) where T : class => await bus.InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() });
        async Task<Guid> Commission(string serial)
        {
            var result = await Invoke<CommissionUnitsResult>(new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
                [new([new(IdentifierType.InternalSerial, serial)])], at, "source-test", entity,
                LocationId: warehouse, LocationKind: LocationKind.Warehouse));
            return result.Lines[0].UnitId!.Value;
        }
        var unit = await Commission(Cx01DocumentScenarios.Unit.Serial);
        var original = await Commission("FIC-ORIGINAL");
        await Invoke<OpenOwnershipResult>(new OpenOwnership(Guid.NewGuid(), original, customer, PartyType.Customer, EpisodeReason.Sale, at.AddMinutes(1), "source-test", entity));
        await Invoke<ChangeCustodyResult>(new ChangeCustody(Guid.NewGuid(), original, LocationKind.CustomerSite, "delivery", at.AddMinutes(2), "source-test", entity,
            LocationId: site, PartyRef: customer, PartyType: PartyType.Customer));
        await Invoke<OpenOwnershipResult>(new OpenOwnership(Guid.NewGuid(), unit, customer, PartyType.Customer, EpisodeReason.Sale, at.AddMinutes(1), "source-test", entity));
        db.ExternalIdMaps.AddRange(Map(tenant, connection, "party", "FIC-CUSTOMER", customer),
            Map(tenant, connection, "location", "store", warehouse), Map(tenant, connection, "location", "SHIP", site),
            Map(tenant, connection, "document", Cx01DocumentScenarios.AttachmentTarget, unit));
        await db.SaveChangesAsync();
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        var configured = await settings.GetAsync(connection, default);
        await settings.SetAsync(connection, configured with { Documents = configured.Documents! with
        {
            AccountMap = new Dictionary<string, string> { ["warranty"] = "6100", ["warranty.offset"] = "2100" },
            LocationMap = new Dictionary<string, Cx01Warehouse>(configured.Documents.LocationMap!) { ["refurbished"] = new("02") },
        } }, default);
        var header = new PublishedEventHeader("test", "1.0", Guid.NewGuid(), unit, at, at, 1, entity, "source-test", null);
        var stage = scope.ServiceProvider.GetRequiredService<OutboundCommandStager>();
        var certificate = Guid.NewGuid();
        var renderer = new SourcePdfRenderer();
        ICx01AttachmentContent attachmentContent = scope.ServiceProvider.GetRequiredService<ICx01AttachmentContent>();
        if (scenario == "attachment")
        {
            var serialDb = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await scope.ServiceProvider.GetRequiredService<CertificateTemplateService>().EnsureStartersAsync(default);
            var templateVersion = await serialDb.CertificateTemplateVersions.Select(x => x.TemplateVersionId).FirstAsync();
            serialDb.CertificateInstances.Add(new CertificateInstance
            {
                TenantId = tenant, CertificateId = certificate, UnitId = unit, DocumentKind = "release_record", FormType = CertificateFormType.GenericRelease,
                Version = 1, TrackingNumber = "CERT-1", TemplateVersionId = templateVersion, Payload = "{}", PayloadHash = new string('a', 64),
                FactsHash = new string('a', 64), TriggeringEventId = header.EventId, IssuerEntityId = entity, IssuerSnapshot = "{}",
                BrandingSnapshot = "{}", Language = "en", Provenance = "system_generated", IssuedAt = at, Status = CertificateStatus.Issued,
            });
            await serialDb.SaveChangesAsync();
            // The renderer is a port fixture; storage lookup, document metadata and content adapter are production code.
            var queries = ActivatorUtilities.CreateInstance<CertificateQueries>(scope.ServiceProvider, renderer);
            var rendered = ActivatorUtilities.CreateInstance<RenderedOutboundDocumentPort>(scope.ServiceProvider, queries, renderer);
            stage = ActivatorUtilities.CreateInstance<OutboundCommandStager>(scope.ServiceProvider, rendered);
            attachmentContent = ActivatorUtilities.CreateInstance<Cx01LocalAttachmentContent>(scope.ServiceProvider, rendered);
            Assert.Equal("CERT-1.pdf", (await rendered.DescribeAsync(certificate, default))!.FileName);
            Assert.Equal(0, renderer.Calls);
        }
        switch (scenario)
        {
            case "delivery":
                await PublishedEventOutboundHandler.Handle(new ReplacementShipped(header, Guid.NewGuid(), "REP-1", Guid.NewGuid(), original, unit, item, item,
                    Guid.NewGuid(), Guid.NewGuid(), "claim", Guid.NewGuid(), "CLAIM-1", "warranty", "zero", 0, "USD", "standard", new(2027, 9, 1), "new", false, 0, customer), stage, default);
                break;
            case "credit":
                await PublishedEventOutboundHandler.Handle(new ReplacementReturnOverdue(header, Guid.NewGuid(), unit, original, customer, new(2026, 9, 1), "full", 1, 25, "USD"), stage, default);
                break;
            case "journal":
                await PublishedEventOutboundHandler.Handle(new ClaimAdjudicated(header, Guid.NewGuid(), null, "warranty", [new(Guid.NewGuid(), 25, 25, 100, "warranty", null)], entity, entity, customer, customer, Currency: "USD"), stage, default);
                break;
            case "transfer":
                await PublishedEventOutboundHandler.Handle(new RefurbishmentReleased(header, Guid.NewGuid(), "REF-1", unit, unit, "repair", "refurbished", 1, "same", 25, "USD"), stage, default);
                break;
            case "attachment":
                await PublishedEventOutboundHandler.Handle(new CertificateIssued(header, certificate, "release_record", "standard", 1, "CERT-1.pdf", "hash", Guid.NewGuid(), null, "system_generated", at), stage, default);
                break;
            default:
                await PublishedEventOutboundHandler.Handle(new RmaAuthorised(header, Guid.NewGuid(), Guid.NewGuid(), "RMA-1", unit, Guid.NewGuid(), new("invoice", Guid.NewGuid()), 100, "warranty", null), stage, default);
                // No RMA renderer exists. Its separate attachment is correctly held; the service document can post.
                break;
        }
        var contentReads = new SourceContentReads(attachmentContent);
        var facts = ActivatorUtilities.CreateInstance<Cx01OutboundFacts>(scope.ServiceProvider);
        var documents = ActivatorUtilities.CreateInstance<Cx01OutboundDocuments>(scope.ServiceProvider, facts, contentReads);
        var adapter = ActivatorUtilities.CreateInstance<Cx01Adapter>(scope.ServiceProvider, documents);
        using var checks = new ServiceCollection().AddKeyedSingleton<IOutboundCompletenessCheck>(Cx01Capabilities.ConnectorCode,
            ActivatorUtilities.CreateInstance<Cx01OutboundCompleteness>(scope.ServiceProvider, facts, contentReads)).BuildServiceProvider();
        var relay = ActivatorUtilities.CreateInstance<OutboundRelay>(scope.ServiceProvider, new DocumentResolver(adapter), new OutboundCompleteness(checks));
        Assert.Equal(0, renderer.Calls);
        var result = await relay.RelayConnectionAsync(connection, default);
        Assert.True(result.Completed == 1, string.Join("; ", await db.OutboundCommands.Select(x => x.LastFailure).ToListAsync()));
        Assert.Equal(1, simulator.DocumentCreateCounts[Cx01DocumentScenarios.Collection(scenario)]);
        var posted = await db.OutboundCommands.SingleAsync(x => x.Status == OutboundCommandStatus.Done);
        if (scenario == "delivery") Assert.Equal("SHIP", JsonSerializer.Deserialize<CreateReplacementDelivery>(posted.Payload, IntegrationContracts.Json)!.ShipToRef);
        if (scenario == "transfer") Assert.Equal("store", JsonSerializer.Deserialize<ChangeStockStatusOrLocation>(posted.Payload, IntegrationContracts.Json)!.FromLocationOrStatus);
        if (scenario is "delivery" or "transfer" or "service" or "return") Assert.Contains(Cx01DocumentScenarios.Unit.Serial, posted.Payload);
        if (scenario == "attachment")
        {
            Assert.Contains("%PDF-source-document", simulator.LastAttachmentUpload);
            Assert.Equal(1, renderer.CallsFor("CERT-1"));
            Assert.Equal(2, contentReads.ReadsFor(OutboundDocumentContentReference.ForDocument(certificate)));
        }
        Assert.Null(simulator.LastHandlerFailure);
    }

    [DatabaseFact]
    public async Task Valuation_and_accrual_facts_without_amounts_do_not_stage_empty_journals()
    {
        var tenant = await fixture.NewTenantAsync("noexpense");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var stage = scope.ServiceProvider.GetRequiredService<OutboundCommandStager>();
        var header = new PublishedEventHeader("test", "1.0", Guid.NewGuid(), Guid.NewGuid(), new(2026, 9, 1), new(2026, 9, 1), 1, Guid.NewGuid(), "test", null);
        Assert.Equal(0, await PublishedEventOutboundHandler.Handle(new EntitlementCreated(header, Guid.NewGuid(), Guid.NewGuid(), "standard", "assurance",
            header.LegalEntityId, null, Guid.NewGuid(), "standard", 1, new(2026, 9, 1), new(2027, 9, 1), "active", null, "assurance", false, "system_generated", null), stage, default));
        Assert.Equal(1, await PublishedEventOutboundHandler.Handle(new UnitGraded(header, "refurbished", null, null, "graded_value"), stage, default));
        var rows = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().OutboundCommands.Where(x => x.ConnectionId == connection).ToListAsync();
        Assert.Equal(nameof(ChangeStockStatusOrLocation), Assert.Single(rows).CommandType);
    }

    [DatabaseFact]
    public async Task An_old_draft_is_checked_against_the_new_documents_mode_at_claim()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>();
        var view = await service.GetAsync(connection, default);
        await service.SetAsync(connection, new("reference_only", view.Settings), "operator", default);
        var command = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "MODE-CHANGE");
        await StageAsync(scope, tenant, connection, command with { ShipToRef = "" });
        await service.SetAsync(connection, new("documents", view.Settings), "operator", default);
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Parked);
        var letter = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().DeadLetterItems.SingleAsync(x => x.ConnectionId == connection);
        Assert.Equal("missing_source_field", letter.ReasonCode);
        Assert.Contains("ship_to_ref", letter.Failure);
        Assert.Empty(simulator.DocumentCreateCounts);
    }

    [DatabaseFact]
    public async Task Large_missing_field_lists_leave_the_canonical_draft_unchanged()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var command = (PostWarrantyExpense)Cx01DocumentScenarios.Command("journal", "MANY-FIELDS");
        var lines = Enumerable.Range(0, 100).Select(i => new JournalLine(i % 2 == 0 ? "warranty" : "reserve", i % 2 == 0 ? 25 : 0, i % 2 == 0 ? 0 : 25, "")).ToList();
        var row = await StageAsync(scope, tenant, connection, command with { JournalLines = lines });
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Parked);
        var task = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == row.OutboundCommandId);
        using var draft = JsonDocument.Parse(task.Draft);
        Assert.False(draft.RootElement.TryGetProperty("payload", out _));
        Assert.Equal(row.Payload, task.Draft);
        Assert.Equal(100, draft.RootElement.GetProperty("journal_lines").GetArrayLength());
        var letter = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().DeadLetterItems.SingleAsync(x => x.SourceId == row.OutboundCommandId);
        Assert.Contains("journal_lines[99].currency", letter.Failure);
        Assert.Equal(row.Payload, letter.CanonicalAttempt);
        Assert.Empty(simulator.DocumentCreateCounts);
    }

    [DatabaseFact]
    public async Task Connection_can_require_an_explicit_replacement_destination()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>();
        var view = await service.GetAsync(connection, default);
        await service.SetAsync(connection, new("documents", view.Settings with { UseRecordedReplacementShipTo = false }), "operator", default);
        var site = Guid.NewGuid();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ExternalIdMaps.Add(Map(tenant, connection, "location", "SHIP", site));
        await db.SaveChangesAsync();
        var stage = ActivatorUtilities.CreateInstance<OutboundCommandStager>(scope.ServiceProvider, new DestinationSource(site));
        var command = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "EXPLICIT-SITE");
        var header = new PublishedEventHeader("test", "1.0", Guid.NewGuid(), command.ReplacementUnit.UnitId,
            new(2026, 9, 1), new(2026, 9, 1), 1, Guid.NewGuid(), "test", null);
        await stage.StageAsync(header, command with { ShipToRef = "" }, default);
        var row = await db.OutboundCommands.SingleAsync(x => x.ConnectionId == connection);
        Assert.Empty(JsonSerializer.Deserialize<CreateReplacementDelivery>(row.Payload, IntegrationContracts.Json)!.ShipToRef);
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Parked);
        Assert.Empty(simulator.DocumentCreateCounts);
    }

    [DatabaseFact]
    public async Task An_expired_draft_can_acquire_a_source_completion_task_after_operator_retry()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var delivery = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "EXPIRED-SOURCE");
        var row = await StageAsync(scope, tenant, connection, delivery with { ShipToRef = "" });
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(8);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.False(await db.ManualOutboundTasks.AnyAsync(x => x.OutboundCommandId == row.OutboundCommandId));
        var letter = await db.DeadLetterItems.SingleAsync(x => x.SourceId == row.OutboundCommandId);
        await scope.ServiceProvider.GetRequiredService<DeadLetterService>().MapAndRetryAsync(letter.DeadLetterItemId,
            new("party", "FIC-RETRY-CUSTOMER", Guid.NewGuid()), "operator", default);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == row.OutboundCommandId);
        Assert.Equal(ManualTaskStatus.Open, task.Status);
        Assert.Contains("ship_to_ref", task.Note);
        Assert.Empty(simulator.DocumentCreateCounts);
    }

    [DatabaseFact]
    public async Task Ambiguous_attachment_mapping_names_the_target_for_operator_completion()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var target = Guid.NewGuid();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ExternalIdMaps.AddRange(Map(tenant, connection, "document", Cx01DocumentScenarios.AttachmentTarget, target),
            Map(tenant, connection, "document", Cx01DocumentScenarios.AlternativeAttachmentTarget, target));
        await db.SaveChangesAsync();
        var facts = ActivatorUtilities.CreateInstance<Cx01OutboundFacts>(scope.ServiceProvider);
        var check = ActivatorUtilities.CreateInstance<Cx01OutboundCompleteness>(scope.ServiceProvider, facts);
        var command = (AttachDocument)Cx01DocumentScenarios.Command("attachment", "AMBIGUOUS-TARGET");
        Assert.Contains("target_record_ref", await check.MissingAsync(connection, "documents", command with { TargetRecordRef = target.ToString() }, default));
        Assert.Empty(simulator.DocumentCreateCounts);
    }

    private sealed class DestinationSource(Guid site) : IOutboundUnitSourcePort
    {
        public Task<OutboundUnitSource> ReadAsync(Guid unitId, DocumentRef? document, CancellationToken cancellationToken, Guid? customerPartyRef = null, bool allowOwnershipFallback = true, DateTime? custodyAt = null) =>
            Task.FromResult(new OutboundUnitSource(Cx01DocumentScenarios.Unit.Serial, null, customerPartyRef, site));
    }

    private sealed class SourceContentReads(ICx01AttachmentContent inner) : ICx01AttachmentContent
    {
        private readonly Dictionary<string, int> reads = new(StringComparer.Ordinal);
        public int ReadsFor(string reference) => reads.GetValueOrDefault(reference);
        public void BeginAttempt() => inner.BeginAttempt();
        public Task<Cx01AttachmentContent?> ReadAsync(string contentRef, long maximumBytes, CancellationToken ct)
        {
            reads[contentRef] = reads.GetValueOrDefault(contentRef) + 1;
            return inner.ReadAsync(contentRef, maximumBytes, ct);
        }
    }

    private sealed class SourcePdfRenderer : ICertificateRenderer
    {
        private readonly Dictionary<string, int> calls = new(StringComparer.Ordinal);
        public int Calls => calls.Values.Sum();
        public int CallsFor(string trackingNumber) => calls.GetValueOrDefault(trackingNumber);
        public Task<CertificateRenderResult> RenderAsync(CertificateRenderRequest request, CancellationToken cancellationToken)
        {
            var number = request.Metadata?.TrackingNumber ?? "unknown";
            calls[number] = calls.GetValueOrDefault(number) + 1;
            return Task.FromResult(new CertificateRenderResult(Encoding.UTF8.GetBytes("%PDF-source-document"), "application/pdf"));
        }
    }
}

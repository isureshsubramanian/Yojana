using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

public sealed class DocumentDatabaseTheoryAttribute : TheoryAttribute
{
    public DocumentDatabaseTheoryAttribute()
    {
        if (IntegrationHostFixture.ConnectionString is null) Skip = "YOJANA_TEST_SQL is required.";
    }
}

[Collection("integration host")]
public sealed partial class Cx01DocumentDatabaseTests(IntegrationHostFixture fixture)
{
    public static IEnumerable<object[]> Commands => Cx01DocumentScenarios.Cases.SelectMany(x => new[] { new object[] { x, false }, new object[] { x, true } });

    [DocumentDatabaseTheory]
    [MemberData(nameof(Commands))]
    public async Task Each_command_posts_once_and_commits_its_reply_with_the_mapping(string scenario, bool crash)
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator, scenario == "return");
        using var scope = fixture.ScopeFor(tenant);
        var command = Cx01DocumentScenarios.Command(scenario, "REF-" + Guid.NewGuid().ToString("N"));
        var row = await StageAsync(scope, tenant, connection, command);
        simulator.DropNextCreateReply = crash;
        var result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(row).ReloadAsync();
        if (crash && result.Completed == 0)
        {
            Assert.Equal(1, result.Retrying);
            Assert.Equal(OutboundCommandStatus.Retry, row.Status);
            Assert.False(await db.ExternalIdMaps.AnyAsync(x => x.ConnectionId == connection && x.EntityType == "document"));
            fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
            result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
            await db.Entry(row).ReloadAsync();
        }
        Assert.True(result.Completed == 1, row.LastFailure ?? simulator.LastHandlerFailure);
        Assert.Equal(OutboundCommandStatus.Done, row.Status);
        Assert.Equal(1, simulator.DocumentCreateCounts.GetValueOrDefault(Cx01DocumentScenarios.Collection(scenario)));
        var map = await db.ExternalIdMaps.SingleAsync(x => x.ConnectionId == connection && x.EntityType == "document" && x.ExternalId == row.ErpExternalId);
        Assert.Equal(Guid.Parse(command.ModuleDocumentRef.Split('/').Last()), map.ModuleId);
        if (scenario == "attachment") Assert.True(Cx01DocumentScenarios.AttachmentIsLinked(simulator, row.ErpExternalId!));
        if (scenario != "attachment")
        {
            Assert.NotNull(row.ErpChangeMarker);
            var own = scope.ServiceProvider.GetRequiredService<ICx01OwnWriteStore>();
            Assert.True(await own.MatchesAsync(connection, row.ErpExternalId!, row.ErpChangeMarker!, default));
            Assert.False(await own.MatchesAsync(connection, row.ErpExternalId!, "later change", default));
        }
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Completed);
        Assert.Equal(1, simulator.DocumentCreateCounts[Cx01DocumentScenarios.Collection(scenario)]);
        Assert.Null(simulator.LastHandlerFailure);
        var configured = await scope.ServiceProvider.GetRequiredService<Cx01Settings>().GetAsync(connection, default);
        var allowList = new Yojana.Relay.RelayAllowList(Cx01RelayProfile.Declaration(configured with { RelayCredentials = "cloud" }, "documents"));
        foreach (var path in simulator.OutboundPaths)
            Assert.True(allowList.Allows(path.Method, path.Path, ""), path.Method + " " + path.Path);
    }

    [DatabaseFact]
    public async Task Business_rejection_and_unmapped_accounts_raise_prefilled_manual_tasks()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        simulator.RejectNextDocumentCollection = Cx01DocumentScenarios.Collection("credit");
        var rejected = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("credit", "CLOSED"));
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Parked);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        var configured = await settings.GetAsync(connection, default);
        await settings.SetAsync(connection, configured with { Documents = configured.Documents! with { AccountMap = null } }, default);
        var unmapped = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("journal", "UNMAPPED"));
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Parked);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(unmapped).ReloadAsync();
        await db.Entry(rejected).ReloadAsync();
        Assert.Equal(OutboundConnectorOutcome.Rejected, rejected.Outcome);
        Assert.Equal(OutboundConnectorOutcome.Rejected, unmapped.Outcome);
        Assert.Contains("Map the warranty account", unmapped.LastFailure);
        Assert.Equal(2, await db.ManualOutboundTasks.CountAsync(x => x.ConnectionId == connection));
        Assert.False(await db.ExternalIdMaps.AnyAsync(x => x.ConnectionId == connection && x.EntityType == "document"));
    }

    [DatabaseFact]
    public async Task Approval_draft_waits_and_review_posted_document_keeps_a_null_own_write_marker()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true, ReturnsApprovalTemplateActive = true };
        var (tenant, connection) = await ConnectionAsync(simulator, returns: true);
        using var scope = fixture.ScopeFor(tenant);
        var row = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("return", "REVIEW"));
        var result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
        Assert.Equal(1, result.AwaitingReview);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(row).ReloadAsync();
        Assert.Equal(OutboundCommandStatus.AwaitingReview, row.Status);
        Assert.Equal(1, simulator.DraftCreateCount);
        Assert.False(await db.ExternalIdMaps.AnyAsync(x => x.ConnectionId == connection && x.EntityType == "document"));
        simulator.AddDraftAsDocument(7001, 9001);
        fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
        result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
        Assert.Equal(1, result.Completed);
        await db.Entry(row).ReloadAsync();
        Assert.Null(row.ErpChangeMarker);
        Assert.Equal(OutboundConnectorOutcome.PostedAfterReview, row.Outcome);
        Assert.Single(await db.ExternalIdMaps.Where(x => x.ConnectionId == connection && x.EntityType == "document").ToListAsync());
    }

    [DatabaseFact]
    public async Task Status_only_stock_change_requires_installation_acceptance_and_pair_recovers_a_missing_half()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var unsupported = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("status", "STATUS-OFF"));
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Parked);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(unsupported).ReloadAsync();
        Assert.Equal(OutboundConnectorOutcome.Unsupported, unsupported.Outcome);
        Assert.Contains("per-installation test", unsupported.LastFailure);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        var configured = await settings.GetAsync(connection, default);
        await settings.SetAsync(connection, configured with { Documents = configured.Documents! with { StatusPostingPair = new("6100", "2100") } }, default);
        simulator.FailNextDocumentCollection = Cx01DocumentScenarios.Collection("receipt");
        var row = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("status", "STATUS-ON"));
        var result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
        Assert.Equal(1, result.Retrying);
        await db.Entry(row).ReloadAsync();
        fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
        result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
        Assert.Equal(1, result.Completed);
        Assert.Equal(1, simulator.DocumentCreateCounts[Cx01DocumentScenarios.Collection("issue")]);
        Assert.Equal(1, simulator.DocumentCreateCounts[Cx01DocumentScenarios.Collection("receipt")]);
        Assert.Equal(2, await db.ExternalIdMaps.CountAsync(x => x.ConnectionId == connection && x.EntityType == "document"));
        await db.Entry(row).ReloadAsync();
        var own = scope.ServiceProvider.GetRequiredService<ICx01OwnWriteStore>();
        foreach (var collection in new[] { Cx01DocumentScenarios.Collection("issue"), Cx01DocumentScenarios.Collection("receipt") })
        {
            var id = Cx01ReviewKey.Text(simulator.DocumentBodies[collection], Cx01DocumentCatalogue.KeyField(collection))!;
            Assert.True(await own.MatchesAsync(connection, collection + ":" + id,
                Cx01Mapper.ChangeMarker(simulator.DocumentBodies[collection], id), default));
        }
    }

    [DatabaseFact]
    public async Task Lost_draft_reply_adopts_the_existing_draft_on_retry()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true, ReturnsApprovalTemplateActive = true };
        var (tenant, connection) = await ConnectionAsync(simulator, returns: true);
        using var scope = fixture.ScopeFor(tenant);
        var row = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("return", "DRAFT-CRASH"));
        simulator.DropNextCreateReply = true;
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Retrying);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(row).ReloadAsync();
        fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).AwaitingReview);
        Assert.Equal(1, simulator.DraftCreateCount);
        Assert.False(await db.ExternalIdMaps.AnyAsync(x => x.ConnectionId == connection && x.EntityType == "document"));
    }

    [DatabaseFact]
    public async Task Both_stock_pair_drafts_are_reviewed_before_the_reply_and_both_mappings_commit()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        var configured = await settings.GetAsync(connection, default);
        await settings.SetAsync(connection, configured with
        {
            Documents = configured.Documents! with { StatusPostingPair = new("6100", "2100") },
            ApprovalHandling = new Dictionary<string, Cx01ApprovalHandling>
            {
                [Cx01DocumentScenarios.Collection("issue")] = new(Cx01ApprovalModes.DraftForReview),
                [Cx01DocumentScenarios.Collection("receipt")] = new(Cx01ApprovalModes.DraftForReview),
            },
        }, default);
        var row = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("status", "PAIR-REVIEW"));
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).AwaitingReview);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(row).ReloadAsync();
        simulator.AddDraftAsDocument(7001, 9001);
        fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
        Assert.Equal(0, (await relay.RelayConnectionAsync(connection, default)).Completed);
        Assert.Equal(2, simulator.DraftCreateCount);
        await db.Entry(row).ReloadAsync();
        Assert.Contains("7002", row.PendingRemoteKey);
        Assert.Contains("7002", (await db.ManualOutboundTasks.SingleAsync(x => x.ConnectionId == connection)).Note);
        Assert.Single(await db.ExternalIdMaps.Where(x => x.ConnectionId == connection && x.EntityType == "document").ToListAsync());
        simulator.AddDraftAsDocument(7002, 9002);
        fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Completed);
        await db.Entry(row).ReloadAsync();
        Assert.Null(row.ErpChangeMarker);
        Assert.Equal(2, await db.ExternalIdMaps.CountAsync(x => x.ConnectionId == connection && x.EntityType == "document"));
    }

    [DatabaseFact]
    public async Task Attachment_size_cap_rejects_before_uploading_and_mode_requires_recorded_server_actor()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>();
        var view = await service.GetAsync(connection, default);
        Assert.Equal("test-actor", view.Acceptance!.AcceptedBy);
        Assert.Equal(fixture.Clock.UtcNow, view.Acceptance.AcceptedAt);
        await service.SetAsync(connection, new("documents", view.Settings with { AttachmentSizeLimit = 1 }), "other-actor", default);
        var row = await StageAsync(scope, tenant, connection, Cx01DocumentScenarios.Command("attachment", "TOO-LARGE"));
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Parked);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(row).ReloadAsync();
        Assert.Equal(OutboundConnectorOutcome.Rejected, row.Outcome);
        Assert.Contains("size cap", row.LastFailure);
        Assert.Empty(simulator.DocumentCreateCounts);
        await service.SetAsync(connection, new("reference_only", view.Settings), "test-actor", default);
        var reference = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>().CreateAsync(new(connection,
            Guid.NewGuid(), nameof(CreateReplacementDelivery), "REF-ONLY", JsonSerializer.Serialize(Cx01DocumentScenarios.Command("delivery", "REF-ONLY"), IntegrationContracts.Json), null), default);
        Assert.Equal(OutboundConnectorOutcome.Unsupported, reference.Outcome);
        Assert.Empty(simulator.DocumentCreateCounts);
    }

    private async Task<(TenantId Tenant, Guid Connection)> ConnectionAsync(Cx01Simulator simulator, bool returns = false)
    {
        fixture.Clock.UtcNow = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);
        var tenant = await fixture.NewTenantAsync("cxdocs");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(simulator.BaseUri, "FICTIONAL_DOCUMENTS", "1000340"), default);
        var service = scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>();
        Assert.Equal("reference_only", (await service.GetAsync(connection, default)).OutboundMode);
        await Assert.ThrowsAsync<Cx01DocumentRuleException>(() => service.SetAsync(connection,
            new("documents", Cx01DocumentScenarios.Options), "test-actor", default));
        await service.SetAsync(connection, new("documents", Cx01DocumentScenarios.Options with { ServiceOrderAction = returns ? "return" : "service_call" }, "Fictional owner accepts stock and financial posting"), "test-actor", default);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("fictional-user", "not-logged"), IntegrationContracts.Json);
        await scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>().SaveAsync(await protector.EncryptAsync(tenant, connection,
            Cx01Client.CredentialPurpose, login, null, fixture.Clock.UtcNow.AddDays(30), default), default);
        await scope.ServiceProvider.GetRequiredService<Cx01SurveyService>().RunAsync(connection, new(), default);
        return (tenant, connection);
    }

    private async Task<OutboundCommandRow> StageAsync(IServiceScope scope, TenantId tenant, Guid connection, IOutboundCommand command)
    {
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = new OutboundCommandRow
        {
            TenantId = tenant, ConnectionId = connection, OutboundCommandId = Guid.CreateVersion7(), CommandType = command.GetType().Name,
            IdempotencyKey = command.IdempotencyKey, ModuleDocumentRef = command.ModuleDocumentRef, ErpReferenceText = command.ErpReferenceText,
            Payload = JsonSerializer.Serialize(command, command.GetType(), IntegrationContracts.Json), Status = OutboundCommandStatus.Pending, CreatedAt = fixture.Clock.UtcNow,
        };
        db.OutboundCommands.Add(row);
        await db.SaveChangesAsync();
        return row;
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class Cx01ApprovalDatabaseTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Survey_finds_approval_templates_and_bypassing_one_needs_recorded_acceptance()
    {
        await using var simulator = new Cx01Simulator { ReturnsApprovalTemplateActive = true };
        var (tenant, connection) = await ConnectionAsync(simulator, "cxappr");
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<Cx01ApprovalService>();

        var unknown = await service.GetAsync(connection, default);
        Assert.False(unknown.TemplatesKnown);
        Assert.All(unknown.DocumentTypes, x => Assert.Equal(Cx01ApprovalModes.DraftForReview, x.EffectiveMode));

        await SurveyAsync(scope, connection);
        var view = await service.GetAsync(connection, default);
        Assert.True(view.TemplatesKnown);
        var returns = Assert.Single(view.DocumentTypes, x => x.Collection == "Returns");
        Assert.Equal(["FICTIONAL_RETURNS"], returns.ActiveTemplates);
        Assert.Equal(Cx01ApprovalModes.DraftForReview, returns.EffectiveMode);
        Assert.Equal(Cx01ApprovalModes.PostDirectly,
            Assert.Single(view.DocumentTypes, x => x.Collection == "InventoryGenExits").EffectiveMode);

        var refused = await Assert.ThrowsAsync<Cx01ApprovalRuleException>(() => service.SetAsync(connection,
            new(new Dictionary<string, Cx01ApprovalHandlingInput?> { ["Returns"] = new(Cx01ApprovalModes.PostDirectly) }),
            "user-1", default));
        Assert.Equal("approval_bypass_needs_acceptance", refused.Rule);

        view = await service.SetAsync(connection, new(new Dictionary<string, Cx01ApprovalHandlingInput?>
        {
            ["Returns"] = new(Cx01ApprovalModes.PostDirectly, " Fictional finance lead agreed by email 2026-09-12 "),
            ["Invoices"] = new(Cx01ApprovalModes.DraftForReview),
        }), "user-1", default);
        returns = Assert.Single(view.DocumentTypes, x => x.Collection == "Returns");
        Assert.Equal(Cx01ApprovalModes.PostDirectly, returns.EffectiveMode);
        Assert.Equal("user-1", returns.Configured!.AcceptedBy);
        Assert.Equal(fixture.Clock.UtcNow, returns.Configured.AcceptedAt);
        Assert.Equal("Fictional finance lead agreed by email 2026-09-12", returns.Configured.Evidence);
        Assert.Equal(Cx01ApprovalModes.DraftForReview,
            Assert.Single(view.DocumentTypes, x => x.Collection == "Invoices").EffectiveMode);

        var unknownType = await Assert.ThrowsAsync<Cx01ApprovalRuleException>(() => service.SetAsync(connection,
            new(new Dictionary<string, Cx01ApprovalHandlingInput?> { ["JournalEntries"] = new(Cx01ApprovalModes.PostDirectly) }),
            "user-1", default));
        Assert.Equal("approval_unknown_document_type", unknownType.Rule);

        view = await service.SetAsync(connection, new(new Dictionary<string, Cx01ApprovalHandlingInput?>
        {
            ["Returns"] = null,
        }), "user-1", default);
        returns = Assert.Single(view.DocumentTypes, x => x.Collection == "Returns");
        Assert.Null(returns.Configured);
        Assert.Equal(Cx01ApprovalModes.DraftForReview, returns.EffectiveMode);
    }

    [DatabaseFact]
    public async Task Unreadable_templates_hold_documents_as_drafts()
    {
        await using var simulator = new Cx01Simulator { ApprovalTemplatesForbidden = true };
        var (tenant, connection) = await ConnectionAsync(simulator, "cxapprx");
        using var scope = fixture.ScopeFor(tenant);
        await SurveyAsync(scope, connection);
        var view = await scope.ServiceProvider.GetRequiredService<Cx01ApprovalService>().GetAsync(connection, default);
        Assert.False(view.TemplatesKnown);
        var returns = Assert.Single(view.DocumentTypes, x => x.Collection == "Returns");
        Assert.Equal(Cx01ApprovalModes.DraftForReview, returns.EffectiveMode);
        Assert.StartsWith("Approval templates are not known", returns.Warning, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Governed_document_is_held_as_one_draft_and_its_outcome_is_tracked()
    {
        await using var simulator = new Cx01Simulator { ReturnsApprovalTemplateActive = true };
        var (tenant, connection) = await ConnectionAsync(simulator, "cxdraft");
        using var scope = fixture.ScopeFor(tenant);
        await SurveyAsync(scope, connection);
        var poster = scope.ServiceProvider.GetRequiredService<Cx01DocumentPoster>();
        var tracker = scope.ServiceProvider.GetRequiredKeyedService<IOutboundReviewTracker>(
            Cx01Capabilities.ConnectorCode);
        var body = new Dictionary<string, object?> { ["CardCode"] = "C-FICTIONAL" };

        var held = await poster.PostAsync(connection, "Returns", body, "U_YojanaRma", "RMA-1", default);
        Assert.Equal(OutboundConnectorOutcome.AwaitingReview, held.Outcome);
        Assert.Equal(Cx01ReviewKey.Format("7001", "Returns", "U_YojanaRma"), held.PendingRemoteKey);
        Assert.Equal("oReturns", simulator.LastDraftBody!.RootElement.GetProperty("DocObjectCode").GetString());
        Assert.Equal("YOJANA:RMA-1", simulator.LastDraftBody.RootElement.GetProperty("U_YojanaRma").GetString());
        var again = await poster.PostAsync(connection, "Returns", body, "U_YojanaRma", "RMA-1", default);
        Assert.Equal(held.PendingRemoteKey, again.PendingRemoteKey);
        Assert.Equal(1, simulator.DraftCreateCount);

        var lookup = new OutboundReviewLookup(connection, "CreateServiceOrder", "RMA-1", held.PendingRemoteKey!);
        Assert.Equal(OutboundReviewStates.Pending, (await tracker.CheckAsync(lookup, default)).State);
        simulator.SetDraftAuthorization(7001, "dasRejected");
        Assert.Equal(OutboundReviewStates.Rejected, (await tracker.CheckAsync(lookup, default)).State);
        simulator.SetDraftAuthorization(7001, "dasPending");
        simulator.AddDraftAsDocument(7001, 9001);
        var posted = await tracker.CheckAsync(lookup, default);
        Assert.Equal(OutboundReviewStates.Posted, posted.State);
        Assert.Equal(new OutboundDocument("9001", null), posted.Document);

        var other = await poster.PostAsync(connection, "Returns", body, "U_YojanaRma", "RMA-2", default);
        simulator.DeleteDraft(7002);
        Assert.Equal(OutboundReviewStates.Withdrawn, (await tracker.CheckAsync(
            new(connection, "CreateServiceOrder", "RMA-2", other.PendingRemoteKey!), default)).State);
    }

    [DatabaseFact]
    public async Task Ungoverned_document_posts_directly()
    {
        await using var simulator = new Cx01Simulator();
        var (tenant, connection) = await ConnectionAsync(simulator, "cxdirect");
        using var scope = fixture.ScopeFor(tenant);
        await SurveyAsync(scope, connection);
        var created = await scope.ServiceProvider.GetRequiredService<Cx01DocumentPoster>().PostAsync(connection,
            "Returns", new Dictionary<string, object?> { ["CardCode"] = "C-FICTIONAL" }, "U_YojanaRma", "RMA-3",
            default);
        Assert.Equal(OutboundConnectorOutcome.Created, created.Outcome);
        Assert.Equal("5101", created.Document!.ErpExternalId);
        Assert.True(simulator.SameWarehouseReturnPosted);
        Assert.Equal(0, simulator.DraftCreateCount);
    }

    private async Task<(TenantId Tenant, Guid Connection)> ConnectionAsync(Cx01Simulator simulator, string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(simulator.BaseUri, "FICTIONAL_DEMO", "1000340"), CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose,
            login, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        return (tenant, connection);
    }

    private static async Task SurveyAsync(IServiceScope scope, Guid connection) =>
        _ = await scope.ServiceProvider.GetRequiredService<Cx01SurveyService>().RunAsync(connection, new(),
            CancellationToken.None);
}

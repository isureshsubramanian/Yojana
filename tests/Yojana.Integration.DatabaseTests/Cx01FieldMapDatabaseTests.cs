using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class Cx01FieldMapDatabaseTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Field_map_is_checked_against_the_survey_and_can_point_at_the_customers_own_field()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxfmap");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(simulator.BaseUri, "FICTIONAL_DEMO", "1000340"), CancellationToken.None);
        var service = scope.ServiceProvider.GetRequiredService<Cx01FieldMapService>();

        var unsurveyed = await service.GetAsync(connection, CancellationToken.None);
        Assert.Null(unsurveyed.SurveyObservedAt);
        Assert.All(unsurveyed.Fields.Where(x => x.Effective.Field is not null),
            x => Assert.StartsWith("No connection survey yet", x.Warning, StringComparison.Ordinal));

        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose,
            login, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        _ = await scope.ServiceProvider.GetRequiredService<Cx01SurveyService>().RunAsync(connection, new(),
            CancellationToken.None);

        var view = await service.GetAsync(connection, CancellationToken.None);
        var reason = Assert.Single(view.Fields, x => x.Code == Cx01FieldMap.ReturnReason);
        Assert.Equal("U_YojanaReturnReason", reason.Effective.Field);
        Assert.False(reason.Customised);
        Assert.Contains("U_RETURN_REASON", reason.SurveyedFields);
        Assert.StartsWith("Not found in the survey", reason.Warning, StringComparison.Ordinal);
        Assert.Null(Assert.Single(view.Fields, x => x.Code == Cx01FieldMap.ItemWarrantyTemplate).Warning);

        view = await service.SetAsync(connection, new(new Dictionary<string, Cx01FieldSource?>
        {
            [Cx01FieldMap.ReturnReason] = new(" U_RETURN_REASON ",
                ValueMap: new Dictionary<string, string> { ["DOA"] = "dead_on_arrival" }),
            [Cx01FieldMap.DeliveryDropShip] = new(null),
        }), CancellationToken.None);
        reason = Assert.Single(view.Fields, x => x.Code == Cx01FieldMap.ReturnReason);
        Assert.Equal("U_RETURN_REASON", reason.Effective.Field);
        Assert.True(reason.Customised);
        Assert.Null(reason.Warning);
        var dropShip = Assert.Single(view.Fields, x => x.Code == Cx01FieldMap.DeliveryDropShip);
        Assert.Null(dropShip.Effective.Field);
        Assert.Null(dropShip.Warning);
        Assert.Equal("dead_on_arrival", (await scope.ServiceProvider.GetRequiredService<Cx01Settings>()
            .GetAsync(connection, CancellationToken.None)).FieldMap![Cx01FieldMap.ReturnReason].ValueMap!["DOA"]);

        view = await service.SetAsync(connection, new(new Dictionary<string, Cx01FieldSource?>
        {
            [Cx01FieldMap.ReturnReason] = null,
        }), CancellationToken.None);
        Assert.False(Assert.Single(view.Fields, x => x.Code == Cx01FieldMap.ReturnReason).Customised);
        Assert.True(Assert.Single(view.Fields, x => x.Code == Cx01FieldMap.DeliveryDropShip).Customised);

        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, (await settings.GetAsync(connection, CancellationToken.None)) with
        {
            MandatoryUserFieldDefaults = new Dictionary<string, string> { ["ORDN.U_RetCause"] = "Other" },
        }, CancellationToken.None);
        Assert.Equal("ORDN.U_RetCause", Assert.Single((await settings.GetAsync(connection, CancellationToken.None))
            .MandatoryUserFieldDefaults!).Key);

        var unknown = await Assert.ThrowsAsync<Cx01FieldMapRuleException>(() => service.SetAsync(connection,
            new(new Dictionary<string, Cx01FieldSource?> { ["party.colour"] = new("U_X") }), CancellationToken.None));
        Assert.Equal("field_map_unknown_attribute", unknown.Rule);
        var invalid = await Assert.ThrowsAsync<Cx01FieldMapRuleException>(() => service.SetAsync(connection,
            new(new Dictionary<string, Cx01FieldSource?> { [Cx01FieldMap.ReturnReason] = new("U_A B") }),
            CancellationToken.None));
        Assert.Equal("field_map_invalid", invalid.Rule);
    }
}

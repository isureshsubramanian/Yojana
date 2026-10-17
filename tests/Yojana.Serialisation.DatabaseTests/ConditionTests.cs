using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Condition;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T09/T11: immutable condition, sanitisation, test, usage and attribute histories.</summary>
[Collection("serialisation host")]
public sealed class ConditionTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("10000000-0000-0000-0000-000000000005");
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<(Guid UnitId, Guid ItemId, string Serial)> UnitAsync(TenantId tenant, string marker, Guid? itemId = null)
    {
        var item = itemId ?? Guid.NewGuid();
        var serial = marker + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At, "receiving", Entity,
            LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return (result.Lines[0].UnitId!.Value, item, serial);
    }

    [DatabaseFact]
    public async Task Grade_is_controlled_ledger_backed_queryable_disclosable_published_and_replayable()
    {
        var tenant = await fixture.NewTenantAsync("grade");
        var unit = await UnitAsync(tenant, "GRD");
        var commandId = Guid.NewGuid();
        var command = new GradeUnit(commandId, unit.UnitId, ConditionGrades.RefurbishedA, "graded_receipt_40pct",
            At.AddHours(1), "quality.inspector", Entity, "F2", "C3");

        var first = await InvokeAsync<GradeUnitResult>(tenant, command);
        var replay = await InvokeAsync<GradeUnitResult>(tenant, command);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal((first.ConditionEventId, first.EventId), (replay.ConditionEventId, replay.EventId));
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<UnitGraded>(e => e.Header.EventId == first.EventId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((ConditionGrades.RefurbishedA, "F2", "C3", "graded_receipt_40pct"),
            (published.Grade, published.FCode, published.CCode, published.ValuationRule));
        Assert.Equal((UnitGraded.Schema, UnitGraded.Version, first.EventId, unit.UnitId, Entity, "quality.inspector"),
            (published.Header.Schema, published.Header.Version, published.Header.EventId, published.Header.UnitId,
                published.Header.LegalEntityId, published.Header.ActorRef));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(ConditionGrades.RefurbishedA, (await db.Current.SingleAsync(c => c.UnitId == unit.UnitId)).ConditionGrade);
        Assert.Single(await db.ConditionEvents.Where(e => e.UnitId == unit.UnitId).ToListAsync());
        var queries = scope.ServiceProvider.GetRequiredService<ConditionQueries>();
        Assert.True((await queries.SatisfiesAsync(unit.UnitId, ConditionRequirement.Refurbished, CancellationToken.None)).Satisfied);
        Assert.False((await queries.SatisfiesAsync(unit.UnitId, ConditionGrades.New, CancellationToken.None)).Satisfied);
        var disclosure = await queries.DisclosureAsync(unit.UnitId, new(true, Guid.NewGuid(), "12-month refurbisher warranty"), CancellationToken.None);
        Assert.Equal((true, "refurbished", "12-month refurbisher warranty"), (disclosure!.Disclosed, disclosure.ConditionTerm, disclosure.WarrantyStatement));

        using var schema = System.Text.Json.JsonDocument.Parse(PublishedEventSchemas.Read(UnitGraded.Schema, UnitGraded.Version));
        Assert.Equal(UnitGraded.Schema, schema.RootElement.GetProperty("title").GetString());
    }

    [DatabaseFact]
    public async Task Grade_test_sanitisation_and_attribute_inputs_have_typed_refusals()
    {
        var tenant = await fixture.NewTenantAsync("condition-refusal");
        var unit = await UnitAsync(tenant, "REF");

        var grade = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<GradeUnitResult>(tenant,
            new GradeUnit(Guid.NewGuid(), unit.UnitId, "excellent", "full", At.AddHours(1), "quality", Entity)));
        Assert.Equal("condition_grade_unknown", grade.Rule);
        var r2 = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<GradeUnitResult>(tenant,
            new GradeUnit(Guid.NewGuid(), unit.UnitId, ConditionGrades.Used, "used_50", At.AddHours(1), "quality", Entity, "F9")));
        Assert.Equal("r2_f_code_invalid", r2.Rule);
        var sanitisation = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<RecordSanitisationResult>(tenant,
            Sanitisation(unit.UnitId, Guid.NewGuid()) with { Method = "format" }));
        Assert.Equal("sanitisation_method_unknown", sanitisation.Rule);
        var test = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<RecordTestResult>(tenant,
            new RecordTest(Guid.NewGuid(), unit.UnitId, "power-on", "maybe", "{}", "bench-7", At.AddHours(2), "quality", Entity)));
        Assert.Equal("test_result_unknown", test.Rule);
        var attributes = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<RecordUnitAttributesResult>(tenant,
            new RecordUnitAttributes(Guid.NewGuid(), unit.UnitId, new Dictionary<string, string?> { ["firmware"] = new string('x', 2001) }, At.AddHours(2), "technician", Entity)));
        Assert.Equal("unit_attribute_value_invalid", attributes.Rule);
    }

    [DatabaseFact]
    public async Task Sanitisation_tests_and_existing_attribute_command_keep_immutable_history()
    {
        var tenant = await fixture.NewTenantAsync("service-history");
        var unit = await UnitAsync(tenant, "HIS");

        var sanitisationCommand = Sanitisation(unit.UnitId, Guid.NewGuid());
        var sanitised = await InvokeAsync<RecordSanitisationResult>(tenant, sanitisationCommand);
        Assert.True((await InvokeAsync<RecordSanitisationResult>(tenant, sanitisationCommand)).Replayed);
        var testCommand = new RecordTest(Guid.NewGuid(), unit.UnitId, "key-functions", UnitTestResult.Pass,
            "{\"voltage\":12.4}", "fixture-12", At.AddHours(3), "quality.operator", Entity);
        var tested = await InvokeAsync<RecordTestResult>(tenant, testCommand);
        Assert.True((await InvokeAsync<RecordTestResult>(tenant, testCommand)).Replayed);
        var attributeCommand = new RecordUnitAttributes(Guid.NewGuid(), unit.UnitId,
            new Dictionary<string, string?> { ["firmware"] = "2.4.1", ["calibration"] = "2026-08" }, At.AddHours(4), "service.technician", Entity);
        var attributes = await InvokeAsync<RecordUnitAttributesResult>(tenant, attributeCommand);
        Assert.True((await InvokeAsync<RecordUnitAttributesResult>(tenant, attributeCommand)).Replayed);
        var updatedAttributes = await InvokeAsync<RecordUnitAttributesResult>(tenant, new RecordUnitAttributes(Guid.NewGuid(), unit.UnitId,
            new Dictionary<string, string?> { ["firmware"] = "2.4.2" }, At.AddHours(5), "service.technician", Entity));

        using var scope = fixture.ScopeFor(tenant);
        var queries = scope.ServiceProvider.GetRequiredService<ConditionQueries>();
        var wipe = Assert.Single(await queries.SanitisationAsync(unit.UnitId, CancellationToken.None));
        Assert.Equal((sanitised.SanitisationRecordId, SanitisationMethod.Purge, "NIST-CERT-448"), (wipe.SanitisationRecordId, wipe.Method, wipe.CertificateRef));
        var test = Assert.Single(await queries.TestsAsync(unit.UnitId, CancellationToken.None));
        Assert.Equal((tested.TestRecordId, UnitTestResult.Pass, "fixture-12"), (test.TestRecordId, test.Result, test.Equipment));
        var history = await queries.AttributesAsync(unit.UnitId, CancellationToken.None);
        Assert.Equal(3, history.Count);
        Assert.Equal(["2.4.2", "2.4.1"], history.Where(h => h.Key == "firmware").Select(h => h.Value).ToList());
        Assert.False(attributes.Replayed);
        Assert.False(updatedAttributes.Replayed);
    }

    [DatabaseFact]
    public async Task Usage_is_monotonic_per_meter_unless_the_meter_was_replaced_and_replays_once()
    {
        var tenant = await fixture.NewTenantAsync("usage");
        var unit = await UnitAsync(tenant, "USE");
        var firstId = Guid.NewGuid();
        var first = new RecordUsageReading(firstId, unit.UnitId, "runtime", 125.5m, "hours", At.AddHours(1), "technician", Entity);
        var recorded = await InvokeAsync<RecordUsageReadingResult>(tenant, first);
        var replay = await InvokeAsync<RecordUsageReadingResult>(tenant, first);
        Assert.True(replay.Replayed);
        Assert.Equal(recorded.UsageReadingId, replay.UsageReadingId);

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<RecordUsageReadingResult>(tenant,
            new RecordUsageReading(Guid.NewGuid(), unit.UnitId, "runtime", 12m, "hours", At.AddHours(2), "technician", Entity)));
        Assert.Equal("usage_not_monotonic", refusal.Rule);
        await InvokeAsync<RecordUsageReadingResult>(tenant,
            new RecordUsageReading(Guid.NewGuid(), unit.UnitId, "runtime", 12m, "hours", At.AddHours(2), "technician", Entity, MeterReplacement: true));

        using var scope = fixture.ScopeFor(tenant);
        var rows = await scope.ServiceProvider.GetRequiredService<ConditionQueries>().UsageAsync(unit.UnitId, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].MeterReplacement);
    }

    [DatabaseFact]
    public async Task New_unit_of_a_flagged_item_class_ships_without_a_sanitisation_record()
    {
        var tenant = await fixture.NewTenantAsync("sanitize-new");
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { RequiresSanitisation = true, ItemClass = "data_bearing_device" });
        var unit = await UnitAsync(tenant, "NEW", item);

        var shipped = await ShipAsync(tenant, unit, At.AddDays(1));

        Assert.Equal(1, shipped.UnitsAffected);
        Assert.Empty(shipped.Exceptions);
    }

    [DatabaseFact]
    public async Task Returned_unit_of_a_flagged_item_class_is_refused_without_sanitisation()
    {
        var tenant = await fixture.NewTenantAsync("sanitize-return");
        var item = RegisterSanitisationItem();
        var unit = await ReturnedUnitAsync(tenant, "RET", item);

        var blocked = await ShipAsync(tenant, unit, At.AddDays(3));

        Assert.Equal(0, blocked.UnitsAffected);
        Assert.Contains(blocked.Exceptions, e => e.Contains("sanitisation_required", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task Returned_unit_ships_when_sanitised_after_the_return()
    {
        var tenant = await fixture.NewTenantAsync("sanitize-after");
        var item = RegisterSanitisationItem();
        var unit = await ReturnedUnitAsync(tenant, "AFT", item);
        await InvokeAsync<RecordSanitisationResult>(tenant, Sanitisation(unit.UnitId, Guid.NewGuid()) with { EventTime = At.AddDays(2).AddHours(1) });

        var shipped = await ShipAsync(tenant, unit, At.AddDays(3));

        Assert.Equal(1, shipped.UnitsAffected);
        Assert.Empty(shipped.Exceptions);
    }

    [DatabaseFact]
    public async Task Sanitisation_before_the_return_does_not_allow_a_flagged_unit_to_ship()
    {
        var tenant = await fixture.NewTenantAsync("sanitize-before");
        var item = RegisterSanitisationItem();
        var unit = await UnitAsync(tenant, "BEF", item);
        await InvokeAsync<RecordSanitisationResult>(tenant, Sanitisation(unit.UnitId, Guid.NewGuid()) with { EventTime = At.AddHours(12) });
        await RecordExternalOwnershipCycleAsync(tenant, unit.UnitId);

        var blocked = await ShipAsync(tenant, unit, At.AddDays(3));

        Assert.Equal(0, blocked.UnitsAffected);
        Assert.Contains(blocked.Exceptions, e => e.Contains("sanitisation_required", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task Life_episode_writer_opens_and_closes_the_kinds_reserved_for_refurbishment()
    {
        var tenant = await fixture.NewTenantAsync("life-episode");
        var unit = await UnitAsync(tenant, "LIFE");
        using var scope = fixture.ScopeFor(tenant);
        var writer = scope.ServiceProvider.GetRequiredService<ILifeEpisodeWriter>();
        var episodeId = await writer.OpenAsync(new OpenLifeEpisode(unit.UnitId, LifeEpisodeKind.Rework, At.AddHours(1), "quality", Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        await writer.CloseAsync(new CloseLifeEpisode(episodeId, At.AddHours(2), Guid.NewGuid()), CancellationToken.None);

        var row = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().LifeEpisodes.SingleAsync(e => e.LifeEpisodeId == episodeId);
        Assert.Equal((LifeEpisodeKind.Rework, At.AddHours(2)), (row.Kind, row.EndedAt));
    }

    private static RecordSanitisation Sanitisation(Guid unitId, Guid commandId) => new(commandId, unitId,
        "Fictional Devices Ltd", "Archive Node 4", "MEDIA-7781", "solid_state", SanitisationMethod.Purge,
        "EraseRig", "cryptographic erase plus read verification", "NIST-CERT-448", At.AddHours(2), "quality.operator", Entity,
        ToolVersion: "5.2", SignatureRef: "SIG-448");

    private Guid RegisterSanitisationItem()
    {
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with
        {
            RequiresSanitisation = true,
            ItemClass = "data_bearing_device",
        });
        return item;
    }

    private async Task<(Guid UnitId, Guid ItemId, string Serial)> ReturnedUnitAsync(TenantId tenant, string marker, Guid itemId)
    {
        var unit = await UnitAsync(tenant, marker, itemId);
        await RecordExternalOwnershipCycleAsync(tenant, unit.UnitId);
        return unit;
    }

    private async Task RecordExternalOwnershipCycleAsync(TenantId tenant, Guid unitId)
    {
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unitId, Guid.NewGuid(), PartyType.Customer,
            EpisodeReason.Sale, At.AddDays(1), "sales-test", Entity));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unitId, Entity, PartyType.Us,
            EpisodeReason.Return, At.AddDays(2), "returns-test", Entity));
    }

    private async Task<ConsumedEventResult> ShipAsync(TenantId tenant, (Guid UnitId, Guid ItemId, string Serial) unit, DateTime shippedAt)
    {
        var shipment = new ShipmentDocument(new DocumentRef("delivery", Guid.NewGuid()), Guid.NewGuid(), shippedAt,
            [new DocumentLine(1, unit.ItemId, 1, [new DocumentSerial(unit.Serial)])]);
        fixture.Services.GetRequiredService<InMemoryDocumentPorts>().Add(shipment);
        return await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(new ThinEnvelope(Guid.NewGuid(), Entity, shipment.PostedAt, "sales-test"), shipment.Document));
    }
}

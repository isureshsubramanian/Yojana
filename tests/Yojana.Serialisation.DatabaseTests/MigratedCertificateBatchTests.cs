using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>
/// P9-T10c (OD-55): a migration chunk records its version-one release records in one batched issue. Each certificate must
/// be the one the single-unit path would have recorded, and anything outside the batched case keeps the single-unit rules.
/// </summary>
[Collection("serialisation host")]
public sealed class MigratedCertificateBatchTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime Received = new(2024, 3, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Shipped = new(2024, 5, 20, 14, 30, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        return await bus.InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static MigratedCertificate Certificate(string evidence) => new(Guid.NewGuid(), 1,
        CertificateProvenance.Migrated, ["The source did not retain the as-built production record."], [evidence]);

    /// <summary>A mixed chunk: shipped servers and switches, warehouse stock, used and defective units, and a replaced pair.</summary>
    private static List<MigratedUnit> MixedChunk(Guid server, Guid networkSwitch, string prefix)
    {
        MigratedUnit Shipped(Guid item, string serial, string grade, bool manufacturerSerial) => new(
            Guid.CreateVersion7(), item, HistoryDepth.Full, Received,
            manufacturerSerial
                ? [new IdentifierInput(IdentifierType.InternalSerial, serial), new IdentifierInput(IdentifierType.ManufacturerSerial, "MF-" + serial)]
                : [new IdentifierInput(IdentifierType.InternalSerial, serial)],
            [
                new MigratedHistoryEvent(Received, "received", "source", "Goods receipt", 4101, "V-ARVEN", "WH-01", 1),
                new MigratedHistoryEvent(MigratedCertificateBatchTests.Shipped, "shipped", "source", "Delivery", 7702, "C-NANDIRA", "WH-01", -1),
            ],
            null, new MigratedOwnership(Guid.NewGuid(), PartyType.Customer, MigratedCertificateBatchTests.Shipped, false), grade, "inferred",
            Certificate: Certificate("delivery " + serial));
        MigratedUnit Stock(Guid item, string serial) => new(
            Guid.CreateVersion7(), item, HistoryDepth.OpeningOnly, Received,
            [new IdentifierInput(IdentifierType.InternalSerial, serial)],
            [new MigratedHistoryEvent(Received, "opening_balance", "reconstructed", null, null, null, "WH-02", 1)],
            new MigratedCustody(Guid.NewGuid(), LocationKind.Warehouse, Received), null, ConditionGrades.New, "inferred",
            Certificate: Certificate("opening balance " + serial));

        var replaced = Shipped(server, prefix + "-R1", ConditionGrades.Defective, true);
        var replacement = Shipped(server, prefix + "-R2", ConditionGrades.New, true) with
        {
            ReplacementLinks = [new MigratedReplacementLink(Guid.NewGuid(), Guid.Empty, Guid.Empty, "unknown", "SWAP-1",
                ReplacementDirection.CustomerFacing, ReplacementReason.WarrantyDefect, ReplacementLinkState.ClosedReturned,
                ReplacementConditionAllowed.Any, ReplacementPricePolicy.Zero, 0m, "XXX")],
        };
        replacement = replacement with
        {
            ReplacementLinks = [replacement.ReplacementLinks![0] with
            {
                ReplacedPlanUnitId = replaced.PlanUnitId, ReplacementPlanUnitId = replacement.PlanUnitId,
            }],
        };
        return
        [
            Shipped(server, prefix + "-S1", ConditionGrades.New, true),
            Shipped(server, prefix + "-S2", ConditionGrades.Used, false),
            Shipped(networkSwitch, prefix + "-N1", ConditionGrades.New, false),
            Stock(networkSwitch, prefix + "-N2"),
            Stock(server, prefix + "-S3"),
            replaced,
            replacement,
        ];
    }

    [DatabaseFact]
    public async Task Each_batched_certificate_carries_the_facts_the_single_unit_path_builds_and_numbers_are_consecutive()
    {
        var tenant = await fixture.NewTenantAsync("mgcertbatch");
        var prefix = "MC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var chunk = MixedChunk(Guid.NewGuid(), Guid.NewGuid(), prefix);
        var load = Guid.NewGuid();

        var result = await InvokeAsync<ApplyMigratedUnitsResult>(tenant,
            new ApplyMigratedUnits(load, Entity, "user:migration-lead", "migration:test", chunk, new(2026, 9, 1)));
        Assert.Equal((chunk.Count, 0, chunk.Count), (result.Applied, result.Refused, result.CertificatesRecorded));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var units = scope.ServiceProvider.GetRequiredService<ICertificateUnitReadPort>();
        var payloads = scope.ServiceProvider.GetRequiredService<CertificatePayloadBuilder>();
        var stored = await db.CertificateInstances.AsNoTracking().Where(row => row.MigrationLoadId == load).ToListAsync();
        Assert.Equal(chunk.Count, stored.Count);
        Assert.All(stored, row => Assert.Equal((1, CertificateProvenance.Migrated, CertificateDocumentKind.ReleaseRecord),
            (row.Version, row.Provenance, row.DocumentKind)));

        foreach (var certificate in stored)
        {
            // The single-unit build for the same command; facts exclude the trigger, so only facts are compared.
            var unit = await units.ReadAsync(certificate.UnitId, CancellationToken.None);
            var command = new IssueCertificateVersion(certificate.TriggeringEventId, certificate.UnitId,
                CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, null, certificate.IssuedAt,
                "user:migration-lead", certificate.IssuerEntityId, CertificateProvenance.Migrated,
                System.Text.Json.JsonSerializer.Deserialize<List<string>>(certificate.Unknowns),
                System.Text.Json.JsonSerializer.Deserialize<List<string>>(certificate.Evidence),
                certificate.Language, "Migration.PlanCertificate", load);
            var single = await payloads.BuildAsync(command, unit!, certificate.TemplateVersionId, CancellationToken.None);
            Assert.Equal(certificate.FactsHash, CertificateVersioningEngine.FactsHash(single.CanonicalPayload));
        }

        var numbers = stored.Select(row => int.Parse(row.TrackingNumber[(row.TrackingNumber.LastIndexOf('-') + 1)..],
            System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        Assert.Equal(Enumerable.Range(numbers[0], numbers.Count), numbers);
        Assert.Equal(chunk.Count, await db.Events.CountAsync(row => row.EventType == CertificateEventType.Issued
            && stored.Select(certificate => certificate.UnitId).Contains(row.UnitId)));
    }

    [DatabaseFact]
    public async Task A_unit_that_already_has_a_chain_keeps_the_single_unit_rules_replay_and_supersede_reason()
    {
        var tenant = await fixture.NewTenantAsync("mgcertchain");
        var prefix = "MC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var chunk = MixedChunk(Guid.NewGuid(), Guid.NewGuid(), prefix).Take(2).ToList();
        var load = Guid.NewGuid();
        await InvokeAsync<ApplyMigratedUnitsResult>(tenant,
            new ApplyMigratedUnits(load, Entity, "user:migration-lead", "migration:test", chunk, new(2026, 9, 1)));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<CertificateVersioningEngine>();
        var stored = await db.CertificateInstances.AsNoTracking().Where(row => row.MigrationLoadId == load)
            .OrderBy(row => row.TrackingNumber).ToListAsync();
        IssueCertificateVersion Again(Guid triggeringEventId, Guid unitId) => new(triggeringEventId, unitId,
            CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, null, Shipped, "user:migration-lead",
            Entity, CertificateProvenance.Migrated, ["The source did not retain the as-built production record."],
            ["resend"], MigrationLoadId: load);

        var results = await engine.IssueMigratedReleaseRecordsAsync(
            [Again(stored[0].TriggeringEventId, stored[0].UnitId), Again(Guid.NewGuid(), stored[1].UnitId)],
            CancellationToken.None);

        Assert.True(results[0].Replayed);
        Assert.Equal(stored[0].CertificateId, results[0].CertificateId);
        Assert.False(results[1].Issued);
        Assert.Equal("certificate_supersede_reason_required", results[1].RefusalRule);
        Assert.Equal(2, await db.CertificateInstances.CountAsync(row => row.MigrationLoadId == load));
    }
}

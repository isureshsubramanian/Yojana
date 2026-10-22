using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P9-T10 (OD-52, OD-53): ApplyMigratedUnits writes a load plan chunk set-based; RollBackMigrationLoad undoes an untouched load.</summary>
[Collection("serialisation host")]
public sealed class MigrationLoadTests(SerialisationHostFixture fixture)
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

    private static string Prefix() => "MG" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    /// <summary>A rack server shipped to a customer, with a manufacturer serial beside the incumbent's serial.</summary>
    private static MigratedUnit ShippedServer(Guid item, string serial, Guid customer) => new(
        Guid.CreateVersion7(), item, HistoryDepth.Full, Received,
        [new IdentifierInput(IdentifierType.InternalSerial, serial), new IdentifierInput(IdentifierType.ManufacturerSerial, "MF-" + serial)],
        [
            new MigratedHistoryEvent(Received, "received", "source", "Goods receipt", 4101, "V-ARVEN", "WH-01", 1),
            new MigratedHistoryEvent(Shipped, "shipped", "source", "Delivery", 7702, "C-NANDIRA", "WH-01", -1),
        ],
        null, new MigratedOwnership(customer, PartyType.Customer, Shipped, false), ConditionGrades.New, "inferred",
        Certificate: Certificate("source history and identifiers"));

    /// <summary>A storage shelf still in the warehouse at cutover, with only an opening balance.</summary>
    private static MigratedUnit StockShelf(Guid item, string serial, Guid warehouse) => new(
        Guid.CreateVersion7(), item, HistoryDepth.OpeningOnly, Received,
        [new IdentifierInput(IdentifierType.InternalSerial, serial)],
        [new MigratedHistoryEvent(Received, "opening_balance", "reconstructed", null, null, null, "WH-02", 1)],
        new MigratedCustody(warehouse, LocationKind.Warehouse, Received), null, ConditionGrades.New, "inferred",
        Certificate: Certificate("source opening balance and identifier"));

    private static ApplyMigratedUnits Chunk(Guid load, params MigratedUnit[] units) =>
        new(load, Entity, "user:migration-lead", "migration:test", units);

    [DatabaseFact]
    public async Task Apply_writes_migrated_units_with_history_custody_ownership_and_current_state_and_a_resend_writes_nothing()
    {
        var tenant = await fixture.NewTenantAsync("mg1");
        var item = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var prefix = Prefix();
        var server = ShippedServer(item, prefix + "-001", customer);
        var shelf = StockShelf(item, prefix + "-002", warehouse);
        var load = Guid.NewGuid();

        var result = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(load, server, shelf));
        Assert.Equal(2, result.Applied);
        Assert.Equal(0, result.Refused);
        // Each unit adds one explicit migrated certificate fact to its unit/history facts.
        Assert.Equal(9, result.EventsWritten);
        Assert.Equal(2, result.CertificatesRecorded);

        var serverUnit = result.Units.Single(u => u.PlanUnitId == server.PlanUnitId).UnitId!.Value;
        var shelfUnit = result.Units.Single(u => u.PlanUnitId == shelf.PlanUnitId).UnitId!.Value;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var units = await db.Units.Where(u => u.UnitId == serverUnit || u.UnitId == shelfUnit).ToListAsync();
            Assert.All(units, u => Assert.Equal(UnitOrigin.Migrated, u.Origin));
            Assert.Equal(HistoryDepth.OpeningOnly, units.Single(u => u.UnitId == shelfUnit).HistoryDepth);
            Assert.Equal(Received, units.Single(u => u.UnitId == serverUnit).CreatedEventTime);
            Assert.Equal(3, await db.Identifiers.CountAsync(i => i.UnitId == serverUnit || i.UnitId == shelfUnit));

            var events = await db.Events.Where(e => e.UnitId == serverUnit).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync();
            Assert.Equal([UnitEventType.Commissioned, UnitEventType.MigratedHistory, UnitEventType.MigratedHistory,
                UnitEventType.OwnershipOpened, CertificateEventType.Issued], events);

            var owner = await db.Episodes.SingleAsync(e => e.UnitId == serverUnit && e.ValidTo == null);
            Assert.Equal(EpisodeKind.Ownership, owner.Kind);
            Assert.Equal(customer, owner.PartyRef);
            Assert.Equal(Shipped, owner.ValidFrom);
            Assert.Equal(EpisodeSource.Migration, owner.Source);
            var custody = await db.Episodes.SingleAsync(e => e.UnitId == shelfUnit && e.ValidTo == null);
            Assert.Equal(EpisodeKind.Custody, custody.Kind);
            Assert.Equal(warehouse, custody.LocationId);

            var current = await db.Current.Where(c => c.UnitId == serverUnit || c.UnitId == shelfUnit).ToListAsync();
            Assert.Equal(customer, current.Single(c => c.UnitId == serverUnit).OwnerPartyRef);
            Assert.Equal(warehouse, current.Single(c => c.UnitId == shelfUnit).LocationId);
            Assert.All(current, c => Assert.Equal(ConditionGrades.New, c.ConditionGrade));
            Assert.All(current, c => Assert.Equal(LifecycleStatus.Released, c.LifecycleStatus));

            // OD-52 suppresses the live ownership observer; the sole receipt is the explicit migrated v1 instance.
            Assert.Equal(1, await db.CertificateTriggers.CountAsync(t => t.UnitId == serverUnit));
            Assert.Equal(CertificateProvenance.Migrated, await db.CertificateInstances
                .Where(row => row.UnitId == serverUnit).Select(row => row.Provenance).SingleAsync());
            Assert.Equal(2, await db.MigrationLoadUnits.CountAsync(r => r.LoadId == load));
        }

        var resend = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(load, server, shelf));
        Assert.Equal(2, resend.AlreadyApplied);
        Assert.Equal(0, resend.EventsWritten);
        Assert.Equal(0, resend.CertificatesRecorded);
        Assert.Equal(serverUnit, resend.Units.Single(u => u.PlanUnitId == server.PlanUnitId).UnitId);
    }

    [DatabaseFact]
    public async Task Apply_refuses_units_whose_identifier_is_open_elsewhere_repeated_in_the_chunk_or_carrying_an_unknown_grade()
    {
        var tenant = await fixture.NewTenantAsync("mg2");
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var first = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(Guid.NewGuid(), StockShelf(item, prefix + "-010", Guid.NewGuid())));
        Assert.Equal(1, first.Applied);

        var again = StockShelf(item, prefix + "-010", Guid.NewGuid());
        var twinA = StockShelf(item, prefix + "-011", Guid.NewGuid());
        var twinB = StockShelf(item, prefix + "-011", Guid.NewGuid());
        var badGrade = StockShelf(item, prefix + "-012", Guid.NewGuid()) with { ConditionGrade = "like_new_ish" };
        var good = StockShelf(item, prefix + "-013", Guid.NewGuid());
        var result = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(Guid.NewGuid(), again, twinA, twinB, badGrade, good));

        Assert.Equal(1, result.Applied);
        Assert.Equal(4, result.Refused);
        Assert.Contains(result.Units.Single(u => u.PlanUnitId == again.PlanUnitId).Reasons, r => r.EndsWith("already_open_on_another_unit", StringComparison.Ordinal));
        Assert.Contains(result.Units.Single(u => u.PlanUnitId == twinA.PlanUnitId).Reasons, r => r.EndsWith("duplicate_in_batch", StringComparison.Ordinal));
        Assert.Contains("condition_grade_unknown", result.Units.Single(u => u.PlanUnitId == badGrade.PlanUnitId).Reasons);
        Assert.Equal(MigratedUnitOutcome.Applied, result.Units.Single(u => u.PlanUnitId == good.PlanUnitId).Outcome);
    }

    [DatabaseFact]
    public async Task Rollback_removes_an_untouched_load_and_refuses_one_with_a_later_fact()
    {
        var tenant = await fixture.NewTenantAsync("mg3");
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var load = Guid.NewGuid();
        var applied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(load,
            ShippedServer(item, prefix + "-020", Guid.NewGuid()), StockShelf(item, prefix + "-021", Guid.NewGuid())));
        var unitIds = applied.Units.Select(u => u.UnitId!.Value).ToList();

        var rolledBack = await InvokeAsync<RollBackMigrationLoadResult>(tenant, new RollBackMigrationLoad(load, "user:admin"));
        Assert.Equal(RollBackMigrationLoadResult.RolledBack, rolledBack.Outcome);
        Assert.Equal(2, rolledBack.UnitsRemoved);
        Assert.Equal(9, rolledBack.EventsRemoved);
        Assert.Equal(2, rolledBack.CertificatesRemoved);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(0, await db.Units.CountAsync(u => unitIds.Contains(u.UnitId)));
            Assert.Equal(0, await db.Identifiers.CountAsync(i => unitIds.Contains(i.UnitId)));
            Assert.Equal(0, await db.Episodes.CountAsync(e => unitIds.Contains(e.UnitId)));
            Assert.Equal(0, await db.Current.CountAsync(c => unitIds.Contains(c.UnitId)));
            Assert.Equal(0, await db.Events.CountAsync(e => unitIds.Contains(e.UnitId)));
            Assert.Equal(0, await db.MigrationLoadUnits.CountAsync(r => r.LoadId == load));
        }

        // The serials are free again: the same plan loads afresh after a rollback.
        var reload = Guid.NewGuid();
        var reapplied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(reload, StockShelf(item, prefix + "-021", Guid.NewGuid())));
        Assert.Equal(1, reapplied.Applied);

        var touchedUnit = reapplied.Units.Single().UnitId!.Value;
        await InvokeAsync<UnitFlagResult>(tenant, new SetUnitFlag(Guid.NewGuid(), touchedUnit, OverlayFlag.Lost, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), "stores", Entity, "missing at cycle count"));
        var refused = await InvokeAsync<RollBackMigrationLoadResult>(tenant, new RollBackMigrationLoad(reload, "user:admin"));
        Assert.Equal(RollBackMigrationLoadResult.RefusedTouched, refused.Outcome);
        Assert.Equal([touchedUnit], refused.TouchedUnitIds);
        Assert.Equal(0, refused.UnitsRemoved);

        var nothing = await InvokeAsync<RollBackMigrationLoadResult>(tenant, new RollBackMigrationLoad(Guid.NewGuid(), "user:admin"));
        Assert.Equal(RollBackMigrationLoadResult.NothingToRemove, nothing.Outcome);
    }

    [DatabaseFact]
    public async Task Load_units_are_listed_in_plan_order_with_a_keyset_cursor()
    {
        var tenant = await fixture.NewTenantAsync("mg4");
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var load = Guid.NewGuid();
        await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(load,
            StockShelf(item, prefix + "-030", Guid.NewGuid()), StockShelf(item, prefix + "-031", Guid.NewGuid()), StockShelf(item, prefix + "-032", Guid.NewGuid())));

        var first = await InvokeAsync<MigrationLoadUnitsPage>(tenant, new MigrationLoadUnitsQuery(load, null, 2));
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextAfterPlanUnitId);
        var second = await InvokeAsync<MigrationLoadUnitsPage>(tenant, new MigrationLoadUnitsQuery(load, first.NextAfterPlanUnitId, 2));
        Assert.Single(second.Items);
        Assert.Null(second.NextAfterPlanUnitId);
    }

    [DatabaseFact]
    public async Task Cover_keeps_source_dates_and_all_evidence_stacks_add_ons_and_updates_current_deviation_and_certificates()
    {
        var tenant = await fixture.NewTenantAsync("mgcover");
        var policies = await CoverPoliciesAsync(tenant);
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var customer = Guid.NewGuid();
        var covered = ShippedServer(item, prefix + "-100", customer) with
        {
            Entitlements =
            [
                Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base, new(2024, 5, 20), new(2028, 5, 19), null,
                    EntitlementDirection.Outbound, "contract_line", "CONTRACT-100", AllEvidence("CONTRACT-100", new(2024, 5, 20), new(2028, 5, 19))),
                Cover(policies["accidental_damage"], "accidental_damage", new(2024, 5, 20), new(2027, 5, 19), null,
                    EntitlementDirection.Outbound, "contract_line", "CONTRACT-100-AD"),
                Cover(policies["media_retention"], "media_retention", new(2024, 5, 20), new(2029, 5, 19), null,
                    EntitlementDirection.Outbound, "contract_line", "CONTRACT-100-MR"),
            ],
            Certificate = Certificate("CERT-100"),
        };
        var expired = ShippedServer(item, prefix + "-101", Guid.NewGuid()) with
        {
            Entitlements = [Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base,
                new(2022, 1, 1), new(2024, 12, 31), null, EntitlementDirection.Outbound,
                "equipment_card", "CARD-101")],
            Certificate = Certificate("CERT-101"),
        };
        var terminated = ShippedServer(item, prefix + "-102", Guid.NewGuid()) with
        {
            Entitlements = [Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base,
                new(2025, 1, 1), new(2026, 7, 15), new(2026, 7, 15), EntitlementDirection.Outbound,
                "contract_line", "CONTRACT-102",
                [new("contract_line", "CONTRACT-102", new(2025, 1, 1), new(2028, 12, 31),
                    new(2026, 7, 15), EntitlementDirection.Outbound, true)])],
            Certificate = Certificate("CERT-102"),
        };
        var inbound = ShippedServer(item, prefix + "-103", Guid.NewGuid()) with
        {
            Entitlements = [Cover(policies[CoverageFamilyCode.Supplier], CoverageFamilyCode.Supplier,
                new(2025, 4, 1), new(2027, 3, 31), null, EntitlementDirection.Inbound,
                "serial_master", "MASTER-103")],
            Certificate = Certificate("CERT-103"),
        };
        var load = Guid.NewGuid();

        var result = await InvokeAsync<ApplyMigratedUnitsResult>(tenant,
            new ApplyMigratedUnits(load, Entity, "user:migration-lead", "migration:test",
                [covered, expired, terminated, inbound], new DateOnly(2026, 9, 1)));

        Assert.Equal((4, 6, 4), (result.Applied, result.EntitlementsWritten, result.CertificatesRecorded));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var loaded = await db.MigrationLoadUnits.Where(row => row.LoadId == load)
            .ToDictionaryAsync(row => row.PlanUnitId, row => row.UnitId);
        var coveredId = loaded[covered.PlanUnitId];
        var rows = await db.Entitlements.Where(row => row.MigrationLoadId == load).ToListAsync();
        var active = rows.Where(row => row.UnitId == coveredId).OrderBy(row => row.CoverageFamily).ToList();
        Assert.Equal(["accidental_damage", CoverageFamilyCode.Base, "media_retention"],
            active.Select(row => row.CoverageFamily));
        Assert.All(active, row => Assert.Equal((EntitlementStatus.Active, EntitlementProvenance.Migrated,
            "migrated"), (row.Status, row.Provenance, row.ComputedByRuleVersion)));
        Assert.Equal(new DateOnly(2029, 5, 19),
            (await db.Current.SingleAsync(row => row.UnitId == coveredId)).WarrantyEndMax);
        Assert.Equal(3, System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(
            (await db.Current.SingleAsync(row => row.UnitId == coveredId)).LiveEntitlementIds)!.Count);
        var deviation = await db.CoverDeviations.SingleAsync(row => row.UnitId == coveredId);
        Assert.Equal(new DateOnly(2024, 5, 20), deviation.SaleDate);
        Assert.Equal("migration_cover", deviation.SaleDocumentType);
        Assert.Equal(2, deviation.ChangeCount);

        var expiredRow = rows.Single(row => row.UnitId == loaded[expired.PlanUnitId]);
        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.Expired, new DateOnly(2024, 12, 31)),
            (expiredRow.Status, expiredRow.ClosedReason, expiredRow.ClosedEffective));
        var terminatedRow = rows.Single(row => row.UnitId == loaded[terminated.PlanUnitId]);
        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.MigratedTermination, new DateOnly(2026, 7, 15)),
            (terminatedRow.Status, terminatedRow.ClosedReason, terminatedRow.ClosedEffective));
        var inboundRow = rows.Single(row => row.UnitId == loaded[inbound.PlanUnitId]);
        Assert.Equal((EntitlementDirection.Inbound, "serial_master", (Guid?)null),
            (inboundRow.Direction, inboundRow.StartBasis, inboundRow.OwnerPartyRef));
        var baseEntitlementId = active.Single(entitlement =>
            entitlement.CoverageFamily == CoverageFamilyCode.Base).EntitlementId;
        Assert.Equal(3, await db.EntitlementEvidence.CountAsync(row =>
            row.EntitlementId == baseEntitlementId));
        Assert.Equal(4, await db.CertificateInstances.CountAsync(row => row.MigrationLoadId == load
            && row.Version == 1 && row.Provenance == CertificateProvenance.Migrated));
        Assert.All(await db.CertificateInstances.Where(row => row.MigrationLoadId == load).ToListAsync(), row =>
        {
            Assert.NotEqual("[]", row.Unknowns);
            Assert.NotEqual("[]", row.Evidence);
        });

        var resend = await InvokeAsync<ApplyMigratedUnitsResult>(tenant,
            new ApplyMigratedUnits(load, Entity, "user:migration-lead", "migration:test",
                [covered, expired, terminated, inbound], new DateOnly(2026, 9, 1)));
        Assert.Equal((4, 0, 0, 0), (resend.AlreadyApplied, resend.EntitlementsWritten,
            resend.ReplacementLinksWritten, resend.CertificatesRecorded));
    }

    [DatabaseFact]
    public async Task Migrated_links_require_both_loaded_units_and_rollback_removes_cover_links_certificates_and_projections()
    {
        var tenant = await fixture.NewTenantAsync("mglink");
        var policies = await CoverPoliciesAsync(tenant);
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var originalPlan = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var replacementPlan = Guid.Parse("00000000-0000-0000-0000-000000000102");
        var link = new MigratedReplacementLink(Guid.NewGuid(), originalPlan, replacementPlan, "unknown", "PAIR-1");
        MigratedUnit Unit(Guid plan, string serial) => ShippedServer(item, serial, Guid.NewGuid()) with
        {
            PlanUnitId = plan,
            Entitlements = [Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base,
                new(2025, 1, 1), new(2027, 12, 31), null, EntitlementDirection.Outbound,
                "contract_line", "CONTRACT-" + serial)],
            Certificate = Certificate("CERT-" + serial),
        };
        var original = Unit(originalPlan, prefix + "-110");
        var replacement = Unit(replacementPlan, prefix + "-111") with { ReplacementLinks = [link] };
        var load = Guid.NewGuid();

        var result = await InvokeAsync<ApplyMigratedUnitsResult>(tenant,
            new ApplyMigratedUnits(load, Entity, "user:migration-lead", "migration:test", [original, replacement], new(2026, 9, 1)));
        Assert.Equal((2, 2, 1, 2), (result.Applied, result.EntitlementsWritten,
            result.ReplacementLinksWritten, result.CertificatesRecorded));
        var loadedUnitIds = result.Units.Select(row => row.UnitId!.Value).ToList();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var stored = await db.ReplacementLinks.SingleAsync(row => row.MigrationLoadId == load);
            Assert.Equal((ReplacementSourceKind.Migrated, (Guid?)null, (Guid?)null,
                    ReplacementPricePolicy.Zero, 0m, "unknown"),
                (stored.SourceKind, stored.WarrantyReplacementId, stored.SourceEntitlementId,
                    stored.PricePolicy, stored.DifferenceAmount, stored.PolicyApplied));
        }

        var rolledBack = await InvokeAsync<RollBackMigrationLoadResult>(tenant,
            new RollBackMigrationLoad(load, "user:admin"));
        Assert.Equal((RollBackMigrationLoadResult.RolledBack, 2, 2, 1, 2),
            (rolledBack.Outcome, rolledBack.UnitsRemoved, rolledBack.EntitlementsRemoved,
                rolledBack.ReplacementLinksRemoved, rolledBack.CertificatesRemoved));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.False(await db.Entitlements.AnyAsync(row => row.MigrationLoadId == load));
            Assert.False(await db.EntitlementEvidence.AnyAsync(row => row.MigrationLoadId == load));
            Assert.False(await db.ReplacementLinks.AnyAsync(row => row.MigrationLoadId == load));
            Assert.False(await db.CertificateInstances.AnyAsync(row => row.MigrationLoadId == load));
            Assert.False(await db.CoverDeviations.AnyAsync(row => loadedUnitIds.Contains(row.UnitId)));
        }

        var missingOriginal = Unit(originalPlan, prefix + "-112") with { ConditionGrade = "unknown_grade" };
        var carrier = Unit(replacementPlan, prefix + "-113") with { ReplacementLinks = [link with { PlanReplacementLinkId = Guid.NewGuid() }] };
        var refused = await InvokeAsync<ApplyMigratedUnitsResult>(tenant,
            new ApplyMigratedUnits(Guid.NewGuid(), Entity, "user:migration-lead", "migration:test",
                [missingOriginal, carrier], new(2026, 9, 1)));
        Assert.Contains("condition_grade_unknown", refused.Units.Single(row => row.PlanUnitId == originalPlan).Reasons);
        Assert.Contains("replacement_unit_not_loaded", refused.Units.Single(row => row.PlanUnitId == replacementPlan).Reasons);
        Assert.Equal(0, refused.ReplacementLinksWritten);
    }

    [DatabaseFact]
    public async Task A_later_claim_or_entitlement_amendment_marks_the_migration_load_touched()
    {
        var tenant = await fixture.NewTenantAsync("mgtouch", "EUR");
        var policies = await CoverPoliciesAsync(tenant);
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var units = new[]
        {
            ShippedServer(item, prefix + "-120", Guid.NewGuid()),
            ShippedServer(item, prefix + "-121", Guid.NewGuid()),
        }.Select((unit, index) => unit with
        {
            Entitlements = [Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base,
                new(2025, 1, 1), new(2028, 12, 31), null, EntitlementDirection.Outbound,
                "contract_line", $"CONTRACT-12{index}")],
            Certificate = Certificate($"CERT-12{index}"),
        }).ToArray();
        var load = Guid.NewGuid();
        var applied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant,
            new ApplyMigratedUnits(load, Entity, "user:migration-lead", "migration:test", units, new(2026, 9, 1)));
        var unitA = applied.Units.Single(row => row.PlanUnitId == units[0].PlanUnitId).UnitId!.Value;
        var unitB = applied.Units.Single(row => row.PlanUnitId == units[1].PlanUnitId).UnitId!.Value;
        Guid entitlementA;
        using (var scope = fixture.ScopeFor(tenant))
        {
            entitlementA = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements
                .Where(row => row.UnitId == unitA).Select(row => row.EntitlementId).SingleAsync();
        }
        await InvokeAsync<AmendEntitlementResult>(tenant, new AmendEntitlement(Guid.NewGuid(), entitlementA,
            AmendmentReason.Correction, new(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc), "manager-user",
            NewEnd: new DateOnly(2029, 1, 10), Note: "Reviewed migrated date",
            CommittedBy: new AmendmentCommitter("manager-user", [YojanaRole.ServiceManager])));
        var owner = units[1].Ownership!.PartyRef;
        await InvokeAsync<SubmitClaimResult>(tenant, new SubmitClaim(Guid.NewGuid(), unitB, null,
            new DateOnly(2026, 9, 2), owner, owner, Entity, Entity, "DE", "EUR",
            [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 10m)],
            [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "FAULT-EVIDENCE-121")],
            ClaimRemedy.Repair, "CUSTOMER-CHOICE-121", new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc),
            "service-desk"));

        var rollback = await InvokeAsync<RollBackMigrationLoadResult>(tenant,
            new RollBackMigrationLoad(load, "user:admin"));
        Assert.Equal(RollBackMigrationLoadResult.RefusedTouched, rollback.Outcome);
        Assert.Equal(new[] { unitA, unitB }.Order().ToList(), rollback.TouchedUnitIds.Order().ToList());
    }

    [DatabaseFact]
    public async Task A_500_unit_cover_chunk_is_written_set_based_and_reports_a_full_load_projection()
    {
        var tenant = await fixture.NewTenantAsync("mgvolume");
        var policies = await CoverPoliciesAsync(tenant);
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var units = Enumerable.Range(1, 500).Select(index =>
        {
            var entitlements = new List<MigratedEntitlement>
            {
                Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base,
                    new(2025, 1, 1), new(2028, 12, 31), null, EntitlementDirection.Outbound,
                    "contract_line", $"VOLUME-BASE-{index}"),
            };
            if (index <= 150)
                entitlements.Add(Cover(policies["service_level"], "service_level",
                    new(2025, 1, 1), new(2027, 12, 31), null, EntitlementDirection.Outbound,
                    "contract_line", $"VOLUME-NBD-{index}"));
            return ShippedServer(item, $"{prefix}-{index:0000}", Guid.NewGuid()) with
            {
                Entitlements = entitlements,
                Certificate = Certificate($"VOLUME-CERT-{index}"),
            };
        }).ToList();
        var load = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        var result = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, new ApplyMigratedUnits(load,
            Entity, "user:migration-lead", "migration:test", units, new(2026, 9, 1)));
        stopwatch.Stop();
        var projected = TimeSpan.FromTicks((long)(stopwatch.Elapsed.Ticks * (420_000d / units.Count)));
        Console.WriteLine("MIGRATION_COVER_500 elapsed={0} projected_420000={1} entitlements={2} certificates={3}",
            stopwatch.Elapsed, projected, result.EntitlementsWritten, result.CertificatesRecorded);
        Assert.Equal((500, 650, 500),
            (result.Applied, result.EntitlementsWritten, result.CertificatesRecorded));

        var rollback = await InvokeAsync<RollBackMigrationLoadResult>(tenant,
            new RollBackMigrationLoad(load, "user:admin"));
        Assert.Equal((500, 650, 500),
            (rollback.UnitsRemoved, rollback.EntitlementsRemoved, rollback.CertificatesRemoved));
    }

    private async Task<Dictionary<string, PolicyDraftResult>> CoverPoliciesAsync(TenantId tenant)
    {
        var definitions = new[]
        {
            (CoverageFamilyCode.Base, "Base warranty", false, EntitlementType.Manufacturer, AssuranceOrService.Assurance),
            (CoverageFamilyCode.Supplier, "Manufacturer cover", false, EntitlementType.Manufacturer, AssuranceOrService.Assurance),
            ("accidental_damage", "Accidental damage", true, EntitlementType.Extended, AssuranceOrService.Service),
            ("media_retention", "Media retention", true, EntitlementType.Extended, AssuranceOrService.Service),
            ("service_level", "Next-business-day service", true, EntitlementType.Extended, AssuranceOrService.Service),
        };
        var result = new Dictionary<string, PolicyDraftResult>(StringComparer.Ordinal);
        foreach (var (family, name, stacking, type, assurance) in definitions)
        {
            await InvokeAsync<CoverageFamilyRow>(tenant,
                new DefineCoverageFamily(family, name, stacking, assurance));
            var code = family[..Math.Min(3, family.Length)].ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N")[..6];
            var terms = PolicyValidatorTestsTerms.Standard24() with { AssuranceOrService = assurance };
            var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code,
                name, family, type, new DateOnly(2020, 1, 1), terms, "test"));
            result[family] = await InvokeAsync<PolicyDraftResult>(tenant,
                new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        }
        return result;
    }

    private static MigratedEntitlement Cover(PolicyDraftResult policy, string family, DateOnly start,
        DateOnly end, DateOnly? termination, string direction, string sourceKind, string sourceKey,
        IReadOnlyList<MigratedWarrantyEvidence>? evidence = null) => new(Guid.NewGuid(), family,
        policy.PolicyVersionId, policy.Version, start, end, termination, direction, sourceKind, sourceKey,
        evidence ?? [new(sourceKind, sourceKey, start, end, termination, direction, true)]);

    private static IReadOnlyList<MigratedWarrantyEvidence> AllEvidence(string selectedKey, DateOnly start,
        DateOnly end) =>
    [
        new("contract_line", selectedKey, start, end, null, EntitlementDirection.Outbound, true),
        new("equipment_card", "CARD-ALTERNATIVE", start.AddDays(2), end.AddDays(2), null,
            EntitlementDirection.Outbound, false),
        new("serial_master", "MASTER-ALTERNATIVE", start.AddDays(-5), end.AddDays(-5), null,
            EntitlementDirection.Inbound, false),
    ];

    private static MigratedCertificate Certificate(string evidence) => new(Guid.NewGuid(), 1,
        CertificateProvenance.Migrated, ["The source did not retain the as-built production record."], [evidence]);
}

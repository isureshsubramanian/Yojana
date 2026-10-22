using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P9-T10b (OD-61, OD-62): cutover cover replacement and exact delta rollback.</summary>
[Collection("serialisation host")]
public sealed class MigrationCoverReplacementTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime Received = new(2025, 1, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Shipped = new(2025, 1, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Cutover = new(2026, 9, 1);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Replacement_changes_cover_and_current_state_empty_cover_closes_all_and_resend_is_a_no_op()
    {
        var tenant = await fixture.NewTenantAsync("mgdelta1");
        var policies = await PoliciesAsync(tenant);
        var item = Guid.NewGuid();
        var first = Unit(item, Serial("R"), Guid.NewGuid(),
            [Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base, new(2025, 1, 3), new(2027, 1, 2), "BASE-OLD")]);
        var second = Unit(item, Serial("E"), Guid.NewGuid(),
            [Cover(policies["service_level"], "service_level", new(2025, 1, 3), new(2028, 1, 2), "NBD-OLD")]);
        var baseLoad = Guid.NewGuid();
        var applied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Load(baseLoad, first, second));
        var firstId = applied.Units.Single(row => row.PlanUnitId == first.PlanUnitId).UnitId!.Value;
        var secondId = applied.Units.Single(row => row.PlanUnitId == second.PlanUnitId).UnitId!.Value;
        var delta = Guid.NewGuid();
        var change = new MigratedCoverChange(firstId, Guid.NewGuid(),
        [
            Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base, new(2025, 1, 3), new(2029, 1, 2), "BASE-NEW"),
            Cover(policies["accidental_damage"], "accidental_damage", new(2026, 9, 1), new(2028, 8, 31), "AD-NEW"),
        ]);

        var result = await InvokeAsync<ReplaceMigratedCoverResult>(tenant,
            new ReplaceMigratedCover(delta, Entity, "user:migration-lead",
                [change, new MigratedCoverChange(secondId, Guid.NewGuid(), [])], Cutover));
        Assert.Equal((2, 0, 2, 2, 4), (result.Replaced, result.Refused,
            result.EntitlementsClosed, result.EntitlementsWritten, result.EventsWritten));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var old = await db.Entitlements.Where(row => row.MigrationLoadId == baseLoad).ToListAsync();
            Assert.All(old, row => Assert.Equal((EntitlementStatus.Closed,
                EntitlementClosedReason.MigrationDeltaReplaced, Cutover),
                (row.Status, row.ClosedReason, row.ClosedEffective)));
            var current = await db.Current.Where(row => row.UnitId == firstId || row.UnitId == secondId)
                .ToDictionaryAsync(row => row.UnitId);
            Assert.Equal(new DateOnly(2029, 1, 2), current[firstId].WarrantyEndMax);
            Assert.Equal(2, JsonSerializer.Deserialize<List<Guid>>(current[firstId].LiveEntitlementIds)!.Count);
            Assert.Equal("[]", current[secondId].LiveEntitlementIds);
            Assert.Null(current[secondId].WarrantyEndMax);
            Assert.Equal(2, await db.MigrationCoverReplacements.CountAsync(row => row.LoadId == delta));
        }

        var resend = await InvokeAsync<ReplaceMigratedCoverResult>(tenant,
            new ReplaceMigratedCover(delta, Entity, "user:migration-lead",
                [change, new MigratedCoverChange(secondId, Guid.NewGuid(), [])], Cutover));
        Assert.Equal((2, 0, 0), (resend.AlreadyReplaced, resend.EventsWritten, resend.EntitlementsWritten));
    }

    [DatabaseFact]
    public async Task Replacement_refuses_unmigrated_touched_invalid_and_family_occupied_units_with_rules()
    {
        var tenant = await fixture.NewTenantAsync("mgdelta2");
        var policies = await PoliciesAsync(tenant);
        var item = Guid.NewGuid();
        var units = new[]
        {
            Unit(item, Serial("T"), Guid.NewGuid(), []),
            Unit(item, Serial("I"), Guid.NewGuid(), []),
            Unit(item, Serial("F"), Guid.NewGuid(), []),
        };
        var applied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Load(Guid.NewGuid(), units));
        var ids = applied.Units.ToDictionary(row => row.PlanUnitId, row => row.UnitId!.Value);
        var touchedId = ids[units[0].PlanUnitId];
        await InvokeAsync<UnitFlagResult>(tenant, new SetUnitFlag(Guid.NewGuid(), touchedId, OverlayFlag.Lost,
            new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc), "stores", Entity, "cycle count"));
        var unknownPolicy = Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base,
            new(2025, 1, 3), new(2027, 1, 2), "INVALID") with { PolicyVersionId = Guid.NewGuid() };
        var duplicateFamily = new[]
        {
            Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base, new(2025, 1, 3), new(2027, 1, 2), "F-1"),
            Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base, new(2025, 1, 3), new(2028, 1, 2), "F-2"),
        };
        var result = await InvokeAsync<ReplaceMigratedCoverResult>(tenant,
            new ReplaceMigratedCover(Guid.NewGuid(), Entity, "user:migration-lead",
            [
                new(touchedId, Guid.NewGuid(), []),
                new(ids[units[1].PlanUnitId], Guid.NewGuid(), [unknownPolicy]),
                new(ids[units[2].PlanUnitId], Guid.NewGuid(), duplicateFamily),
                new(Guid.NewGuid(), Guid.NewGuid(), []),
            ], Cutover));

        Assert.Equal(MigratedCoverOutcome.RefusedTouched, result.Units[0].Outcome);
        Assert.Contains("entitlement_policy_unknown", result.Units[1].Reasons);
        Assert.Contains("family_occupied", result.Units[2].Reasons);
        Assert.Contains("unit_not_migrated", result.Units[3].Reasons);
        Assert.Equal((0, 4, 0), (result.Replaced, result.Refused, result.EventsWritten));
    }

    [DatabaseFact]
    public async Task Delta_only_rollback_restores_old_cover_exactly_and_removes_new_rows_and_facts()
    {
        var tenant = await fixture.NewTenantAsync("mgdelta3");
        var policies = await PoliciesAsync(tenant);
        var oldCover = Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base,
            new(2025, 1, 3), new(2027, 1, 2), "OLD");
        var unit = Unit(Guid.NewGuid(), Serial("U"), Guid.NewGuid(), [oldCover]);
        var applied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Load(Guid.NewGuid(), unit));
        var unitId = applied.Units.Single().UnitId!.Value;
        Guid oldEntitlementId;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            oldEntitlementId = await db.Entitlements.Where(row => row.UnitId == unitId)
                .Select(row => row.EntitlementId).SingleAsync();
        }
        var delta = Guid.NewGuid();
        var replaced = await InvokeAsync<ReplaceMigratedCoverResult>(tenant,
            new ReplaceMigratedCover(delta, Entity, "user:migration-lead",
            [new(unitId, Guid.NewGuid(),
                [Cover(policies["service_level"], "service_level", new(2026, 9, 1), new(2030, 8, 31), "NEW")])], Cutover));
        Assert.Equal(1, replaced.Replaced);

        var rollback = await InvokeAsync<RollBackMigrationLoadResult>(tenant,
            new RollBackMigrationLoad(delta, "user:admin"));
        Assert.Equal((RollBackMigrationLoadResult.RolledBack, 0, 1, 1),
            (rollback.Outcome, rollback.UnitsRemoved, rollback.EntitlementsRemoved,
                rollback.CoverReplacementsReversed));
        using var verifyScope = fixture.ScopeFor(tenant);
        var verify = verifyScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var restored = await verify.Entitlements.SingleAsync(row => row.EntitlementId == oldEntitlementId);
        Assert.Equal((EntitlementStatus.Active, (string?)null, (DateOnly?)null, (DateTime?)null),
            (restored.Status, restored.ClosedReason, restored.ClosedEffective, restored.ClosedAt));
        Assert.False(await verify.Entitlements.AnyAsync(row => row.MigrationLoadId == delta));
        Assert.False(await verify.EntitlementEvidence.AnyAsync(row => row.MigrationLoadId == delta));
        Assert.False(await verify.MigrationCoverReplacements.AnyAsync(row => row.LoadId == delta));
        var current = await verify.Current.SingleAsync(row => row.UnitId == unitId);
        Assert.Equal(new DateOnly(2027, 1, 2), current.WarrantyEndMax);
        Assert.Equal([oldEntitlementId], JsonSerializer.Deserialize<List<Guid>>(current.LiveEntitlementIds));
    }

    [DatabaseFact]
    public async Task Rollback_refuses_a_replacement_touched_after_the_delta()
    {
        var setup = await ReplacedUnitAsync("mgdelta4");
        await InvokeAsync<UnitFlagResult>(setup.Tenant, new SetUnitFlag(Guid.NewGuid(), setup.UnitId,
            OverlayFlag.Lost, new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc), "stores", Entity, "cycle count"));

        var rollback = await InvokeAsync<RollBackMigrationLoadResult>(setup.Tenant,
            new RollBackMigrationLoad(setup.DeltaLoadId, "user:admin"));
        Assert.Equal(RollBackMigrationLoadResult.RefusedTouched, rollback.Outcome);
        Assert.Equal([setup.UnitId], rollback.TouchedUnitIds);
        Assert.Equal(0, rollback.CoverReplacementsReversed);
    }

    [DatabaseFact]
    public async Task Base_load_rollback_is_refused_until_its_later_cover_replacement_is_undone()
    {
        var setup = await ReplacedUnitAsync("mgdelta5");
        var refused = await InvokeAsync<RollBackMigrationLoadResult>(setup.Tenant,
            new RollBackMigrationLoad(setup.BaseLoadId, "user:admin"));
        Assert.Equal(RollBackMigrationLoadResult.RefusedTouched, refused.Outcome);
        Assert.Equal([setup.UnitId], refused.TouchedUnitIds);

        var delta = await InvokeAsync<RollBackMigrationLoadResult>(setup.Tenant,
            new RollBackMigrationLoad(setup.DeltaLoadId, "user:admin"));
        Assert.Equal(1, delta.CoverReplacementsReversed);
        var baseResult = await InvokeAsync<RollBackMigrationLoadResult>(setup.Tenant,
            new RollBackMigrationLoad(setup.BaseLoadId, "user:admin"));
        Assert.Equal((RollBackMigrationLoadResult.RolledBack, 1), (baseResult.Outcome, baseResult.UnitsRemoved));
    }

    private async Task<(TenantId Tenant, Guid UnitId, Guid BaseLoadId, Guid DeltaLoadId)> ReplacedUnitAsync(string key)
    {
        var tenant = await fixture.NewTenantAsync(key);
        var policies = await PoliciesAsync(tenant);
        var unit = Unit(Guid.NewGuid(), Serial("S"), Guid.NewGuid(),
            [Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base, new(2025, 1, 3), new(2027, 1, 2), "OLD")]);
        var baseLoad = Guid.NewGuid();
        var applied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Load(baseLoad, unit));
        var unitId = applied.Units.Single().UnitId!.Value;
        var delta = Guid.NewGuid();
        var replaced = await InvokeAsync<ReplaceMigratedCoverResult>(tenant,
            new ReplaceMigratedCover(delta, Entity, "user:migration-lead",
            [new(unitId, Guid.NewGuid(),
                [Cover(policies[CoverageFamilyCode.Base], CoverageFamilyCode.Base, new(2025, 1, 3), new(2029, 1, 2), "NEW")])], Cutover));
        Assert.Equal(1, replaced.Replaced);
        return (tenant, unitId, baseLoad, delta);
    }

    private static ApplyMigratedUnits Load(Guid load, params MigratedUnit[] units) =>
        new(load, Entity, "user:migration-lead", "migration:test", units, Cutover);

    private static MigratedUnit Unit(Guid item, string serial, Guid customer,
        IReadOnlyList<MigratedEntitlement> entitlements) => new(Guid.CreateVersion7(), item,
        HistoryDepth.Full, Received, [new IdentifierInput(IdentifierType.InternalSerial, serial)],
        [new MigratedHistoryEvent(Received, "received", "source", "Goods receipt", 1, "V-FICTIONAL", "WH-01", 1),
         new MigratedHistoryEvent(Shipped, "shipped", "source", "Delivery", 2, "C-FICTIONAL", "WH-01", -1)],
        null, new MigratedOwnership(customer, PartyType.Customer, Shipped, false), ConditionGrades.New,
        "inferred", entitlements, Certificate: new(Guid.NewGuid(), 1, CertificateProvenance.Migrated,
            ["The source did not retain the production record."], ["source history"]));

    private async Task<Dictionary<string, PolicyDraftResult>> PoliciesAsync(TenantId tenant)
    {
        var definitions = new[]
        {
            (CoverageFamilyCode.Base, "Base warranty", false, EntitlementType.Manufacturer, AssuranceOrService.Assurance),
            ("accidental_damage", "Accidental damage", true, EntitlementType.Extended, AssuranceOrService.Service),
            ("service_level", "Next-business-day service", true, EntitlementType.Extended, AssuranceOrService.Service),
        };
        var result = new Dictionary<string, PolicyDraftResult>(StringComparer.Ordinal);
        foreach (var (family, name, stacking, type, assurance) in definitions)
        {
            await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(family, name, stacking, assurance));
            var terms = PolicyValidatorTestsTerms.Standard24() with { AssuranceOrService = assurance };
            var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(),
                family[..Math.Min(3, family.Length)].ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N")[..6],
                name, family, type, new DateOnly(2020, 1, 1), terms, "test"));
            result[family] = await InvokeAsync<PolicyDraftResult>(tenant,
                new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        }
        return result;
    }

    private static MigratedEntitlement Cover(PolicyDraftResult policy, string family, DateOnly start,
        DateOnly end, string sourceKey) => new(Guid.NewGuid(), family, policy.PolicyVersionId,
        policy.Version, start, end, null, EntitlementDirection.Outbound, "contract_line", sourceKey,
        [new("contract_line", sourceKey, start, end, null, EntitlementDirection.Outbound, true)]);

    private static string Serial(string marker) =>
        "DL" + marker + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
}

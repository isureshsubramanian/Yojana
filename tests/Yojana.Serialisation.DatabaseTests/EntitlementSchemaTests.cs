using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.DatabaseTests;

// EF1002: the interpolated values are schema constants, never user input.
#pragma warning disable EF1002

/// <summary>P2-T02: the entitlement tables hold MOD-06's invariants that the schema can hold (one live per family, closed names a reason, a term ends after it starts) and are temporal where ADR-0001 says so.</summary>
[Collection("serialisation host")]
public sealed class EntitlementSchemaTests(SerialisationHostFixture fixture)
{
    private const string S = SerialisationDbContext.SchemaName;
    private static readonly DateTime T0 = new(2026, 9, 2, 8, 40, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Entitlement_and_policy_version_are_system_versioned_and_their_json_columns_are_checked()
    {
        using var scope = fixture.ScopeFor(null);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        foreach (var table in new[] { "entitlement", "warranty_policy_version" })
        {
            var temporal = await db.Database.SqlQueryRaw<int>($"SELECT CAST(temporal_type AS int) AS [Value] FROM sys.tables WHERE name = '{table}' AND schema_id = SCHEMA_ID('{S}')").SingleAsync();
            Assert.Equal(2, temporal);
        }

        var checks = (await db.Database.SqlQueryRaw<string>($"SELECT name AS [Value] FROM sys.check_constraints WHERE schema_id = SCHEMA_ID('{S}') AND (name LIKE 'ck_entitlement%' OR name LIKE 'ck_warranty_policy_version%')").ToListAsync()).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "ck_entitlement_coverage_json", "ck_entitlement_region_scope_json", "ck_entitlement_closed_reason", "ck_entitlement_term", "ck_entitlement_suspension_dates", "ck_entitlement_usage_limit_values",
            "ck_warranty_policy_version_coverage_json", "ck_warranty_policy_version_fallback_chain_json", "ck_warranty_policy_version_refurbished_policies_json",
        }, checks);
    }

    [DatabaseFact]
    public async Task One_live_entitlement_per_unit_and_coverage_family_while_families_stack_and_closed_rows_do_not_count()
    {
        var tenant = await fixture.NewTenantAsync("en");
        var unit = Guid.NewGuid();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var policy = await NewPolicyAsync(db, tenant);
        var base1 = NewEntitlement(tenant, unit, "base_warranty", policy);
        db.Entitlements.Add(base1);
        db.Entitlements.Add(NewEntitlement(tenant, unit, "service_level", policy));
        db.Entitlements.Add(NewEntitlement(tenant, unit, "accessory_cover", policy, EntitlementStatus.Suspended));
        await db.SaveChangesAsync();

        // a second live row in a family is refused, whatever its live status
        db.Entitlements.Add(NewEntitlement(tenant, unit, "base_warranty", policy, EntitlementStatus.PendingEvidence));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("ux_entitlement_tenant_id_unit_id_coverage_family", ex.InnerException!.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        // closing the first frees the family for its successor (the supersede and transfer shape)
        var first = await db.Entitlements.SingleAsync(e => e.EntitlementId == base1.EntitlementId);
        first.Status = EntitlementStatus.Closed;
        first.ClosedReason = EntitlementClosedReason.ReplacedByChange;
        first.ClosedEffective = new DateOnly(2026, 10, 14);
        var successor = NewEntitlement(tenant, unit, "base_warranty", policy);
        successor.SupersedesEntitlementId = first.EntitlementId;
        db.Entitlements.Add(successor);
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Entitlements.CountAsync(e => e.UnitId == unit && e.CoverageFamily == "base_warranty" && e.Status != EntitlementStatus.Closed));
        Assert.Equal(2, await db.Entitlements.CountAsync(e => e.UnitId == unit && e.CoverageFamily == "base_warranty"));
    }

    [DatabaseFact]
    public async Task A_closed_entitlement_names_a_reason_a_live_one_names_none_and_a_term_never_ends_before_it_starts()
    {
        var tenant = await fixture.NewTenantAsync("ec");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var policy = await NewPolicyAsync(db, tenant);

        var closedWithout = NewEntitlement(tenant, Guid.NewGuid(), "base_warranty", policy, EntitlementStatus.Closed);
        db.Entitlements.Add(closedWithout);
        Assert.Contains("ck_entitlement_closed_reason", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException!.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        var liveWith = NewEntitlement(tenant, Guid.NewGuid(), "base_warranty", policy);
        liveWith.ClosedReason = EntitlementClosedReason.Expired;
        db.Entitlements.Add(liveWith);
        Assert.Contains("ck_entitlement_closed_reason", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException!.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        var backwards = NewEntitlement(tenant, Guid.NewGuid(), "base_warranty", policy);
        backwards.EndDate = backwards.StartDate!.Value.AddDays(-1);
        db.Entitlements.Add(backwards);
        Assert.Contains("ck_entitlement_term", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException!.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        // pending evidence: no start yet, so no end either
        var pending = NewEntitlement(tenant, Guid.NewGuid(), "base_warranty", policy, EntitlementStatus.PendingEvidence);
        pending.StartDate = null;
        pending.EndDate = null;
        db.Entitlements.Add(pending);
        await db.SaveChangesAsync();
    }

    [DatabaseFact]
    public async Task Policy_versions_are_numbered_once_per_policy_and_child_rows_keep_one_open_suspension()
    {
        var tenant = await fixture.NewTenantAsync("ep");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var policy = await NewPolicyAsync(db, tenant);
        db.PolicyVersions.Add(Policy(tenant, policy.PolicyCode, policy.Version));
        Assert.Contains("ux_warranty_policy_version_tenant_id_policy_code_version", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException!.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        var entitlement = NewEntitlement(tenant, Guid.NewGuid(), "base_warranty", policy, EntitlementStatus.Suspended);
        db.Entitlements.Add(entitlement);
        db.EntitlementSuspensions.Add(new EntitlementSuspension { TenantId = tenant, SuspensionId = TimeOrderedId.New(T0), EntitlementId = entitlement.EntitlementId, FromDate = new DateOnly(2026, 10, 1), Reason = "unit_in_dispute", ClockPaused = true, ActorRef = "test", EventId = Guid.NewGuid() });
        db.EntitlementUsageLimits.Add(new EntitlementUsageLimit { TenantId = tenant, EntitlementId = entitlement.EntitlementId, MeterType = "operating_hours", LimitValue = 12000m, UsedValue = 1284.5m, Period = "term" });
        await db.SaveChangesAsync();
        db.EntitlementSuspensions.Add(new EntitlementSuspension { TenantId = tenant, SuspensionId = TimeOrderedId.New(T0), EntitlementId = entitlement.EntitlementId, FromDate = new DateOnly(2026, 10, 5), Reason = "second", ClockPaused = false, ActorRef = "test", EventId = Guid.NewGuid() });
        Assert.Contains("ux_entitlement_suspension_tenant_id_entitlement_id", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException!.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        Assert.Equal(1284.5m, await db.EntitlementUsageLimits.Where(u => u.EntitlementId == entitlement.EntitlementId).Select(u => u.UsedValue).SingleAsync());

        // another tenant sees none of it
        var other = await fixture.NewTenantAsync("eq");
        using var otherScope = fixture.ScopeFor(other);
        var otherDb = otherScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(0, await otherDb.Entitlements.CountAsync(e => e.EntitlementId == entitlement.EntitlementId));
        Assert.Equal(0, await otherDb.PolicyVersions.CountAsync(v => v.PolicyVersionId == policy.PolicyVersionId));
    }

    private static async Task<WarrantyPolicyVersion> NewPolicyAsync(SerialisationDbContext db, TenantId tenant)
    {
        var policy = Policy(tenant, "VX220-STD-24-" + Guid.NewGuid().ToString("N")[..6], 4);
        db.PolicyVersions.Add(policy);
        await db.SaveChangesAsync();
        return policy;
    }

    private static WarrantyPolicyVersion Policy(TenantId tenant, string code, int version) => new()
    {
        TenantId = tenant, PolicyVersionId = TimeOrderedId.New(T0), PolicyCode = code, Version = version, Name = "VX-220 Standard 24 months", CoverageFamily = "base_warranty", EntitlementType = EntitlementType.Manufacturer,
        AppliesToContractsFrom = new DateOnly(2026, 7, 31), Status = PolicyVersionStatus.Published, RuleVersion = "r4.2", DurationValue = 24, DurationUnit = "months", EndOfMonthRule = "clamp", DurationBasis = DurationBasis.Time,
        StartTrigger = StartTrigger.Deliver, FallbackChain = """[{"trigger":"invoice","offset_days":14}]""", StartCapDays = 90, Coverage = """{"parts_pct":100,"labour_pct":100}""", Transferable = Transferability.Yes, SettlementFormula = "full",
        AssuranceOrService = AssuranceOrService.Assurance, ReferenceTimeZoneRule = "customer_site", CreatedAt = T0, CreatedBy = "test",
    };

    private static Entitlement NewEntitlement(TenantId tenant, Guid unit, string family, WarrantyPolicyVersion policy, string status = EntitlementStatus.Active) => new()
    {
        TenantId = tenant, EntitlementId = TimeOrderedId.New(T0), UnitId = unit, Scope = EntitlementScope.Unit, Type = EntitlementType.Manufacturer, CoverageFamily = family, Direction = EntitlementDirection.Outbound,
        OwnerPartyRef = Guid.NewGuid(), OwnerPartyType = PartyType.Customer, ObligorEntityId = Guid.NewGuid(), PolicyVersionId = policy.PolicyVersionId, StartTrigger = StartTrigger.Deliver, StartBasis = StartTrigger.Deliver,
        StartDate = new DateOnly(2026, 10, 2), StartTimeZone = "Europe/Berlin", EndDate = new DateOnly(2028, 10, 1), ComputedByRuleVersion = policy.RuleVersion, DurationBasis = DurationBasis.Time, Transferable = Transferability.Yes,
        AssuranceOrService = AssuranceOrService.Assurance, Status = status, Confidence = "high", Provenance = EntitlementProvenance.Created, CreatedEventId = Guid.NewGuid(), CreatedAt = T0,
    };
}

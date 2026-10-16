using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P2-T13: ownership and its durable entitlement requirement commit together; resolution follows through the outbox.</summary>
[Collection("serialisation host")]
public sealed class EntitlementRequirementTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Obligor = Guid.NewGuid();
    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<(TenantId Tenant, Guid Item, Guid Unit)> UnitAsync(string prefix, bool required, bool publishPolicy)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        var item = Guid.NewGuid();
        var code = "RQ-" + Guid.NewGuid().ToString("N")[..6];
        if (publishPolicy)
        {
            await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
            await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("RQ-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
            var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, CoverageFamilyCode.Base, EntitlementType.Manufacturer,
                new DateOnly(2020, 1, 1), PolicyValidatorTestsTerms.Standard24() with { ReplacementPolicyCode = "RQ-90" }, "test"));
            await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        }
        Ports.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, code, required));
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N"))])],
            new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), "test", Obligor));
        return (tenant, item, commissioned.Lines[0].UnitId!.Value);
    }

    [DatabaseFact]
    public async Task Required_sale_is_satisfied_once_and_replay_adds_nothing()
    {
        var setup = await UnitAsync("rq", true, true);
        var command = new OpenOwnership(Guid.NewGuid(), setup.Unit, Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale,
            new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), "sales", Obligor, EpisodeSource.Delivery, Document: new DocumentRef("delivery", Guid.NewGuid(), 1));
        var opened = await InvokeAsync<OpenOwnershipResult>(setup.Tenant, command);
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, command);
        var requirement = await WaitAsync(setup.Tenant, setup.Unit, EntitlementRequirementStatus.Satisfied);
        Assert.Equal(opened.EpisodeId, requirement.OwnershipEpisodeId);
        Assert.NotNull(requirement.EntitlementId);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(1, await db.EntitlementRequirements.CountAsync(r => r.UnitId == setup.Unit));
        Assert.Equal(1, await db.Entitlements.CountAsync(e => e.UnitId == setup.Unit && e.CoverageFamily == CoverageFamilyCode.Base));
    }

    [DatabaseFact]
    public async Task Missing_policy_is_visible_and_an_item_not_requiring_cover_has_no_row()
    {
        var missing = await UnitAsync("rm", true, false);
        await InvokeAsync<OpenOwnershipResult>(missing.Tenant, new OpenOwnership(Guid.NewGuid(), missing.Unit, Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale,
            DateTime.UtcNow, "sales", Obligor, EpisodeSource.Delivery, Document: new DocumentRef("delivery", Guid.NewGuid(), 1)));
        Assert.Equal(EntitlementMissingInput.NoPolicy, (await WaitAsync(missing.Tenant, missing.Unit, EntitlementRequirementStatus.Exception)).MissingInput);

        var optional = await UnitAsync("ro", false, false);
        await InvokeAsync<OpenOwnershipResult>(optional.Tenant, new OpenOwnership(Guid.NewGuid(), optional.Unit, Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale,
            DateTime.UtcNow, "sales", Obligor, EpisodeSource.Delivery, Document: new DocumentRef("delivery", Guid.NewGuid(), 1)));
        using var scope = fixture.ScopeFor(optional.Tenant);
        Assert.False(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().EntitlementRequirements.AnyAsync(r => r.UnitId == optional.Unit));
    }

    [DatabaseFact]
    public async Task Daily_sweep_recovers_an_episode_that_preceded_the_requirement_rule()
    {
        var setup = await UnitAsync("rs", false, false);
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.Unit, Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale,
            DateTime.UtcNow.AddMinutes(-1), "migration", Obligor));
        using (var scope = fixture.ScopeFor(setup.Tenant)) await scope.ServiceProvider.GetRequiredService<EntitlementRequirementSweep>().RunAsync(CancellationToken.None);
        var row = await WaitAsync(setup.Tenant, setup.Unit, EntitlementRequirementStatus.Exception);
        Assert.Equal(EntitlementMissingInput.NoStartEvidence, row.MissingInput);
    }

    private async Task<Yojana.Serialisation.Data.Entities.EntitlementRequirement> WaitAsync(TenantId tenant, Guid unit, string status)
    {
        for (var i = 0; i < 100; i++)
        {
            using var scope = fixture.ScopeFor(tenant);
            var row = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().EntitlementRequirements.AsNoTracking().SingleOrDefaultAsync(r => r.UnitId == unit && r.Status == status);
            if (row is not null) return row;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Requirement for {unit} did not reach {status}.");
    }
}

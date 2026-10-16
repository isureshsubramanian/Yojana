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

/*
P2-T20 coverage map (type.method):
Cases
  27  -> EntitlementEdgeCaseScenarioTests.Inter_company_transfer_waits_for_the_first_external_sale_and_a_resale_never_restarts_cover
  31  -> EntitlementEdgeCaseScenarioTests.Total_loss_closes_only_the_selected_entitlement_and_keeps_its_history
  44  -> EntitlementCommandTests.Creation_takes_the_first_evidence_in_the_chain_a_rebase_moves_it_within_the_cap_and_unit_current_follows
  45  -> EntitlementEdgeCaseScenarioTests.Customer_resale_moves_cover_only_when_the_policy_allows_it
  49  -> EntitlementEdgeCaseScenarioTests.Customer_supplied_unit_gets_only_the_explicit_contract_entitlement
  52  -> EntitlementCommandTests.Creation_takes_the_first_evidence_in_the_chain_a_rebase_moves_it_within_the_cap_and_unit_current_follows
  53  -> ExtendedWarrantyTests.An_extended_term_starts_at_base_expiry_stacks_beside_the_base_and_is_published_as_sold_with_its_schedule
  54  -> PolicyResolutionTests.The_stack_runs_unit_then_parent_then_contract_then_product_and_versions_go_by_contract_date
  57  -> EntitlementCommandTests.Transfer_closes_the_source_builds_one_rooted_chain_and_suppresses_the_second_accrual
  60  -> EntitlementCommandTests.Refurbished_resale_snapshots_the_floor_and_disclosure_and_starts_a_new_chain
  61  -> EntitlementCommandTests.Pending_evidence_suspension_void_close_and_lazy_expiry
  62  -> EntitlementCommandTests.Pending_evidence_suspension_void_close_and_lazy_expiry
  63  -> PolicyResolutionTests.The_stack_runs_unit_then_parent_then_contract_then_product_and_versions_go_by_contract_date
  64  -> ExtendedWarrantyTests.An_extended_term_starts_at_base_expiry_stacks_beside_the_base_and_is_published_as_sold_with_its_schedule
  66  -> P4 (claims after discontinuation); placeholder EntitlementEdgeCaseScenarioTests.Discontinued_item_remains_serviceable_until_its_last_service_date
  71  -> EntitlementCommandTests.Transfer_closes_the_source_builds_one_rooted_chain_and_suppresses_the_second_accrual
  72  -> EntitlementCommandTests.Creation_takes_the_first_evidence_in_the_chain_a_rebase_moves_it_within_the_cap_and_unit_current_follows
  110 -> EntitlementEdgeCaseScenarioTests.Inter_company_transfer_waits_for_the_first_external_sale_and_a_resale_never_restarts_cover
  112 -> TermArithmeticTests.The_reference_zone_decides_the_calendar_day_of_an_instant; TermArithmeticTests.Across_a_year_of_hours_the_local_day_never_goes_back_and_never_skips_whatever_the_zone_and_daylight_saving
  113 -> TermArithmeticTests.Months_end_by_the_rule; TermArithmeticTests.Clamp_keeps_the_day_of_month_when_it_exists_otherwise_the_month_end_and_is_monotonic
  114 -> PolicyResolutionTests.The_stack_runs_unit_then_parent_then_contract_then_product_and_versions_go_by_contract_date
  123 -> SupersedeEntitlementTests.Active_cover_is_superseded_once_without_a_second_created_event_and_replay_is_idempotent
  124 -> EntitlementBulkTests.Sixty_one_of_sixty_five_units_are_superseded_in_one_transaction_after_a_preview_that_writes_nothing; ExtendedWarrantyTests.An_add_on_starts_at_purchase_in_the_policy_zone_and_eligibility_family_and_price_rules_refuse_with_their_rule
  127 -> BackfillEntitlementsTests.Delivery_and_vendor_confirmation_start_backfills_publish_once_and_replay_idempotently
  129 -> AmendEntitlementTests.An_extension_is_approved_by_reason_and_days_kept_before_and_after_and_published_as_amended
  132 -> WarrantySalesLineTests.Attached_serial_range_resolves_and_an_explicit_list_can_close_an_exception; WarrantySalesLineTests.Referenced_delivery_resolves_later_from_the_original_trigger_and_regulation_is_a_visible_exception
  140 -> AmendEntitlementTests.A_reduction_needs_the_manager_a_notification_after_a_certificate_and_no_open_claims
Tickets
  T9/T11  -> AmendEntitlementTests.An_extension_is_approved_by_reason_and_days_kept_before_and_after_and_published_as_amended
  T10/T12 -> AmendEntitlementTests.An_extension_is_approved_by_reason_and_days_kept_before_and_after_and_published_as_amended
  T18     -> EntitlementBulkTests.One_unit_needing_a_higher_approval_refuses_the_whole_set_until_that_approval_is_given
  T21/T22 -> BackfillEntitlementsTests.Delivery_and_vendor_confirmation_start_backfills_publish_once_and_replay_idempotently
  T23     -> EntitlementBulkTests.Sixty_one_of_sixty_five_units_are_superseded_in_one_transaction_after_a_preview_that_writes_nothing
  T25     -> EntitlementBulkTests.Sixty_one_of_sixty_five_units_are_superseded_in_one_transaction_after_a_preview_that_writes_nothing
  T26     -> WarrantySalesLineTests.Same_order_three_units_stack_with_invoice_and_po_and_replay_once
  T27     -> EntitlementBulkTests.One_unit_needing_a_higher_approval_refuses_the_whole_set_until_that_approval_is_given
  T29     -> WarrantySalesLineTests.Non_stacking_purchase_extends_matching_service_mode_and_supersedes_a_changed_mode; WarrantySalesLineTests.Same_order_three_units_stack_with_invoice_and_po_and_replay_once
  T30     -> EntitlementBulkTests.One_unit_needing_a_higher_approval_refuses_the_whole_set_until_that_approval_is_given
  T35     -> EntitlementBulkTests.Sixty_one_of_sixty_five_units_are_superseded_in_one_transaction_after_a_preview_that_writes_nothing
  T36     -> WarrantySalesLineTests.Same_order_three_units_stack_with_invoice_and_po_and_replay_once
  T37/T38 -> EntitlementBulkTests.One_unit_needing_a_higher_approval_refuses_the_whole_set_until_that_approval_is_given
  T48     -> ExtendedWarrantyTests.An_extended_term_starts_at_base_expiry_stacks_beside_the_base_and_is_published_as_sold_with_its_schedule
  T52     -> WarrantySalesLineTests.Attached_serial_range_resolves_and_an_explicit_list_can_close_an_exception
  T54     -> EntitlementBulkTests.One_unit_needing_a_higher_approval_refuses_the_whole_set_until_that_approval_is_given
  T55     -> EntitlementBulkTests.Sixty_one_of_sixty_five_units_are_superseded_in_one_transaction_after_a_preview_that_writes_nothing
  T56     -> WarrantySalesLineTests.Referenced_delivery_resolves_later_from_the_original_trigger_and_regulation_is_a_visible_exception
  T57     -> AmendEntitlementTests.A_reduction_needs_the_manager_a_notification_after_a_certificate_and_no_open_claims
  T61     -> BackfillEntitlementsTests.Delivery_and_vendor_confirmation_start_backfills_publish_once_and_replay_idempotently; WarrantySalesLineTests.Attached_serial_range_resolves_and_an_explicit_list_can_close_an_exception
  T64     -> ExtendedWarrantyTests.An_extended_term_starts_at_base_expiry_stacks_beside_the_base_and_is_published_as_sold_with_its_schedule
  T72     -> EntitlementCommandTests.Pending_evidence_suspension_void_close_and_lazy_expiry (CloseEntitlement path)
Reference scenario step 1 -> EntitlementCommandTests.Creation_rebase_and_close_are_published_with_their_coverage_changes (cohort); RecordedScenarioTests.Reference_scenario_steps_0_and_1_replay_from_recorded_messages
Reference scenario transfer -> EntitlementCommandTests.Transfer_closes_the_source_builds_one_rooted_chain_and_suppresses_the_second_accrual
*/

/// <summary>P2-T20: uncovered entitlement edge cases, exercised only through commands on real units.</summary>
[Collection("serialisation host")]
public sealed class EntitlementEdgeCaseScenarioTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Obligor = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<(TenantId Tenant, Guid Unit, Guid PolicyVersion)> SetUpAsync(string prefix, string entitlementType, string origin)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("contract_cover", "Contract cover", false, AssuranceOrService.Service));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("CONTRACT-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            AssuranceOrService = AssuranceOrService.Service,
            ReplacementPolicyCode = "CONTRACT-90",
            Refurbished = [],
        };
        var code = "CE-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, "contract_cover", entitlementType, new DateOnly(2026, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(), origin,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, prefix.ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N")[..8])])], At.AddDays(-1), "test", Obligor));
        return (tenant, Assert.Single(commissioned.Lines).UnitId!.Value, draft.PolicyVersionId);
    }

    [DatabaseFact]
    public async Task Total_loss_closes_only_the_selected_entitlement_and_keeps_its_history()
    {
        var setup = await SetUpAsync("tl", EntitlementType.Contract, UnitOrigin.Purchased);
        var created = await InvokeAsync<CreateEntitlementResult>(setup.Tenant, new CreateEntitlement(Guid.NewGuid(), setup.Unit, "contract_cover",
            DateOnly.FromDateTime(At), Obligor, At, "service.desk", new Dictionary<string, DateTime> { [StartTrigger.Invoice] = At }, setup.PolicyVersion));

        var closed = await InvokeAsync<EntitlementChangeResult>(setup.Tenant, new CloseEntitlement(Guid.NewGuid(), created.EntitlementId,
            EntitlementClosedReason.TerminatedTotalLoss, DateOnly.FromDateTime(At.AddDays(10)), At.AddDays(10), "service.manager"));

        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.TerminatedTotalLoss), (closed.Status, closed.ClosedReason));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var row = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking().SingleAsync(e => e.EntitlementId == created.EntitlementId);
        Assert.Equal(EntitlementClosedReason.TerminatedTotalLoss, row.ClosedReason);
    }

    [DatabaseFact]
    public async Task Customer_supplied_unit_gets_only_the_explicit_contract_entitlement()
    {
        var setup = await SetUpAsync("cs", EntitlementType.Contract, UnitOrigin.CustomerSupplied);
        var created = await InvokeAsync<CreateEntitlementResult>(setup.Tenant, new CreateEntitlement(Guid.NewGuid(), setup.Unit, "contract_cover",
            DateOnly.FromDateTime(At), Obligor, At, "service.desk", new Dictionary<string, DateTime> { [StartTrigger.Invoice] = At }, setup.PolicyVersion,
            OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, SourceDocument: new DocumentRef("service_contract", Guid.NewGuid())));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var rows = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking().Where(e => e.UnitId == setup.Unit).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal((created.EntitlementId, EntitlementType.Contract, "contract_cover"), (row.EntitlementId, row.Type, row.CoverageFamily));
    }

    // Cases 27, 110 and 45: ownership changes that are not a plain first sale. The base policy is required on sale; the
    // requirement flow (P2-T13) is what creates, moves or closes cover, so every scenario ends in that command path.
    private async Task<(TenantId Tenant, Guid Unit)> ResaleSetUpAsync(string prefix, string transferable, StartRule? start = null)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("RS-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var code = "RS-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2020, 1, 1),
            PolicyValidatorTestsTerms.Standard24() with { ReplacementPolicyCode = "RS-90", Transferable = transferable, Start = start ?? PolicyValidatorTestsTerms.Standard24().Start }, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryWarrantyPorts>().Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, code, true));
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N"))])], At.AddDays(-20), "test", Obligor));
        return (tenant, commissioned.Lines[0].UnitId!.Value);
    }

    private Task<OpenOwnershipResult> SellAsync(TenantId tenant, Guid unit, Guid party, DateTime at, string partyType = PartyType.Customer, bool groupMember = false) =>
        InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, party, partyType, EpisodeReason.Sale, at, "sales", Obligor, EpisodeSource.Delivery,
            Document: new DocumentRef("delivery", Guid.NewGuid(), 1), PartyIsGroupMember: groupMember));

    private async Task<Data.Entities.EntitlementRequirement> RequirementAsync(TenantId tenant, Guid episode, string status)
    {
        for (var i = 0; i < 150; i++)
        {
            using var scope = fixture.ScopeFor(tenant);
            var row = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().EntitlementRequirements.AsNoTracking().SingleOrDefaultAsync(r => r.OwnershipEpisodeId == episode && r.Status == status);
            if (row is not null) return row;
            await Task.Delay(100);
        }

        throw new TimeoutException($"The requirement of episode {episode} did not reach {status}.");
    }

    private async Task<List<Data.Entities.Entitlement>> BaseAsync(TenantId tenant, Guid unit)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking().Where(e => e.UnitId == unit && e.CoverageFamily == CoverageFamilyCode.Base).ToListAsync();
    }

    [DatabaseFact]
    public async Task Inter_company_transfer_waits_for_the_first_external_sale_and_a_resale_never_restarts_cover()
    {
        // cases 27 and 110: manufacturer cover starts at the first external sale; the reserve stays with the obligor
        var (tenant, unit) = await ResaleSetUpAsync("ic", Transferability.Yes, new StartRule(StartTrigger.FirstExternalSale, [], 365));
        var sister = await SellAsync(tenant, unit, Guid.NewGuid(), new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), PartyType.GroupEntity, groupMember: true);
        await Task.Delay(1000);
        using (var scope = fixture.ScopeFor(tenant))
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().EntitlementRequirements.AnyAsync(r => r.OwnershipEpisodeId == sister.EpisodeId));
        }

        Assert.Empty(await BaseAsync(tenant, unit));
        var external = await SellAsync(tenant, unit, Guid.NewGuid(), new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc));
        var satisfied = await RequirementAsync(tenant, external.EpisodeId, EntitlementRequirementStatus.Satisfied);
        var cover = Assert.Single(await BaseAsync(tenant, unit));
        Assert.Equal(((Guid?)cover.EntitlementId, (DateOnly?)new DateOnly(2026, 11, 20), Obligor), (satisfied.EntitlementId, cover.StartDate, cover.ObligorEntityId));

        // the customer resells: the same cover follows, its start does not move
        var resale = await SellAsync(tenant, unit, Guid.NewGuid(), new DateTime(2027, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        await RequirementAsync(tenant, resale.EpisodeId, EntitlementRequirementStatus.Satisfied);
        var after = Assert.Single(await BaseAsync(tenant, unit));
        Assert.Equal((cover.EntitlementId, cover.StartDate, cover.EndDate), (after.EntitlementId, after.StartDate, after.EndDate));
    }

    [DatabaseFact]
    public async Task Customer_resale_moves_cover_only_when_the_policy_allows_it()
    {
        // case 45, transferable: the cover follows to the new owner; the previous owner is kept in the ledger only
        var (yesTenant, yesUnit) = await ResaleSetUpAsync("ry", Transferability.Yes);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var sold = await SellAsync(yesTenant, yesUnit, first, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));
        await RequirementAsync(yesTenant, sold.EpisodeId, EntitlementRequirementStatus.Satisfied);
        var resold = await SellAsync(yesTenant, yesUnit, second, new DateTime(2027, 1, 15, 9, 0, 0, DateTimeKind.Utc));
        await RequirementAsync(yesTenant, resold.EpisodeId, EntitlementRequirementStatus.Satisfied);
        var moved = Assert.Single(await BaseAsync(yesTenant, yesUnit));
        Assert.Equal((second, EntitlementStatus.Active), (moved.OwnerPartyRef, moved.Status));

        // not transferable: the cover closes at the resale and the new owner's requirement is waived by the policy
        var (noTenant, noUnit) = await ResaleSetUpAsync("rn", Transferability.No);
        await RequirementAsync(noTenant, (await SellAsync(noTenant, noUnit, first, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc))).EpisodeId, EntitlementRequirementStatus.Satisfied);
        var refused = await SellAsync(noTenant, noUnit, second, new DateTime(2027, 1, 15, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal("policy", (await RequirementAsync(noTenant, refused.EpisodeId, EntitlementRequirementStatus.Waived)).WaivedBy);
        var closed = Assert.Single(await BaseAsync(noTenant, noUnit));
        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.NotTransferable, first, new DateOnly(2027, 1, 15)), (closed.Status, closed.ClosedReason, closed.OwnerPartyRef, closed.ClosedEffective));

        // registration required: an exception until the registration is recorded, then the cover moves
        var (regTenant, regUnit) = await ResaleSetUpAsync("rr", Transferability.RegistrationRequired);
        await RequirementAsync(regTenant, (await SellAsync(regTenant, regUnit, first, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc))).EpisodeId, EntitlementRequirementStatus.Satisfied);
        var pending = await SellAsync(regTenant, regUnit, second, new DateTime(2027, 1, 15, 9, 0, 0, DateTimeKind.Utc));
        var waiting = await RequirementAsync(regTenant, pending.EpisodeId, EntitlementRequirementStatus.Exception);
        Assert.Equal(EntitlementMissingInput.TransferRegistrationRequired, waiting.MissingInput);
        Assert.Equal(first, Assert.Single(await BaseAsync(regTenant, regUnit)).OwnerPartyRef);
        Assert.Equal("evidence_required", (await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<EntitlementRequirementRow>(regTenant, new CompleteEntitlementTransfer(waiting.RequirementId, " ", new DateTime(2027, 1, 20, 9, 0, 0, DateTimeKind.Utc), "service.desk")))).Rule);
        var completed = await InvokeAsync<EntitlementRequirementRow>(regTenant, new CompleteEntitlementTransfer(waiting.RequirementId, "REG-4471", new DateTime(2027, 1, 20, 9, 0, 0, DateTimeKind.Utc), "service.desk"));
        Assert.Equal(EntitlementRequirementStatus.Satisfied, completed.Status);
        Assert.Equal(second, Assert.Single(await BaseAsync(regTenant, regUnit)).OwnerPartyRef);
    }

    // Case 66 belongs with claims (P4): repairs continue after an item's sales block; nothing in an entitlement changes
    // when the item is discontinued, and the item master's sales/service block and last service date reach this module
    // through the item port in P8/P12.
    [DatabaseFact(Skip = "Claims after discontinuation are adjudicated in P4; the item serviceability facts arrive with the item port (P8/P12).")]
    public Task Discontinued_item_remains_serviceable_until_its_last_service_date() => Task.CompletedTask;
}

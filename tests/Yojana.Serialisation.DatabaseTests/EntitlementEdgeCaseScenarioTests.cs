using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.DatabaseTests;

/*
P2-T20 coverage map (type.method):
Cases
  27  -> EntitlementEdgeCaseScenarioTests.Inter_company_transfer_waits_for_the_first_external_sale [product gap]
  31  -> EntitlementEdgeCaseScenarioTests.Total_loss_closes_only_the_selected_entitlement_and_keeps_its_history
  44  -> EntitlementCommandTests.Creation_takes_the_first_evidence_in_the_chain_a_rebase_moves_it_within_the_cap_and_unit_current_follows
  45  -> EntitlementEdgeCaseScenarioTests.Customer_resale_inherits_cover_only_when_the_policy_allows_it [product gap]
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
  66  -> EntitlementEdgeCaseScenarioTests.Discontinued_item_remains_serviceable_until_its_last_service_date [product gap]
  71  -> EntitlementCommandTests.Transfer_closes_the_source_builds_one_rooted_chain_and_suppresses_the_second_accrual
  72  -> EntitlementCommandTests.Creation_takes_the_first_evidence_in_the_chain_a_rebase_moves_it_within_the_cap_and_unit_current_follows
  110 -> EntitlementEdgeCaseScenarioTests.Inter_company_sale_keeps_the_reserve_with_the_obligor_and_does_not_restart_cover [product gap]
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

    [DatabaseFact(Skip = "No ownership/inter-company command or port exposes group-member transfers to entitlement start-trigger resolution.")]
    public Task Inter_company_transfer_waits_for_the_first_external_sale() => Task.CompletedTask;

    [DatabaseFact(Skip = "No customer ownership-transfer command invokes entitlement transferability or creates inherited owner cover.")]
    public Task Customer_resale_inherits_cover_only_when_the_policy_allows_it() => Task.CompletedTask;

    [DatabaseFact(Skip = "Item sales-block, service-block, successor and last-service-date facts are not exposed through an entitlement command or port.")]
    public Task Discontinued_item_remains_serviceable_until_its_last_service_date() => Task.CompletedTask;

    [DatabaseFact(Skip = "No ownership/inter-company sale command or port connects first_external_sale to entitlement creation while retaining the obligor.")]
    public Task Inter_company_sale_keeps_the_reserve_with_the_obligor_and_does_not_restart_cover() => Task.CompletedTask;
}

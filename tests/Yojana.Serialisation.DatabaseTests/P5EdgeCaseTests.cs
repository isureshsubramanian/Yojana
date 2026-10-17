using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Replacements;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T18a: ADR-0001 edge cases allocated to the RMA and replacement slice.</summary>
[Collection("serialisation host")]
public sealed class P5EdgeCaseTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Case_56_remaining_warranty_moves_to_the_replacement_without_a_second_accrual()
    {
        // The focused P5-T05 scenario also proves ticket T60 through Create/ShipWarrantyReplacement commands.
        await new WarrantyReplacementTests(fixture)
            .Ship_transfers_once_repoints_the_asset_tag_issues_stock_and_publishes_suppressed_accrual();
    }

    [DatabaseFact]
    public async Task Case_58_a_different_model_inherits_the_source_policy_and_records_both_items()
    {
        // WarrantyReplacementTests deliberately commissions the replacement under a different item.
        await new WarrantyReplacementTests(fixture)
            .Every_reason_uses_its_required_term_rule(ReplacementReason.WarrantyDefect, null, "STD-90", "2026-12-04");
    }

    [DatabaseFact]
    public async Task Case_59_chained_replacements_keep_one_root_and_compound_the_floor()
    {
        await new EntitlementCommandTests(fixture)
            .Transfer_closes_the_source_builds_one_rooted_chain_and_suppresses_the_second_accrual();
    }

    [DatabaseFact]
    public async Task Case_74_an_unreturned_advance_replacement_is_billed_and_suspends_privilege()
    {
        await new AdvanceReplacementTests(fixture)
            .Overdue_is_published_once_hold_blocks_then_extend_bill_and_lift_are_audited();
    }

    [DatabaseFact]
    public async Task Case_75_a_different_returned_serial_stays_in_quarantine_as_a_mismatch()
    {
        await new RmaTests(fixture).Receipt_exceptions_are_explicit_and_never_rebind_the_authorised_unit(ReturnOutcome.Mismatch);
    }

    [DatabaseFact]
    public async Task Case_76_a_counterfeit_return_gets_a_provisional_identity_and_never_enters_stock()
    {
        await new RmaTests(fixture).Receipt_exceptions_are_explicit_and_never_rebind_the_authorised_unit(ReturnOutcome.Counterfeit);
    }

    [DatabaseFact]
    public async Task Case_77_the_original_sale_is_resolved_from_the_unit_not_chosen_by_the_returning_party()
    {
        await new RmaTests(fixture).Proof_of_purchase_request_resolves_the_serial_and_list_supports_stage_age_summary_and_find();
    }

    [DatabaseFact]
    public async Task Case_79_a_partial_kit_return_uses_the_sale_allocation_and_marks_the_parent_incomplete()
    {
        await new RmaTests(fixture).Case_79_child_return_stores_sale_allocation_closes_the_link_and_marks_parent_incomplete();
    }

    [DatabaseFact]
    public async Task Case_80_a_second_return_matches_the_current_owner_and_latest_shipment()
    {
        await new RmaTests(fixture).Case_80_second_return_uses_the_current_owner_episode_and_latest_shipment();
    }

    [DatabaseFact]
    public async Task Case_81_credit_closes_cover_as_refunded_and_creates_no_replacement_link()
    {
        await new RmaTests(fixture).Every_disposition_has_the_specified_custody_entitlement_and_ownership_effect(
            RmaDisposition.Credit, LocationKind.Warehouse, EntitlementClosedReason.Refunded, true);
    }

    [DatabaseFact]
    public async Task Case_82_price_difference_is_explicit_on_the_link_and_document_line()
    {
        var (customerCase, created, shipped) = await CreateAndShipAsync("case82", command => command with
        {
            PricePolicy = ReplacementPricePolicy.Difference,
            DifferenceAmount = 175m,
        });

        Assert.Equal((ReplacementPricePolicy.Difference, 175m),
            (created.Document.Document.PricePolicy, created.Document.Document.DifferenceAmount));
        var line = Assert.Single(created.Document.Lines);
        Assert.Equal((WarrantyReplacementLineKind.Replacement, 175m, 175m), (line.Kind, line.UnitAmount, line.LineAmount));
        await AssertReplacementConsistencyAsync(customerCase, shipped);
    }

    [DatabaseFact]
    public async Task Case_83_restocking_fee_is_service_revenue_and_never_inventory_value()
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, "case83");
        await P5AdvanceTestData.InvokeAsync<ReturnReasonRow>(fixture, world.Tenant,
            new DefineReturnReason("changed_mind", "Changed mind", RestockFeeRule.Percentage, 10m));
        var customerCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "fee");
        var command = P5AdvanceTestData.CreateCommand(customerCase, ReplacementSourceKind.Claim, customerCase.ClaimId,
            At.AddDays(2), null) with
        {
            ReplacementReason = ReplacementReason.Goodwill,
            ReplacementPolicyCode = "ADV-90",
            ReturnReasonCode = "changed_mind",
            RestockingFeeBasis = 800m,
        };
        var created = await P5AdvanceTestData.InvokeAsync<CreateWarrantyReplacementResult>(fixture, world.Tenant, command);

        Assert.Equal(80m, created.Document.Document.RestockingFeeAmount);
        var fee = Assert.Single(created.Document.Lines, line => line.Kind == WarrantyReplacementLineKind.ServiceRevenue);
        Assert.Equal((80m, "percentage:changed_mind"), (fee.LineAmount, fee.RevenueRule));
        var shipped = await P5AdvanceTestData.InvokeAsync<ShipWarrantyReplacementResult>(fixture, world.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), created.Document.Document.WarrantyReplacementId,
                new DateOnly(2026, 9, 4), At.AddDays(3)));
        await AssertReplacementConsistencyAsync(customerCase, shipped);
    }

    [DatabaseFact]
    public async Task Case_84_an_in_transit_unit_can_enter_the_normal_RMA_receipt_path()
    {
        await new RmaTests(fixture).Six_stages_are_adjacent_illegal_transitions_are_refused_and_all_commands_replay();
    }

    [DatabaseFact]
    public async Task Case_85_cross_shipment_closes_after_both_document_orders_and_not_on_a_mismatch()
    {
        await new AdvanceReplacementTests(fixture).Cross_shipment_closes_in_both_orders_and_a_mismatch_never_closes_the_link();
    }

    [DatabaseFact]
    public async Task Case_86_vendor_replacement_moves_only_the_inbound_chain_and_records_what_returned()
    {
        await new VendorReturnTests(fixture).Return_to_vendor_and_vendor_replacement_move_ownership_and_only_the_inbound_chain();
    }

    [DatabaseFact]
    public async Task Case_88_DOA_requires_a_new_replacement_and_carries_no_restocking_fee()
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, "case88");
        await P5AdvanceTestData.InvokeAsync<ReplacementPolicyRow>(fixture, world.Tenant,
            new DefineReplacementPolicy("PRESERVE", ReplacementRule.CarryRemaining, null, null, null));
        await P5AdvanceTestData.InvokeAsync<ReturnReasonRow>(fixture, world.Tenant,
            new DefineReturnReason("doa_fee", "DOA", RestockFeeRule.Fixed, 50m));
        var customerCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "doa");
        var command = P5AdvanceTestData.CreateCommand(customerCase, ReplacementSourceKind.Claim,
            customerCase.ClaimId, At.AddDays(2), null) with
        {
            ReplacementReason = ReplacementReason.DeadOnArrival,
            ReplacementPolicyCode = "PRESERVE",
            ReturnReasonCode = "doa_fee",
            RestockingFeeBasis = 500m,
            ReplacementConditionAllowed = ReplacementConditionAllowed.New,
        };

        var created = await P5AdvanceTestData.InvokeAsync<CreateWarrantyReplacementResult>(fixture, world.Tenant, command);
        Assert.Equal(0m, created.Document.Document.RestockingFeeAmount);
        Assert.DoesNotContain(created.Document.Lines, line => line.Kind == WarrantyReplacementLineKind.ServiceRevenue);

        var shipped = await P5AdvanceTestData.InvokeAsync<ShipWarrantyReplacementResult>(fixture, world.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), created.Document.Document.WarrantyReplacementId,
                new DateOnly(2026, 9, 4), At.AddDays(3)));
        Assert.Equal(ConditionGrades.New, shipped.ReplacementCondition);
        await AssertReplacementConsistencyAsync(customerCase, shipped);
    }

    [DatabaseFact]
    public async Task Case_89_a_closed_account_does_not_block_the_physical_return()
    {
        await new RmaTests(fixture).Case_89_return_from_a_closed_account_is_accepted_against_the_recorded_owner_episode();
    }

    [DatabaseFact]
    public async Task Case_92_refurbished_stock_cannot_satisfy_new_or_unconsented_EU_replacement_demand()
    {
        await new WarrantyReplacementTests(fixture).Doa_requires_new_and_an_EU_consumer_requires_consent_for_refurbished_stock();
    }

    [DatabaseFact]
    public async Task Case_93_the_replacement_document_and_event_disclose_refurbished_condition()
    {
        var (customerCase, created, shipped) = await CreateAndShipAsync("case93", command => command with
        {
            ConsentRefurbished = true,
            ReplacementConditionAllowed = ReplacementConditionAllowed.NewOrRefurbished,
        }, ConditionGrades.RefurbishedA);

        Assert.Equal(ConditionGrades.RefurbishedA, shipped.ReplacementCondition);
        Assert.Equal(ConditionGrades.RefurbishedA, created.Document.ReplacementUnit.ConditionGrade);
        await AssertReplacementConsistencyAsync(customerCase, shipped);
    }

    [DatabaseFact]
    public async Task Case_126_the_customer_asset_tag_follows_the_replacement_and_requests_a_label()
    {
        await new WarrantyReplacementTests(fixture)
            .Ship_transfers_once_repoints_the_asset_tag_issues_stock_and_publishes_suppressed_accrual();
    }

    [DatabaseFact]
    public async Task Case_128_seller_error_preserves_the_clock_and_the_return_can_restock_as_new()
    {
        // Together these commands reproduce ticket T20: exchange the wrong configuration and inspect the returned unit.
        await new WarrantyReplacementTests(fixture)
            .Every_reason_uses_its_required_term_rule(ReplacementReason.SellerError, "PRESERVE", "PRESERVE", "2026-09-30");
        await new RmaTests(fixture).Every_disposition_has_the_specified_custody_entitlement_and_ownership_effect(
            RmaDisposition.RestockAsNew, LocationKind.Warehouse, null, true);
    }

    [DatabaseFact]
    public async Task Case_130_entitlement_change_re_adjudicates_open_work_and_keeps_prior_decisions()
    {
        await new CoverageTests(fixture).Coverage_change_re_adjudicates_open_rma_and_claim_but_not_closed_work();
    }

    [DatabaseFact]
    public async Task Case_144_wrong_replacement_is_reversed_and_reapplied_without_losing_the_bad_link()
    {
        // This is the command reproduction for ticket T73; certificate output is asserted by its focused P5-T13 test.
        await new CorrectReplacementLinkTests(fixture)
            .Correction_swaps_whole_unit_state_preserves_policy_and_reason_and_replays_once();
    }

    [DatabaseFact]
    public async Task Ticket_T8_refurbishment_re_serialisation_keeps_cover_on_the_unit_at_any_depth()
    {
        await new RefurbishmentTests(fixture)
            .Same_unit_new_serial_twice_preserves_unit_ownership_and_entitlements_with_two_link_lineage();
    }

    [DatabaseFact]
    public async Task Ticket_T51_rework_keeps_the_existing_units_and_never_voids_them()
    {
        await new RefurbishmentTests(fixture)
            .Rework_keeps_identity_never_voids_and_requests_release_record_reissue_only_after_as_built_change();
    }

    private async Task<(P5CustomerCase CustomerCase, CreateWarrantyReplacementResult Created, ShipWarrantyReplacementResult Shipped)>
        CreateAndShipAsync(string prefix, Func<CreateWarrantyReplacement, CreateWarrantyReplacement> change, string? grade = null)
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, prefix);
        var customerCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, prefix);
        if (grade is not null)
        {
            await P5AdvanceTestData.InvokeAsync<GradeUnitResult>(fixture, world.Tenant,
                new GradeUnit(Guid.NewGuid(), customerCase.ReplacementUnitId, grade, "graded_receipt",
                    At.AddDays(1), "quality", world.Entity));
        }
        var command = change(P5AdvanceTestData.CreateCommand(customerCase, ReplacementSourceKind.Claim,
            customerCase.ClaimId, At.AddDays(2), null));
        var created = await P5AdvanceTestData.InvokeAsync<CreateWarrantyReplacementResult>(fixture, world.Tenant, command);
        var shipped = await P5AdvanceTestData.InvokeAsync<ShipWarrantyReplacementResult>(fixture, world.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), created.Document.Document.WarrantyReplacementId,
                new DateOnly(2026, 9, 4), At.AddDays(3)));
        return (customerCase, created, shipped);
    }

    private async Task AssertReplacementConsistencyAsync(P5CustomerCase customerCase, ShipWarrantyReplacementResult shipped)
    {
        using var scope = fixture.ScopeFor(customerCase.World.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var originalOwner = await db.Episodes.SingleAsync(row => row.UnitId == customerCase.OriginalUnitId
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var originalCustody = await db.Episodes.SingleAsync(row => row.UnitId == customerCase.OriginalUnitId
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        var replacementOwner = await db.Episodes.SingleAsync(row => row.UnitId == customerCase.ReplacementUnitId
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var replacementCustody = await db.Episodes.SingleAsync(row => row.UnitId == customerCase.ReplacementUnitId
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((customerCase.World.Customer, LocationKind.CustomerSite),
            (originalOwner.PartyRef, originalCustody.LocationKind));
        Assert.Equal((customerCase.World.Customer, LocationKind.CustomerSite),
            (replacementOwner.PartyRef, replacementCustody.LocationKind));
        Assert.Equal(EntitlementStatus.Closed,
            (await db.Entitlements.SingleAsync(row => row.EntitlementId == customerCase.SourceEntitlementId)).Status);
        var target = await db.Entitlements.SingleAsync(row => row.EntitlementId == shipped.TargetEntitlementId);
        Assert.Equal((EntitlementStatus.Active, customerCase.SourceEntitlementId, customerCase.SourceEntitlementId, (Guid?)null),
            (target.Status, target.ParentEntitlementId, target.RootEntitlementId, target.AccrualCohortId));
        Assert.Equal(2, await db.Identifiers.CountAsync(row => row.UnitId == customerCase.OriginalUnitId
            || row.UnitId == customerCase.ReplacementUnitId));
        Assert.Contains(await db.Events.Where(row => row.UnitId == customerCase.ReplacementUnitId)
            .Select(row => row.EventType).ToListAsync(), type => type == ReplacementEventType.Shipped);
    }
}

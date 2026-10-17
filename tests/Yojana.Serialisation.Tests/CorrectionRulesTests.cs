using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;

namespace Yojana.Serialisation.Tests;

public sealed class CorrectionRulesTests
{
    [Fact]
    public void Every_correction_command_has_its_exact_non_empty_reason_vocabulary()
    {
        Assert.Equal(["cross_recorded_at_build", "cross_recorded_at_receipt", "label_applied_to_wrong_unit"], SwapIdentifiersReason.All);
        Assert.Equal(["wrong_item_at_receipt", "wrong_item_at_build", "configuration_mismatch"], CorrectItemReason.All);
        Assert.Equal(["component_not_captured", "wrong_component_recorded", "late_serialization", "bom_change_after_release"], CorrectAsBuiltReason.All);
        Assert.Equal(["coding_error_at_item_setup", "policy_change"], LateSerializationReason.All);
        Assert.Equal(["work_order_quantity_reduced", "work_order_cancelled", "duplicate_allocation"], VoidUnitReason.All);
        Assert.Equal(["duplicate_record", "migration_duplicate", "vendor_serial_keyed_twice"], MergeUnitsReason.All);
        Assert.Equal(["vendor_list", "vendor_list_supplement", "late_capture"], AssignIdentifiersReason.All);
        Assert.Equal(["allocation_error", "work_order_changed", "work_order_cancelled"], ReassignAllocationReason.All);
        Assert.Equal(["shipped_wrong_units", "copied_serials_without_scan", "vendor_list_wrong", "returned_never_delivered"], CorrectDocumentSerialsReason.All);
        Assert.Equal(["released_under_wrong_order", "wrong_delivery_selected", "invoice_split_error"], CorrectSourceDocumentReason.All);
        Assert.All(CorrectionCommand.All, command => Assert.NotEmpty(CorrectionRules.Reasons(command)));
    }

    [Fact]
    public void Unknown_reason_is_refused_by_the_shared_validator()
    {
        var refusal = Assert.Throws<CommandRefusedException>(() => CorrectionRules.ValidateReason(CorrectionCommand.SwapIdentifiers, CorrectItemReason.WrongItemAtBuild));
        Assert.Equal("reason_unknown", refusal.Rule);
    }

    [Fact]
    public void A_correction_can_link_to_any_earlier_correction_without_a_time_limit()
    {
        Assert.Null(CorrectionRules.LinkProblem(Guid.NewGuid(), Guid.NewGuid()));
        var id = Guid.NewGuid();
        Assert.Equal("correction_cannot_correct_itself", CorrectionRules.LinkProblem(id, id)!.Rule);
    }
}

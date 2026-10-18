using Xunit;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P6-T13 ownership map: one named database scenario for every P6 catalogue case.</summary>
[Collection("serialisation host")]
public sealed class P6EdgeCaseScenarioTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public Task Case_101_repair_or_refurbishment_creates_a_new_version_and_leaves_v1_untouched() =>
        new CertificateDatabaseTests(fixture).Refurbishment_release_absorbs_overlapping_release_triggers_and_redelivery();

    [DatabaseFact]
    public Task Case_102_reconstructed_certificates_require_explicit_unknowns_and_evidence() =>
        new CertificateDatabaseTests(fixture).Unreleased_units_and_incomplete_reconstruction_provenance_are_typed_refusals();

    [DatabaseFact]
    public Task Case_103_component_and_as_built_changes_are_immutable_release_record_facts() =>
        new CertificateDatabaseTests(fixture).Release_versions_are_immutable_signed_snapshots_and_preserve_the_whole_unit();

    [DatabaseFact]
    public Task Case_104_changed_owner_receives_a_Copy_of_Bs_snapshot_and_never_As_name() =>
        new ReferenceScenarioTests(fixture).Steps_0_to_8_reproduce_the_reference_end_state_without_rewriting_history();

    [DatabaseFact]
    public Task Case_105_electronic_signature_is_bound_to_the_payload_hash_and_cannot_transfer() =>
        new CertificateDatabaseTests(fixture).Release_versions_are_immutable_signed_snapshots_and_preserve_the_whole_unit();

    [DatabaseFact]
    public Task Case_106_language_and_issuing_entity_are_explicit_certificate_inputs() =>
        new CertificateDatabaseTests(fixture).Release_and_warranty_triggers_never_cross_chains_and_warranty_keeps_owner_snapshot();

    [DatabaseFact]
    public Task Case_107_distributor_branding_is_an_issue_time_snapshot_kept_on_reprint() =>
        new CertificateSelectionTests(fixture).Selection_preview_issues_version_one_reprints_Copy_lists_refusals_and_replays_exactly();

    [DatabaseFact]
    public async Task Case_125_blank_labels_are_rejected_and_reprints_send_the_stored_payload()
    {
        var labels = new LabelTests(fixture);
        await labels.Ticket_T6_required_empty_and_Code39_asterisk_are_rejected_before_any_printer_send();
        await labels.Tickets_T44_and_T50_keep_the_printed_expiry_and_reprint_the_stored_payload_after_template_and_entitlement_changes();
    }
}

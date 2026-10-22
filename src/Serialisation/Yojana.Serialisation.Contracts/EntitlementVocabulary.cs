namespace Yojana.Serialisation.Contracts;

// Entitlement vocabularies (MOD-06, ADR-0001 "Core model: WarrantyEntitlement" and "Warranty entitlement engine"). Stored
// as strings like the unit vocabularies in Vocabulary.cs. Coverage families are not here: they are tenant reference
// data (table coverage_family), and a tenant adds one without a code change.

public static class EntitlementType
{
    public const string Manufacturer = "manufacturer";
    public const string Dealer = "dealer";
    public const string Extended = "extended";
    public const string Contract = "contract";
    public const string Statutory = "statutory";
    public const string RecallCampaign = "recall_campaign";
    public const string Goodwill = "goodwill";
}

/// <summary>Outbound: we owe the customer. Inbound: the vendor owes us (case 70).</summary>
/// <summary>
/// Coverage families are tenant reference data, but one code is structural: the base warranty every sold unit should
/// carry, which the gap report, backfill and creation on sale look for. A tenant defines it under this code.
/// </summary>
public static class CoverageFamilyCode
{
    public const string Base = "base_warranty";

    /// <summary>Supplier cover (direction inbound, P2-T16): its own family, so it never meets the customer's base cover under the one-live-per-family rule.</summary>
    public const string Supplier = "supplier_warranty";
}

public static class EntitlementDirection
{
    public const string Outbound = "outbound";
    public const string Inbound = "inbound";
    public const string VendorFacing = "vendor_facing";
}

public static class EntitlementScope
{
    public const string Unit = "unit";
    public const string Component = "component";
    public const string Kit = "kit";
}

/// <summary>Live means any status but closed; the one-live-per-family index is filtered on it.</summary>
public static class EntitlementStatus
{
    public const string PendingEvidence = "pending_evidence";
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Closed = "closed";
}

public static class EntitlementRequirementStatus
{
    public const string Open = "open";
    public const string Satisfied = "satisfied";
    public const string Exception = "exception";
    public const string Waived = "waived";
}

public static class WarrantyLineExceptionStatus
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Waived = "waived";
}

public static class WarrantyLineMissingInput
{
    public const string SerialsNotCaptured = "serials_not_captured";
    public const string ReferencedDocumentNotPosted = "referenced_document_not_posted";
    public const string TargetNotSerialised = "target_not_serialised";
    public const string NoPolicy = "no_policy";
    public const string SerialsPendingAsn = "serials_pending_asn";
    public const string RegulatedJurisdiction = "regulated_jurisdiction";
    public const string CreationFailed = "creation_failed";
}

public static class EntitlementMissingInput
{
    public const string NoPolicy = "no_policy";
    public const string NoStartEvidence = "no_start_evidence";
    public const string SerialsPendingAsn = "serials_pending_asn";
    public const string CreationFailed = "creation_failed";
    // Resale by the customer (case 45): the policy's transferability decides whether cover follows the unit.
    public const string TransferRegistrationRequired = "transfer_registration_required";
    public const string TransferFeeRequired = "transfer_fee_required";
}

public static class EntitlementClosedReason
{
    public const string Expired = "expired";
    public const string MigratedTermination = "terminated";
    public const string Replaced = "replaced";
    public const string Refunded = "refunded";
    public const string TransferredOut = "transferred_out";
    public const string Voided = "voided";
    public const string TerminatedTotalLoss = "terminated_total_loss";
    public const string Superseded = "superseded";
    public const string ReplacedByChange = "replaced_by_change";
    public const string ReturnedToStock = "returned_to_stock";
    public const string VoidedShippingError = "voided_shipping_error";
    public const string VoidedDocumentCorrection = "voided_document_correction";
    public const string SourceDocumentCorrected = "source_document_corrected";
    public const string IncludedInError = "included_in_error";
    public const string MigrationDeltaReplaced = "migration_delta_replaced";
    /// <summary>The unit was resold and the policy does not let cover follow it (case 45).</summary>
    public const string NotTransferable = "not_transferable";
}

public static class StartTrigger
{
    public const string Receipt = "receipt";
    public const string Ship = "ship";
    public const string Deliver = "deliver";
    public const string Install = "install";
    public const string Register = "register";
    public const string Invoice = "invoice";
    public const string ConsumptionFromConsignment = "consumption_from_consignment";
    public const string FirstExternalSale = "first_external_sale";
    public const string PlanPurchase = "plan_purchase";
    public const string BaseExpiry = "base_expiry";
}

public static class DurationBasis
{
    public const string Time = "time";
    public const string Usage = "usage";
    public const string Both = "both";
}

public static class Transferability
{
    public const string Yes = "yes";
    public const string No = "no";
    public const string Fee = "fee";
    public const string RegistrationRequired = "registration_required";
}

/// <summary>The accounting tag: an assurance warranty comes with the sale (reserve); a service warranty is sold separately (deferred revenue).</summary>
public static class AssuranceOrService
{
    public const string Assurance = "assurance";
    public const string Service = "service";
}

/// <summary>How the entitlement came to exist; backfilled and migrated rows carry their evidence list.</summary>
public static class EntitlementProvenance
{
    public const string Created = "created";
    public const string Transferred = "transferred";
    public const string Superseding = "superseding";
    public const string Backfilled = "backfilled";
    public const string Migrated = "migrated";
    public const string Reconstructed = "reconstructed";
    public const string ServicePart = "service_part";
}

public static class CoverageDecisionReason
{
    public const string EntitlementChanged = "entitlement_changed";
}

public static class CoverageWorkType
{
    public const string Rma = "rma";
    public const string Claim = "claim";
    public const string ServiceOrder = "service_order";
}

public static class CoverageEventType
{
    public const string DecisionChanged = "coverage.decision_changed";
}

public static class ReplacementRule
{
    public const string CarryRemaining = "carry_remaining";
    public const string MaxRemainingOrMinDays = "max_remaining_or_min_days";
    public const string RestartFull = "restart_full";
    public const string StatutoryExtensionOnce = "statutory_extension_once";
}

public static class PolicyVersionStatus
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Closed = "closed";
    public const string Retired = "retired";
}

public static class AmendmentReason
{
    public const string Purchase = "purchase";
    public const string Goodwill = "goodwill";
    public const string Correction = "correction";
    public const string StatutoryExtension = "statutory_extension";
    public const string DisputeSettlement = "dispute_settlement";
    public const string SellerError = "seller_error";

    public static readonly IReadOnlyList<string> All = [Purchase, Goodwill, Correction, StatutoryExtension, DisputeSettlement, SellerError];
}

/// <summary>Where an amendment's value came from (P2-T08b): the policy's value per unit of term, or the user.</summary>
public static class AmendmentValueSource
{
    public const string Policy = "policy";
    public const string Entered = "entered";
}

public static class BackfillReason
{
    public const string HistoricalGap = "historical_gap";
    public const string DropShipGap = "drop_ship_gap";
    public const string MigrationGap = "migration_gap";

    public static readonly IReadOnlyList<string> All = [HistoricalGap, DropShipGap, MigrationGap];
}

public static class BackfillStartBasis
{
    public const string DeliveryDocument = "delivery_document";
    public const string VendorShipConfirmation = "vendor_ship_confirmation";
    /// <summary>The incumbent's delivery document recorded on a migrated ownership fact (P9-T09).</summary>
    public const string MigratedDelivery = "migrated_delivery";
}

/// <summary>The fields AmendEntitlement changes, as named on EntitlementAmendment rows and in Entitlement.Amended.</summary>
public static class AmendedField
{
    public const string StartDate = "start_date";
    public const string Term = "term";
    public const string EndDate = "end_date";
    public const string Coverage = "coverage";
}

/// <summary>Ledger event types of the entitlement state machine (MOD-06). They are unit events: the entitlement is attached to the unit.</summary>
public static class EntitlementEventType
{
    public const string Created = "entitlement.created";
    public const string EvidenceAttached = "entitlement.evidence_attached";
    public const string Rebased = "entitlement.rebased";
    public const string Suspended = "entitlement.suspended";
    public const string Resumed = "entitlement.resumed";
    public const string Voided = "entitlement.voided";
    public const string Closed = "entitlement.closed";
    public const string Transferred = "entitlement.transferred";
    public const string ExtendedSold = "entitlement.extended_sold";
    public const string Amended = "entitlement.amended";
    public const string Superseded = "entitlement.superseded";
    /// <summary>The unit was resold and its cover followed it to the new owner (case 45); the previous owner stays in the ledger only.</summary>
    public const string OwnerTransferred = "entitlement.owner_transferred";
}

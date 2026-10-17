namespace Yojana.Serialisation.Contracts;

// Controlled vocabularies of the Serialisation module (ADR-0001 "Core model"). Stored as lower snake_case
// strings so the database reads without a lookup table; other modules reference these constants, never
// their own copies. Adding a value is an ADR amendment first.

/// <summary>PhysicalUnit lifecycle status (ADR-0001 PhysicalUnit field table and state machine).</summary>
public static class LifecycleStatus
{
    public const string PendingIdentifier = "pending_identifier";
    public const string PreRelease = "pre_release";
    public const string Void = "void";
    public const string Released = "released";
    public const string Dormant = "dormant";
    public const string ScrappedPreRelease = "scrapped_pre_release";
    public const string Scrapped = "scrapped";
    public const string Decommissioned = "decommissioned";
    /// <summary>Alias left behind by MergeUnits; every row of the absorbed unit points at the survivor.</summary>
    public const string Merged = "merged";

    public static readonly IReadOnlyList<string> All = [PendingIdentifier, PreRelease, Void, Released, Dormant, ScrappedPreRelease, Scrapped, Decommissioned, Merged];
}

/// <summary>Overlay flags; any status may carry them and they block entitlement evaluation (cases 30, 31, 67, 76).</summary>
public static class OverlayFlag
{
    public const string Lost = "lost";
    public const string Stolen = "stolen";
    public const string WrittenOff = "written_off";
    public const string CloneSeen = "clone_seen";
    public const string Recalled = "recalled";
    public const string AuthenticityFailed = "authenticity_failed";
    public const string DocumentMismatch = "document_mismatch";
    /// <summary>The production order has not yet supplied this unit's real serial.</summary>
    public const string AwaitingVendorSerial = "awaiting_vendor_serial";
}

/// <summary>How a unit came into the tenant's world.</summary>
public static class UnitOrigin
{
    public const string Manufactured = "manufactured";
    public const string Purchased = "purchased";
    public const string VendorAsn = "vendor_asn";
    public const string TradeIn = "trade_in";
    public const string CustomerSupplied = "customer_supplied";
    public const string LegacyUnknown = "legacy_unknown";
    public const string Migrated = "migrated";
}

/// <summary>Where in the flow an item of this class gets its identifier (case 6).</summary>
public static class SerialisationPoint
{
    public const string Receipt = "receipt";
    public const string ProductionRelease = "production_release";
    public const string PickPack = "pick_pack";
    public const string Ship = "ship";
    public const string Never = "never";
}

/// <summary>How much of a unit's past the ledger holds (migration).</summary>
public static class HistoryDepth
{
    public const string Full = "full";
    public const string Partial = "partial";
    public const string OpeningOnly = "opening_only";
}

/// <summary>UnitIdentifier types (Identity rule 2). Each is its own row with its own issuer.</summary>
public static class IdentifierType
{
    public const string ManufacturerSerial = "manufacturer_serial";
    public const string InternalSerial = "internal_serial";
    public const string VendorSerial = "vendor_serial";
    public const string CustomerSerial = "customer_serial";
    public const string Imei = "imei";
    public const string Mac = "mac";
    public const string UdiDi = "udi_di";
    public const string UdiPi = "udi_pi";
    public const string Uii = "uii";
    public const string Vin = "vin";
    public const string Sgtin = "sgtin";
    public const string Giai = "giai";
    public const string ChannelOrderRef = "channel_order_ref";
    public const string CustomerAssetTag = "customer_asset_tag";
    public const string SoftwareLicense = "software_license";
    public const string Provisional = "provisional";
}

/// <summary>Identifier schemes: who defines the format.</summary>
public static class IdentifierScheme
{
    public const string Gs1 = "GS1";
    public const string Iso15459 = "ISO15459";
    public const string Udi = "UDI";
    public const string DoD = "DoD";
    public const string Oem = "OEM";
    public const string Internal = "internal";
}

/// <summary>The constant scope key when a tenant sets serial uniqueness to global for a type (Identity rule 1).</summary>
public static class ScopeKeys
{
    public const string Tenant = "TENANT";
}

/// <summary>Why a UnitItemAssignment row was written (case 42).</summary>
public static class ItemAssignmentReason
{
    public const string Initial = "initial";
    public const string Upgrade = "upgrade";
    public const string Conversion = "conversion";
    public const string Relabel = "relabel";
    public const string Correction = "correction";
}

/// <summary>AssemblyLink relation between parent and child (Identity rule 8).</summary>
public static class AssemblyRelation
{
    public const string Component = "component";
    public const string Kit = "kit";
    public const string CoProductSibling = "co_product_sibling";
}

/// <summary>How CorrectAsBuilt changes one effective-dated component link.</summary>
public static class AsBuiltCorrectionChange
{
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Replace = "replace";

    public static readonly IReadOnlyList<string> All = [Add, Remove, Replace];
}

/// <summary>Release status of an installed child (as-built and maintenance vocabulary).</summary>
public static class ReleaseStatus
{
    public const string New = "new";
    public const string Repaired = "repaired";
    public const string Overhauled = "overhauled";
    public const string RebuiltOemOnly = "rebuilt_oem_only";
}

/// <summary>SerialRange status.</summary>
public static class SerialRangeStatus
{
    public const string Reserved = "reserved";
    public const string Commissioning = "commissioning";
    public const string Exhausted = "exhausted";
    /// <summary>Allocation released because the order was cancelled; serials stay unused by policy (OD-07).</summary>
    public const string Released = "released";
}

/// <summary>CustodyEpisode kinds (ADR-0001 CustodyEpisode).</summary>
public static class EpisodeKind
{
    public const string Ownership = "ownership";
    public const string Custody = "custody";
}

public static class PartyType
{
    public const string Customer = "customer";
    public const string Distributor = "distributor";
    public const string EndUser = "end_user";
    public const string Vendor = "vendor";
    public const string Subcontractor = "subcontractor";
    public const string ThreePl = "three_pl";
    public const string Insurer = "insurer";
    public const string MarketplaceBuyer = "marketplace_buyer";
    public const string GroupEntity = "group_entity";
    public const string Us = "us";
}

/// <summary>Custody location kinds (custody rows only).</summary>
public static class LocationKind
{
    public const string Warehouse = "warehouse";
    public const string Bin = "bin";
    public const string CustomerSite = "customer_site";
    public const string InTransit = "in_transit";
    public const string Returning = "returning";
    public const string Quarantine = "quarantine";
    public const string AtSubcontractor = "at_subcontractor";
    public const string At3pl = "at_3pl";
    public const string InRepair = "in_repair";
    public const string Vendor = "vendor";
}

public static class EpisodeReason
{
    public const string Sale = "sale";
    public const string Consignment = "consignment";
    public const string Loan = "loan";
    public const string Rental = "rental";
    public const string Demo = "demo";
    public const string Subcontract = "subcontract";
    public const string Transfer = "transfer";
    public const string Return = "return";
    public const string TradeIn = "trade_in";
    public const string Replacement = "replacement";
    public const string InsuranceWriteOff = "insurance_write_off";
    public const string Succession = "succession";
    public const string MigrationOpening = "migration_opening";
    public const string Receipt = "receipt";
    public const string Production = "production";
}

public static class EpisodeSource
{
    public const string Invoice = "invoice";
    public const string Delivery = "delivery";
    public const string Receipt = "receipt";
    public const string Registration = "registration";
    public const string Claim = "claim";
    public const string PartnerReport = "partner_report";
    public const string CarrierPod = "carrier_pod";
    public const string Migration = "migration";
}

public static class Confidence
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
}

/// <summary>Event types written by this module in P1 (later phases add theirs beside them).</summary>
public static class UnitEventType
{
    public const string Commissioned = "unit.commissioned";
    public const string CreatedPendingIdentifier = "unit.created_pending_identifier";
    public const string IdentifierAssigned = "unit.identifier_assigned";
    public const string IdentifierCorrected = "unit.identifier_corrected";
    public const string IdentifiersSwapped = "unit.identifiers_swapped";
    public const string ItemAssigned = "unit.item_assigned";
    public const string ItemCorrected = "unit.item_corrected";
    public const string Assembled = "unit.assembled";
    public const string Disassembled = "unit.disassembled";
    /// <summary>On the child: it went into a parent.</summary>
    public const string Installed = "unit.installed";
    /// <summary>On the child: it came out of a parent.</summary>
    public const string Removed = "unit.removed";
    public const string Reassembled = "unit.reassembled";
    public const string AsBuiltCorrected = "unit.asbuilt_corrected";
    public const string Released = "unit.released";
    public const string Scrapped = "unit.scrapped";
    public const string Voided = "unit.voided";
    public const string VoidReversed = "unit.void_reversed";
    public const string Decommissioned = "unit.decommissioned";
    public const string FlagSet = "unit.flag_set";
    public const string FlagCleared = "unit.flag_cleared";
    public const string Shipped = "unit.shipped";
    public const string Delivered = "unit.delivered";
    public const string CustodyChanged = "custody.changed";
    public const string OwnershipOpened = "ownership.opened";
    public const string OwnershipClosed = "ownership.closed";
    public const string OwnershipTransferred = "ownership.transferred";
}

/// <summary>The correction commands governed by the shared correction policy and metadata contract.</summary>
public static class CorrectionCommand
{
    public const string CorrectIdentifier = "correct_identifier";
    public const string SwapIdentifiers = "swap_identifiers";
    public const string CorrectItem = "correct_item";
    public const string CorrectAsBuilt = "correct_as_built";
    public const string LateSerialization = "late_serialization";
    public const string VoidUnit = "void_unit";
    public const string MergeUnits = "merge_units";
    public const string AssignIdentifiers = "assign_identifiers";
    public const string ReassignAllocation = "reassign_allocation";
    public const string CorrectDocumentSerials = "correct_document_serials";
    public const string CorrectSourceDocument = "correct_source_document";

    public static readonly IReadOnlyList<string> All =
    [
        CorrectIdentifier, SwapIdentifiers, CorrectItem, CorrectAsBuilt, LateSerialization, VoidUnit, MergeUnits,
        AssignIdentifiers, ReassignAllocation, CorrectDocumentSerials, CorrectSourceDocument,
    ];
}

/// <summary>Where a correction entered the module; HTTP endpoints default to <see cref="Api"/>.</summary>
public static class CorrectionInitiatedVia
{
    public const string Workbench = "workbench";
    public const string Api = "api";
    public const string Migration = "migration";
    public const string System = "system";

    public static readonly IReadOnlyList<string> All = [Workbench, Api, Migration, System];
}

/// <summary>The initiating-role dimension of the correction-volume KPI.</summary>
public static class CorrectionInitiatingRole
{
    public const string CustomerService = "customer_service";
    public const string WarehouseLead = "warehouse_lead";
    public const string ProductionSupervisor = "production_supervisor";
    public const string ServiceTechnician = "service_technician";
    public const string SerialAdministrator = "serial_administrator";
    public const string System = "system";

    public static readonly IReadOnlyList<string> All = [CustomerService, WarehouseLead, ProductionSupervisor, ServiceTechnician, SerialAdministrator, System];
}

/// <summary>The seven root-cause categories in ADR-0001's correction-volume KPI.</summary>
public static class CorrectionRootCause
{
    public const int DocumentSerials = 1;
    public const int ReplacementOrExchange = 2;
    public const int RefurbishmentReserialisation = 3;
    public const int TypoOrCrossedPair = 4;
    public const int WrongItemOrOrder = 5;
    public const int AllocationBeforeBirth = 6;
    public const int Warranty = 7;
}

public static class IdentifierCorrectionReason
{
    public const string Typo = "typo";
    public const string Misread = "misread";
    public const string Relabel = "relabel";
    public const string Regulatory = "regulatory";

    public static readonly IReadOnlyList<string> All = [Typo, Misread, Relabel, Regulatory];
}

public static class SwapIdentifiersReason
{
    public const string CrossRecordedAtBuild = "cross_recorded_at_build";
    public const string CrossRecordedAtReceipt = "cross_recorded_at_receipt";
    public const string LabelAppliedToWrongUnit = "label_applied_to_wrong_unit";
    public static readonly IReadOnlyList<string> All = [CrossRecordedAtBuild, CrossRecordedAtReceipt, LabelAppliedToWrongUnit];
}

public static class CorrectItemReason
{
    public const string WrongItemAtReceipt = "wrong_item_at_receipt";
    public const string WrongItemAtBuild = "wrong_item_at_build";
    public const string ConfigurationMismatch = "configuration_mismatch";
    public static readonly IReadOnlyList<string> All = [WrongItemAtReceipt, WrongItemAtBuild, ConfigurationMismatch];
}

public static class CorrectAsBuiltReason
{
    public const string ComponentNotCaptured = "component_not_captured";
    public const string WrongComponentRecorded = "wrong_component_recorded";
    public const string LateSerialization = "late_serialization";
    public const string BomChangeAfterRelease = "bom_change_after_release";
    public static readonly IReadOnlyList<string> All = [ComponentNotCaptured, WrongComponentRecorded, LateSerialization, BomChangeAfterRelease];
}

public static class LateSerializationReason
{
    public const string CodingErrorAtItemSetup = "coding_error_at_item_setup";
    public const string PolicyChange = "policy_change";
    public static readonly IReadOnlyList<string> All = [CodingErrorAtItemSetup, PolicyChange];
}

public static class VoidUnitReason
{
    public const string WorkOrderQuantityReduced = "work_order_quantity_reduced";
    public const string WorkOrderCancelled = "work_order_cancelled";
    public const string DuplicateAllocation = "duplicate_allocation";
    public static readonly IReadOnlyList<string> All = [WorkOrderQuantityReduced, WorkOrderCancelled, DuplicateAllocation];
}

public static class SerialReuseOutcome
{
    public const string NeverReuse = "never_reuse";
    public const string EligibleForReuse = "eligible_for_reuse";
    public const string ReusedFromVoidedUnit = "reused_from_voided_unit";
    public const string VoidReversed = "void_reversed";
}

public static class MergeUnitsReason
{
    public const string DuplicateRecord = "duplicate_record";
    public const string MigrationDuplicate = "migration_duplicate";
    public const string VendorSerialKeyedTwice = "vendor_serial_keyed_twice";
    public static readonly IReadOnlyList<string> All = [DuplicateRecord, MigrationDuplicate, VendorSerialKeyedTwice];
}

public static class AssignIdentifiersReason
{
    public const string VendorList = "vendor_list";
    public const string VendorListSupplement = "vendor_list_supplement";
    public const string LateCapture = "late_capture";
    public static readonly IReadOnlyList<string> All = [VendorList, VendorListSupplement, LateCapture];
}

/// <summary>P3-T02 proposal: ADR-0001 defines the command but not its reason codes.</summary>
public static class ReassignAllocationReason
{
    public const string AllocationError = "allocation_error";
    public const string WorkOrderChanged = "work_order_changed";
    public const string WorkOrderCancelled = "work_order_cancelled";
    public static readonly IReadOnlyList<string> All = [AllocationError, WorkOrderChanged, WorkOrderCancelled];
}

public static class CorrectDocumentSerialsReason
{
    public const string ShippedWrongUnits = "shipped_wrong_units";
    public const string CopiedSerialsWithoutScan = "copied_serials_without_scan";
    public const string VendorListWrong = "vendor_list_wrong";
    public const string ReturnedNeverDelivered = "returned_never_delivered";
    public static readonly IReadOnlyList<string> All = [ShippedWrongUnits, CopiedSerialsWithoutScan, VendorListWrong, ReturnedNeverDelivered];
}

public static class CorrectSourceDocumentReason
{
    public const string ReleasedUnderWrongOrder = "released_under_wrong_order";
    public const string WrongDeliverySelected = "wrong_delivery_selected";
    public const string InvoiceSplitError = "invoice_split_error";
    public static readonly IReadOnlyList<string> All = [ReleasedUnderWrongOrder, WrongDeliverySelected, InvoiceSplitError];
}

/// <summary>Disposition of a unit in <c>unit_current</c> (CBV 2.0 terms where one exists).</summary>
public static class UnitDisposition
{
    public const string Active = "active";
    public const string InProgress = "in_progress";
    public const string InTransit = "in_transit";
    public const string Returned = "returned";
    public const string Available = "available";
    public const string Recalled = "recalled";
    public const string Inactive = "inactive";
    public const string Destroyed = "destroyed";
}

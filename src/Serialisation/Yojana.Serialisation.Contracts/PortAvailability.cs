namespace Yojana.Serialisation.Contracts;

/// <summary>Marks an adapter that may be resolved only by Development hosts and test or demo composition roots.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DevelopmentOnlyAttribute : Attribute;

/// <summary>Marker used by the startup inventory to distinguish an honest unavailable adapter from a real source.</summary>
public interface IUnavailablePort
{
    string UnavailableCode { get; }
}

/// <summary>
/// Lets the startup inventory inspect source adapters hidden behind a production-safe decorator. This is metadata for
/// composition checks only; business code continues to depend on the narrow port interface.
/// </summary>
public interface ISerialisationPortComposition
{
    IReadOnlyCollection<object> Sources { get; }
}

/// <summary>
/// Stable public refusal codes for Serialisation sources that a connection does not provide. These values are API
/// contract: add codes when a new port is introduced, but never rename an existing value.
/// </summary>
public static class SerialisationUnavailableCodes
{
    public const string PartyNameUnavailable = "party_name_unavailable";
    public const string ItemNameUnavailable = "item_name_unavailable";
    public const string ItemPolicyUnavailable = "item_policy_unavailable";
    public const string CustomerContractUnavailable = "customer_contract_unavailable";
    public const string VendorWarrantyTermsUnavailable = "vendor_warranty_terms_unavailable";
    public const string WarrantyCertificateUnavailable = "warranty_certificate_unavailable";
    public const string TimeZoneUnavailable = "time_zone_unavailable";
    public const string StockQuantityUnavailable = "stock_quantity_unavailable";
    public const string StockReclassificationUnavailable = "stock_reclassification_unavailable";
    public const string StockUnitEventUnavailable = "stock_unit_event_unavailable";
    public const string LabelPrinterNotConfigured = "label_printer_not_configured";
    public const string SerialSchemeNotConfigured = "serial_scheme_not_configured";
    public const string ItemFamilyUnavailable = "item_family_unavailable";
    public const string PartyContactUnavailable = "party_contact_unavailable";
    public const string ProductionOrderUnavailable = "production_order_unavailable";
    public const string OwnershipSourceUnavailable = "ownership_source_unavailable";
    public const string ServiceDocumentUnavailable = "service_document_unavailable";
    public const string InternalSaleDocumentUnavailable = "internal_sale_document_unavailable";

    public const string UnavailableLabel = "Not available with this connection";
}

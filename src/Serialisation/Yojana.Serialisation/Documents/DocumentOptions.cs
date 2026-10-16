namespace Yojana.Serialisation.Documents;

/// <summary>Tenant-level behaviour of the document handlers (section <c>Serialisation:Documents</c>); tenant data replaces the configuration file in a later phase.</summary>
public sealed class DocumentOptions
{
    public const string SectionName = "Serialisation:Documents";

    /// <summary>When ownership passes to the customer on a sale: <c>ship</c>, <c>deliver</c> (default) or <c>invoice</c> (ADR-0001 ownership transitions "trigger per policy").</summary>
    public string OwnershipTrigger { get; set; } = "deliver";

    /// <summary><c>scan_required</c> (default for serialised items) or <c>copy_allowed</c>; recorded on every <c>unit.shipped</c> (case 141).</summary>
    public string SerialConfirmationMode { get; set; } = "scan_required";

    /// <summary>True when a delivery confirmation follows the shipment; false moves custody straight to the customer site on shipment.</summary>
    public bool ConfirmDeliverySeparately { get; set; } = true;

    /// <summary>Documents that append at least this many events write them through the bulk path (P1-T26) instead of SaveChanges batches.</summary>
    public int BulkWriteThreshold { get; set; } = 100;
}

using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Ports;

internal static class UnavailableRefusal
{
    public static CommandRefusedException For(string code) => new(code,
        code == SerialisationUnavailableCodes.LabelPrinterNotConfigured
            ? "Label printing is not set up for this site"
            : SerialisationUnavailableCodes.UnavailableLabel);
}

/// <summary>Honest catalogue fallback for item references absent from the connected source.</summary>
public sealed class UnavailableItemNamePort : IItemNamePort
{
    public Task<IReadOnlyDictionary<Guid, ItemLabel>> NamesAsync(
        IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ItemLabel>>(itemIds.Distinct().ToDictionary(
            id => id, id => new ItemLabel(id, SerialisationUnavailableCodes.UnavailableLabel,
                SerialisationUnavailableCodes.UnavailableLabel, SerialisationUnavailableCodes.ItemNameUnavailable)));

    public Task<IReadOnlyList<ItemLabel>> SearchAsync(
        string text, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ItemLabel>>([new(Guid.Empty,
            SerialisationUnavailableCodes.UnavailableLabel, SerialisationUnavailableCodes.UnavailableLabel,
            SerialisationUnavailableCodes.ItemNameUnavailable)]);
}

/// <summary>Party labels stay renderable while clearly recording that no party source is connected.</summary>
public sealed class UnavailablePartyNamePort : IPartyNamePort
{
    public Task<IReadOnlyDictionary<Guid, PartyLabel>> NamesAsync(
        IReadOnlyCollection<Guid> partyRefs, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PartyLabel>>(partyRefs.Distinct().ToDictionary(
            id => id, id => Label(id)));

    public Task<IReadOnlyList<PartyLabel>> SearchAsync(
        string text, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PartyLabel>>([Label(Guid.Empty)]);

    private static PartyLabel Label(Guid id) => new(id, SerialisationUnavailableCodes.UnavailableLabel,
        SerialisationUnavailableCodes.UnavailableLabel, null, SerialisationUnavailableCodes.PartyNameUnavailable);
}

public sealed class UnavailableItemFamilyNamePort : IItemFamilyNamePort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.ItemFamilyUnavailable;

    public Task<IReadOnlyList<ItemFamilyLabel>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ItemFamilyLabel>>([new(UnavailableCode,
            SerialisationUnavailableCodes.UnavailableLabel, UnavailableCode)]);
}

public sealed class UnavailablePartyContactPort : IPartyContactPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.PartyContactUnavailable;

    public Task<IReadOnlyList<PartyContactLabel>> ContactsAsync(Guid partyRef, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PartyContactLabel>>([new(partyRef,
            SerialisationUnavailableCodes.UnavailableLabel, "", AvailabilityCode: UnavailableCode)]);
}

public sealed class UnavailableItemPolicyPort : IItemPolicyPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.ItemPolicyUnavailable;

    public Task<IReadOnlyList<ItemWarrantyPolicy>> PoliciesForAsync(Guid itemId, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<ItemWarrantyPolicy>>(UnavailableRefusal.For(UnavailableCode));

    public Task<IReadOnlyList<WarrantyPolicyItem>> ItemsForPoliciesAsync(
        IReadOnlyCollection<string> policyCodes, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<WarrantyPolicyItem>>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableCustomerContractPort : ICustomerContractPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.CustomerContractUnavailable;

    public Task<IReadOnlyList<CustomerContractTerm>> ContractsForAsync(
        Guid partyRef, Guid itemId, DateOnly contractDate, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<CustomerContractTerm>>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableVendorWarrantyTermsPort : IVendorWarrantyTermsPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.VendorWarrantyTermsUnavailable;

    public Task<VendorWarrantyTerm?> TermsForAsync(
        Guid vendorPartyRef, Guid itemId, DateOnly receiptDate, CancellationToken cancellationToken) =>
        Task.FromException<VendorWarrantyTerm?>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableWarrantyCertificatePort : IWarrantyCertificatePort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.WarrantyCertificateUnavailable;

    public Task<bool> CertificateIssuedAsync(Guid entitlementId, CancellationToken cancellationToken) =>
        Task.FromException<bool>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableTimeZoneSourcePort : ITimeZoneSourcePort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.TimeZoneUnavailable;

    public Task<string?> ForLocationAsync(Guid locationId, CancellationToken cancellationToken) =>
        Task.FromException<string?>(UnavailableRefusal.For(UnavailableCode));

    public Task<string?> ForLegalEntityAsync(Guid legalEntityId, CancellationToken cancellationToken) =>
        Task.FromException<string?>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableStockQuantityPort : IStockQuantityPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.StockQuantityUnavailable;

    public Task<IReadOnlyList<OpenStockQuantity>> GetOpenStockAsync(
        Guid itemId, DateTime effectiveAt, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<OpenStockQuantity>>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableStockReclassificationPort : IStockReclassificationPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.StockReclassificationUnavailable;

    public Task<StockReclassificationResult> ReclassifyAsync(
        StockReclassificationRequest request, CancellationToken cancellationToken) =>
        Task.FromException<StockReclassificationResult>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableStockUnitEventPort : IStockUnitEventPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.StockUnitEventUnavailable;

    public Task<WarrantyReplacementStockIssueResult> IssueWarrantyReplacementAsync(
        WarrantyReplacementStockIssue issue, CancellationToken cancellationToken) =>
        Task.FromException<WarrantyReplacementStockIssueResult>(UnavailableRefusal.For(UnavailableCode));

    public Task RecordCorrectionForPosting(
        StockPostingSerialCorrection correction, CancellationToken cancellationToken) =>
        Task.FromException(UnavailableRefusal.For(UnavailableCode));

    public Task RecordReturnForPosting(StockReturnPosting request, CancellationToken cancellationToken) =>
        Task.FromException(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableLabelPrinterPort : ILabelPrinterPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.LabelPrinterNotConfigured;

    public Task<LabelPrinterReceipt> SendAsync(LabelPrinterRequest request, CancellationToken cancellationToken) =>
        Task.FromException<LabelPrinterReceipt>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableSerialSchemeSource : ISerialSchemeSource, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.SerialSchemeNotConfigured;

    public SerialSchemeDefinition? Find(string code) => throw UnavailableRefusal.For(UnavailableCode);

    public IReadOnlyList<SerialSchemeDefinition> All() => throw UnavailableRefusal.For(UnavailableCode);

    public SerialSchemeDefinition? Recognise(string normalisedValue) => throw UnavailableRefusal.For(UnavailableCode);
}

public sealed class UnavailableProductionOrderAllocationPort : IProductionOrderAllocationPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.ProductionOrderUnavailable;

    public Task<ProductionOrderAllocation?> GetAsync(DocumentRef productionOrder, CancellationToken cancellationToken) =>
        Task.FromException<ProductionOrderAllocation?>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableOwnershipSourceDocumentQueryPort : IOwnershipSourceDocumentQueryPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.OwnershipSourceUnavailable;

    public Task<OwnershipSourceDocument?> GetOwnershipSourceAsync(
        DocumentRef document, CancellationToken cancellationToken) =>
        Task.FromException<OwnershipSourceDocument?>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableServiceDocumentQueryPort : IServiceDocumentQueryPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.ServiceDocumentUnavailable;

    public Task<LoanerIssue?> GetLoanerIssueAsync(DocumentRef document, CancellationToken cancellationToken) =>
        Task.FromException<LoanerIssue?>(UnavailableRefusal.For(UnavailableCode));
}

public sealed class UnavailableInternalSaleDocumentQueryPort : IInternalSaleDocumentQueryPort, IUnavailablePort
{
    public string UnavailableCode => SerialisationUnavailableCodes.InternalSaleDocumentUnavailable;

    public Task<IReadOnlyList<InternalSaleChainFact>> ReadAsync(
        IReadOnlyCollection<InternalSaleChainRequest> requests, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<InternalSaleChainFact>>(UnavailableRefusal.For(UnavailableCode));
}

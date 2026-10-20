using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Claims;

/// <summary>
/// Narrow intake seam for ADR-0001 case 73. Identity and entitlement creation stay owned by their existing command
/// paths; this service supplies only the truthful proof-of-purchase facts and publishes P6's certificate request.
/// </summary>
public sealed class LegacyUnknownClaimUnitCreator(
    SerialisationDbContext db,
    IUnitEventWriter writer,
    BulkIngestion bulk,
    IdentifierService identifiers,
    EffectiveItemProfiles items,
    SerialRangeService ranges,
    SerialReuseSettings reuseSettings,
    EntitlementLedger entitlementLedger,
    PolicyResolver policies,
    PolicyStore policyStore,
    ITimeZonePort zones,
    RefurbishedGoodsSettings refurbishedSettings,
    IJurisdictionRegionPort regions,
    IJurisdictionRuleResolver jurisdictionRules,
    IItemQueryPort itemQueries,
    ITenantContext tenant,
    IClock clock,
    IMessageBus bus)
{
    public async Task<Guid> CreateAsync(SubmitClaim command, Guid claimId, CancellationToken cancellationToken)
    {
        var identifier = command.Identifier
            ?? throw new CommandRefusedException("identifier_required", "A no-record claim needs the identifier printed on the physical unit.");
        if (string.IsNullOrWhiteSpace(identifier.Value))
        {
            throw new CommandRefusedException("identifier_required", "A no-record claim needs the identifier printed on the physical unit.");
        }
        if (command.ItemId is not { } itemId)
        {
            throw new CommandRefusedException("legacy_unit_item_required", "A no-record claim needs the evidenced item before its unit can be created.", "select the item shown by the proof of purchase");
        }
        if (string.IsNullOrWhiteSpace(command.ProofOfPurchaseRef))
        {
            throw new CommandRefusedException("proof_of_purchase_required", "A no-record claim needs proof-of-purchase evidence.", "attach the proof of purchase and record its reference");
        }
        if (command.ProofOfPurchaseDate is not { } purchaseDate)
        {
            throw new CommandRefusedException("proof_of_purchase_date_required", "A no-record claim needs the purchase date stated by its evidence.", "record the date from the proof of purchase");
        }
        if (purchaseDate > command.ClaimDate)
        {
            throw new CommandRefusedException("proof_of_purchase_after_claim", $"The proof-of-purchase date {purchaseDate:yyyy-MM-dd} is after the claim date {command.ClaimDate:yyyy-MM-dd}.");
        }

        var identifierType = identifier.IdentifierType ?? IdentifierType.InternalSerial;
        var commissioned = await CommissionUnitsHandler.Handle(
            new CommissionUnits(command.CommandId, itemId, UnitOrigin.LegacyUnknown,
                [new CommissionLine([new IdentifierInput(identifierType, identifier.Value, identifier.IssuerPartyRef)], identifier.Value)],
                command.EventTime, command.ActorRef, command.ServiceEntityId, Source: EpisodeSource.Claim,
                HistoryDepth: HistoryDepth.OpeningOnly),
            db, writer, bulk, identifiers, items, ranges, reuseSettings, tenant, clock, cancellationToken);
        var line = commissioned.Lines.Single();
        if (line.UnitId is not { } unitId || line.Outcome == CommissionLineResult.Refused)
        {
            throw new CommandRefusedException("legacy_unit_creation_refused",
                $"The no-record unit could not be created: {string.Join(", ", line.Reasons)}.",
                "correct the item or identifier evidence and submit the claim again");
        }

        // The existing entitlement path reads unit_current from SQL. This save exposes the commissioned unit inside the
        // ambient handler transaction; it is not a commit, and a later refusal rolls the unit and outbox facts back.
        await db.SaveChangesAsync(cancellationToken);

        var purchaseEvidenceAt = purchaseDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await CreateEntitlementHandler.CreateAsync(
            new CreateEntitlement(command.CommandId, unitId, CoverageFamilyCode.Base, purchaseDate,
                command.ObligorEntityId, command.EventTime, command.ActorRef,
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = purchaseEvidenceAt },
                OwnerPartyRef: command.ClaimantPartyRef, OwnerPartyType: PartyType.Customer,
                Country: command.ServiceCountry, StartEvidenceRef: command.ProofOfPurchaseRef,
                Provenance: EntitlementProvenance.Reconstructed, Confidence: Confidence.Low),
            db, entitlementLedger, policies, policyStore, zones, refurbishedSettings, regions, jurisdictionRules, tenant, clock, itemQueries,
            null, [command.ProofOfPurchaseRef], false, null, null, null, cancellationToken, purchaseDateOverride: purchaseDate);

        await bus.PublishAsync(new CertificateIssueRequested(
                TimeOrderedId.New(command.EventTime), unitId, CertificateDocumentKind.ReleaseRecord,
                CertificateProvenance.Reconstructed, claimId, [command.ProofOfPurchaseRef], command.EventTime,
                command.ServiceEntityId, command.ActorRef),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        return unitId;
    }
}

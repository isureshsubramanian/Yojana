using System.Text.Json;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Certificates;

public sealed class ReleaseRecordPayloadBuilder(
    ICertificateUnitReadPort units,
    ICertificateConditionReadPort conditions,
    ICertificateRefurbishmentReadPort refurbishments,
    ICertificateReplacementReadPort replacements,
    AsBuiltCompleteness completeness,
    IItemNamePort items,
    ILocationNamePort locations,
    IPartyNamePort parties)
{
    public async Task<ReleaseRecordPayload> BuildAsync(CertificateUnitFact unit, CertificateTriggerSnapshot trigger,
        string formType, CertificatePresentation presentation, CertificateProvenanceSnapshot provenance,
        CancellationToken cancellationToken) =>
        (await BuildManyAsync([new(unit, trigger, formType, presentation, provenance)], cancellationToken))[0];

    /// <summary>
    /// Builds many release records with one read per port for the whole list (a migration chunk records thousands),
    /// then composes each unit exactly as a single build does: the single build above is this method with one input.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseRecordPayload>> BuildManyAsync(IReadOnlyList<ReleaseRecordBuildInput> inputs,
        CancellationToken cancellationToken)
    {
        var unitIds = inputs.Select(input => input.Unit.UnitId).Distinct().ToList();
        var conditionFacts = await conditions.ReadManyAsync(unitIds, cancellationToken);
        var refurbishmentFacts = await refurbishments.ReadManyAsync(unitIds, cancellationToken);
        var replacementFacts = await replacements.ReadManyAsync(unitIds, cancellationToken);
        var reports = await completeness.QueryAsync(inputs.Select(input => (input.Unit.UnitId, input.Unit.ItemId,
            DateOnly.FromDateTime(input.Unit.Build?.ReleasedAt ?? input.Unit.CommissionedAt))).ToList(), cancellationToken);
        var itemIds = inputs.SelectMany(input => input.Unit.Assembly.Select(row => row.ChildItemId).OfType<Guid>()
                .Append(input.Unit.ItemId))
            .Concat(reports.SelectMany(report => report.Slots.Select(row => row.ExpectedItemId))).Distinct().ToList();
        var itemNames = await items.NamesAsync(itemIds, cancellationToken);
        var childIds = inputs.SelectMany(input => input.Unit.Assembly.Select(row => row.ChildUnitId).OfType<Guid>())
            .Distinct().ToList();
        var childSerials = childIds.Count == 0 ? new Dictionary<Guid, string>()
            : await units.PrimarySerialsAsync(childIds, cancellationToken);
        var partyRefs = inputs.SelectMany(input => input.Unit.Identifiers.Select(row => row.IssuerRef).OfType<Guid>()
                .Append(refurbishmentFacts[input.Unit.UnitId].RefurbisherRef ?? Guid.Empty))
            .Where(id => id != Guid.Empty).Distinct().ToList();
        var partyNames = partyRefs.Count == 0 ? new Dictionary<Guid, PartyLabel>()
            : await parties.NamesAsync(partyRefs, cancellationToken);
        var facilityIds = inputs.Select(input => input.Unit.Build?.FacilityId).OfType<Guid>().Distinct().ToList();
        var facilities = facilityIds.Count == 0 ? new Dictionary<Guid, LocationLabel>()
            : await locations.NamesAsync(facilityIds, cancellationToken);
        return inputs.Select((input, index) => Compose(input, conditionFacts[input.Unit.UnitId],
            refurbishmentFacts[input.Unit.UnitId], replacementFacts[input.Unit.UnitId], reports[index], itemNames,
            childSerials, partyNames, facilities)).ToList();
    }

    private static ReleaseRecordPayload Compose(ReleaseRecordBuildInput input, CertificateConditionFact condition,
        CertificateRefurbishmentFact refurbishment, CertificateReplacementFact replacement, UnitCompleteness report,
        IReadOnlyDictionary<Guid, ItemLabel> itemNames, IReadOnlyDictionary<Guid, string> childSerials,
        IReadOnlyDictionary<Guid, PartyLabel> partyNames, IReadOnlyDictionary<Guid, LocationLabel> facilities)
    {
        var (unit, trigger, formType, presentation, provenance) = input;
        var facility = unit.Build?.FacilityId is not { } facilityId ? null
            : facilities.GetValueOrDefault(facilityId) is { } label
                ? string.IsNullOrWhiteSpace(label.Name) ? label.Code : label.Name : null;
        var identifierById = unit.Identifiers.ToDictionary(row => row.IdentifierId);
        var primaryOpen = unit.Identifiers.Where(row => row.ValidTo == null).OrderBy(row => IdentifierPriority(row.Type)).FirstOrDefault();
        var identifiers = unit.Identifiers.OrderBy(row => row.ValidFrom).ThenBy(row => row.Type).Select(row =>
        {
            var replaces = row.SupersedesIdentifierId is { } prior && identifierById.TryGetValue(prior, out var replaced)
                ? replaced.Value : null;
            var replacedBy = row.SupersededByIdentifierId is { } next && identifierById.TryGetValue(next, out var replacementIdentifier)
                ? replacementIdentifier.Value : null;
            if (row.IdentifierId == primaryOpen?.IdentifierId)
            {
                replaces ??= Join(replacement.Replaces.Values);
                replacedBy ??= Join(replacement.ReplacedBy.Values);
            }
            var issuer = unit.Build is not null && row.IssuerRef is { } issuerRef && partyNames.TryGetValue(issuerRef, out var issuerLabel)
                ? issuerLabel.Name ?? issuerLabel.Code : null;
            return new ReleaseIdentifierSnapshot(row.Type, row.Scheme, row.Value, issuer, row.ValidFrom, row.ValidTo,
                replaces, replacedBy);
        }).ToList();
        var assembly = unit.Assembly.OrderBy(row => row.RemovedAt == null ? 0 : 1).ThenBy(row => row.Slot).Select(row =>
        {
            ItemLabel? item = null;
            if (row.ChildItemId is { } childItemId) itemNames.TryGetValue(childItemId, out item);
            return new ReleaseComponentSnapshot(row.Slot, row.Relation,
                row.ChildUnitId is { } childId ? childSerials.GetValueOrDefault(childId) : null,
                item?.Code, item?.Name, row.ChildLotId?.ToString("N"), row.Quantity, row.ReleaseStatus,
                row.ReleaseDocument, row.InstalledAt, row.RemovedAt);
        }).ToList();
        var required = report.Slots.Select(slot =>
        {
            itemNames.TryGetValue(slot.ExpectedItemId, out var item);
            var status = slot.Outcome switch
            {
                SlotStatus.Complete => RequiredSerializedComponentSnapshot.Captured,
                SlotStatus.UnexpectedItem => RequiredSerializedComponentSnapshot.UnexpectedItem,
                _ => RequiredSerializedComponentSnapshot.Missing,
            };
            return new RequiredSerializedComponentSnapshot(slot.Slot, item?.Code ?? slot.ExpectedItemCode,
                item?.Name, slot.ExpectedQuantity, status, slot.Captured.Select(row => row.Serial).OfType<string>().ToList());
        }).ToList();
        itemNames.TryGetValue(unit.ItemId, out var model);
        var lots = unit.Assembly.Select(row => row.ChildLotId).OfType<Guid>().Append(unit.LotId ?? Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().Select(id => id.ToString("N")).ToList();
        var refurbisher = refurbishment.RefurbisherRef is { } refId && partyNames.TryGetValue(refId, out var refLabel)
            ? refLabel.Name ?? refLabel.Code : null;
        return new("1.0", formType, trigger, identifiers,
            new ReleaseModelSnapshot(model?.Code ?? "Unknown item", model?.Name, unit.Build?.Revision),
            unit.Build is null ? "purchased" : "built_in_house",
            unit.Build?.ReleasedAt ?? unit.CommissionedAt, facility, lots, assembly, required, condition.Tests, condition.Grade,
            refurbishment.CycleNumber, condition.SanitisationReference, condition.Firmware,
            refurbishment.WorkReference, refurbisher, refurbishment.LifeEpisode, presentation.Issuer,
            presentation.Branding, presentation.Snapshot, provenance, condition.Battery);
    }

    private static int IdentifierPriority(string type) => type switch
    {
        IdentifierType.InternalSerial => 0,
        IdentifierType.ManufacturerSerial => 1,
        IdentifierType.VendorSerial => 2,
        _ => 3,
    };

    private static string? Join(IEnumerable<string> values)
    {
        var list = values.Order(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }
}

/// <summary>One release record to build: the unit snapshot and what the issuing command supplies.</summary>
public sealed record ReleaseRecordBuildInput(CertificateUnitFact Unit, CertificateTriggerSnapshot Trigger, string FormType,
    CertificatePresentation Presentation, CertificateProvenanceSnapshot Provenance);

public sealed class WarrantyCertificatePayloadBuilder(
    ICertificateEntitlementReadPort entitlements,
    IPartyNamePort parties)
{
    public async Task<(WarrantyCertificatePayload Payload, CertificateOwnerSnapshot Owner)> BuildAsync(
        CertificateUnitFact unit, CertificateTriggerSnapshot trigger, CertificatePresentation presentation,
        CertificateProvenanceSnapshot provenance, CancellationToken cancellationToken)
    {
        if (unit.Ownership?.HolderRef is not { } holderRef)
            throw new CertificatePayloadRefusedException(new CommandRefusal("owner_snapshot_unavailable",
                "A warranty certificate needs the unit's current ownership snapshot.", "open an ownership episode first"));
        var names = await parties.NamesAsync([holderRef], cancellationToken);
        if (!names.TryGetValue(holderRef, out var ownerName))
            throw new CertificatePayloadRefusedException(new CommandRefusal("owner_name_unavailable",
                "The current holder has no name available through the party-name port.", "make the holder available to the reference-name service"));
        var owner = new CertificateOwnerSnapshot(ownerName.Code, ownerName.Name ?? ownerName.Code,
            unit.Ownership.HolderKind, unit.Ownership.EffectiveFrom);
        var live = await entitlements.LiveAsync(unit.UnitId, cancellationToken);
        var rows = live.Select(row => new WarrantyEntitlementSnapshot(row.EntitlementId, row.CoverageFamily,
            row.Type, row.StartBasis ?? "unknown", row.StartDate, row.EndDate, row.PolicyCode, row.PolicyName,
            row.PolicyVersion, Term(row), row.SupersededLineage)).OrderBy(row => row.CoverageFamily).ToList();
        return (new WarrantyCertificatePayload("1.0", trigger, rows, owner, presentation.Issuer,
            presentation.Branding, presentation.Snapshot, provenance), owner);
    }

    private static string Term(CertificateEntitlementFact row)
    {
        using var document = JsonDocument.Parse(row.Coverage);
        var serviceMode = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("service_mode", out var mode) ? mode.GetString() : null;
        return serviceMode is null ? $"{row.DurationValue} {row.DurationUnit}" : $"{row.DurationValue} {row.DurationUnit}; {serviceMode}";
    }
}

public sealed class CertificatePayloadRefusedException(CommandRefusal refusal) : Exception(refusal.Message)
{
    public CommandRefusal Refusal { get; } = refusal;
}

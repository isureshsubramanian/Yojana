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
        CancellationToken cancellationToken)
    {
        var condition = await conditions.ReadAsync(unit.UnitId, cancellationToken);
        var refurbishment = await refurbishments.ReadAsync(unit.UnitId, cancellationToken);
        var replacement = await replacements.ReadAsync(unit.UnitId, cancellationToken);
        var buildAt = unit.Build?.ReleasedAt ?? trigger.EffectiveAt;
        var report = await completeness.CheckAsync(unit.UnitId, unit.ItemId, DateOnly.FromDateTime(buildAt), cancellationToken);
        var itemIds = unit.Assembly.Select(row => row.ChildItemId).OfType<Guid>().Append(unit.ItemId)
            .Concat(report.Slots.Select(row => row.ExpectedItemId)).Distinct().ToList();
        var itemNames = await items.NamesAsync(itemIds, cancellationToken);
        var childIds = unit.Assembly.Select(row => row.ChildUnitId).OfType<Guid>().Distinct().ToList();
        var childSerials = await units.PrimarySerialsAsync(childIds, cancellationToken);
        var issuerRefs = unit.Identifiers.Select(row => row.IssuerRef).OfType<Guid>()
            .Append(refurbishment.RefurbisherRef ?? Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
        var partyNames = await parties.NamesAsync(issuerRefs, cancellationToken);
        var facility = unit.Build?.FacilityId is not { } facilityId ? null
            : (await locations.NamesAsync([facilityId], cancellationToken)).GetValueOrDefault(facilityId) is { } label
                ? label.Name ?? label.Code : null;
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
            var issuer = row.IssuerRef is { } issuerRef && partyNames.TryGetValue(issuerRef, out var issuerLabel)
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
            unit.Build?.ReleasedAt, facility, lots, assembly, required, condition.Tests, condition.Grade,
            refurbishment.CycleNumber, condition.SanitisationReference, condition.Firmware,
            refurbishment.WorkReference, refurbisher, refurbishment.LifeEpisode, presentation.Issuer,
            presentation.Branding, provenance);
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
            row.Type, row.StartBasis ?? "unknown", row.StartDate, row.EndDate, row.PolicyCode, row.PolicyVersion,
            Term(row), row.SupersededLineage)).OrderBy(row => row.CoverageFamily).ToList();
        return (new WarrantyCertificatePayload("1.0", trigger, rows, owner, presentation.Issuer,
            presentation.Branding, provenance), owner);
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

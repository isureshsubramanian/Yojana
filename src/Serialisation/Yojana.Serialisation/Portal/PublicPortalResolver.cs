using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Portal;

public sealed class PublicPortalResolver(SerialisationDbContext db, ITenantContext tenant,
    DisclosurePolicyService disclosure, IIdentifierPolicySource identifierPolicies, IItemNamePort itemNames,
    IClock clock)
{
    private static readonly string[] LookupTypes =
    [
        IdentifierType.InternalSerial, IdentifierType.ManufacturerSerial, IdentifierType.VendorSerial,
        IdentifierType.CustomerSerial, IdentifierType.Sgtin, IdentifierType.Giai, IdentifierType.Imei,
        IdentifierType.Mac, IdentifierType.Vin, IdentifierType.UdiDi, IdentifierType.UdiPi, IdentifierType.Uii,
        IdentifierType.CustomerAssetTag, IdentifierType.ChannelOrderRef, IdentifierType.SoftwareLicense,
    ];

    public async Task<PublicPortalResolution> ResolveAsync(string tenantCode, string rawIdentifier,
        string? identifierType, CancellationToken cancellationToken)
    {
        var entry = $"/portal/{Uri.EscapeDataString(tenantCode)}";
        var disclosureView = await disclosure.GetAsync(cancellationToken);
        if (!disclosureView.PublicTierEnabled)
            return Empty(PublicPortalOutcome.AuthenticationRequired, entry);
        var fields = disclosureView.Fields.Where(row => row.Enabled && row.Tier == DisclosureTier.Public
                && PortalDisclosureField.PublicSafe.Contains(row.Field))
            .Select(row => row.Field).ToHashSet(StringComparer.Ordinal);
        var types = identifierType is null ? LookupTypes : [identifierType];
        var candidates = new List<PortalIdentifierCandidate>();
        foreach (var type in types)
        {
            try
            {
                var policy = identifierPolicies.For(type);
                var value = IdentifierNormaliser.Normalise(rawIdentifier, policy).Value;
                if (IdentifierValidator.Validate(value, policy).IsValid) candidates.Add(new(type, value));
            }
            catch (ArgumentException)
            {
                return Empty(PublicPortalOutcome.NotFound, entry);
            }
        }
        var hits = await (from identifier in db.Identifiers.AsNoTracking().MatchExact(tenant.TenantId, candidates)
                          join current in db.Current.AsNoTracking()
                              on new { identifier.TenantId, identifier.UnitId }
                              equals new { current.TenantId, current.UnitId }
                          join unit in db.Units.AsNoTracking()
                              on new { identifier.TenantId, identifier.UnitId }
                              equals new { unit.TenantId, unit.UnitId }
                          select new { identifier.UnitId, identifier.NormalisedValue, identifier.Type,
                              current.ItemId, current.ConditionGrade, unit.Origin, unit.CreatedEventTime })
            .Take(3).ToListAsync(cancellationToken);
        if (hits.Count == 0) return Empty(PublicPortalOutcome.NotFound, entry);
        if (hits.Select(row => row.UnitId).Distinct().Count() > 1)
            return Empty(PublicPortalOutcome.Ambiguous, entry);
        var hit = hits[0];
        var item = (await itemNames.NamesAsync([hit.ItemId], cancellationToken)).GetValueOrDefault(hit.ItemId);
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var entitlementEnds = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == hit.UnitId && row.ClosedAt == null)
            .Select(row => row.EndDate).ToListAsync(cancellationToken);
        var warranty = entitlementEnds.Count == 0 ? "no"
            : entitlementEnds.Any(end => end is null || end >= today) ? "yes" : "expired";
        return new(PublicPortalOutcome.Found,
            fields.Contains(PortalDisclosureField.IdentifierValue) ? hit.NormalisedValue : null,
            fields.Contains(PortalDisclosureField.IdentifierType) ? hit.Type : null,
            fields.Contains(PortalDisclosureField.ItemCode) ? item?.Code ?? "Unknown item" : null,
            fields.Contains(PortalDisclosureField.ItemName) ? item?.Name : null,
            fields.Contains(PortalDisclosureField.Condition) ? hit.ConditionGrade : null,
            fields.Contains(PortalDisclosureField.ManufactureMonth) && hit.Origin == UnitOrigin.Manufactured
                ? hit.CreatedEventTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)
                : null,
            fields.Contains(PortalDisclosureField.WarrantyStatus) ? warranty : null,
            entry);
    }

    private static PublicPortalResolution Empty(string outcome, string entry) =>
        new(outcome, null, null, null, null, null, null, null, entry);
}

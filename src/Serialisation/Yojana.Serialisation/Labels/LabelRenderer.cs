using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Labels;

public sealed record LabelRenderOutcome(
    string? Payload,
    IReadOnlyList<LabelResolvedBinding> Bindings,
    string? EmptyRequiredField,
    string? RejectionReason);

/// <summary>Small, non-executable {{name}} substitution engine over the fixed field catalogue.</summary>
public sealed class LabelRenderer(SerialisationDbContext db, ITenantContext tenant, IItemNamePort itemNames, LabelSettings settings)
{
    public async Task<LabelRenderOutcome> RenderAsync(LabelTemplate template, LabelTemplateVersion version, Guid unitId,
        Guid? identifierId, DateTime at, CancellationToken cancellationToken, bool enforceExpirySetting = true)
    {
        var tenantId = tenant.TenantId;
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.UnitId == unitId, cancellationToken);
        if (unit is null) return Reject("unit", $"unit {unitId} does not exist");
        var assignment = await db.ItemAssignments.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.UnitId == unitId && row.ValidFrom <= at && (row.ValidTo == null || row.ValidTo > at))
            .OrderByDescending(row => row.ValidFrom).FirstOrDefaultAsync(cancellationToken);
        if (assignment is null) return Reject("item", $"item assignment is empty for unit {unitId}");
        var identifiers = await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.UnitId == unitId && row.ValidFrom <= at && (row.ValidTo == null || row.ValidTo > at))
            .ToListAsync(cancellationToken);
        var selectedIdentifier = identifierId is { } requested
            ? identifiers.FirstOrDefault(row => row.IdentifierId == requested)
            : template.AppliesTo == LabelTemplateAppliesTo.IdentifierType
                ? identifiers.FirstOrDefault(row => row.Type == template.AppliesToValue)
                : identifiers.FirstOrDefault(row => row.Type == IdentifierType.InternalSerial)
                    ?? identifiers.FirstOrDefault(row => row.Type == IdentifierType.ManufacturerSerial)
                    ?? identifiers.FirstOrDefault();
        if (identifierId is not null && selectedIdentifier is null) return Reject("identifier.value", $"identifier {identifierId} is not open on unit {unitId} at print time");

        var build = await db.BuildRecords.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId && row.ReleasedAt <= at)
            .OrderByDescending(row => row.Version).FirstOrDefaultAsync(cancellationToken);
        var itemLabels = await itemNames.NamesAsync([assignment.ItemId], cancellationToken);
        itemLabels.TryGetValue(assignment.ItemId, out var itemLabel);
        var baseEnd = await db.Entitlements.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.UnitId == unitId && row.CoverageFamily == CoverageFamilyCode.Base
                && row.Direction == EntitlementDirection.Outbound && row.Status != EntitlementStatus.Closed)
            .Select(row => row.EndDate).FirstOrDefaultAsync(cancellationToken);
        var policy = await settings.GetAsync(cancellationToken);

        string? Identifier(string type) => identifiers.FirstOrDefault(row => row.Type == type)?.RawValue;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [LabelBindableField.UnitLifecycleStatus] = unit.LifecycleStatus,
            [LabelBindableField.UnitExpiryDate] = Date(unit.ExpiryDate),
            [LabelBindableField.ItemCode] = itemLabel?.Code,
            [LabelBindableField.ItemName] = itemLabel?.Name,
            [LabelBindableField.ItemRevision] = build?.Revision,
            [LabelBindableField.IdentifierValue] = selectedIdentifier?.RawValue,
            [LabelBindableField.ManufacturerSerial] = Identifier(IdentifierType.ManufacturerSerial),
            [LabelBindableField.InternalSerial] = Identifier(IdentifierType.InternalSerial),
            [LabelBindableField.VendorSerial] = Identifier(IdentifierType.VendorSerial),
            [LabelBindableField.CustomerAssetTag] = Identifier(IdentifierType.CustomerAssetTag),
            [LabelBindableField.AsBuiltRevision] = build?.Revision,
            [LabelBindableField.AsBuiltBuildDate] = build is null ? null : build.ReleasedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [LabelBindableField.BaseEntitlementEndDate] = !enforceExpirySetting || policy.PrintExpiryDates ? Date(baseEnd) : null,
        };

        var resolved = LabelTemplateRules.Bindings(version.Bindings)
            .Select(binding => new LabelResolvedBinding(binding.Placeholder, binding.FieldPath, binding.Required,
                values.GetValueOrDefault(binding.FieldPath))).ToList();
        if (resolved.FirstOrDefault(binding => binding.Required && string.IsNullOrWhiteSpace(binding.Value)) is { } missing)
            return new(null, resolved, missing.FieldPath, $"{missing.FieldPath} is empty for unit {unitId}");
        if (version.Symbology == LabelSymbology.Code39
            && resolved.FirstOrDefault(binding => binding.FieldPath.StartsWith("identifier.", StringComparison.Ordinal)
                && binding.Value?.Contains('*', StringComparison.Ordinal) == true) is { } unsafeSerial)
            return new(null, resolved, unsafeSerial.FieldPath, $"{unsafeSerial.FieldPath} contains '*' and cannot be printed with Code 39");

        var payload = version.Layout;
        foreach (var binding in resolved)
            payload = payload.Replace("{{" + binding.Placeholder + "}}", binding.Value ?? string.Empty, StringComparison.Ordinal);
        return new(payload, resolved, null, null);
    }

    private static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static LabelRenderOutcome Reject(string field, string reason) => new(null, [], field, reason);
}

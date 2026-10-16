using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Selection;

namespace Yojana.Serialisation.Entitlements;

public sealed class WarrantyLineValidationService(ISalesDocumentQueryPort sales, SelectionSetResolver selections, SerialisationDbContext db,
    PolicyStore store, WarrantyReferenceSettings settings, ITenantContext tenant)
{
    public async Task<WarrantyLineValidationResult> ValidateAsync(WarrantyLineValidation request, CancellationToken cancellationToken)
    {
        var unknown = new List<string>();
        var nonSerial = new List<string>();
        var resolved = 0;
        var structural = WarrantyLineRules.ValidateTarget(request.Target, true, true).ToList();
        if (structural.Count == 0)
        {
            if (request.Target.Rule is WarrantyTargetRule.SameOrder or WarrantyTargetRule.ReferencedDocument)
            {
                var document = request.Target.Rule == WarrantyTargetRule.ReferencedDocument ? request.Target.Document : request.SourceDocument ?? request.Target.Document;
                if (document is null) unknown.Add("document_required");
                else if (await sales.GetWarrantyTargetAsync(document, cancellationToken) is not { } target) unknown.Add($"{document.Type}:{document.Id:N}");
                else
                {
                    foreach (var line in target.Lines)
                    {
                        if (!line.SerialTracked) nonSerial.Add($"line:{line.LineNo}");
                        else if (line.Serials.Count == 0) unknown.Add($"line:{line.LineNo}");
                        else resolved += line.Serials.Count;
                    }
                }
            }
            else
            {
                var source = request.Target.Serials is { Count: > 0 }
                    ? SelectionSource.ByPastedList(request.Target.Serials)
                    : SelectionSource.BySerialRange(request.Target.Scheme!, request.Target.RangeStart!, request.Target.RangeEnd!);
                var selectionId = Guid.NewGuid();
                var result = await selections.ResolveAsync(new ResolveSelectionSet(selectionId, source, "warranty-line-validation"), cancellationToken);
                resolved = result.Resolved;
                unknown.AddRange(result.Unknown);
                unknown.AddRange(result.Rejected);
                db.SelectionSetMembers.RemoveRange(db.SelectionSetMembers.Where(m => m.TenantId == tenant.TenantId && m.SelectionId == selectionId));
                db.SelectionSets.RemoveRange(db.SelectionSets.Where(s => s.TenantId == tenant.TenantId && s.SelectionId == selectionId));
            }
        }

        var reasons = structural.Concat(WarrantyLineRules.ValidateResolvedTarget(request.Target, resolved, unknown.Count, nonSerial.Count)).Distinct(StringComparer.Ordinal).ToList();
        var version = request.PolicyVersionId is { } id
            ? await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenant.TenantId && v.PolicyVersionId == id, cancellationToken)
            : await db.PolicyVersions.AsNoTracking().Where(v => v.TenantId == tenant.TenantId && v.PolicyCode == request.PolicyCode && v.Status == PolicyVersionStatus.Published).OrderByDescending(v => v.Version).FirstOrDefaultAsync(cancellationToken);
        if (version is null) reasons.Add("no_policy");
        else if (WarrantyLineRules.Regulation(await store.ReadTermsAsync(version, cancellationToken), await settings.GetAsync(cancellationToken), request.Country, request.RegionGroup, request.HasLicensedObligor) is { } regulated) reasons.Add(regulated);
        return new(reasons.Count == 0, reasons, unknown, nonSerial);
    }
}

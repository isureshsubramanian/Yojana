using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Labels;

public static class LabelTemplateLifecycleHandler
{
    public static async Task<LabelTemplatePreview> Handle(PreviewLabelTemplate command, SerialisationDbContext db,
        LabelRenderer renderer, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var (template, version) = await FindAsync(command.TemplateVersionId, db, tenant, cancellationToken);
        var unitId = command.SampleUnitId ?? version.PreviewSampleUnitId
            ?? throw new CommandRefusedException("label_preview_sample_required", "Choose a sample unit before previewing the label.");
        // Preview can inspect an expiry binding while OD-11 is off; actual printing still enforces the tenant switch.
        var result = await renderer.RenderAsync(template, version, unitId, null, clock.UtcNow, cancellationToken,
            enforceExpirySetting: false, includeBarcodePreview: true);
        if (version.Status == LabelTemplateStatus.Draft && result.RejectionReason is null)
        {
            version.PreviewedAt = clock.UtcNow;
            version.PreviewedBy = command.ActorRef;
            version.PreviewedContentHash = version.ContentHash;
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(version.TemplateVersionId, unitId, version.LayoutKind, result.Payload, result.Bindings,
            result.EmptyRequiredField, result.RejectionReason, result.Barcode, version.WidthMm, version.HeightMm);
    }

    public static async Task<LabelTemplateVersionChanged> Handle(RequestLabelLegalReview command, SerialisationDbContext db,
        LabelSettings settings, IJurisdictionLabelRulesPort jurisdictionRules, ITenantContext tenant, IClock clock,
        CancellationToken cancellationToken)
    {
        var (_, version) = await FindAsync(command.TemplateVersionId, db, tenant, cancellationToken);
        if (Replay(version, command.CommandId)) return Changed(version, true);
        Draft(version);
        var policy = await settings.GetAsync(cancellationToken);
        var phrases = policy.LegalReviewPhrases.Concat(await jurisdictionRules.ForbiddenPhrasesAsync(cancellationToken))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        version.LegalReview = LabelTemplateRules.NeedsLegalReview(LabelTemplateRules.StaticText(version.StaticText), phrases, version.Layout);
        if (!version.LegalReview) throw new CommandRefusedException("label_legal_review_not_required", "The configured phrase list does not flag this label version.");
        version.LegalReviewRequestedAt = clock.UtcNow;
        version.LegalReviewRequestedBy = command.ActorRef;
        AddCommand(version, command.CommandId);
        await db.SaveChangesAsync(cancellationToken);
        return Changed(version, false);
    }

    public static async Task<LabelTemplateVersionChanged> Handle(SignLabelLegalReview command, SerialisationDbContext db,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var (_, version) = await FindAsync(command.TemplateVersionId, db, tenant, cancellationToken);
        if (Replay(version, command.CommandId)) return Changed(version, true);
        Draft(version);
        if (!version.LegalReview || version.LegalReviewRequestedAt is null)
            throw new CommandRefusedException("label_legal_review_not_requested", "Request legal review before signing it.");
        EnsureReviewerIsNotAuthor(version, command.ActorRef, "label_legal_review_author_conflict");
        version.LegalReviewedAt = clock.UtcNow;
        version.LegalReviewedBy = command.ActorRef;
        version.LegalReviewContentHash = version.ContentHash;
        AddCommand(version, command.CommandId);
        await db.SaveChangesAsync(cancellationToken);
        return Changed(version, false);
    }

    public static async Task<LabelTemplateVersionChanged> Handle(ApproveRegulatedLabel command, SerialisationDbContext db,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var (_, version) = await FindAsync(command.TemplateVersionId, db, tenant, cancellationToken);
        if (Replay(version, command.CommandId)) return Changed(version, true);
        Draft(version);
        if (!version.Regulated) throw new CommandRefusedException("label_regulated_approval_not_required", "This label version is not marked as regulated.");
        EnsureReviewerIsNotAuthor(version, command.ActorRef, "label_regulated_approval_author_conflict");
        version.ApprovedAt = clock.UtcNow;
        version.ApprovedBy = command.ActorRef;
        version.ApprovalContentHash = version.ContentHash;
        AddCommand(version, command.CommandId);
        await db.SaveChangesAsync(cancellationToken);
        return Changed(version, false);
    }

    public static async Task<LabelTemplateVersionChanged> Handle(ActivateLabelTemplateVersion command, SerialisationDbContext db,
        LabelSettings settings, IJurisdictionLabelRulesPort jurisdictionRules, ITenantContext tenant, IClock clock,
        CancellationToken cancellationToken)
    {
        var (template, version) = await FindAsync(command.TemplateVersionId, db, tenant, cancellationToken);
        if (Replay(version, command.CommandId)) return Changed(version, true);
        Draft(version);
        var policy = await settings.GetAsync(cancellationToken);
        var phrases = policy.LegalReviewPhrases.Concat(await jurisdictionRules.ForbiddenPhrasesAsync(cancellationToken))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        version.LegalReview = LabelTemplateRules.NeedsLegalReview(LabelTemplateRules.StaticText(version.StaticText), phrases, version.Layout);
        if (version.PreviewedContentHash != version.ContentHash)
            throw new CommandRefusedException("label_preview_required", "Render a successful preview of this draft before activation.");
        if (version.LegalReview && version.LegalReviewContentHash != version.ContentHash)
            throw new CommandRefusedException("label_legal_review_required", "Legal review sign-off is required for the current label content.");
        if (version.Regulated && version.ApprovalContentHash != version.ContentHash)
            throw new CommandRefusedException("label_regulated_approval_required", "Regulated-label approval is required for the current label content.");

        var siblings = await db.LabelTemplateVersions.Where(row => row.TenantId == tenant.TenantId
            && row.TemplateId == template.TemplateId && row.TemplateVersionId != version.TemplateVersionId
            && row.Status != LabelTemplateStatus.Draft).ToListAsync(cancellationToken);
        if (siblings.Any(row => row.EffectiveFrom >= version.EffectiveFrom
            && (version.EffectiveTo == null || row.EffectiveFrom < version.EffectiveTo)))
            throw new CommandRefusedException("label_effective_dates_overlap", "The version overlaps a later activated label version.");
        foreach (var current in siblings.Where(row => row.EffectiveFrom < version.EffectiveFrom
            && (row.EffectiveTo == null || row.EffectiveTo > version.EffectiveFrom)))
        {
            current.EffectiveTo = version.EffectiveFrom;
            current.Status = LabelTemplateStatus.Retired;
            current.RetiredAt = command.ActivatedAt;
            current.RetiredBy = command.ActorRef;
        }
        version.Status = LabelTemplateStatus.Active;
        version.ActivatedAt = command.ActivatedAt;
        version.ActivatedBy = command.ActorRef;
        AddCommand(version, command.CommandId);
        await db.SaveChangesAsync(cancellationToken);
        return Changed(version, false);
    }

    public static async Task<LabelTemplateVersionChanged> Handle(RetireLabelTemplateVersion command, SerialisationDbContext db,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var (_, version) = await FindAsync(command.TemplateVersionId, db, tenant, cancellationToken);
        if (Replay(version, command.CommandId)) return Changed(version, true);
        if (version.Status != LabelTemplateStatus.Active)
            throw new CommandRefusedException("label_version_not_active", "Only an active label version can be retired.");
        if (command.RetiredAt <= version.EffectiveFrom)
            throw new CommandRefusedException("label_retirement_time_invalid", "Retirement must be after the version's effective-from time.");
        version.Status = LabelTemplateStatus.Retired;
        version.EffectiveTo = version.EffectiveTo is null || version.EffectiveTo > command.RetiredAt ? command.RetiredAt : version.EffectiveTo;
        version.RetiredAt = command.RetiredAt;
        version.RetiredBy = command.ActorRef;
        AddCommand(version, command.CommandId);
        await db.SaveChangesAsync(cancellationToken);
        return Changed(version, false);
    }

    private static async Task<(LabelTemplate Template, LabelTemplateVersion Version)> FindAsync(Guid versionId,
        SerialisationDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var version = await db.LabelTemplateVersions.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.TemplateVersionId == versionId, cancellationToken)
            ?? throw new CommandRefusedException("label_template_version_not_found", "The label template version does not exist.");
        var template = await db.LabelTemplates.FirstAsync(row => row.TenantId == tenant.TenantId && row.TemplateId == version.TemplateId, cancellationToken);
        return (template, version);
    }

    private static void Draft(LabelTemplateVersion version)
    {
        if (version.Status != LabelTemplateStatus.Draft)
            throw new CommandRefusedException("label_template_version_immutable", "An active or retired label version cannot be edited.");
    }

    private static bool Replay(LabelTemplateVersion version, Guid commandId) => LabelTemplateRules.CommandIds(version.CommandIds).Contains(commandId);

    private static void AddCommand(LabelTemplateVersion version, Guid commandId)
    {
        version.CommandIds = LabelTemplateRules.AddCommand(version.CommandIds, commandId);
    }

    private static void EnsureReviewerIsNotAuthor(LabelTemplateVersion version, string reviewer, string rule)
    {
        if (string.Equals(version.ActorRef, reviewer, StringComparison.Ordinal))
            throw new CommandRefusedException(rule, "The author of a label draft cannot review or approve their own work.");
    }

    private static LabelTemplateVersionChanged Changed(LabelTemplateVersion version, bool replayed) =>
        new(version.TemplateVersionId, version.Status, version.LegalReview, replayed);
}

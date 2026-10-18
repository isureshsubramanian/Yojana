using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Labels;

public static class LabelTemplateHandler
{
    public static async Task<LabelTemplateCreated> Handle(CreateLabelTemplate command, SerialisationDbContext db,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.LabelTemplates.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.CreateCommandId == command.CommandId, cancellationToken) is { } replay)
            return new(replay.TemplateId, true);
        var name = command.Name.Trim();
        var appliesValue = command.AppliesToValue.Trim();
        if (name.Length is 0 or > 200) throw new CommandRefusedException("label_template_name_invalid", "A label template name is required and cannot exceed 200 characters.");
        if (!LabelTemplateAppliesTo.All.Contains(command.AppliesTo)) throw new CommandRefusedException("label_template_applies_to_invalid", $"Applies-to kind '{command.AppliesTo}' is unknown.");
        if (appliesValue.Length is 0 or > 100) throw new CommandRefusedException("label_template_applies_to_value_invalid", "An applies-to value is required and cannot exceed 100 characters.");
        if (command.AppliesTo == LabelTemplateAppliesTo.IdentifierType && !IdentifierTypes.Contains(appliesValue))
            throw new CommandRefusedException("label_template_identifier_type_invalid", $"Identifier type '{appliesValue}' is unknown.");
        if (await db.LabelTemplates.AnyAsync(row => row.TenantId == tenantId && row.Name == name, cancellationToken))
            throw new CommandRefusedException("label_template_name_in_use", $"A label template named '{name}' already exists.");
        var id = TimeOrderedId.New(clock.UtcNow);
        db.LabelTemplates.Add(new LabelTemplate
        {
            TenantId = tenantId, TemplateId = id, CreateCommandId = command.CommandId, Name = name,
            AppliesTo = command.AppliesTo, AppliesToValue = appliesValue, CreatedAt = clock.UtcNow, CreatedBy = command.ActorRef,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new(id, false);
    }

    public static async Task<LabelTemplateVersionSaved> Handle(SaveLabelTemplateDraft command, SerialisationDbContext db,
        LabelSettings settings, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var template = await db.LabelTemplates.FirstOrDefaultAsync(row => row.TenantId == tenantId && row.TemplateId == command.TemplateId, cancellationToken)
            ?? throw new CommandRefusedException("label_template_not_found", "The label template does not exist.");
        var versions = await db.LabelTemplateVersions.Where(row => row.TenantId == tenantId && row.TemplateId == template.TemplateId).ToListAsync(cancellationToken);
        if (versions.FirstOrDefault(row => LabelTemplateRules.CommandIds(row.CommandIds).Contains(command.CommandId)) is { } replay)
            return new(template.TemplateId, replay.TemplateVersionId, replay.Version, replay.LegalReview, true);

        var errors = LabelTemplateRules.Validate(command);
        if (errors.Count > 0) throw new CommandRefusedException("label_template_draft_invalid", string.Join("; ", errors));
        var policy = await settings.GetAsync(cancellationToken);
        var staticText = command.StaticText.Select(value => value.Trim()).ToList();
        var legalReview = LabelTemplateRules.NeedsLegalReview(staticText, policy.LegalReviewPhrases, command.Layout);
        var hash = LabelTemplateRules.ContentHash(command with { StaticText = staticText });
        var now = clock.UtcNow;

        LabelTemplateVersion version;
        if (command.TemplateVersionId is { } versionId)
        {
            version = versions.FirstOrDefault(row => row.TemplateVersionId == versionId)
                ?? throw new CommandRefusedException("label_template_version_not_found", "The label template version does not exist.");
            if (version.Status != LabelTemplateStatus.Draft)
                throw new CommandRefusedException("label_template_version_immutable", "An active or retired label version cannot be edited; create a new draft version.");
        }
        else
        {
            version = new LabelTemplateVersion
            {
                TenantId = tenantId, TemplateVersionId = TimeOrderedId.New(now), TemplateId = template.TemplateId,
                Version = versions.Count == 0 ? 1 : versions.Max(row => row.Version) + 1,
                LayoutKind = command.LayoutKind, Layout = command.Layout, Symbology = command.Symbology,
                Status = LabelTemplateStatus.Draft, ContentHash = hash, ActorRef = command.ActorRef, RecordedAt = now,
            };
            db.LabelTemplateVersions.Add(version);
        }

        version.EffectiveFrom = command.EffectiveFrom;
        version.EffectiveTo = command.EffectiveTo;
        version.LayoutKind = command.LayoutKind;
        version.Layout = command.Layout;
        version.StaticText = JsonSerializer.Serialize(staticText, LedgerJson.Options);
        version.Bindings = JsonSerializer.Serialize(command.Bindings, LedgerJson.Options);
        version.Symbology = command.Symbology;
        version.PreviewSampleUnitId = command.PreviewSampleUnitId;
        version.LegalReview = legalReview;
        version.Regulated = command.Regulated;
        version.ContentHash = hash;
        version.CommandIds = LabelTemplateRules.AddCommand(version.CommandIds, command.CommandId);
        version.RecordedAt = now;
        version.ActorRef = command.ActorRef;
        version.PreviewedAt = null;
        version.PreviewedBy = null;
        version.PreviewedContentHash = null;
        version.LegalReviewRequestedAt = null;
        version.LegalReviewRequestedBy = null;
        version.LegalReviewedAt = null;
        version.LegalReviewedBy = null;
        version.LegalReviewContentHash = null;
        version.ApprovedAt = null;
        version.ApprovedBy = null;
        version.ApprovalContentHash = null;
        await db.SaveChangesAsync(cancellationToken);
        return new(template.TemplateId, version.TemplateVersionId, version.Version, version.LegalReview, false);
    }

    private static readonly HashSet<string> IdentifierTypes = typeof(IdentifierType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
}

using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;

namespace Yojana.Serialisation.Entitlements;

// Warranty policy editor commands (P2-T03). One open draft per policy; a published version is never edited, and live
// entitlements keep the version they were created with. Validation is the same function for the editor's card and for
// publication, so what the editor shows as an error is exactly what stops a publish.

public static class CreatePolicyDraftHandler
{
    public static async Task<PolicyDraftResult> Handle(CreatePolicyDraft command, SerialisationDbContext db, PolicyStore store, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var code = command.PolicyCode.Trim();
        if (code.Length is 0 or > 60) throw new CommandRefusedException("policy_code_invalid", "A policy code is 1 to 60 characters.");
        var versions = await db.PolicyVersions.Where(v => v.TenantId == tenantId && v.PolicyCode == code).OrderByDescending(v => v.Version).ToListAsync(cancellationToken);
        if (versions.FirstOrDefault(v => v.Status == PolicyVersionStatus.Draft) is { } open)
        {
            throw new CommandRefusedException("draft_open", $"{code} already has draft v{open.Version}.", "edit or discard the open draft");
        }

        var newest = versions.FirstOrDefault();
        if (newest is not null && (newest.CoverageFamily != command.CoverageFamily || newest.EntitlementType != command.EntitlementType))
        {
            throw new CommandRefusedException("policy_identity_changed", $"{code} is a {newest.CoverageFamily} {newest.EntitlementType} policy; a version cannot change its family or type.", "create a new policy code");
        }

        var terms = command.Terms ?? (newest is null ? throw new CommandRefusedException("terms_required", $"{code} has no version to copy; give the terms.") : await store.ReadTermsAsync(newest, cancellationToken, includeItems: true));
        if (terms.Items is null && newest is not null)
            terms = terms with { Items = (await store.ReadTermsAsync(newest, cancellationToken, includeItems: true)).Items };
        var version = (newest?.Version ?? 0) + 1;
        var now = clock.UtcNow;
        var row = new WarrantyPolicyVersion
        {
            TenantId = tenantId, PolicyVersionId = TimeOrderedId.New(now), PolicyCode = code, Version = version, Name = command.Name, CoverageFamily = command.CoverageFamily, EntitlementType = command.EntitlementType,
            AppliesToContractsFrom = command.AppliesToContractsFrom, Status = PolicyVersionStatus.Draft, RuleVersion = PolicyStore.RuleVersionFor(version), DurationUnit = "", EndOfMonthRule = "", DurationBasis = "", StartTrigger = "",
            Transferable = "", SettlementFormula = "", AssuranceOrService = "", ReferenceTimeZoneRule = "", CreatedAt = now, CreatedBy = command.ActorRef,
            LastModifiedAt = now, LastModifiedBy = command.ActorRef,
        };
        await store.WriteTermsAsync(row, terms, cancellationToken);
        db.PolicyVersions.Add(row);
        return new PolicyDraftResult(row.PolicyVersionId, code, version, row.Status, await store.ValidateAsync(row, terms, cancellationToken));
    }
}

public static class UpdatePolicyDraftHandler
{
    public static async Task<PolicyDraftResult> Handle(UpdatePolicyDraft command, SerialisationDbContext db, PolicyStore store, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var row = await PolicyDrafts.DraftAsync(db, tenant.TenantId, command.PolicyVersionId, cancellationToken);
        DocumentVersion.RequireExpected(command.ExpectedVersion, row.RowVersion, row.LastModifiedBy, row.LastModifiedAt);
        row.Name = command.Name;
        row.AppliesToContractsFrom = command.AppliesToContractsFrom;
        row.LastModifiedAt = clock.UtcNow;
        row.LastModifiedBy = command.ActorRef;
        await store.WriteTermsAsync(row, command.Terms, cancellationToken);
        return new PolicyDraftResult(row.PolicyVersionId, row.PolicyCode, row.Version, row.Status, await store.ValidateAsync(row, command.Terms, cancellationToken));
    }
}

public static class ValidatePolicyDraftHandler
{
    public static async Task<PolicyValidation> Handle(ValidatePolicyDraft command, SerialisationDbContext db, PolicyStore store, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var row = await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenantId && v.PolicyVersionId == command.PolicyVersionId, cancellationToken)
            ?? throw new CommandRefusedException("policy_version_unknown", $"Policy version {command.PolicyVersionId} does not exist.");
        return await store.ValidateAsync(row, await store.ReadTermsAsync(row, cancellationToken, includeItems: true), cancellationToken);
    }
}

public static class PublishPolicyVersionHandler
{
    public static async Task<PolicyDraftResult> Handle(PublishPolicyVersion command, SerialisationDbContext db, PolicyStore store,
        ClaimWaitingPeriodSettings waitingPeriods, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var row = await PolicyDrafts.DraftAsync(db, tenant.TenantId, command.PolicyVersionId, cancellationToken);
        var terms = await store.ReadTermsAsync(row, cancellationToken, includeItems: true);
        if (terms.NoClaimWithinDaysOfPurchase is null)
        {
            var families = await db.CoverageFamilies.AsNoTracking().Where(f => f.TenantId == tenant.TenantId)
                .Select(f => f.FamilyCode).ToListAsync(cancellationToken);
            var defaults = await waitingPeriods.GetAsync(families, cancellationToken);
            row.NoClaimWithinDaysOfPurchase = defaults.DaysByCoverageFamily.GetValueOrDefault(row.CoverageFamily);
            terms = terms with { NoClaimWithinDaysOfPurchase = row.NoClaimWithinDaysOfPurchase };
        }
        var validation = await store.ValidateAsync(row, terms, cancellationToken);
        if (!validation.CanPublish)
        {
            var first = validation.Issues.First(i => i.Severity == PolicyIssue.Error);
            throw new CommandRefusedException("policy_invalid", $"{row.PolicyCode} v{row.Version} has {validation.Issues.Count(i => i.Severity == PolicyIssue.Error)} error(s); first: {first.Message}", "fix the errors the validation lists, then publish");
        }

        // Coupled to PolicyCoverItems.WriteAsync: publication and child-item edits compete on this parent's
        // rowversion across competing transactions. Each command saves atomically; independent child writes would
        // allow a concurrent edit after publication.
        row.Status = PolicyVersionStatus.Published;
        row.PublishedAt = clock.UtcNow;
        row.PublishedBy = command.ActorRef;
        row.LastModifiedAt = clock.UtcNow;
        row.LastModifiedBy = command.ActorRef;
        return new PolicyDraftResult(row.PolicyVersionId, row.PolicyCode, row.Version, row.Status, validation);
    }
}

public static class DiscardPolicyDraftHandler
{
    public static async Task<PolicyDraftResult> Handle(DiscardPolicyDraft command, SerialisationDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var row = await PolicyDrafts.DraftAsync(db, tenant.TenantId, command.PolicyVersionId, cancellationToken);
        // a draft was never in force, so removing it changes no entitlement; the temporal history keeps the row
        db.PolicyVersions.Remove(row);
        return new PolicyDraftResult(row.PolicyVersionId, row.PolicyCode, row.Version, "discarded", new PolicyValidation([]));
    }
}

public static class DefineCoverageFamilyHandler
{
    public static async Task<CoverageFamilyRow> Handle(DefineCoverageFamily command, SerialisationDbContext db, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (command.FamilyCode.Length is 0 or > 40 || command.FamilyCode.Any(ch => !(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_')))
        {
            throw new CommandRefusedException("family_code_invalid", $"'{command.FamilyCode}' must be 1 to 40 lower-case letters, digits or underscores.");
        }

        if (command.AccountingTag is not (AssuranceOrService.Assurance or AssuranceOrService.Service))
        {
            throw new CommandRefusedException("accounting_tag_unknown", $"'{command.AccountingTag}' is not assurance or service.");
        }

        var row = await db.CoverageFamilies.FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FamilyCode == command.FamilyCode, cancellationToken);
        if (row is not null && row.Active && !command.Active && await db.Entitlements.AnyAsync(e => e.TenantId == tenantId && e.CoverageFamily == command.FamilyCode && e.Status != EntitlementStatus.Closed, cancellationToken))
        {
            throw new CommandRefusedException("family_in_use", $"Coverage family '{command.FamilyCode}' has live entitlements; it cannot be deactivated.", "close or supersede them first, or leave the family active");
        }

        if (row is null)
        {
            row = new CoverageFamily { TenantId = tenantId, FamilyCode = command.FamilyCode, Name = command.Name, AccountingTag = command.AccountingTag, CreatedAt = clock.UtcNow,
                LastModifiedAt = clock.UtcNow, LastModifiedBy = command.ActorRef };
            db.CoverageFamilies.Add(row);
        }
        else DocumentVersion.RequireExpected(command.ExpectedVersion, row.RowVersion, row.LastModifiedBy, row.LastModifiedAt);

        row.Name = command.Name;
        row.Stacking = command.Stacking;
        row.AccountingTag = command.AccountingTag;
        row.Active = command.Active;
        row.LastModifiedAt = clock.UtcNow;
        row.LastModifiedBy = command.ActorRef;
        return new CoverageFamilyRow(row.FamilyCode, row.Name, row.Stacking, row.AccountingTag, row.Active, DocumentVersion.Encode(row.RowVersion));
    }
}

public static class DefineReplacementPolicyHandler
{
    public static async Task<ReplacementPolicyRow> Handle(DefineReplacementPolicy command, SerialisationDbContext db, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        switch (command.Rule)
        {
            case ReplacementRule.CarryRemaining or ReplacementRule.RestartFull:
                break;
            case ReplacementRule.MaxRemainingOrMinDays when command.MinDays is > 0:
                break;
            case ReplacementRule.StatutoryExtensionOnce when command.ExtensionMonths is > 0:
                break;
            case ReplacementRule.MaxRemainingOrMinDays:
                throw new CommandRefusedException("min_days_required", "max_remaining_or_min_days needs N, the minimum days a replacement is covered.");
            case ReplacementRule.StatutoryExtensionOnce:
                throw new CommandRefusedException("extension_months_required", "statutory_extension_once needs the extension in months.");
            default:
                throw new CommandRefusedException("replacement_rule_unknown", $"'{command.Rule}' is not a replacement rule.");
        }

        var row = await db.ReplacementPolicies.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Code == command.Code, cancellationToken);
        if (row is not null && (row.Rule != command.Rule || row.MinDays != command.MinDays || row.ExtensionMonths != command.ExtensionMonths)
            && await db.PolicyVersions.AnyAsync(v => v.TenantId == tenantId && v.ReplacementPolicyId == row.ReplacementPolicyId && v.Status != PolicyVersionStatus.Draft, cancellationToken))
        {
            // a published version promised these terms; changing them would change what a replacement inherits under it
            throw new CommandRefusedException("replacement_policy_in_use", $"Replacement policy '{command.Code}' is named by a published policy version; its rule cannot change.", "define a new code and use it in the next policy version");
        }

        if (row is null)
        {
            row = new ReplacementPolicy { TenantId = tenantId, ReplacementPolicyId = TimeOrderedId.New(clock.UtcNow), Code = command.Code, Rule = command.Rule, CreatedAt = clock.UtcNow,
                LastModifiedAt = clock.UtcNow, LastModifiedBy = command.ActorRef };
            db.ReplacementPolicies.Add(row);
        }
        else DocumentVersion.RequireExpected(command.ExpectedVersion, row.RowVersion, row.LastModifiedBy, row.LastModifiedAt);

        row.Rule = command.Rule;
        row.MinDays = command.MinDays;
        row.ExtensionMonths = command.ExtensionMonths;
        row.Description = command.Description;
        row.LastModifiedAt = clock.UtcNow;
        row.LastModifiedBy = command.ActorRef;
        return new ReplacementPolicyRow(row.ReplacementPolicyId, row.Code, row.Rule, row.MinDays, row.ExtensionMonths, row.Description, DocumentVersion.Encode(row.RowVersion));
    }
}

internal static class PolicyDrafts
{
    public static async Task<WarrantyPolicyVersion> DraftAsync(SerialisationDbContext db, TenantId tenantId, Guid policyVersionId, CancellationToken cancellationToken)
    {
        var row = await db.PolicyVersions.FirstOrDefaultAsync(v => v.TenantId == tenantId && v.PolicyVersionId == policyVersionId, cancellationToken)
            ?? throw new CommandRefusedException("policy_version_unknown", $"Policy version {policyVersionId} does not exist.");
        return row.Status == PolicyVersionStatus.Draft ? row
            : throw new CommandRefusedException("version_not_draft", $"{row.PolicyCode} v{row.Version} is {row.Status}; a published version is never edited.", "create the next version as a draft");
    }
}

using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Templates;

public sealed class TemplateMappingService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IPolicyCatalogueQuery catalogueQuery,
    TemplateKeywordRules keywordRules,
    IUserDirectory people)
{
    public async Task<ProposeTemplatesResponse> ProposeAsync(ProposeTemplatesRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var sourceRows = await db.Set<ContractTemplateStagingRow>().AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.AbsentSinceBatchId == null)
            .OrderBy(row => row.SourceSystem).ThenBy(row => row.SourcePk)
            .ToListAsync(cancellationToken);
        var requested = request.TemplateKeys?.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (requested is not null)
        {
            sourceRows = sourceRows.Where(row => requested.Contains(
                TemplateProposalBuilder.TemplateKey(row.SourceSystem, row.SourcePk))).ToList();
            if (sourceRows.Count != requested.Count)
                throw new TemplateMappingRuleException("template_not_found",
                    "One or more requested staged templates were not found.");
        }

        var catalogue = await catalogueQuery.ReadAsync(cancellationToken);
        var rules = (await keywordRules.GetAsync(cancellationToken)).Rules;
        var usage = await UsageAsync(cancellationToken);
        var proposed = 0;
        var unchanged = 0;
        var superseded = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var source in sourceRows)
        {
            var key = TemplateProposalBuilder.TemplateKey(source.SourceSystem, source.SourcePk);
            var draft = TemplateProposalBuilder.Build(source, catalogue, rules);
            var fingerprint = TemplateProposalBuilder.SourceFingerprint(source);
            var current = await db.TemplateMappings.SingleOrDefaultAsync(mapping =>
                mapping.TenantId == tenantId && mapping.TemplateKey == key
                && mapping.State != TemplateMappingState.Superseded, cancellationToken);
            var counts = usage.GetValueOrDefault((source.SourceSystem, source.TemplateName ?? source.SourcePk))
                ?? new(0, 0, 0, 0);
            if (current is not null && current.State == TemplateMappingState.Approved
                && current.SourceFingerprint == fingerprint
                && current.DecompositionFingerprint == draft.DecompositionFingerprint)
            {
                ApplyUsage(current, counts);
                unchanged++;
                continue;
            }

            if (current is not null && current.State == TemplateMappingState.Approved)
            {
                var replacementId = Guid.CreateVersion7(clock.UtcNow);
                current.State = TemplateMappingState.Superseded;
                current.SupersededAt = clock.UtcNow;
                current.SupersededByMappingId = replacementId;
                current = NewMapping(source, key, fingerprint, draft, counts, replacementId);
                db.TemplateMappings.Add(current);
                superseded++;
            }
            else if (current is null)
            {
                current = NewMapping(source, key, fingerprint, draft, counts,
                    Guid.CreateVersion7(clock.UtcNow));
                db.TemplateMappings.Add(current);
            }
            else
            {
                Apply(current, source, fingerprint, draft, counts);
                db.TemplateMappingBlocks.RemoveRange(db.TemplateMappingBlocks.Where(block =>
                    block.TenantId == tenantId && block.TemplateMappingId == current.TemplateMappingId));
            }
            AddBlocks(current, draft.Blocks);
            proposed++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(sourceRows.Count, proposed, unchanged, superseded);
    }

    public async Task<TemplatePatternPage> ListAsync(string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 100);
        var tenantId = tenant.TenantId;
        var query = db.TemplateMappings.AsNoTracking().Where(mapping => mapping.TenantId == tenantId
            && mapping.State != TemplateMappingState.Superseded);
        if (!string.IsNullOrWhiteSpace(cursor)) query = query.Where(mapping => mapping.PatternKey.CompareTo(cursor) > 0);
        var patternKeys = await query.GroupBy(mapping => mapping.PatternKey)
            .Select(group => group.Key).OrderBy(key => key).Take(take + 1).ToListAsync(cancellationToken);
        var pageKeys = patternKeys.Take(take).ToList();
        var mappings = await db.TemplateMappings.AsNoTracking().Where(mapping => mapping.TenantId == tenantId
                && pageKeys.Contains(mapping.PatternKey) && mapping.State != TemplateMappingState.Superseded)
            .OrderBy(mapping => mapping.PatternKey).ThenBy(mapping => mapping.TemplateName)
            .ToListAsync(cancellationToken);
        var items = mappings.GroupBy(mapping => mapping.PatternKey, StringComparer.Ordinal).Select(group =>
        {
            var rows = group.ToList();
            return new TemplatePatternView(group.Key, rows[0].PatternLabel,
                rows.All(row => row.State == TemplateMappingState.Approved)
                    ? TemplateMappingState.Approved : TemplateMappingState.Draft,
                rows.Count, SumUsage(rows), rows.Select(Summary).ToList());
        }).ToList();
        return new(items, patternKeys.Count > take && pageKeys.Count != 0 ? pageKeys[^1] : null);
    }

    public async Task<TemplateMappingDetail?> DetailAsync(string templateKey,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var mapping = await db.TemplateMappings.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenantId && row.TemplateKey == templateKey
            && row.State != TemplateMappingState.Superseded, cancellationToken);
        if (mapping is null) return null;
        var blocks = await db.TemplateMappingBlocks.AsNoTracking().Where(block =>
                block.TenantId == tenantId && block.TemplateMappingId == mapping.TemplateMappingId)
            .OrderBy(block => block.Position).ToListAsync(cancellationToken);
        string? approver = null;
        if (mapping.ApprovedBy is not null)
        {
            var names = await people.DisplayNamesAsync([mapping.ApprovedBy], cancellationToken);
            approver = names.GetValueOrDefault(mapping.ApprovedBy) ?? "Unknown user";
        }
        return Detail(mapping, blocks, approver);
    }

    public async Task<TemplateMappingDetail> UpdateAsync(string templateKey,
        UpdateTemplateMappingRequest request, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var mapping = await CurrentAsync(templateKey, cancellationToken);
        Draft(mapping);
        CheckVersion(mapping, request.ExpectedVersion);
        var catalogue = await catalogueQuery.ReadAsync(cancellationToken);
        var families = catalogue.Families.Where(family => family.Active)
            .ToDictionary(family => family.FamilyCode, StringComparer.Ordinal);
        if (request.Blocks.Count == 0 || request.Blocks.Select(block => block.FamilyCode)
                .Distinct(StringComparer.Ordinal).Count() != request.Blocks.Count)
            throw new TemplateMappingRuleException("template_blocks_invalid",
                "A mapping needs one or more distinct coverage families.");
        var terms = TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(mapping.SourceTermsJson);
        var proposed = new List<ProposedTemplateBlock>();
        foreach (var edit in request.Blocks)
        {
            if (!families.TryGetValue(edit.FamilyCode, out var family))
                throw new TemplateMappingRuleException("template_family_unknown",
                    $"Coverage family '{edit.FamilyCode}' is not active for this tenant.");
            PolicyCataloguePolicy? policy = null;
            if (edit.PolicyId is { } policyId)
            {
                policy = catalogue.Policies.SingleOrDefault(item => item.PolicyId == policyId)
                    ?? throw new TemplateMappingRuleException("template_policy_not_published",
                        "The selected policy version is not published.");
                if (policy.FamilyCode != family.FamilyCode)
                    throw new TemplateMappingRuleException("template_policy_family_mismatch",
                        "The selected policy belongs to another coverage family.");
            }
            proposed.Add(new(family, policy is null ? TemplateBlockResolution.PolicyToCreate
                : TemplateBlockResolution.ExistingPolicy, policy, terms));
        }
        if (proposed.Count(block => !block.Family.Stacking) != 1)
            throw new TemplateMappingRuleException("template_base_family_required",
                "A decomposition needs exactly one non-stacking base family.");

        db.TemplateMappingBlocks.RemoveRange(db.TemplateMappingBlocks.Where(block =>
            block.TenantId == tenantId && block.TemplateMappingId == mapping.TemplateMappingId));
        var needsPerson = proposed.Any(block => block.Policy is null);
        var reason = needsPerson ? "Choose a published policy for every coverage family before approval." : null;
        var pattern = TemplateProposalBuilder.Pattern(proposed, needsPerson, reason);
        mapping.NeedsPerson = needsPerson;
        mapping.ReviewReason = reason;
        mapping.PatternKey = pattern.PatternKey;
        mapping.PatternLabel = pattern.PatternLabel;
        mapping.DecompositionFingerprint = pattern.Fingerprint;
        mapping.ProposedAt = clock.UtcNow;
        AddBlocks(mapping, proposed);
        await db.SaveChangesAsync(cancellationToken);
        return (await DetailAsync(templateKey, cancellationToken))!;
    }

    public async Task<TemplateMappingDetail> ApproveAsync(string templateKey, string actor,
        CancellationToken cancellationToken)
    {
        Actor(actor);
        var mapping = await CurrentAsync(templateKey, cancellationToken);
        await ApproveMappingsAsync([mapping], actor, cancellationToken);
        return (await DetailAsync(templateKey, cancellationToken))!;
    }

    public async Task<ApprovePatternResponse> ApprovePatternAsync(string patternKey, string actor,
        CancellationToken cancellationToken)
    {
        Actor(actor);
        var tenantId = tenant.TenantId;
        var mappings = await db.TemplateMappings.Where(mapping => mapping.TenantId == tenantId
                && mapping.PatternKey == patternKey && mapping.State != TemplateMappingState.Superseded)
            .ToListAsync(cancellationToken);
        if (mappings.Count == 0)
            throw new TemplateMappingRuleException("template_pattern_not_found", "The proposal pattern was not found.");
        if (mappings.Any(mapping => mapping.State != TemplateMappingState.Draft))
            throw new TemplateMappingRuleException("template_pattern_not_draft",
                "Only a pattern whose mappings are all draft can be approved.");
        await ApproveMappingsAsync(mappings, actor, cancellationToken);
        return new(patternKey, mappings.Count);
    }

    private async Task ApproveMappingsAsync(IReadOnlyList<TemplateMapping> mappings, string actor,
        CancellationToken cancellationToken)
    {
        if (mappings.Any(mapping => mapping.State != TemplateMappingState.Draft))
            throw new TemplateMappingRuleException("template_mapping_not_draft", "Only a draft mapping can be approved.");
        if (mappings.Any(mapping => mapping.NeedsPerson))
            throw new TemplateMappingRuleException("template_mapping_needs_review",
                "Resolve every review issue before approval.");
        var tenantId = tenant.TenantId;
        var ids = mappings.Select(mapping => mapping.TemplateMappingId).ToList();
        var unresolved = await db.TemplateMappingBlocks.AnyAsync(block => block.TenantId == tenantId
            && ids.Contains(block.TemplateMappingId)
            && (block.Resolution != TemplateBlockResolution.ExistingPolicy || block.PolicyId == null),
            cancellationToken);
        if (unresolved)
            throw new TemplateMappingRuleException("template_policy_unresolved",
                "Every block must name an existing published policy before approval.");
        var now = clock.UtcNow;
        foreach (var mapping in mappings)
        {
            mapping.State = TemplateMappingState.Approved;
            mapping.ApprovedBy = actor;
            mapping.ApprovedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<TemplateMapping> CurrentAsync(string templateKey, CancellationToken cancellationToken) =>
        await db.TemplateMappings.SingleOrDefaultAsync(mapping => mapping.TenantId == tenant.TenantId
            && mapping.TemplateKey == templateKey && mapping.State != TemplateMappingState.Superseded,
            cancellationToken) ?? throw new TemplateMappingRuleException("template_mapping_not_found",
            "The template mapping was not found.");

    private static void Draft(TemplateMapping mapping)
    {
        if (mapping.State != TemplateMappingState.Draft)
            throw new TemplateMappingRuleException("template_mapping_immutable",
                "An approved mapping cannot be edited; re-propose it to preserve the approved version.");
    }

    private static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new TemplateMappingRuleException("audit_actor_required", "An authenticated actor is required.");
    }

    private static void CheckVersion(TemplateMapping mapping, string expected)
    {
        var current = Convert.ToBase64String(mapping.RowVersion);
        if (!string.Equals(current, expected, StringComparison.Ordinal))
            throw new TemplateMappingRuleException("template_mapping_changed",
                "The mapping changed after it was opened. Reload it before saving.");
    }

    private TemplateMapping NewMapping(ContractTemplateStagingRow source, string key, string fingerprint,
        TemplateProposalDraft draft, TemplateUsageCounts usage, Guid id)
    {
        var mapping = new TemplateMapping
        {
            TenantId = tenant.TenantId,
            TemplateMappingId = id,
            TemplateKey = key,
            SourceSystem = source.SourceSystem,
            TemplateSourceKey = source.SourcePk,
            TemplateName = source.TemplateName ?? source.SourcePk,
            State = TemplateMappingState.Draft,
            ProposedAt = clock.UtcNow,
            SourceFingerprint = fingerprint,
            SourceTermsJson = "{}",
            WordingToReviewJson = "[]",
            PatternKey = draft.PatternKey,
            PatternLabel = draft.PatternLabel,
            DecompositionFingerprint = draft.DecompositionFingerprint,
        };
        Apply(mapping, source, fingerprint, draft, usage);
        return mapping;
    }

    private void Apply(TemplateMapping mapping, ContractTemplateStagingRow source, string fingerprint,
        TemplateProposalDraft draft, TemplateUsageCounts usage)
    {
        mapping.TemplateName = source.TemplateName ?? source.SourcePk;
        mapping.ExtractionBatchId = source.BatchId;
        mapping.ExtractedAt = source.ExtractedAt;
        mapping.SourceFingerprint = fingerprint;
        mapping.SourceTermsJson = TemplateProposalBuilder.JsonValue(draft.Terms);
        mapping.WordingToReviewJson = TemplateProposalBuilder.JsonValue(draft.Wording);
        mapping.PatternKey = draft.PatternKey;
        mapping.PatternLabel = draft.PatternLabel;
        mapping.DecompositionFingerprint = draft.DecompositionFingerprint;
        mapping.NeedsPerson = draft.NeedsPerson;
        mapping.ReviewReason = draft.ReviewReason;
        mapping.ProposedAt = clock.UtcNow;
        mapping.ApprovedBy = null;
        mapping.ApprovedAt = null;
        ApplyUsage(mapping, usage);
    }

    private static void ApplyUsage(TemplateMapping mapping, TemplateUsageCounts usage)
    {
        mapping.ContractCount = usage.Contracts;
        mapping.SerialCount = usage.Serials;
        mapping.ActiveContractCount = usage.ActiveContracts;
        mapping.ActiveSerialCount = usage.ActiveSerials;
    }

    private void AddBlocks(TemplateMapping mapping, IReadOnlyList<ProposedTemplateBlock> blocks)
    {
        var position = 0;
        foreach (var block in blocks)
            db.TemplateMappingBlocks.Add(new()
            {
                TenantId = tenant.TenantId,
                TemplateMappingBlockId = Guid.CreateVersion7(clock.UtcNow.AddTicks(position)),
                TemplateMappingId = mapping.TemplateMappingId,
                Position = position++,
                FamilyCode = block.Family.FamilyCode,
                FamilyName = block.Family.FamilyName,
                Stacking = block.Family.Stacking,
                Resolution = block.Resolution,
                PolicyId = block.Policy?.PolicyId,
                PolicyCode = block.Policy?.PolicyCode,
                PolicyVersion = block.Policy?.Version,
                PolicyName = block.Policy?.PolicyName,
                ProposedTermsJson = TemplateProposalBuilder.JsonValue(block.Terms),
            });
    }

    private async Task<Dictionary<(string SourceSystem, string TemplateName), TemplateUsageCounts>> UsageAsync(
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var contracts = db.Set<ServiceContractStagingRow>().AsNoTracking().Where(contract =>
            contract.TenantId == tenantId && contract.AbsentSinceBatchId == null && contract.TemplateName != null);
        var contractRows = await contracts.GroupBy(contract => new { contract.SourceSystem, contract.TemplateName })
            .Select(group => new
            {
                group.Key.SourceSystem,
                TemplateName = group.Key.TemplateName!,
                Contracts = group.Count(),
                ActiveContracts = group.Count(contract => contract.Status == "A"
                    && (contract.StartDate == null || contract.StartDate <= contract.ExtractedAt.Date)
                    && (contract.EndDate == null || contract.EndDate >= contract.ExtractedAt.Date)
                    && (contract.TerminationDate == null || contract.TerminationDate > contract.ExtractedAt.Date)),
            }).ToListAsync(cancellationToken);
        var lineRows = await (from line in db.Set<ContractLineStagingRow>().AsNoTracking()
                join contract in contracts on new { line.TenantId, line.SourceSystem, line.ContractId }
                    equals new { contract.TenantId, contract.SourceSystem, contract.ContractId }
                where line.TenantId == tenantId && line.AbsentSinceBatchId == null
                group new { line, contract } by new { contract.SourceSystem, contract.TemplateName } into grouped
                select new
                {
                    grouped.Key.SourceSystem,
                    TemplateName = grouped.Key.TemplateName!,
                    Serials = grouped.Count(),
                    ActiveSerials = grouped.Count(value => value.contract.Status == "A"
                        && (value.contract.StartDate == null || value.contract.StartDate <= value.contract.ExtractedAt.Date)
                        && (value.contract.EndDate == null || value.contract.EndDate >= value.contract.ExtractedAt.Date)
                        && (value.contract.TerminationDate == null || value.contract.TerminationDate > value.contract.ExtractedAt.Date)
                        && (value.line.StartDate == null || value.line.StartDate <= value.contract.ExtractedAt.Date)
                        && (value.line.EndDate == null || value.line.EndDate >= value.contract.ExtractedAt.Date)
                        && (value.line.TerminationDate == null || value.line.TerminationDate > value.contract.ExtractedAt.Date)),
                }).ToListAsync(cancellationToken);
        var lineLookup = lineRows.ToDictionary(row => (row.SourceSystem, row.TemplateName),
            row => (row.Serials, row.ActiveSerials));
        return contractRows.ToDictionary(row => (row.SourceSystem, row.TemplateName), row =>
        {
            var lines = lineLookup.GetValueOrDefault((row.SourceSystem, row.TemplateName));
            return new TemplateUsageCounts(row.Contracts, lines.Serials, row.ActiveContracts, lines.ActiveSerials);
        });
    }

    private static TemplateUsageCounts SumUsage(IReadOnlyList<TemplateMapping> mappings) => new(
        mappings.Sum(mapping => mapping.ContractCount), mappings.Sum(mapping => mapping.SerialCount),
        mappings.Sum(mapping => mapping.ActiveContractCount), mappings.Sum(mapping => mapping.ActiveSerialCount));

    private static TemplateMappingSummary Summary(TemplateMapping mapping) => new(mapping.TemplateKey,
        mapping.TemplateName, mapping.State, mapping.NeedsPerson, mapping.ReviewReason,
        new(mapping.ContractCount, mapping.SerialCount, mapping.ActiveContractCount, mapping.ActiveSerialCount));

    private static TemplateMappingDetail Detail(TemplateMapping mapping,
        IReadOnlyList<TemplateMappingBlock> blocks, string? approver) => new(mapping.TemplateKey,
        mapping.TemplateName, mapping.SourceSystem, mapping.TemplateSourceKey, mapping.ExtractionBatchId,
        mapping.ExtractedAt, mapping.State, mapping.NeedsPerson, mapping.ReviewReason, mapping.PatternKey,
        mapping.PatternLabel, TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(mapping.SourceTermsJson),
        TemplateProposalBuilder.ReadJson<List<TemplateWordingToReview>>(mapping.WordingToReviewJson),
        new(mapping.ContractCount, mapping.SerialCount, mapping.ActiveContractCount, mapping.ActiveSerialCount),
        blocks.Select(block => new TemplateBlockView(block.FamilyCode, block.FamilyName, block.Stacking,
            block.Resolution, block.PolicyId, block.PolicyCode, block.PolicyVersion, block.PolicyName,
            TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(block.ProposedTermsJson))).ToList(),
        approver, mapping.ApprovedAt, Convert.ToBase64String(mapping.RowVersion));
}

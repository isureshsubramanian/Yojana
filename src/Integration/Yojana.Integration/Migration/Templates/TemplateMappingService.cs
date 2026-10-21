using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Wolverine;
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
    IUserDirectory people,
    IMessageBus bus)
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

    public async Task<TemplatePatternPage> ListAsync(string? state, bool? deletedInSource, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 100);
        CheckPresentationState(state);
        var rows = await PresentationRowsAsync(cancellationToken);
        if (deletedInSource is { } deleted) rows = rows.Where(row => row.DeletedInSource == deleted).ToList();
        var patterns = rows.GroupBy(row => row.Mapping.PatternKey, StringComparer.Ordinal).Select(group =>
        {
            var members = group.ToList();
            return new TemplatePatternView(group.Key, members[0].Mapping.PatternLabel,
                PatternState(members), members.Count, SumUsage(members.Select(row => row.Mapping).ToList()),
                members.OrderBy(row => row.Mapping.TemplateName, StringComparer.Ordinal)
                    .Select(Summary).ToList());
        }).Where(pattern => state is null || pattern.State == state)
            .OrderByDescending(pattern => pattern.Usage.ActiveContracts)
            .ThenBy(pattern => pattern.PatternKey, StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var after = ReadCursor(cursor);
            patterns = patterns.Where(pattern => pattern.Usage.ActiveContracts < after.ActiveContracts
                || pattern.Usage.ActiveContracts == after.ActiveContracts
                && string.CompareOrdinal(pattern.PatternKey, after.PatternKey) > 0).ToList();
        }
        var page = patterns.Take(take + 1).ToList();
        var items = page.Take(take).ToList();
        return new(items, page.Count > take && items.Count != 0
            ? Cursor(items[^1].Usage.ActiveContracts, items[^1].PatternKey) : null);
    }

    public async Task<TemplateMappingSummaryView> SummaryAsync(CancellationToken cancellationToken)
    {
        var rows = await PresentationRowsAsync(cancellationToken);
        var published = (await catalogueQuery.ReadAsync(cancellationToken)).Policies
            .Select(policy => policy.PolicyId).ToHashSet();
        var ids = rows.Select(row => row.Mapping.TemplateMappingId).ToList();
        var policyBlocks = await db.TemplateMappingBlocks.AsNoTracking().Where(block =>
                block.TenantId == tenant.TenantId && ids.Contains(block.TemplateMappingId)
                && block.Resolution == TemplateBlockResolution.PolicyToCreate)
            .Select(block => new { block.FamilyCode, block.ProposedTermsJson, block.PolicyId })
            .ToListAsync(cancellationToken);
        var policiesToCreate = policyBlocks.Where(block => block.PolicyId is null
                || !published.Contains(block.PolicyId.Value))
            .Select(block => block.FamilyCode + "\0" + TemplateProposalBuilder.PolicyTermsKey(block.FamilyCode,
                TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(block.ProposedTermsJson)))
            .Distinct(StringComparer.Ordinal).Count();
        TemplateStateSummary Count(string state)
        {
            var matching = rows.Where(row => row.State == state).ToList();
            return new(matching.Count, matching.Select(row => row.Mapping.PatternKey).Distinct().Count(),
                matching.Sum(row => row.Mapping.ActiveContractCount));
        }
        return new(rows.Count, rows.Select(row => row.Mapping.PatternKey).Distinct().Count(),
            rows.Sum(row => row.Mapping.ActiveContractCount), Count(TemplateMappingState.Approved),
            Count(TemplateMappingState.Waiting), Count(TemplateMappingState.NeedsPerson),
            Count(TemplateMappingState.WaitingForPolicyPublication), policiesToCreate);
    }

    public Task<PolicyCatalogue> CatalogueAsync(CancellationToken cancellationToken) =>
        catalogueQuery.ReadAsync(cancellationToken);

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
        var catalogue = await catalogueQuery.ReadAsync(cancellationToken);
        var published = catalogue.Policies.Select(policy => policy.PolicyId).ToHashSet();
        var source = await db.Set<ContractTemplateStagingRow>().AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenantId && row.SourceSystem == mapping.SourceSystem
            && row.SourcePk == mapping.TemplateSourceKey, cancellationToken);
        return Detail(mapping, blocks, approver, State(mapping, blocks, published), Deleted(source), published);
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
        var needsPerson = proposed.Any(block => block.Policy is null) && !CanCreatePolicy(terms);
        var reason = needsPerson ? "The source terms are incomplete; choose a published policy for every block." : null;
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
        if (mapping.State == TemplateMappingState.Approved)
            return (await DetailAsync(templateKey, cancellationToken))!;
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
        if (mappings.All(mapping => mapping.State == TemplateMappingState.Approved))
            return new(patternKey, mappings.Count);
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
        var blocks = await db.TemplateMappingBlocks.Where(block => block.TenantId == tenantId
                && ids.Contains(block.TemplateMappingId)).OrderBy(block => block.Position)
            .ToListAsync(cancellationToken);
        if (blocks.Count == 0 || blocks.Any(block => block.Resolution == TemplateBlockResolution.ExistingPolicy
                && block.PolicyId == null))
            throw new TemplateMappingRuleException("template_policy_unresolved", "Every existing-policy block must name a published policy.");
        await CreateDraftPoliciesAsync(mappings, blocks, actor, cancellationToken);
        var now = clock.UtcNow;
        foreach (var mapping in mappings)
        {
            mapping.State = TemplateMappingState.Approved;
            mapping.ApprovedBy = actor;
            mapping.ApprovedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CreateDraftPoliciesAsync(IReadOnlyList<TemplateMapping> mappings,
        IReadOnlyList<TemplateMappingBlock> blocks, string actor, CancellationToken cancellationToken)
    {
        var toCreate = blocks.Where(block => block.Resolution == TemplateBlockResolution.PolicyToCreate)
            .GroupBy(block => block.FamilyCode + "\0" + TemplateProposalBuilder.PolicyTermsKey(block.FamilyCode,
                TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(block.ProposedTermsJson)), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToList();
        if (toCreate.Count == 0) return;
        var sourceSystems = mappings.Select(mapping => mapping.SourceSystem).Distinct().ToList();
        var templateNames = mappings.Select(mapping => mapping.TemplateName).Distinct().ToList();
        var starts = await db.Set<ServiceContractStagingRow>().AsNoTracking().Where(contract =>
                contract.TenantId == tenant.TenantId && contract.AbsentSinceBatchId == null
                && sourceSystems.Contains(contract.SourceSystem) && contract.TemplateName != null
                && templateNames.Contains(contract.TemplateName) && contract.StartDate != null)
            .Select(contract => new { contract.SourceSystem, contract.TemplateName, contract.StartDate })
            .ToListAsync(cancellationToken);
        var mappingById = mappings.ToDictionary(mapping => mapping.TemplateMappingId);
        foreach (var group in toCreate)
        {
            var assigned = group.FirstOrDefault(block => block.PolicyId is not null);
            PolicyDraftResult policy;
            if (assigned is not null)
            {
                policy = new(assigned.PolicyId!.Value, assigned.PolicyCode!, assigned.PolicyVersion!.Value,
                    "draft", new([]));
            }
            else
            {
                var sample = group.First();
                var terms = TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(sample.ProposedTermsJson);
                if (!CanCreatePolicy(terms))
                    throw new TemplateMappingRuleException("template_policy_terms_incomplete",
                        "A draft policy cannot be created until duration, parts, labour and travel terms are resolved.");
                var related = group.Select(block => mappingById[block.TemplateMappingId]).ToList();
                var earliest = starts.Where(start => related.Any(mapping =>
                        mapping.SourceSystem == start.SourceSystem && mapping.TemplateName == start.TemplateName))
                    .Min(start => start.StartDate);
                var appliesFrom = earliest is { } start
                    ? DateOnly.FromDateTime(start)
                    : DateOnly.FromDateTime(related.Min(mapping => mapping.ExtractedAt));
                var commandId = DraftCommandId(tenant.TenantId.ToString(), sample.FamilyCode,
                    TemplateProposalBuilder.PolicyTermsKey(sample.FamilyCode, terms));
                policy = await bus.InvokeAsync<PolicyDraftResult>(new CreatePolicyDraft(commandId,
                        DraftPolicyCode(sample, terms), DraftPolicyName(sample, terms), sample.FamilyCode,
                        sample.Stacking ? EntitlementType.Extended : EntitlementType.Manufacturer, appliesFrom,
                        DraftPolicyTerms(sample, terms), actor),
                    new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, cancellationToken);
            }
            foreach (var block in group)
            {
                block.PolicyId = policy.PolicyVersionId;
                block.PolicyCode = policy.PolicyCode;
                block.PolicyVersion = policy.Version;
                block.PolicyName = DraftPolicyName(block,
                    TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(block.ProposedTermsJson));
            }
        }
    }

    private async Task<List<TemplatePresentationRow>> PresentationRowsAsync(CancellationToken cancellationToken)
    {
        var mappings = await db.TemplateMappings.AsNoTracking().Where(mapping =>
                mapping.TenantId == tenant.TenantId && mapping.State != TemplateMappingState.Superseded)
            .ToListAsync(cancellationToken);
        var ids = mappings.Select(mapping => mapping.TemplateMappingId).ToList();
        var blocks = await db.TemplateMappingBlocks.AsNoTracking().Where(block =>
                block.TenantId == tenant.TenantId && ids.Contains(block.TemplateMappingId))
            .ToListAsync(cancellationToken);
        var published = (await catalogueQuery.ReadAsync(cancellationToken)).Policies
            .Select(policy => policy.PolicyId).ToHashSet();
        var sources = await db.Set<ContractTemplateStagingRow>().AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId)
            .Select(row => new { row.SourceSystem, row.SourcePk, row.AbsentSinceBatchId, row.Deleted })
            .ToListAsync(cancellationToken);
        var sourceByKey = sources.ToDictionary(row => (row.SourceSystem, row.SourcePk));
        return mappings.Select(mapping =>
        {
            sourceByKey.TryGetValue((mapping.SourceSystem, mapping.TemplateSourceKey), out var source);
            var mappingBlocks = blocks.Where(block => block.TemplateMappingId == mapping.TemplateMappingId).ToList();
            return new TemplatePresentationRow(mapping, Deleted(source?.AbsentSinceBatchId, source?.Deleted),
                State(mapping, mappingBlocks, published));
        }).ToList();
    }

    private static string State(TemplateMapping mapping, IReadOnlyList<TemplateMappingBlock> blocks,
        HashSet<Guid> published) => mapping.State switch
    {
        TemplateMappingState.Draft when mapping.NeedsPerson => TemplateMappingState.NeedsPerson,
        TemplateMappingState.Draft => TemplateMappingState.Waiting,
        TemplateMappingState.Approved when blocks.Any(block => block.Resolution == TemplateBlockResolution.PolicyToCreate
            && (block.PolicyId is null || !published.Contains(block.PolicyId.Value))) =>
                TemplateMappingState.WaitingForPolicyPublication,
        _ => TemplateMappingState.Approved,
    };

    private static string PatternState(IReadOnlyList<TemplatePresentationRow> rows)
    {
        if (rows.Any(row => row.State == TemplateMappingState.NeedsPerson)) return TemplateMappingState.NeedsPerson;
        if (rows.Any(row => row.State == TemplateMappingState.Waiting)) return TemplateMappingState.Waiting;
        if (rows.Any(row => row.State == TemplateMappingState.WaitingForPolicyPublication))
            return TemplateMappingState.WaitingForPolicyPublication;
        return TemplateMappingState.Approved;
    }

    private static bool CanCreatePolicy(TemplateSourceTerms terms) => terms.DurationValue is > 0
        && terms.PartsPct is not null && terms.LabourPct is not null && terms.TravelPct is not null;

    private static PolicyTerms DraftPolicyTerms(TemplateMappingBlock block, TemplateSourceTerms terms) => new(
        new DurationRule(terms.DurationValue!.Value, DurationRule.Months), DurationBasis.Time, [],
        new StartRule(StartTrigger.Deliver, [new StartFallback(StartTrigger.Invoice)], 90),
        new CoverageTerms(terms.PartsPct!.Value, terms.LabourPct!.Value, terms.TravelPct!.Value,
            TemplateProposalBuilder.ServiceMode(block.FamilyCode), terms.ResponseHours,
            terms.CoverageHours), [], ["WORLD"], Transferability.Yes, "full", null, [], null,
        block.Stacking ? AssuranceOrService.Service : AssuranceOrService.Assurance, PolicyTerms.TimeZoneUtc);

    private static string DraftPolicyName(TemplateMappingBlock block, TemplateSourceTerms terms)
    {
        var response = block.FamilyCode == "service_level" && terms.ResponseHours is { } hours
            ? $" · {hours.ToString(CultureInfo.InvariantCulture)}-hour response" : "";
        return $"{block.FamilyName} · {terms.DurationValue?.ToString(CultureInfo.InvariantCulture)} months{response}";
    }

    private static string DraftPolicyCode(TemplateMappingBlock block, TemplateSourceTerms terms)
    {
        var family = new string(block.FamilyCode.ToUpperInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (family.Length > 24) family = family[..24].TrimEnd('-');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            block.FamilyCode + "\0" + TemplateProposalBuilder.PolicyTermsKey(block.FamilyCode, terms))))[..8]
            .ToUpperInvariant();
        return $"MIG-{family}-{terms.DurationValue}M-{hash}";
    }

    private static Guid DraftCommandId(string tenantId, string familyCode, string termsJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId + "\0" + familyCode + "\0" + termsJson));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static bool Deleted(ContractTemplateStagingRow? source) => source is not null
        && Deleted(source.AbsentSinceBatchId, source.Deleted);

    private static bool Deleted(Guid? absentSinceBatchId, string? value) => absentSinceBatchId is not null
        || value?.Trim().ToUpperInvariant() is "Y" or "YES" or "TYES" or "1" or "TRUE";

    private static string? SourceReference(string value) => Guid.TryParse(value, out _) ? null : value;

    private static void CheckPresentationState(string? state)
    {
        if (state is null or TemplateMappingState.Waiting or TemplateMappingState.NeedsPerson
            or TemplateMappingState.Approved or TemplateMappingState.WaitingForPolicyPublication) return;
        throw new TemplateMappingRuleException("template_state_invalid", "The template state filter is not recognised.");
    }

    private static string Cursor(int activeContracts, string patternKey) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(activeContracts.ToString(CultureInfo.InvariantCulture) + "\n" + patternKey));

    private static (int ActiveContracts, string PatternKey) ReadCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var activeContracts) || activeContracts < 0 || parts[1].Length != 64)
                throw new FormatException();
            return (activeContracts, parts[1]);
        }
        catch (FormatException)
        {
            throw new TemplateMappingRuleException("template_cursor_invalid", "The template page is no longer available.");
        }
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

    private static TemplateMappingSummary Summary(TemplatePresentationRow row) => new(row.Mapping.TemplateKey,
        row.Mapping.TemplateName, row.State, row.Mapping.NeedsPerson, row.Mapping.ReviewReason,
        row.DeletedInSource, SourceReference(row.Mapping.TemplateSourceKey),
        TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(row.Mapping.SourceTermsJson),
        new(row.Mapping.ContractCount, row.Mapping.SerialCount, row.Mapping.ActiveContractCount,
            row.Mapping.ActiveSerialCount));

    private static TemplateMappingDetail Detail(TemplateMapping mapping,
        IReadOnlyList<TemplateMappingBlock> blocks, string? approver, string state, bool deletedInSource,
        HashSet<Guid> published) => new(mapping.TemplateKey,
        mapping.TemplateName, mapping.SourceSystem, mapping.TemplateSourceKey, mapping.ExtractionBatchId,
        mapping.ExtractedAt, state, mapping.NeedsPerson, mapping.ReviewReason, deletedInSource,
        SourceReference(mapping.TemplateSourceKey), mapping.PatternKey, mapping.PatternLabel,
        TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(mapping.SourceTermsJson),
        TemplateProposalBuilder.ReadJson<List<TemplateWordingToReview>>(mapping.WordingToReviewJson),
        new(mapping.ContractCount, mapping.SerialCount, mapping.ActiveContractCount, mapping.ActiveSerialCount),
        blocks.Select(block => new TemplateBlockView(block.FamilyCode, block.FamilyName, block.Stacking,
            block.Resolution, block.PolicyId, block.PolicyCode, block.PolicyVersion, block.PolicyName,
            block.PolicyId is null ? null : published.Contains(block.PolicyId.Value) ? "published" : "draft",
            TemplateProposalBuilder.ReadJson<TemplateSourceTerms>(block.ProposedTermsJson))).ToList(),
        approver, mapping.ApprovedAt, Convert.ToBase64String(mapping.RowVersion));

    private sealed record TemplatePresentationRow(TemplateMapping Mapping, bool DeletedInSource, string State);
}

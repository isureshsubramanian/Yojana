using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;

namespace Yojana.Serialisation.Jurisdictions;

/// <summary>
/// Tenant rule catalogue and its narrow read ports. Resolution is deterministic: subdivision, country, region group,
/// then any; within that, an exact buyer, item and bounded-price qualifier wins in that order. A later effective start,
/// then rule version and id are final stable tie-breaks. Saves reject overlaps at equal precedence and specificity.
/// </summary>
public sealed class JurisdictionCatalogueService(SerialisationDbContext db, ITenantContext tenant, IClock clock)
    : IJurisdictionRuleResolver, IJurisdictionRegionPort, IJurisdictionLabelRulesPort, IJurisdictionCertificateTextPort
{
    private const string StarterResourceSuffix = "Jurisdictions.StarterPacks.eu-starter.json";
    private const string StarterActor = "system:jurisdiction-starters";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<string> PlaceholderPaths => ["jurisdiction.mandatory_text"];

    public async Task<JurisdictionCatalogue> CatalogueAsync(string? country, string? kind, CancellationToken cancellationToken)
    {
        await EnsureStarterPacksAsync(cancellationToken);
        var groups = await RegionGroupsAsync(cancellationToken);
        var rules = await RulesAsync(kind, cancellationToken);
        if (!string.IsNullOrWhiteSpace(country))
        {
            var code = Country(country);
            var applicable = await ApplicableScopesAsync(code, null, cancellationToken);
            rules = rules.Where(row => row.Scope == JurisdictionScope.Any
                || row.CountryCode == code || row.SubdivisionCode?.StartsWith(code + "-", StringComparison.Ordinal) == true
                || row.RegionGroupId is { } groupId && applicable.Contains(groupId.ToString("N"))).ToList();
        }
        return new(JurisdictionRuleKinds.All, groups, rules, await RulePacksAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<RegionGroupRow>> RegionGroupsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.RegionGroups.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .OrderBy(row => row.Name).ToListAsync(cancellationToken);
        return rows.Select(ToRow).ToList();
    }

    public async Task<IReadOnlyList<JurisdictionRuleRow>> RulesAsync(string? kind, CancellationToken cancellationToken)
    {
        var query = db.JurisdictionRules.AsNoTracking().Where(row => row.TenantId == tenant.TenantId);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(row => row.Kind == kind);
        var rows = await query.OrderBy(row => row.Kind).ThenByDescending(row => row.EffectiveFrom)
            .ThenByDescending(row => row.Version).ToListAsync(cancellationToken);
        var groupNames = await db.RegionGroups.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .ToDictionaryAsync(row => row.RegionGroupId, row => row.Name, cancellationToken);
        return rows.Select(row => ToRow(row, row.RegionGroupId is { } id && groupNames.TryGetValue(id, out var name) ? name : null)).ToList();
    }

    public async Task<IReadOnlyList<RulePackRow>> RulePacksAsync(CancellationToken cancellationToken)
    {
        await EnsureStarterPacksAsync(cancellationToken);
        var packs = await db.RulePacks.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .OrderBy(row => row.Name).ToListAsync(cancellationToken);
        var packEntries = await db.RulePackRules.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .Select(row => new { row.RulePackId, row.Template }).ToListAsync(cancellationToken);
        var counts = packEntries.Where(entry => IsRuleTemplate(entry.Template)).GroupBy(entry => entry.RulePackId)
            .ToDictionary(group => group.Key, group => group.Count());
        return packs.Select(pack => new RulePackRow(pack.RulePackId, pack.Code, pack.Name, pack.Warning,
            counts.GetValueOrDefault(pack.RulePackId), pack.ImportedAt is not null, pack.ImportedAt, pack.ImportedBy)).ToList();
    }

    public async Task<RulePackPreview?> PreviewPackAsync(Guid rulePackId, CancellationToken cancellationToken)
    {
        await EnsureStarterPacksAsync(cancellationToken);
        var pack = await db.RulePacks.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.RulePackId == rulePackId, cancellationToken);
        if (pack is null) return null;
        var entries = await db.RulePackRules.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.RulePackId == rulePackId).OrderBy(row => row.Sequence).ToListAsync(cancellationToken);
        return Preview(pack, entries);
    }

    public async Task<RegionGroupSaved> SaveRegionGroupAsync(SaveRegionGroup command, CancellationToken cancellationToken)
    {
        var code = GroupCode(command.Code);
        var name = Required(command.Name, 200, "region_group_name_invalid", "A region-group name is required and cannot exceed 200 characters.");
        var members = NormaliseMembers(command.Members);
        var tenantId = tenant.TenantId;
        var existingByCommand = await db.RegionGroups.FirstOrDefaultAsync(row => row.TenantId == tenantId
            && row.LastCommandId == command.CommandId, cancellationToken);
        if (existingByCommand is not null) return new(existingByCommand.RegionGroupId, DocumentVersion.Encode(existingByCommand.RowVersion), true);

        RegionGroup row;
        if (command.RegionGroupId is { } id)
        {
            row = await db.RegionGroups.FirstOrDefaultAsync(group => group.TenantId == tenantId && group.RegionGroupId == id, cancellationToken)
                ?? throw new CommandRefusedException("region_group_unknown", $"Region group {id} does not exist.");
            DocumentVersion.RequireExpected(command.ExpectedVersion, row.RowVersion, row.LastModifiedBy, row.LastModifiedAt);
        }
        else
        {
            row = new RegionGroup
            {
                TenantId = tenantId, RegionGroupId = TimeOrderedId.New(clock.UtcNow), LastCommandId = command.CommandId,
                Code = code, Name = name, Members = "[]", CreatedAt = clock.UtcNow, CreatedBy = command.ActorRef,
                LastModifiedAt = clock.UtcNow, LastModifiedBy = command.ActorRef,
            };
            db.RegionGroups.Add(row);
        }
        if (await db.RegionGroups.AsNoTracking().AnyAsync(group => group.TenantId == tenantId && group.Code == code
            && group.RegionGroupId != row.RegionGroupId, cancellationToken))
            throw new CommandRefusedException("region_group_code_in_use", $"Region-group code '{code}' is already in use.");

        await EnsureGroupChangeUnambiguousAsync(row.RegionGroupId, members, cancellationToken);
        row.Code = code;
        row.Name = name;
        row.Members = JsonSerializer.Serialize(members, Json);
        row.LastCommandId = command.CommandId;
        row.LastModifiedAt = clock.UtcNow;
        row.LastModifiedBy = command.ActorRef;
        await db.SaveChangesAsync(cancellationToken);
        return new(row.RegionGroupId, DocumentVersion.Encode(row.RowVersion), false);
    }

    public async Task<JurisdictionRuleSaved> SaveRuleAsync(SaveJurisdictionRule command, CancellationToken cancellationToken)
    {
        var replay = await db.JurisdictionRules.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.CommandId == command.CommandId, cancellationToken);
        if (replay is not null) return new(replay.RuleId, replay.RootRuleId, replay.Version, DocumentVersion.Encode(replay.RowVersion), true);

        var clean = await ValidateAsync(command, cancellationToken);
        JurisdictionRule? previous = null;
        Guid root;
        var version = 1;
        if (command.RuleId is { } ruleId)
        {
            previous = await db.JurisdictionRules.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
                && row.RuleId == ruleId, cancellationToken)
                ?? throw new CommandRefusedException("jurisdiction_rule_unknown", $"Jurisdiction rule {ruleId} does not exist.");
            DocumentVersion.RequireExpected(command.ExpectedVersion, previous.RowVersion, previous.ApprovedBy, previous.CreatedAt);
            if (clean.EffectiveFrom <= previous.EffectiveFrom)
                throw new CommandRefusedException("jurisdiction_rule_version_date_invalid",
                    "A replacement rule must start after the version it replaces.", "choose a later effective-from date");
            if (previous.EffectiveTo is not null && previous.EffectiveTo <= clean.EffectiveFrom)
                throw new CommandRefusedException("jurisdiction_rule_already_closed", "The selected rule version is already closed before this replacement starts.");
            root = previous.RootRuleId;
            version = await db.JurisdictionRules.Where(row => row.TenantId == tenant.TenantId && row.RootRuleId == root)
                .MaxAsync(row => row.Version, cancellationToken) + 1;
        }
        else root = TimeOrderedId.New(clock.UtcNow);

        await EnsureNoAmbiguousOverlapAsync(clean, previous?.RuleId, cancellationToken);
        if (previous is not null)
        {
            previous.EffectiveTo = clean.EffectiveFrom;
            previous.ClosedAt = clock.UtcNow;
            previous.ClosedBy = command.ActorRef;
        }
        var row = NewRule(clean, command.CommandId, root, previous?.RuleId, version, command.ActorRef);
        db.JurisdictionRules.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return new(row.RuleId, row.RootRuleId, row.Version, DocumentVersion.Encode(row.RowVersion), false);
    }

    public async Task<JurisdictionRuleClosed> CloseRuleAsync(CloseJurisdictionRule command, CancellationToken cancellationToken)
    {
        var row = await db.JurisdictionRules.FirstOrDefaultAsync(rule => rule.TenantId == tenant.TenantId
            && rule.RuleId == command.RuleId, cancellationToken)
            ?? throw new CommandRefusedException("jurisdiction_rule_unknown", $"Jurisdiction rule {command.RuleId} does not exist.");
        if (row.ClosedCommandId == command.CommandId)
            return new(row.RuleId, row.EffectiveTo!.Value, DocumentVersion.Encode(row.RowVersion), true);
        DocumentVersion.RequireExpected(command.ExpectedVersion, row.RowVersion, row.ApprovedBy, row.CreatedAt);
        if (command.EffectiveTo <= row.EffectiveFrom)
            throw new CommandRefusedException("jurisdiction_rule_close_date_invalid", "A rule must close after it starts.");
        if (row.EffectiveTo is { } closed && closed <= command.EffectiveTo)
            throw new CommandRefusedException("jurisdiction_rule_already_closed", $"The rule already closes on {closed:yyyy-MM-dd}.");
        row.EffectiveTo = command.EffectiveTo;
        row.ClosedCommandId = command.CommandId;
        row.ClosedBy = command.ActorRef;
        row.ClosedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new(row.RuleId, command.EffectiveTo, DocumentVersion.Encode(row.RowVersion), false);
    }

    public async Task<RulePackImported> ImportPackAsync(ImportRulePack command, CancellationToken cancellationToken)
    {
        await EnsureStarterPacksAsync(cancellationToken);
        var pack = await db.RulePacks.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.RulePackId == command.RulePackId, cancellationToken)
            ?? throw new CommandRefusedException("jurisdiction_rule_pack_unknown", $"Rule pack {command.RulePackId} does not exist.");
        var entries = await db.RulePackRules.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.RulePackId == pack.RulePackId).OrderBy(row => row.Sequence).ToListAsync(cancellationToken);
        var preview = Preview(pack, entries);
        if (pack.ImportCommandId == command.CommandId)
            return new(pack.RulePackId, preview.RegionGroups.Count, preview.Rules.Count, true);
        if (pack.ImportedAt is not null)
            throw new CommandRefusedException("jurisdiction_rule_pack_already_imported", $"'{pack.Name}' was already imported.");

        var groups = new Dictionary<string, RegionGroup>(StringComparer.Ordinal);
        foreach (var definition in preview.RegionGroups)
        {
            var existing = await db.RegionGroups.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
                && row.Code == definition.Code, cancellationToken);
            var members = NormaliseMembers(definition.Members);
            if (existing is not null)
            {
                var current = Members(existing.Members);
                if (!current.SequenceEqual(members))
                    throw new CommandRefusedException("jurisdiction_rule_pack_group_conflict",
                        $"Region group '{definition.Code}' already exists with different members.");
                groups[definition.Code] = existing;
                continue;
            }
            var row = new RegionGroup
            {
                TenantId = tenant.TenantId, RegionGroupId = TimeOrderedId.New(clock.UtcNow), LastCommandId = Guid.CreateVersion7(),
                Code = definition.Code, Name = definition.Name, Members = JsonSerializer.Serialize(members, Json),
                CreatedAt = clock.UtcNow, CreatedBy = command.ActorRef, LastModifiedAt = clock.UtcNow, LastModifiedBy = command.ActorRef,
            };
            db.RegionGroups.Add(row);
            groups[row.Code] = row;
        }

        var added = 0;
        foreach (var packed in preview.Rules)
        {
            var groupCode = packed.Scope == JurisdictionScope.RegionGroup ? packed.ScopeLabel : null;
            var groupId = groupCode is null ? (Guid?)null : groups[groupCode].RegionGroupId;
            var save = new SaveJurisdictionRule(command.CommandId, null, packed.Kind, packed.Scope,
                packed.Scope == JurisdictionScope.Country ? packed.ScopeLabel : null,
                packed.Scope == JurisdictionScope.Subdivision ? packed.ScopeLabel : null, groupId,
                packed.BuyerClass, null, null, null, null, packed.DateTested, packed.EffectiveFrom, null,
                packed.Value, packed.SourceCitation, packed.SourceNote, false, null, ActorRef: command.ActorRef);
            var clean = await ValidateAsync(save, cancellationToken, groups.Values);
            await EnsureNoAmbiguousOverlapAsync(clean, null, cancellationToken);
            db.JurisdictionRules.Add(NewRule(clean, Guid.CreateVersion7(), TimeOrderedId.New(clock.UtcNow), null, 1, command.ActorRef));
            added++;
        }
        pack.ImportCommandId = command.CommandId;
        pack.ImportedAt = clock.UtcNow;
        pack.ImportedBy = command.ActorRef;
        await db.SaveChangesAsync(cancellationToken);
        return new(pack.RulePackId, preview.RegionGroups.Count, added, false);
    }

    public async Task<IReadOnlyList<JurisdictionRuleResolution>> ResolveAsync(string kind, JurisdictionRuleContext context,
        CancellationToken cancellationToken)
    {
        JurisdictionRuleKinds.Require(kind);
        var rows = await db.JurisdictionRules.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.Kind == kind).ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];
        var groups = await db.RegionGroups.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .ToDictionaryAsync(row => row.RegionGroupId, row => Members(row.Members), cancellationToken);
        var matches = rows.Select(row => Match(row, context, groups)).Where(match => match is not null)
            .Select(match => match!).OrderByDescending(match => match.Precedence)
            .ThenByDescending(match => match.BuyerSpecificity).ThenByDescending(match => match.ItemSpecificity)
            .ThenByDescending(match => match.PriceSpecificity).ThenByDescending(match => match.Rule.EffectiveFrom)
            .ThenByDescending(match => match.Rule.Version).ThenBy(match => match.Rule.RuleId).ToList();
        if (matches.Count == 0) return [];
        var winner = matches[0];
        return matches.Where(match => match.Precedence == winner.Precedence
            && match.BuyerSpecificity == winner.BuyerSpecificity && match.ItemSpecificity == winner.ItemSpecificity
            && match.PriceSpecificity == winner.PriceSpecificity)
            .Take(1).Select(ToResolution).ToList();
    }

    public async Task<bool> MatchesAsync(string regionOrCountry, string country, string? subdivision,
        CancellationToken cancellationToken)
    {
        if (string.Equals(regionOrCountry, JurisdictionScope.Any, StringComparison.OrdinalIgnoreCase)
            || string.Equals(regionOrCountry, "WORLD", StringComparison.OrdinalIgnoreCase)) return true;
        var scopes = await ApplicableScopesAsync(country, subdivision, cancellationToken);
        return scopes.Contains(regionOrCountry.Trim().ToUpperInvariant());
    }

    public Task<bool> IsKnownScopeAsync(string regionOrCountry, CancellationToken cancellationToken)
    {
        // Coupled to PolicyStore's save guard: a code accepted here must also be emitted by ApplicableScopesAsync,
        // otherwise a saved policy could never cover a sale. WORLD is the deliberate universal non-group keyword.
        var code = regionOrCountry.Trim().ToUpperInvariant();
        if (code == "WORLD" || IsoCountryCodes.Contains(code)) return Task.FromResult(true);
        return db.RegionGroups.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId && row.Code == code,
            cancellationToken);
    }

    public async Task<IReadOnlySet<string>> ApplicableScopesAsync(string country, string? subdivision,
        CancellationToken cancellationToken)
    {
        var countryCode = Country(country);
        var result = new HashSet<string>(StringComparer.Ordinal) { countryCode, JurisdictionScope.Any, "WORLD" };
        if (!string.IsNullOrWhiteSpace(subdivision)) result.Add(Subdivision(subdivision, countryCode));
        var groups = await db.RegionGroups.AsNoTracking().Where(row => row.TenantId == tenant.TenantId).ToListAsync(cancellationToken);
        foreach (var group in groups)
        {
            var members = Members(group.Members);
            if (members.Contains(countryCode) || subdivision is not null && members.Contains(Subdivision(subdivision, countryCode)))
            {
                result.Add(group.Code);
                result.Add(group.RegionGroupId.ToString("N"));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> ForbiddenPhrasesAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var values = await db.JurisdictionRules.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.Kind == JurisdictionRuleKind.ProhibitedWording && row.EffectiveFrom <= today
            && (row.EffectiveTo == null || row.EffectiveTo > today)).Select(row => row.Value).ToListAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            using var document = JsonDocument.Parse(value);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) Add(item.GetString());
                else if (item.TryGetProperty("pattern", out var pattern)
                    && (!item.TryGetProperty("applies_to", out var applies) || AppliesToLabel(applies))) Add(pattern.GetString());
            }
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();

        void Add(string? phrase) { if (!string.IsNullOrWhiteSpace(phrase)) result.Add(phrase.Trim()); }
    }

    public async Task<IReadOnlyList<JurisdictionMandatoryText>> MandatoryTextAsync(JurisdictionRuleContext context,
        CancellationToken cancellationToken)
    {
        var rules = await ResolveAsync(JurisdictionRuleKind.MandatoryStatement, context, cancellationToken);
        var result = new List<JurisdictionMandatoryText>();
        foreach (var rule in rules)
        {
            var value = rule.Value;
            if (value.ValueKind != JsonValueKind.Object) continue;
            var text = value.TryGetProperty("text", out var textValue) ? textValue.GetString() : null;
            var language = value.TryGetProperty("language", out var languageValue) ? languageValue.GetString() : null;
            var placement = value.TryGetProperty("placement", out var placementValue) ? placementValue.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(placement))
                result.Add(new(text, language, placement, value.TryGetProperty("verbatim", out var verbatim) && verbatim.ValueKind == JsonValueKind.True, rule.RuleId));
        }
        return result;
    }

    public async Task EnsureStarterPacksAsync(CancellationToken cancellationToken)
    {
        if (await db.RulePacks.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId && row.Code == "eu-starter", cancellationToken)) return;
        using var document = LoadStarter();
        var root = document.RootElement;
        var now = clock.UtcNow;
        var pack = new RulePack
        {
            TenantId = tenant.TenantId, RulePackId = TimeOrderedId.New(now), Code = root.GetProperty("code").GetString()!,
            Name = root.GetProperty("name").GetString()!, Warning = root.GetProperty("warning").GetString()!, BuiltIn = true,
        };
        db.RulePacks.Add(pack);
        var sequence = 0;
        foreach (var entry in root.GetProperty("entries").EnumerateArray())
            db.RulePackRules.Add(new RulePackRule
            {
                TenantId = tenant.TenantId, RulePackRuleId = TimeOrderedId.New(now.AddTicks(++sequence)), RulePackId = pack.RulePackId,
                Sequence = sequence, Template = entry.GetRawText(),
            });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsRuleTemplate(string template)
    {
        using var document = JsonDocument.Parse(template);
        return document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "rule";
    }

    private async Task<CleanRule> ValidateAsync(SaveJurisdictionRule command, CancellationToken cancellationToken,
        IEnumerable<RegionGroup>? pendingGroups = null)
    {
        JurisdictionRuleKinds.ValidateValue(command.Kind, command.Value);
        if (!JurisdictionScope.All.Contains(command.Scope)) throw new CommandRefusedException("jurisdiction_scope_unknown", $"Scope '{command.Scope}' is unknown.");
        if (!JurisdictionBuyerClass.All.Contains(command.BuyerClass)) throw new CommandRefusedException("jurisdiction_buyer_class_unknown", $"Buyer class '{command.BuyerClass}' is unknown.");
        if (!JurisdictionDateTested.All.Contains(command.DateTested)) throw new CommandRefusedException("jurisdiction_date_tested_unknown", $"Date tested '{command.DateTested}' is unknown.");
        if (command.EffectiveTo is { } until && until <= command.EffectiveFrom)
            throw new CommandRefusedException("jurisdiction_effective_dates_invalid", "Effective-to must be after effective-from.");
        if (command.PriceMin is { } minimum && command.PriceMax is { } maximum && maximum < minimum)
            throw new CommandRefusedException("jurisdiction_price_band_invalid", "Price maximum cannot be less than price minimum.");
        if ((command.PriceMin is not null || command.PriceMax is not null) && string.IsNullOrWhiteSpace(command.Currency))
            throw new CommandRefusedException("jurisdiction_price_currency_required", "A priced rule needs its ISO currency.");
        if (command.ConfirmedByCounsel != command.CounselConfirmedOn.HasValue)
            throw new CommandRefusedException("jurisdiction_counsel_confirmation_invalid", "Counsel confirmation needs both the flag and confirmation date, or neither.");

        var country = command.Scope == JurisdictionScope.Country ? Country(command.CountryCode) : null;
        var subdivision = command.Scope == JurisdictionScope.Subdivision ? Subdivision(command.SubdivisionCode, null) : null;
        Guid? groupId = command.Scope == JurisdictionScope.RegionGroup ? command.RegionGroupId
            ?? throw new CommandRefusedException("jurisdiction_region_group_required", "A region-group rule must name its group.") : null;
        if (command.Scope == JurisdictionScope.Any && (command.CountryCode is not null || command.SubdivisionCode is not null || command.RegionGroupId is not null)
            || command.Scope == JurisdictionScope.Country && (command.SubdivisionCode is not null || command.RegionGroupId is not null)
            || command.Scope == JurisdictionScope.Subdivision && (command.CountryCode is not null || command.RegionGroupId is not null)
            || command.Scope == JurisdictionScope.RegionGroup && (command.CountryCode is not null || command.SubdivisionCode is not null))
            throw new CommandRefusedException("jurisdiction_scope_fields_invalid", "Only the field belonging to the selected scope may be populated.");
        if (groupId is { } id && !(pendingGroups?.Any(group => group.RegionGroupId == id) == true)
            && !await db.RegionGroups.AsNoTracking().AnyAsync(group => group.TenantId == tenant.TenantId && group.RegionGroupId == id, cancellationToken))
            throw new CommandRefusedException("jurisdiction_region_group_unknown", $"Region group {id} does not exist.");

        var itemClass = Empty(command.ItemClass, 100, "jurisdiction_item_class_too_long");
        var citation = Empty(command.SourceCitation, 1000, "jurisdiction_source_citation_too_long");
        var note = Empty(command.SourceNote, 2000, "jurisdiction_source_note_too_long");
        var currency = command.Currency?.Trim().ToUpperInvariant();
        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)))
            throw new CommandRefusedException("jurisdiction_currency_invalid", "Currency must be a three-letter ISO code.");
        return new(command.Kind, command.Scope, country, subdivision, groupId, command.BuyerClass, itemClass,
            command.PriceMin, command.PriceMax, currency, command.DateTested, command.EffectiveFrom, command.EffectiveTo,
            command.Value.GetRawText(), citation, note, command.ConfirmedByCounsel, command.CounselConfirmedOn);
    }

    private async Task EnsureNoAmbiguousOverlapAsync(CleanRule candidate, Guid? excludedRuleId, CancellationToken cancellationToken)
    {
        var rows = await db.JurisdictionRules.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.Kind == candidate.Kind && row.RuleId != excludedRuleId && row.BuyerClass == candidate.BuyerClass
            && row.ItemClass == candidate.ItemClass).ToListAsync(cancellationToken);
        var groups = await db.RegionGroups.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .ToDictionaryAsync(row => row.RegionGroupId, row => Members(row.Members), cancellationToken);
        foreach (var row in rows)
        {
            if (!SameScopePrecedenceOverlaps(candidate, row, groups) || !EffectiveOverlaps(candidate, row)
                || !PriceOverlapsAtSameSpecificity(candidate, row)) continue;
            throw new CommandRefusedException("jurisdiction_rule_overlap",
                $"The rule overlaps {row.RuleId} at the same scope and qualifier precedence.",
                "close or narrow the existing rule before saving this one");
        }
    }

    private async Task EnsureGroupChangeUnambiguousAsync(Guid changedGroupId, IReadOnlyList<string> changedMembers,
        CancellationToken cancellationToken)
    {
        var rules = await db.JurisdictionRules.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.Scope == JurisdictionScope.RegionGroup).ToListAsync(cancellationToken);
        var groups = await db.RegionGroups.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .ToDictionaryAsync(row => row.RegionGroupId, row => row.RegionGroupId == changedGroupId ? changedMembers : Members(row.Members), cancellationToken);
        groups[changedGroupId] = changedMembers;
        foreach (var left in rules.Where(row => row.RegionGroupId == changedGroupId))
        foreach (var right in rules.Where(row => row.RegionGroupId != changedGroupId && row.Kind == left.Kind
            && row.BuyerClass == left.BuyerClass && row.ItemClass == left.ItemClass))
        {
            if (left.RegionGroupId is not { } leftId || right.RegionGroupId is not { } rightId
                || !groups.GetValueOrDefault(leftId, []).Intersect(groups.GetValueOrDefault(rightId, [])).Any()
                || !EffectiveOverlaps(left, right) || !PriceOverlapsAtSameSpecificity(left, right)) continue;
            throw new CommandRefusedException("region_group_rule_overlap",
                $"The new membership makes rules {left.RuleId} and {right.RuleId} ambiguous.",
                "separate the group membership or close one of the overlapping rules");
        }
    }

    private static RuleMatch? Match(JurisdictionRule row, JurisdictionRuleContext context,
        IReadOnlyDictionary<Guid, List<string>> groups)
    {
        var country = context.Country.Trim().ToUpperInvariant();
        var subdivision = context.Subdivision?.Trim().ToUpperInvariant();
        var precedence = row.Scope switch
        {
            JurisdictionScope.Subdivision when row.SubdivisionCode == subdivision => 3,
            JurisdictionScope.Country when row.CountryCode == country => 2,
            JurisdictionScope.RegionGroup when row.RegionGroupId is { } id && groups.GetValueOrDefault(id, []).Contains(subdivision ?? country) => 1,
            JurisdictionScope.RegionGroup when row.RegionGroupId is { } id && groups.GetValueOrDefault(id, []).Contains(country) => 1,
            JurisdictionScope.Any => 0,
            _ => -1,
        };
        if (precedence < 0 || row.BuyerClass != JurisdictionBuyerClass.Any && row.BuyerClass != context.BuyerClass
            || row.ItemClass is not null && row.ItemClass != context.ItemClass) return null;
        var tested = TestedDate(row.DateTested, context);
        if (tested is null || tested < row.EffectiveFrom || row.EffectiveTo is { } until && tested >= until) return null;
        var bounded = row.PriceMin is not null || row.PriceMax is not null;
        if (bounded && (context.Price is null || !string.Equals(row.Currency, context.Currency, StringComparison.OrdinalIgnoreCase)
            || row.PriceMin is { } min && context.Price < min || row.PriceMax is { } max && context.Price > max)) return null;
        return new(row, tested.Value, precedence, row.BuyerClass == context.BuyerClass ? 1 : 0,
            row.ItemClass == context.ItemClass && row.ItemClass is not null ? 1 : 0, bounded ? 1 : 0);
    }

    private static JurisdictionRuleResolution ToResolution(RuleMatch match)
    {
        var row = match.Rule;
        return new(row.RuleId, row.Kind, Parse(row.Value), row.Scope, ScopeLabel(row), row.BuyerClass,
            row.ItemClass, row.DateTested, match.TestedDate, match.Precedence, row.EffectiveFrom, row.EffectiveTo,
            row.SourceCitation, row.SourceNote);
    }

    private static DateOnly? TestedDate(string basis, JurisdictionRuleContext context) => basis switch
    {
        JurisdictionDateTested.Contract => context.ContractDate,
        JurisdictionDateTested.Purchase => context.PurchaseDate,
        JurisdictionDateTested.Delivery => context.DeliveryDate,
        JurisdictionDateTested.FirstSale => context.FirstSaleDate,
        _ => null,
    };

    private static bool SameScopePrecedenceOverlaps(CleanRule candidate, JurisdictionRule row,
        IReadOnlyDictionary<Guid, List<string>> groups)
    {
        if (candidate.Scope != row.Scope) return false;
        return candidate.Scope switch
        {
            JurisdictionScope.Any => true,
            JurisdictionScope.Country => candidate.CountryCode == row.CountryCode,
            JurisdictionScope.Subdivision => candidate.SubdivisionCode == row.SubdivisionCode,
            JurisdictionScope.RegionGroup when candidate.RegionGroupId is { } left && row.RegionGroupId is { } right =>
                left == right || groups.GetValueOrDefault(left, []).Intersect(groups.GetValueOrDefault(right, [])).Any(),
            _ => false,
        };
    }

    private static bool EffectiveOverlaps(CleanRule left, JurisdictionRule right) =>
        left.EffectiveFrom < (right.EffectiveTo ?? DateOnly.MaxValue) && right.EffectiveFrom < (left.EffectiveTo ?? DateOnly.MaxValue);
    private static bool EffectiveOverlaps(JurisdictionRule left, JurisdictionRule right) =>
        left.EffectiveFrom < (right.EffectiveTo ?? DateOnly.MaxValue) && right.EffectiveFrom < (left.EffectiveTo ?? DateOnly.MaxValue);

    private static bool PriceOverlapsAtSameSpecificity(CleanRule left, JurisdictionRule right)
    {
        var leftBounded = left.PriceMin is not null || left.PriceMax is not null;
        var rightBounded = right.PriceMin is not null || right.PriceMax is not null;
        if (leftBounded != rightBounded) return false;
        if (!leftBounded) return true;
        return left.Currency == right.Currency && (left.PriceMin ?? decimal.MinValue) <= (right.PriceMax ?? decimal.MaxValue)
            && (right.PriceMin ?? decimal.MinValue) <= (left.PriceMax ?? decimal.MaxValue);
    }

    private static bool PriceOverlapsAtSameSpecificity(JurisdictionRule left, JurisdictionRule right)
    {
        var leftBounded = left.PriceMin is not null || left.PriceMax is not null;
        var rightBounded = right.PriceMin is not null || right.PriceMax is not null;
        if (leftBounded != rightBounded) return false;
        if (!leftBounded) return true;
        return left.Currency == right.Currency && (left.PriceMin ?? decimal.MinValue) <= (right.PriceMax ?? decimal.MaxValue)
            && (right.PriceMin ?? decimal.MinValue) <= (left.PriceMax ?? decimal.MaxValue);
    }

    private JurisdictionRule NewRule(CleanRule clean, Guid commandId, Guid rootId, Guid? supersedesId, int version, string actor) => new()
    {
        TenantId = tenant.TenantId, RuleId = TimeOrderedId.New(clock.UtcNow), RootRuleId = rootId, SupersedesRuleId = supersedesId,
        CommandId = commandId, Version = version, Kind = clean.Kind, Scope = clean.Scope, CountryCode = clean.CountryCode,
        SubdivisionCode = clean.SubdivisionCode, RegionGroupId = clean.RegionGroupId, BuyerClass = clean.BuyerClass,
        ItemClass = clean.ItemClass, PriceMin = clean.PriceMin, PriceMax = clean.PriceMax, Currency = clean.Currency,
        DateTested = clean.DateTested, EffectiveFrom = clean.EffectiveFrom, EffectiveTo = clean.EffectiveTo, Value = clean.Value,
        SourceCitation = clean.SourceCitation, SourceNote = clean.SourceNote, ConfirmedByCounsel = clean.ConfirmedByCounsel,
        CounselConfirmedOn = clean.CounselConfirmedOn, CreatedBy = actor, ApprovedBy = actor, CreatedAt = clock.UtcNow,
    };

    private static RegionGroupRow ToRow(RegionGroup row) => new(row.RegionGroupId, row.Code, row.Name, Members(row.Members),
        DocumentVersion.Encode(row.RowVersion), row.CreatedAt, row.CreatedBy, row.LastModifiedAt, row.LastModifiedBy);

    private JurisdictionRuleRow ToRow(JurisdictionRule row, string? groupName) => new(row.RuleId, row.RootRuleId,
        row.SupersedesRuleId, row.Version, row.Kind, row.Scope, row.CountryCode, row.SubdivisionCode, row.RegionGroupId,
        groupName, row.BuyerClass, row.ItemClass, row.PriceMin, row.PriceMax, row.Currency, row.DateTested,
        row.EffectiveFrom, row.EffectiveTo, Parse(row.Value), row.SourceCitation, row.SourceNote, row.ConfirmedByCounsel,
        row.CounselConfirmedOn, row.CreatedBy, row.ApprovedBy, row.CreatedAt, DocumentVersion.Encode(row.RowVersion),
        row.EffectiveTo is null || row.EffectiveTo > DateOnly.FromDateTime(clock.UtcNow));

    private static RulePackPreview Preview(RulePack pack, IReadOnlyList<RulePackRule> entries)
    {
        var groups = new List<RegionGroupPreviewRow>();
        var rules = new List<RulePackRulePreviewRow>();
        foreach (var entry in entries)
        {
            using var document = JsonDocument.Parse(entry.Template);
            var value = document.RootElement;
            var type = value.GetProperty("type").GetString();
            if (type == "region_group")
            {
                groups.Add(new(value.GetProperty("code").GetString()!, value.GetProperty("name").GetString()!,
                    value.GetProperty("members").EnumerateArray().Select(member => member.GetString()!).ToList()));
                continue;
            }
            var scope = value.GetProperty("scope").GetString()!;
            var scopeLabel = scope switch
            {
                JurisdictionScope.RegionGroup => value.GetProperty("region_group_code").GetString()!,
                JurisdictionScope.Country => value.GetProperty("country_code").GetString()!,
                JurisdictionScope.Subdivision => value.GetProperty("subdivision_code").GetString()!,
                _ => JurisdictionScope.Any,
            };
            rules.Add(new(value.GetProperty("kind").GetString()!, scope, scopeLabel,
                value.GetProperty("buyer_class").GetString()!, value.GetProperty("date_tested").GetString()!,
                DateOnly.Parse(value.GetProperty("effective_from").GetString()!), value.GetProperty("value").Clone(),
                value.TryGetProperty("source_citation", out var citation) ? citation.GetString() : null,
                value.TryGetProperty("source_note", out var note) ? note.GetString() : null));
        }
        return new(pack.RulePackId, pack.Name, pack.Warning, groups, rules);
    }

    private static JsonDocument LoadStarter()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(StarterResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonDocument.Parse(stream);
    }

    private static bool AppliesToLabel(JsonElement applies) => applies.ValueKind == JsonValueKind.String
        ? applies.GetString() is "label" or "all"
        : applies.ValueKind == JsonValueKind.Array && applies.EnumerateArray().Any(value => value.GetString() == "label");

    private static string ScopeLabel(JurisdictionRule row) => row.Scope switch
    {
        JurisdictionScope.Country => row.CountryCode ?? "",
        JurisdictionScope.Subdivision => row.SubdivisionCode ?? "",
        JurisdictionScope.RegionGroup => row.RegionGroupId?.ToString("N") ?? "",
        _ => JurisdictionScope.Any,
    };

    private static JsonElement Parse(string value) => JsonSerializer.Deserialize<JsonElement>(value, Json);
    private static List<string> Members(string json) => JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];

    private static List<string> NormaliseMembers(IEnumerable<string> members)
    {
        var result = members.Select(value => value.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order().ToList();
        if (result.Count == 0) throw new CommandRefusedException("region_group_members_empty", "A region group needs at least one country or subdivision.");
        foreach (var member in result)
        {
            if (IsoCountryCodes.Contains(member)) continue;
            var parts = member.Split('-', 2);
            if (parts.Length == 2 && IsoCountryCodes.Contains(parts[0])
                && parts[1].Length is >= 1 and <= 3 && parts[1].All(char.IsAsciiLetterOrDigit)) continue;
            throw new CommandRefusedException("region_group_member_invalid", $"'{member}' is not an ISO country or subdivision code.");
        }
        return result;
    }

    private static string Country(string? value)
    {
        var code = value?.Trim().ToUpperInvariant();
        if (code is null || !IsoCountryCodes.Contains(code))
            throw new CommandRefusedException("jurisdiction_country_invalid", "Country must be an ISO 3166-1 alpha-2 code.");
        return code;
    }

    private static string Subdivision(string? value, string? country)
    {
        var code = value?.Trim().ToUpperInvariant();
        var parts = code?.Split('-', 2);
        if (parts?.Length != 2 || !IsoCountryCodes.Contains(parts[0])
            || parts[1].Length is < 1 or > 3 || !parts[1].All(char.IsAsciiLetterOrDigit)
            || country is not null && parts[0] != country)
            throw new CommandRefusedException("jurisdiction_subdivision_invalid", "Subdivision must be an ISO 3166-2 code for the selected country.");
        return code!;
    }

    // ISO identifiers are reference syntax, not jurisdiction law. Legal values remain exclusively tenant rule data.
    private static readonly HashSet<string> IsoCountryCodes = new(
        "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static string GroupCode(string value)
    {
        var code = Required(value, 40, "region_group_code_invalid", "A region-group code is required and cannot exceed 40 characters.").ToUpperInvariant();
        if (!code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new CommandRefusedException("region_group_code_invalid", "A region-group code may contain letters, digits, hyphens and underscores.");
        return code;
    }

    private static string Required(string value, int maximum, string rule, string message)
    {
        var clean = value.Trim();
        if (clean.Length is 0 || clean.Length > maximum) throw new CommandRefusedException(rule, message);
        return clean;
    }

    private static string? Empty(string? value, int maximum, string rule)
    {
        var clean = value?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        if (clean.Length > maximum) throw new CommandRefusedException(rule, $"The value cannot exceed {maximum} characters.");
        return clean;
    }

    private sealed record CleanRule(string Kind, string Scope, string? CountryCode, string? SubdivisionCode,
        Guid? RegionGroupId, string BuyerClass, string? ItemClass, decimal? PriceMin, decimal? PriceMax, string? Currency,
        string DateTested, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Value, string? SourceCitation,
        string? SourceNote, bool ConfirmedByCounsel, DateOnly? CounselConfirmedOn);

    private sealed record RuleMatch(JurisdictionRule Rule, DateOnly TestedDate, int Precedence, int BuyerSpecificity,
        int ItemSpecificity, int PriceSpecificity);
}

/// <summary>Wolverine command boundary; the class name deliberately ends in Handler for discovery.</summary>
public static class JurisdictionCommandHandler
{
    public static async Task<RegionGroupSaved> Handle(SaveRegionGroup command, JurisdictionCatalogueService service,
        JurisdictionPublishedEventMapper mapper, IMessageBus bus, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var result = await service.SaveRegionGroupAsync(command, cancellationToken);
        if (!result.Replayed) await PublishAsync(mapper.Map(command, result), bus, tenant);
        return result;
    }

    public static async Task<JurisdictionRuleSaved> Handle(SaveJurisdictionRule command, JurisdictionCatalogueService service,
        JurisdictionPublishedEventMapper mapper, IMessageBus bus, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var result = await service.SaveRuleAsync(command, cancellationToken);
        if (!result.Replayed) await PublishAsync(mapper.Map(command, result), bus, tenant);
        return result;
    }

    public static async Task<JurisdictionRuleClosed> Handle(CloseJurisdictionRule command, JurisdictionCatalogueService service,
        JurisdictionPublishedEventMapper mapper, IMessageBus bus, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var result = await service.CloseRuleAsync(command, cancellationToken);
        if (!result.Replayed) await PublishAsync(mapper.Map(command), bus, tenant);
        return result;
    }

    public static async Task<RulePackImported> Handle(ImportRulePack command, JurisdictionCatalogueService service,
        JurisdictionPublishedEventMapper mapper, IMessageBus bus, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var result = await service.ImportPackAsync(command, cancellationToken);
        if (!result.Replayed) await PublishAsync(mapper.Map(command, result), bus, tenant);
        return result;
    }

    private static ValueTask PublishAsync(IPublishedSummaryEvent message, IMessageBus bus, ITenantContext tenant) =>
        bus.PublishAsync(message, new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
}

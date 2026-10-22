using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.LegacyMigration.LinkOverrides;

public sealed record LinkOverrideView(string SourceKey, string? Direction, string? ReplacementReason,
    string? State, string? ReplacementConditionAllowed, string? PricePolicy, decimal? DifferenceAmount,
    string? Currency, DateTime UploadedAt, string UploadedBy, string UploadFileName);
public sealed record LinkOverridePage(IReadOnlyList<LinkOverrideView> Items, string? NextCursor);
public sealed record LinkOverrideUploadError(int Row, string Column, string Reason);
public sealed record LinkOverrideUploadResponse(int Added, int Changed, int Removed, int Unchanged,
    int ErrorTotal, IReadOnlyList<LinkOverrideUploadError> Errors);
public sealed record DeleteLinkOverridesRequest(bool Confirm);
public sealed record DeleteLinkOverridesResponse(int Removed);

public sealed class LinkOverrideRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}

public sealed class LinkOverrideService(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    MappingSettings mappingSettings, ReplacementLinkValueValidator validator)
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxRows = 10_000;
    public const int ErrorLimit = 100;
    private const string InternalSerial = "internal_serial";
    private static readonly string[] Headers =
    [
        "source_key", "replaced_serial", "replaced_item_code", "replacement_serial", "replacement_item_code",
        "direction", "replacement_reason", "state", "replacement_condition_allowed", "price_policy",
        "difference_amount", "currency", "direction_overridden", "replacement_reason_overridden",
        "state_overridden", "replacement_condition_allowed_overridden", "price_policy_overridden",
        "difference_amount_overridden", "currency_overridden",
    ];

    public async Task<byte[]> ExportAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var run = await db.MigrationMapRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId && row.Status == MappingRunStatus.Completed)
            .OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.RunId)
            .Select(row => (Guid?)row.RunId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new LinkOverrideRuleException("migration_link_export_map_not_found",
                "No completed load plan exists for this batch.");
        var links = await db.MigrationPlanReplacementLinks.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.MapRunId == run)
            .OrderBy(row => row.SourceKey).ThenBy(row => row.PlanReplacementLinkId).ToListAsync(cancellationToken);
        var unitIds = links.SelectMany(row => new[] { row.ReplacedPlanUnitId, row.ReplacementPlanUnitId })
            .Distinct().ToList();
        var units = await db.MigrationPlanUnits.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                unitIds.Contains(row.PlanUnitId)).Select(row => new { row.PlanUnitId, row.ItemCode })
            .ToDictionaryAsync(row => row.PlanUnitId, cancellationToken);
        var serials = await db.MigrationPlanIdentifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                unitIds.Contains(row.PlanUnitId) && row.IdentifierType == InternalSerial)
            .Select(row => new { row.PlanUnitId, row.RawValue }).ToListAsync(cancellationToken);
        var serialByUnit = serials.GroupBy(row => row.PlanUnitId).ToDictionary(group => group.Key,
            group => group.First().RawValue);
        var sourceKeys = links.Select(row => row.SourceKey).Distinct().ToList();
        var overrides = await db.Set<MigrationLinkOverride>().AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && sourceKeys.Contains(row.SourceKey))
            .ToDictionaryAsync(row => row.SourceKey, StringComparer.Ordinal, cancellationToken);
        var defaults = (await mappingSettings.GetAsync(cancellationToken)).LinkDefaults;
        var csv = new StringBuilder().AppendJoin(',', Headers).Append("\r\n");
        foreach (var link in links)
        {
            overrides.TryGetValue(link.SourceKey, out var itemOverride);
            var values = itemOverride?.Effective(defaults) ?? defaults;
            var replaced = units[link.ReplacedPlanUnitId];
            var replacement = units[link.ReplacementPlanUnitId];
            var fields = new object?[]
            {
                link.SourceKey, serialByUnit.GetValueOrDefault(link.ReplacedPlanUnitId), replaced.ItemCode,
                serialByUnit.GetValueOrDefault(link.ReplacementPlanUnitId), replacement.ItemCode,
                values.Direction, values.ReplacementReason, values.State, values.ReplacementConditionAllowed,
                values.PricePolicy, values.DifferenceAmount.ToString(CultureInfo.InvariantCulture), values.Currency,
                itemOverride?.Direction is not null, itemOverride?.ReplacementReason is not null,
                itemOverride?.State is not null, itemOverride?.ReplacementConditionAllowed is not null,
                itemOverride?.PricePolicy is not null, itemOverride?.DifferenceAmount is not null,
                itemOverride?.Currency is not null,
            };
            csv.AppendJoin(',', fields.Select(Csv)).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    public async Task<LinkOverrideUploadResponse> UploadAsync(Stream stream, long length, string fileName,
        string actor, CancellationToken cancellationToken)
    {
        if (length > MaxFileBytes)
            throw new LinkOverrideRuleException("migration_link_override_file_too_large",
                $"The CSV file may be at most {MaxFileBytes} bytes.");
        if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new LinkOverrideRuleException("migration_link_override_csv_required", "Upload a CSV file.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, leaveOpen: true);
        string text;
        try { text = await reader.ReadToEndAsync(cancellationToken); }
        catch (DecoderFallbackException)
        {
            return Invalid([new(1, "file", "invalid_utf8")]);
        }
        IReadOnlyList<IReadOnlyList<string>> rows;
        try { rows = MigrationCsvReader.Read(text); }
        catch (MigrationCsvFormatException ex)
        {
            return Invalid([new(ex.Row, "file", "invalid_csv")]);
        }
        if (rows.Count == 0) return Invalid([new(1, "file", "header_required")]);
        if (rows.Count - 1 > MaxRows)
            throw new LinkOverrideRuleException("migration_link_override_row_limit",
                $"The CSV file may contain at most {MaxRows} data rows.");
        var header = rows[0].Select(value => value.Trim().ToLowerInvariant()).ToList();
        var missingHeaders = Headers.Take(12).Where(required => !header.Contains(required, StringComparer.Ordinal)).ToList();
        if (missingHeaders.Count > 0)
            return Invalid(missingHeaders.Select(value => new LinkOverrideUploadError(1, value, "column_required")).ToList());
        var index = header.Select((value, position) => (value, position)).GroupBy(x => x.value)
            .ToDictionary(group => group.Key, group => group.First().position, StringComparer.Ordinal);
        var defaults = (await mappingSettings.GetAsync(cancellationToken)).LinkDefaults;
        var parsed = new List<ParsedRow>();
        var errors = new List<LinkOverrideUploadError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var dataIndex = 1; dataIndex < rows.Count; dataIndex++)
        {
            var rowNumber = dataIndex + 1;
            var row = rows[dataIndex];
            string Cell(string name) => index[name] < row.Count ? row[index[name]].Trim() : "";
            bool Flag(string name) => index.TryGetValue(name, out var position) && position < row.Count &&
                bool.TryParse(row[position].Trim(), out var value) && value;
            var sourceKey = Cell("source_key");
            if (sourceKey.Length is < 1 or > 320) errors.Add(new(rowNumber, "source_key", "source_key_invalid"));
            else if (!seen.Add(sourceKey)) errors.Add(new(rowNumber, "source_key", "source_key_repeated"));
            decimal? amount = null;
            var amountText = Cell("difference_amount");
            if (amountText.Length > 0)
            {
                if (decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) amount = value;
                else errors.Add(new(rowNumber, "difference_amount", "difference_amount_invalid"));
            }
            if (amount == defaults.DifferenceAmount && !Flag("difference_amount_overridden")) amount = null;
            var candidate = new ParsedRow(rowNumber, sourceKey,
                Override(Cell("direction"), Flag("direction_overridden"), defaults.Direction, false),
                Override(Cell("replacement_reason"), Flag("replacement_reason_overridden"), defaults.ReplacementReason, false),
                Override(Cell("state"), Flag("state_overridden"), defaults.State, false),
                Override(Cell("replacement_condition_allowed"), Flag("replacement_condition_allowed_overridden"),
                    defaults.ReplacementConditionAllowed, false),
                Override(Cell("price_policy"), Flag("price_policy_overridden"), defaults.PricePolicy, false),
                amount, Override(Cell("currency"), Flag("currency_overridden"), defaults.Currency, true));
            if (!candidate.IsEmpty && !errors.Any(error => error.Row == rowNumber))
            {
                try { validator.Validate(candidate.Effective(defaults)); }
                catch (MappingRuleException ex) { errors.Add(new(rowNumber, Column(ex.Rule), ex.Rule)); }
            }
            parsed.Add(candidate);
        }
        var keys = parsed.Where(row => row.SourceKey.Length is > 0 and <= 320).Select(row => row.SourceKey)
            .Distinct(StringComparer.Ordinal).ToList();
        var known = await db.MigrationPlanReplacementLinks.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && keys.Contains(row.SourceKey))
            .Select(row => row.SourceKey).Distinct().ToListAsync(cancellationToken);
        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        errors.AddRange(parsed.Where(row => row.SourceKey.Length is > 0 and <= 320 && !knownSet.Contains(row.SourceKey))
            .Select(row => new LinkOverrideUploadError(row.Row, "source_key", "source_key_unknown")));
        if (errors.Count > 0)
            return new(0, 0, 0, 0, errors.Count, errors.OrderBy(error => error.Row).ThenBy(error => error.Column)
                .Take(ErrorLimit).ToList());

        var existing = await db.Set<MigrationLinkOverride>().Where(row => row.TenantId == tenant.TenantId &&
                keys.Contains(row.SourceKey)).ToDictionaryAsync(row => row.SourceKey, StringComparer.Ordinal, cancellationToken);
        var added = 0;
        var changed = 0;
        var removed = 0;
        var unchanged = 0;
        foreach (var row in parsed)
        {
            if (row.IsEmpty)
            {
                if (existing.Remove(row.SourceKey, out var prior)) { db.Remove(prior); removed++; }
                else unchanged++;
                continue;
            }
            if (!existing.TryGetValue(row.SourceKey, out var entity))
            {
                entity = new MigrationLinkOverride { TenantId = tenant.TenantId, SourceKey = row.SourceKey,
                    UploadedBy = actor, UploadFileName = SafeFileName(fileName) };
                db.Add(entity);
                existing[row.SourceKey] = entity;
                added++;
            }
            else if (Same(entity, row)) { unchanged++; continue; }
            else changed++;
            entity.Direction = row.Direction;
            entity.ReplacementReason = row.ReplacementReason;
            entity.State = row.State;
            entity.ReplacementConditionAllowed = row.ReplacementConditionAllowed;
            entity.PricePolicy = row.PricePolicy;
            entity.DifferenceAmount = row.DifferenceAmount;
            entity.Currency = row.Currency;
            entity.UploadedAt = clock.UtcNow;
            entity.UploadedBy = actor;
            entity.UploadFileName = SafeFileName(fileName);
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(added, changed, removed, unchanged, 0, []);
    }

    public async Task<LinkOverridePage> ListAsync(string? cursor, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var query = db.Set<MigrationLinkOverride>().AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
            (cursor == null || row.SourceKey.CompareTo(cursor) > 0)).OrderBy(row => row.SourceKey);
        var rows = await query.Take(take + 1).ToListAsync(cancellationToken);
        var items = rows.Take(take).Select(row => new LinkOverrideView(row.SourceKey, row.Direction,
            row.ReplacementReason, row.State, row.ReplacementConditionAllowed, row.PricePolicy,
            row.DifferenceAmount, row.Currency, row.UploadedAt, row.UploadedBy, row.UploadFileName)).ToList();
        return new(items, rows.Count > take ? items[^1].SourceKey : null);
    }

    public async Task<DeleteLinkOverridesResponse> DeleteAllAsync(DeleteLinkOverridesRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Confirm)
            throw new LinkOverrideRuleException("migration_link_override_confirmation_required",
                "Set confirm to true to remove every replacement-link override.");
        var removed = await db.Set<MigrationLinkOverride>().Where(row => row.TenantId == tenant.TenantId)
            .ExecuteDeleteAsync(cancellationToken);
        return new(removed);
    }

    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string? Override(string value, bool flagged, string defaultValue, bool upper)
    {
        if (value.Length == 0) return null;
        var normalised = upper ? value.ToUpperInvariant() : value.ToLowerInvariant();
        return !flagged && normalised == defaultValue ? null : normalised;
    }
    private static string SafeFileName(string value) => Path.GetFileName(value) is { Length: > 260 } name ? name[..260] : Path.GetFileName(value);
    private static string Column(string rule) => rule switch
    {
        "replacement_direction_unknown" => "direction",
        "replacement_reason_unknown" => "replacement_reason",
        "replacement_state_not_terminal" => "state",
        "replacement_condition_allowed_unknown" => "replacement_condition_allowed",
        "replacement_price_policy_unknown" => "price_policy",
        "replacement_difference_amount_negative" or "replacement_zero_price_difference" => "difference_amount",
        _ => "currency",
    };
    private static LinkOverrideUploadResponse Invalid(List<LinkOverrideUploadError> errors) =>
        new(0, 0, 0, 0, errors.Count, errors.Take(ErrorLimit).ToList());
    private static bool Same(MigrationLinkOverride entity, ParsedRow row) => entity.Direction == row.Direction &&
        entity.ReplacementReason == row.ReplacementReason && entity.State == row.State &&
        entity.ReplacementConditionAllowed == row.ReplacementConditionAllowed && entity.PricePolicy == row.PricePolicy &&
        entity.DifferenceAmount == row.DifferenceAmount && entity.Currency == row.Currency;

    private sealed record ParsedRow(int Row, string SourceKey, string? Direction, string? ReplacementReason,
        string? State, string? ReplacementConditionAllowed, string? PricePolicy, decimal? DifferenceAmount,
        string? Currency)
    {
        public bool IsEmpty => Direction is null && ReplacementReason is null && State is null &&
            ReplacementConditionAllowed is null && PricePolicy is null && DifferenceAmount is null && Currency is null;
        public ReplacementLinkValues Effective(ReplacementLinkValues defaults) => new(Direction ?? defaults.Direction,
            ReplacementReason ?? defaults.ReplacementReason, State ?? defaults.State,
            ReplacementConditionAllowed ?? defaults.ReplacementConditionAllowed, PricePolicy ?? defaults.PricePolicy,
            DifferenceAmount ?? defaults.DifferenceAmount, Currency ?? defaults.Currency);
    }
}

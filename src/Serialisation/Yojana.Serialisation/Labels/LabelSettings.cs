using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Labels;

/// <summary>OD-10/OD-11 and ADR-0024 tenant policy. Physical stock remains tenant data, never a product rule.</summary>
public sealed class LabelSettings(ITenantSettings settings)
{
    public const string Key = "labels.policy";
    public const string PrinterAdapterKey = "labels.printer_adapter";
    public const string PrintLayoutKey = "labels.print_layout";
    public const string TenantDefaultProfileId = "tenant-default";

    public static readonly LabelSheetProfile DefaultSheet = new(TenantDefaultProfileId,
        "A4 · 3 × 8 · 70 × 37 mm", LabelPageSize.A4, 3, 8, 70, 37,
        0.5m, 0, 0.5m, 0, 0, 0);

    public static readonly IReadOnlyList<LabelSheetProfile> BuiltInProfiles =
    [
        new("a4-3x8-70x37", "A4 · 3 × 8 · 70 × 37 mm", LabelPageSize.A4, 3, 8, 70, 37,
            .5m, 0, .5m, 0, 0, 0, true),
        new("a4-2x7-99.1x38.1", "A4 · 2 × 7 · 99.1 × 38.1 mm", LabelPageSize.A4, 2, 7, 99.1m, 38.1m,
            15.15m, 5.9m, 15.15m, 5.9m, 0, 0, true),
        new("letter-3x10-66.7x25.4", "Letter · 3 × 10 · 66.7 × 25.4 mm", LabelPageSize.Letter, 3, 10, 66.7m, 25.4m,
            12.7m, 7.9m, 12.7m, 7.9m, 0, 0, true),
    ];

    public static readonly LabelPolicy Default = new(["void", "voids warranty", "warranty void"], false,
        LabelPrinterAdapter.PdfDownload, LabelPrinterAdapter.PdfDownload, LabelOutputFormat.Sheet, DefaultSheet, []);

    // Coupled to the factory registration in SerialisationModule: exposing ITenantSettings directly to label handlers
    // adds PlatformDbContext beside SerialisationDbContext and prevents Wolverine transaction discovery.
    public async Task<LabelPolicy> GetAsync(CancellationToken cancellationToken)
    {
        var policy = await settings.GetAsync(Key, cancellationToken) is { } policyText
            ? JsonSerializer.Deserialize<LabelPolicy>(policyText, LedgerJson.Options) ?? Default
            : Default;
        var adapter = await settings.GetAsync(PrinterAdapterKey, cancellationToken) ?? policy.PrinterAdapter;
        var layout = await settings.GetAsync(PrintLayoutKey, cancellationToken) is { } layoutText
            ? JsonSerializer.Deserialize<StoredPrintLayout>(layoutText, LedgerJson.Options)
            : null;
        return Normalise(policy with
        {
            PrinterAdapter = adapter,
            DefaultFormat = layout?.DefaultFormat ?? policy.DefaultFormat,
            DefaultSheet = layout?.DefaultSheet ?? policy.DefaultSheet,
            ExtraSheetProfiles = layout?.ExtraSheetProfiles ?? policy.ExtraSheetProfiles,
        });
    }

    public async Task<LabelPolicy> SetAsync(LabelPolicy policy, CancellationToken cancellationToken)
    {
        var phrases = policy.LegalReviewPhrases.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (phrases.Count == 0)
            throw new CommandRefusedException("label_legal_phrases_empty", "At least one legal-review phrase is required.");
        if (phrases.Any(value => value.Length > 100))
            throw new CommandRefusedException("label_legal_phrase_too_long", "A legal-review phrase cannot exceed 100 characters.");
        if (!LabelPrinterAdapter.All.Contains(policy.PrinterAdapter))
            throw new CommandRefusedException("label_printer_adapter_invalid", "The label printer adapter must be pdf_download or unavailable.");
        if (!LabelOutputFormat.All.Contains(policy.DefaultFormat))
            throw new CommandRefusedException("label_output_format_invalid", "The default label format must be sheet or one_per_page.");

        var defaultSheet = Clean(policy.DefaultSheet ?? DefaultSheet, TenantDefaultProfileId, builtIn: false);
        if (policy.DefaultSheet is not null) LabelSheetMath.EnsureFits(defaultSheet);
        var extra = (policy.ExtraSheetProfiles ?? []).Select(profile => Clean(profile,
            string.IsNullOrWhiteSpace(profile.ProfileId) ? $"sheet-{Guid.NewGuid():N}" : profile.ProfileId.Trim(), builtIn: false)).ToList();
        if (extra.Count > 20)
            throw new CommandRefusedException("label_sheet_profile_limit", "No more than 20 extra sticker-sheet profiles may be saved.");
        if (extra.Select(profile => profile.ProfileId).Append(defaultSheet.ProfileId)
            .Distinct(StringComparer.Ordinal).Count() != extra.Count + 1
            || extra.Any(profile => BuiltInProfiles.Any(builtIn => builtIn.ProfileId == profile.ProfileId)))
            throw new CommandRefusedException("label_sheet_profile_id_duplicate", "Every sticker-sheet profile must have a distinct identifier.");
        foreach (var profile in extra) LabelSheetMath.EnsureFits(profile);

        var printer = string.IsNullOrWhiteSpace(policy.DefaultPrinterRef)
            ? LabelPrinterAdapter.PdfDownload : policy.DefaultPrinterRef.Trim();
        var clean = new LabelPolicy(phrases, policy.PrintExpiryDates, printer, policy.PrinterAdapter,
            policy.DefaultFormat, defaultSheet, extra);
        await settings.SetAsync(Key, JsonSerializer.Serialize(new LabelPolicy(phrases, policy.PrintExpiryDates, printer),
            LedgerJson.Options), cancellationToken);
        await settings.SetAsync(PrinterAdapterKey, clean.PrinterAdapter, cancellationToken);
        await settings.SetAsync(PrintLayoutKey, JsonSerializer.Serialize(new StoredPrintLayout(clean.DefaultFormat,
            defaultSheet, extra), LedgerJson.Options), cancellationToken);
        return clean;
    }

    public async Task<LabelPrintOptions> PrintOptionsAsync(CancellationToken cancellationToken)
    {
        var policy = await GetAsync(cancellationToken);
        var profiles = BuiltInProfiles.Concat([policy.DefaultSheet!]).Concat(policy.ExtraSheetProfiles ?? []).ToList();
        return new(policy.DefaultFormat, profiles, TenantDefaultProfileId);
    }

    public async Task<LabelSheetProfile> SheetProfileAsync(string? profileId, CancellationToken cancellationToken)
    {
        var options = await PrintOptionsAsync(cancellationToken);
        var id = string.IsNullOrWhiteSpace(profileId) ? options.DefaultSheetProfileId : profileId.Trim();
        return options.SheetProfiles.FirstOrDefault(profile => profile.ProfileId == id)
            ?? throw new CommandRefusedException("label_sheet_profile_not_found", "The selected sticker-sheet profile does not exist.");
    }

    private static LabelPolicy Normalise(LabelPolicy policy) => policy with
    {
        DefaultPrinterRef = string.IsNullOrWhiteSpace(policy.DefaultPrinterRef)
            ? LabelPrinterAdapter.PdfDownload : policy.DefaultPrinterRef,
        PrinterAdapter = string.IsNullOrWhiteSpace(policy.PrinterAdapter)
            ? LabelPrinterAdapter.PdfDownload : policy.PrinterAdapter,
        DefaultFormat = string.IsNullOrWhiteSpace(policy.DefaultFormat) ? LabelOutputFormat.Sheet : policy.DefaultFormat,
        DefaultSheet = Clean(policy.DefaultSheet ?? DefaultSheet, TenantDefaultProfileId, builtIn: false),
        ExtraSheetProfiles = (policy.ExtraSheetProfiles ?? []).Select(profile => Clean(profile, profile.ProfileId, false)).ToList(),
    };

    private static LabelSheetProfile Clean(LabelSheetProfile profile, string profileId, bool builtIn)
    {
        if (!LabelPageSize.All.Contains(profile.PageSize))
            throw new CommandRefusedException("label_sheet_page_size_invalid", "Sticker-sheet page size must be A4 or Letter.");
        if (profile.Columns is < 1 or > 50 || profile.Rows is < 1 or > 50)
            throw new CommandRefusedException("label_sheet_grid_invalid", "Sticker-sheet columns and rows must each be between 1 and 50.");
        if (profile.LabelWidthMm is < 10 or > 200 || profile.LabelHeightMm is < 10 or > 200)
            throw new CommandRefusedException("label_sheet_label_size_invalid", "Sticker width and height must each be between 10 and 200 mm.");
        if (new[] { profile.MarginTopMm, profile.MarginRightMm, profile.MarginBottomMm, profile.MarginLeftMm,
                profile.HorizontalGapMm, profile.VerticalGapMm }.Any(value => value is < 0 or > 200))
            throw new CommandRefusedException("label_sheet_spacing_invalid", "Sticker-sheet margins and gaps must be between 0 and 200 mm.");
        if (profileId.Length is 0 or > 100)
            throw new CommandRefusedException("label_sheet_profile_id_invalid", "A sticker-sheet profile identifier is required and cannot exceed 100 characters.");
        var name = $"{profile.PageSize} · {profile.Columns} × {profile.Rows} · {Number(profile.LabelWidthMm)} × {Number(profile.LabelHeightMm)} mm";
        return profile with { ProfileId = profileId, Name = name, BuiltIn = builtIn };
    }

    private static string Number(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record StoredPrintLayout(string DefaultFormat, LabelSheetProfile DefaultSheet,
        IReadOnlyList<LabelSheetProfile> ExtraSheetProfiles);
}

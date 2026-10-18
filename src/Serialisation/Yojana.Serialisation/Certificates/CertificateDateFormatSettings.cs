using System.Globalization;
using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Certificates;

/// <summary>Tenant-owned certificate date presentation, resolved to exact patterns before a certificate is signed.</summary>
public sealed class CertificateDateFormatSettings(ITenantSettings settings)
{
    public const string Key = "certificates.date_format";
    public static readonly IReadOnlyList<string> IssuedLanguages = ["en", "de", "fr"];

    // Coupled to the factory registration in SerialisationModule for the same two-DbContext Wolverine constraint as
    // ReleaseRecordIssuePointSettings. A direct registration exposes PlatformDbContext to certificate handlers.
    public async Task<CertificateDateFormatSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(Key, cancellationToken);
        var policy = stored is null
            ? new CertificateDateFormatPolicy([])
            : Deserialise(stored.Value);
        return new(WithDefaults(policy.Languages), stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<CertificateDateFormatSettingsView> SetAsync(UpdateCertificateDateFormatSettings update,
        CancellationToken cancellationToken)
    {
        var policy = Normalise(new CertificateDateFormatPolicy(update.Languages));
        var stored = await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options),
            update.ExpectedVersion, cancellationToken);
        return new(WithDefaults(policy.Languages), stored.Version);
    }

    public async Task<CertificateDateFormatChoice> ResolveAsync(string language,
        CancellationToken cancellationToken)
    {
        var view = await GetAsync(cancellationToken);
        return view.Languages.FirstOrDefault(row => row.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?? DefaultFor(language);
    }

    public static CertificateDateFormatChoice DefaultFor(string language)
    {
        var culture = Culture(language);
        var shortDate = culture.DateTimeFormat.ShortDatePattern;
        var datePattern = YearBeforeMonth(shortDate)
            ? CertificateDatePattern.YearMonthDay
            : MonthBeforeDay(shortDate)
                ? CertificateDatePattern.MonthDayYear
                : language.Equals("en", StringComparison.OrdinalIgnoreCase)
                    ? CertificateDatePattern.DayMonthNameYear
                    : CertificateDatePattern.DayMonthYear;
        var timePattern = culture.DateTimeFormat.ShortTimePattern.Contains('H')
            ? CertificateTimePattern.TwentyFourHour
            : CertificateTimePattern.TwelveHour;
        return new(NormaliseLanguage(language), datePattern, timePattern);
    }

    public static CultureInfo Culture(string language)
    {
        if (language.Equals("en", StringComparison.OrdinalIgnoreCase))
            return CultureInfo.GetCultureInfo("en-GB");
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            return culture.IsNeutralCulture ? CultureInfo.CreateSpecificCulture(language) : culture;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static CertificateDateFormatPolicy Deserialise(string text) =>
        Normalise(JsonSerializer.Deserialize<CertificateDateFormatPolicy>(text, LedgerJson.Options)
            ?? new CertificateDateFormatPolicy([]));

    private static CertificateDateFormatPolicy Normalise(CertificateDateFormatPolicy policy)
    {
        var rows = policy.Languages.Select(row => new CertificateDateFormatChoice(
                NormaliseLanguage(row.Language), row.DatePattern.Trim(), row.TimePattern.Trim()))
            .Where(row => row.Language.Length > 0)
            .OrderBy(row => row.Language, StringComparer.OrdinalIgnoreCase).ToList();
        if (rows.GroupBy(row => row.Language, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new CommandRefusedException("certificate_date_format_language_duplicate",
                "Each certificate language can have only one date and time format.");
        foreach (var row in rows)
        {
            try { _ = CultureInfo.GetCultureInfo(row.Language); }
            catch (CultureNotFoundException)
            {
                throw new CommandRefusedException("certificate_date_format_language_unknown",
                    $"Unknown certificate language '{row.Language}'.");
            }
            if (!CertificateDatePattern.All.Contains(row.DatePattern, StringComparer.Ordinal))
                throw new CommandRefusedException("certificate_date_pattern_unknown",
                    $"Unknown certificate date pattern '{row.DatePattern}'.");
            if (!CertificateTimePattern.All.Contains(row.TimePattern, StringComparer.Ordinal))
                throw new CommandRefusedException("certificate_time_pattern_unknown",
                    $"Unknown certificate time pattern '{row.TimePattern}'.");
        }
        return new(rows);
    }

    private static List<CertificateDateFormatChoice> WithDefaults(
        IReadOnlyList<CertificateDateFormatChoice> configured)
    {
        var rows = configured.ToDictionary(row => row.Language, StringComparer.OrdinalIgnoreCase);
        foreach (var language in IssuedLanguages)
            rows.TryAdd(language, DefaultFor(language));
        return rows.Values.OrderBy(row => LanguageOrder(row.Language))
            .ThenBy(row => row.Language, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int LanguageOrder(string language) => language switch
    {
        "en" => 0,
        "de" => 1,
        "fr" => 2,
        _ => int.MaxValue,
    };

    private static string NormaliseLanguage(string language) => language.Trim().Replace('_', '-').ToLowerInvariant();

    private static bool YearBeforeMonth(string pattern) => Index(pattern, 'y') < Index(pattern, 'M');
    private static bool MonthBeforeDay(string pattern) => Index(pattern, 'M') < Index(pattern, 'd');
    private static int Index(string pattern, char value)
    {
        var index = pattern.IndexOf(value);
        return index < 0 ? int.MaxValue : index;
    }
}

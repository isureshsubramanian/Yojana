using System.Text.Json.Serialization;

namespace Yojana.DemoIncumbent;

public enum DemoDisposition
{
    Data,
    ProductOnly,
}

public sealed record DemoScenario(
    IReadOnlyList<string> Tickets,
    string MarkerTicket,
    string Scenario,
    string IncumbentState,
    string ProductOutcome,
    DemoDisposition Disposition,
    IReadOnlyList<string> RecipeKeys);

public sealed record DemoObject(
    string Ticket,
    string Type,
    string Key,
    string Description,
    IReadOnlyList<string> Serials,
    bool Exists = false,
    string? Number = null);

public sealed record DemoManifest(
    DateTimeOffset GeneratedAt,
    string Company,
    IReadOnlyList<DemoTicketManifest> Tickets,
    IReadOnlyDictionary<string, int> ObjectCounts,
    PipelineEvidence? Pipeline);

public sealed record DemoTicketManifest(
    IReadOnlyList<string> Tickets,
    string Scenario,
    string IncumbentState,
    string ProductOutcome,
    string PipelineOutcome,
    IReadOnlyList<DemoObject> Objects,
    IReadOnlyList<string> Serials);

public sealed record PipelineEvidence(
    IReadOnlyDictionary<string, long> ProfilingAnomalies,
    IReadOnlyDictionary<string, long> ReviewItems,
    IReadOnlyDictionary<string, long> PlanGaps,
    long ReplacementLinks,
    long UnitsApplied);

internal sealed record RemoteObject(string Type, string Key, string? Number, IReadOnlyList<string> Serials);

internal sealed record ApplySummary(IReadOnlyDictionary<string, int> Created,
    IReadOnlyDictionary<string, int> Existing, IReadOnlyList<RemoteObject> Objects,
    IReadOnlyList<string> Unsupported);

internal sealed record ServiceContext(string Warehouse, int ItemGroup, string LocalCurrency,
    IReadOnlySet<string> EntitySets);

internal sealed record CreateResult(string Key, string? Number);

internal sealed record DemoOptions(Uri ServiceUri, string Company, string UserName, string Password,
    string CertificateSha256)
{
    public static DemoOptions FromEnvironment()
    {
        var rawUrl = Required("YOJANA_CX01_URL");
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("YOJANA_CX01_URL must be an absolute HTTPS URL.");
        var suppliedUser = Required("YOJANA_CX01_USER");
        var identity = suppliedUser.Split('\\', 2, StringSplitOptions.TrimEntries);
        var company = Environment.GetEnvironmentVariable("YOJANA_DEMO_COMPANY")
            ?? Environment.GetEnvironmentVariable("YOJANA_CX01_COMPANY")
            ?? (identity.Length == 2 ? identity[0] : null)
            ?? throw new InvalidOperationException("Set YOJANA_DEMO_COMPANY or use COMPANY\\USER in YOJANA_CX01_USER.");
        var user = identity.Length == 2 ? identity[1] : suppliedUser;
        var pin = Required("YOJANA_CX01_CERT_SHA256").Replace(":", "", StringComparison.Ordinal);
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit))
            throw new InvalidOperationException("YOJANA_CX01_CERT_SHA256 must be a SHA-256 certificate fingerprint.");
        return new(uri, company, user, Required("YOJANA_CX01_PASSWORD"), pin.ToUpperInvariant());
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
            : throw new InvalidOperationException($"{name} is required.");
}

using System.Text;
using System.Text.Json;

namespace Yojana.DemoIncumbent;

internal static class ManifestWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static (string JsonPath, string MarkdownPath) ResolvePaths(string supplied)
    {
        var full = Path.GetFullPath(supplied);
        if (Directory.Exists(full) || supplied.EndsWith(Path.DirectorySeparatorChar)
            || supplied.EndsWith(Path.AltDirectorySeparatorChar))
            return (Path.Combine(full, "yojana-demo-manifest.json"),
                Path.Combine(full, "yojana-demo-manifest.md"));
        var extension = Path.GetExtension(full);
        var stem = extension.Length == 0 ? full : full[..^extension.Length];
        return (stem + ".json", stem + ".md");
    }

    public static async Task<(string JsonPath, string MarkdownPath)> WriteAsync(string supplied,
        string company, IReadOnlyList<DemoObject> objects, CancellationToken cancellationToken)
    {
        var paths = ResolvePaths(supplied);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.JsonPath)!);
        var pipeline = await ReadPipelineAsync(paths.JsonPath, cancellationToken);
        var manifests = ScenarioCatalogue.All.Select(scenario =>
        {
            var scenarioObjects = objects.Where(value => value.Ticket == scenario.MarkerTicket).ToArray();
            var serials = scenarioObjects.SelectMany(value => value.Serials).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            return new DemoTicketManifest(scenario.Tickets, scenario.Scenario, scenario.IncumbentState,
                scenario.ProductOutcome, PipelineOutcome(scenario, pipeline), scenarioObjects, serials);
        }).ToArray();
        var counts = objects.Where(value => value.Exists).GroupBy(value => value.Type, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var manifest = new DemoManifest(DateTimeOffset.UtcNow, company, manifests, counts, pipeline);
        await File.WriteAllTextAsync(paths.JsonPath, JsonSerializer.Serialize(manifest, Json) + Environment.NewLine,
            cancellationToken);
        await File.WriteAllTextAsync(paths.MarkdownPath, Markdown(manifest), cancellationToken);
        return paths;
    }

    private static async Task<PipelineEvidence?> ReadPipelineAsync(string manifestPath,
        CancellationToken cancellationToken)
    {
        // Coupled to MigrationMappingLiveTests' evidence writer. A shared assembly would make this isolated tool
        // part of the test dependency graph; changing these property names or types breaks manifest ingestion.
        var configured = Environment.GetEnvironmentVariable("YOJANA_DEMO_PIPELINE_EVIDENCE");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.ChangeExtension(manifestPath, ".pipeline.json") : Path.GetFullPath(configured);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PipelineEvidence>(stream, Json, cancellationToken);
    }

    private static string PipelineOutcome(DemoScenario scenario, PipelineEvidence? evidence)
    {
        if (scenario.Disposition == DemoDisposition.ProductOnly)
            return "Product action: this ticket is demonstrated from the marked units; it does not require a distinct incumbent record.";
        if (scenario.RecipeKeys.Contains("dirty-identifiers"))
            return "Profiling: serial_whitespace_or_case; canonicalisation proposes the normalised identity.";
        if (scenario.RecipeKeys.Contains("asterisk"))
            return "Product action: identifier validation and correction are demonstrated after migration.";
        if (scenario.RecipeKeys.Any(value => value is "sold-no-card" or "drop-no-card"))
            return "Profiling: shipped_serial_without_card; mapping: a cover or ownership plan gap may also be raised.";
        if (scenario.RecipeKeys.Contains("wrong-item"))
            return "Product action: the physical model is outside source data, so a reviewer uses CorrectItem after comparing the unit.";
        if (scenario.RecipeKeys.Any(value => value.StartsWith("production", StringComparison.Ordinal)))
            return "Review or product action: the source order and serial allocation are visible, but the intended physical allocation needs a person.";
        if (scenario.RecipeKeys.Contains("duplicate-oem"))
            return "Review or product action: choose MergeUnits or acknowledge a manufacturer-identifier collision.";
        if (scenario.RecipeKeys.Contains("delivery-return"))
            return "Reconstruction: the delivery and same-warehouse return form a reversal; the unit remains available for a later episode.";
        if (scenario.RecipeKeys.Any(value => value.Contains("contract", StringComparison.Ordinal)
            || value.StartsWith("addon", StringComparison.Ordinal) || value.StartsWith("bulk-date", StringComparison.Ordinal)))
            return "Mapping or review: source contract/card evidence becomes entitlement candidates; inconsistent terms remain reviewable.";
        if (scenario.RecipeKeys.Contains("replacement") || scenario.RecipeKeys.Contains("replacement-bulk")
            || scenario.RecipeKeys.Contains("replacement-wrong"))
            return "Mapping: installed-base replacement fields create replacement-link candidates; a wrong physical pairing still needs correction.";
        return evidence is null
            ? "Pipeline evidence not attached yet; demonstrate the marked source state and the named product action."
            : "The pipeline loaded the marked source population; use profiling, review and gap totals in the pipeline section.";
    }

    private static string Markdown(DemoManifest manifest)
    {
        var text = new StringBuilder();
        text.AppendLine("# Yojana incumbent demo manifest").AppendLine();
        text.Append("Generated: ").AppendLine(manifest.GeneratedAt.ToString("O"));
        text.Append("Company: `").Append(manifest.Company).AppendLine("`").AppendLine();
        text.AppendLine("## Object counts").AppendLine();
        if (manifest.ObjectCounts.Count == 0) text.AppendLine("No marked objects were found.");
        foreach (var count in manifest.ObjectCounts.OrderBy(value => value.Key, StringComparer.Ordinal))
            text.Append("- ").Append(count.Key).Append(": ").AppendLine(count.Value.ToString());
        text.AppendLine();
        if (manifest.Pipeline is not null)
        {
            text.AppendLine("## Pipeline evidence").AppendLine();
            AppendCounts(text, "Profiling anomalies", manifest.Pipeline.ProfilingAnomalies);
            AppendCounts(text, "Review items", manifest.Pipeline.ReviewItems);
            AppendCounts(text, "Plan gaps", manifest.Pipeline.PlanGaps);
            text.Append("- Replacement links: ").AppendLine(manifest.Pipeline.ReplacementLinks.ToString());
            text.Append("- Units applied: ").AppendLine(manifest.Pipeline.UnitsApplied.ToString()).AppendLine();
        }
        text.AppendLine("## Tickets").AppendLine();
        foreach (var ticket in manifest.Tickets)
        {
            text.Append("### ").Append(string.Join(" / ", ticket.Tickets)).Append(" — ")
                .AppendLine(ticket.Scenario).AppendLine();
            text.Append("- Incumbent: ").AppendLine(ticket.IncumbentState);
            text.Append("- Product: ").AppendLine(ticket.ProductOutcome);
            text.Append("- Pipeline: ").AppendLine(ticket.PipelineOutcome);
            text.Append("- Serials: ").AppendLine(ticket.Serials.Count == 0 ? "product-only"
                : string.Join(", ", ticket.Serials));
            if (ticket.Objects.Count == 0) text.AppendLine("- Objects: product-only");
            foreach (var item in ticket.Objects)
                text.Append("- Object: ").Append(item.Type).Append(" key `").Append(item.Key).Append('`')
                    .Append(item.Exists ? " (present" : " (planned")
                    .Append(item.Number is null ? ")" : ", number " + item.Number + ")").AppendLine();
            text.AppendLine();
        }
        return text.ToString();
    }

    private static void AppendCounts(StringBuilder text, string heading, IReadOnlyDictionary<string, long> counts)
    {
        text.Append("- ").Append(heading).AppendLine(":");
        foreach (var count in counts.OrderBy(value => value.Key, StringComparer.Ordinal))
            text.Append("  - ").Append(count.Key).Append(": ").AppendLine(count.Value.ToString());
    }
}

using System.Globalization;
using System.Text;

namespace Yojana.Integration.DatabaseTests;

internal sealed record ErpChangeRehearsalStepReport(
    int Step,
    string Name,
    DateTime StartedAt,
    DateTime ExitedAt,
    bool ExitPassed,
    string ExitOutcome);

internal sealed record ErpChangeRehearsalCaseReport(
    string Route,
    IReadOnlyList<string> Simulators,
    IReadOnlyDictionary<string, int> SeedSizes,
    IReadOnlyList<ErpChangeRehearsalStepReport> Steps,
    IReadOnlyList<string> Divergences,
    IReadOnlyList<string> Exceptions,
    string BeforeHash,
    string AfterHash,
    int BeforeRows,
    int AfterRows,
    string Outcome,
    string? Note = null);

internal static class ErpChangeRehearsalReportWriter
{
    public static async Task<string> WriteAsync(DateOnly date,
        IReadOnlyList<ErpChangeRehearsalCaseReport> cases,
        string outcome,
        string? failure,
        CancellationToken cancellationToken = default)
    {
        var root = WorktreeRoot();
        var directory = Path.Combine(root, "artifacts", "rehearsal");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "erp-change-rehearsal.md");
        var markdown = Render(date, cases, outcome, failure);
        await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(false), cancellationToken);
        return path;
    }

    private static string Render(DateOnly date, IReadOnlyList<ErpChangeRehearsalCaseReport> cases,
        string outcome, string? failure)
    {
        var text = new StringBuilder();
        text.AppendLine("# ERP change rehearsal").AppendLine()
            .Append("- Date: ").AppendLine(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("- Result: ").AppendLine(outcome)
            .AppendLine();
        if (!string.IsNullOrWhiteSpace(failure))
            text.Append("- Failure: ").AppendLine(OneLine(failure)).AppendLine();

        foreach (var item in cases)
        {
            text.Append("## ").AppendLine(item.Route).AppendLine()
                .Append("- Simulators: ").AppendLine(string.Join(", ", item.Simulators))
                .Append("- Result: ").AppendLine(item.Outcome)
                .Append("- UnitEvent before: `").Append(item.BeforeHash).Append("` (")
                .Append(item.BeforeRows).AppendLine(" rows)")
                .Append("- UnitEvent after: `").Append(item.AfterHash).Append("` (")
                .Append(item.AfterRows).AppendLine(" rows)");
            if (!string.IsNullOrWhiteSpace(item.Note))
                text.Append("- Note: ").AppendLine(OneLine(item.Note));
            text.AppendLine().AppendLine("### Seed sizes").AppendLine();
            foreach (var seed in item.SeedSizes.OrderBy(value => value.Key, StringComparer.Ordinal))
                text.Append("- ").Append(seed.Key).Append(": ").AppendLine(seed.Value.ToString(CultureInfo.InvariantCulture));

            text.AppendLine().AppendLine("### Step results").AppendLine()
                .AppendLine("| Step | Started (UTC) | Exited (UTC) | Simulated duration | Exit check | Outcome |")
                .AppendLine("|---|---|---|---:|---|---|");
            foreach (var step in item.Steps)
                text.Append("| ").Append(step.Step).Append(' ').Append(step.Name).Append(" | ")
                    .Append(Time(step.StartedAt)).Append(" | ").Append(Time(step.ExitedAt)).Append(" | ")
                    .Append(Duration(step.ExitedAt - step.StartedAt)).Append(" | ")
                    .Append(step.ExitPassed ? "PASS" : "FAIL").Append(" | ")
                    .Append(OneLine(step.ExitOutcome)).AppendLine(" |");

            List(text, "Divergences", item.Divergences);
            List(text, "Exceptions", item.Exceptions);
            text.AppendLine();
        }
        return text.ToString();
    }

    private static void List(StringBuilder text, string heading, IReadOnlyList<string> values)
    {
        text.AppendLine().Append("### ").AppendLine(heading).AppendLine();
        if (values.Count == 0) text.AppendLine("- None.");
        else foreach (var value in values) text.Append("- ").AppendLine(OneLine(value));
    }

    private static string WorktreeRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, ".git"))
                && Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;
        throw new InvalidOperationException("The worktree root could not be located from the test output directory.");
    }

    private static string Time(DateTime value) => value.ToUniversalTime()
        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan value) => value.TotalDays >= 1
        ? value.TotalDays.ToString("0.###", CultureInfo.InvariantCulture) + " days"
        : value.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture) + " minutes";

    private static string OneLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
}

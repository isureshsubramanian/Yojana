using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Labels;

internal static partial class LabelTemplateRules
{
    [GeneratedRegex("\\{\\{(?<name>[a-z][a-z0-9_]*)\\}\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\brev(?:ision)?\s*[:#-]?\s*[a-z0-9]+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StaticRevisionRegex();

    public static IReadOnlyList<string> Validate(SaveLabelTemplateDraft command)
    {
        var errors = new List<string>();
        if (!LabelLayoutKind.All.Contains(command.LayoutKind)) errors.Add($"layout kind '{command.LayoutKind}' is unknown");
        if (!LabelSymbology.All.Contains(command.Symbology)) errors.Add($"symbology '{command.Symbology}' is unknown");
        if (string.IsNullOrWhiteSpace(command.Layout)) errors.Add("layout is required");
        if (command.Layout.Length > 100_000) errors.Add("layout cannot exceed 100000 characters");
        if (command.EffectiveTo is { } end && end <= command.EffectiveFrom) errors.Add("effective_to must be after effective_from");
        if (command.StaticText.Any(value => value.Length > 1000)) errors.Add("static text entries cannot exceed 1000 characters");
        if (command.StaticText.Any(value => !command.Layout.Contains(value, StringComparison.Ordinal)))
            errors.Add("every static text entry must occur in the layout so the legal-review inventory matches what prints");
        var literalLayout = PlaceholderRegex().Replace(command.Layout, string.Empty);
        if (command.StaticText.Any(value => StaticRevisionRegex().IsMatch(value)) || StaticRevisionRegex().IsMatch(literalLayout))
            errors.Add("item revision text must use an item.revision or as_built.revision binding, never static text");

        var names = PlaceholderRegex().Matches(command.Layout).Select(match => match.Groups["name"].Value).ToList();
        var residue = PlaceholderRegex().Replace(command.Layout, "");
        if (residue.Contains("{{", StringComparison.Ordinal) || residue.Contains("}}", StringComparison.Ordinal))
            errors.Add("placeholders use {{name}} with lower-case letters, digits and underscores");
        if (names.Count == 0) errors.Add("layout must contain at least one {{name}} placeholder");
        if (command.Bindings.GroupBy(binding => binding.Placeholder, StringComparer.Ordinal).Any(group => group.Count() > 1))
            errors.Add("a placeholder has more than one binding");
        foreach (var binding in command.Bindings)
        {
            if (!LabelBindableField.All.Contains(binding.FieldPath)) errors.Add($"binding field '{binding.FieldPath}' is not in the fixed catalogue");
            if (!names.Contains(binding.Placeholder, StringComparer.Ordinal)) errors.Add($"binding '{binding.Placeholder}' has no placeholder in the layout");
        }
        foreach (var name in names.Distinct(StringComparer.Ordinal))
            if (!command.Bindings.Any(binding => binding.Placeholder == name)) errors.Add($"placeholder '{name}' has no binding");
        return errors;
    }

    public static bool NeedsLegalReview(IEnumerable<string> staticText, IEnumerable<string> phrases, string? layout = null)
    {
        var text = string.Join('\n', staticText.Append(layout ?? string.Empty));
        return phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    public static string ContentHash(SaveLabelTemplateDraft command) => Hash(JsonSerializer.Serialize(new
    {
        command.LayoutKind,
        command.Layout,
        command.StaticText,
        command.Bindings,
        command.Symbology,
        command.PreviewSampleUnitId,
        command.Regulated,
    }, LedgerJson.Options));

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static IReadOnlyList<Guid> CommandIds(string json) =>
        JsonSerializer.Deserialize<List<Guid>>(json, LedgerJson.Options) ?? [];

    public static string AddCommand(string json, Guid commandId)
    {
        var ids = CommandIds(json).ToList();
        if (!ids.Contains(commandId)) ids.Add(commandId);
        return JsonSerializer.Serialize(ids, LedgerJson.Options);
    }

    public static IReadOnlyList<LabelBinding> Bindings(string json) =>
        JsonSerializer.Deserialize<List<LabelBinding>>(json, LedgerJson.Options) ?? [];

    public static IReadOnlyList<string> StaticText(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, LedgerJson.Options) ?? [];
}

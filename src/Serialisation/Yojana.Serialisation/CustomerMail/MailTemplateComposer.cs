using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yojana.Platform.Contracts.Mail;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.CustomerMail;

/// <summary>What surrounds a message's body and is not the template's to change: who it comes from and why it was sent.</summary>
public sealed record MailShell(string CompanyName, string? CompanyLine, string Footer, string ClosingLine);

public sealed record RenderedMail(string Subject, string? Preheader, string Html, string Text);

/// <summary>
/// Turns a template's blocks into the shared template language (one HTML and one plain-text template per language), checks
/// them against the kind's typed field list, and renders them with <see cref="IDocumentTemplateEngine"/>. Author text is
/// HTML-encoded before the three marks are turned into tags, so nothing typed in the designer becomes markup; field values
/// are encoded by the engine. The look comes from <see cref="EmailLayout"/>, shared with account mail.
/// </summary>
public sealed partial class MailTemplateComposer(IDocumentTemplateEngine engine)
{
    public const int MaxBlocks = 40;
    public const int MaxBlockText = 2000;
    public const int MaxSubject = 200;

    private const string LinkColour = "#1d4ed8";

    public MailTemplateCheck Check(MailTemplateContent content, MailKindView kind)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var known = kind.Fields.ToDictionary(field => field.Path, StringComparer.Ordinal);
        if (content.Languages.Count == 0) errors.Add("Add at least one language.");
        if (content.Languages.Select(x => x.Language).Distinct(StringComparer.OrdinalIgnoreCase).Count() != content.Languages.Count)
            errors.Add("Each language appears once.");
        if (content.AttachPdf && !kind.HasPdf) errors.Add($"{kind.Name} has no document to attach.");
        foreach (var language in content.Languages)
        {
            var name = LanguageName(language.Language);
            if (!LanguageCode().IsMatch(language.Language)) errors.Add($"'{language.Language}' is not a language code such as en or de.");
            if (string.IsNullOrWhiteSpace(language.Subject)) errors.Add($"{name}: the subject is empty.");
            else if (language.Subject.Length > MaxSubject) errors.Add($"{name}: the subject is longer than {MaxSubject} characters.");
            if (language.Subject.Contains('\n') || (language.Preheader?.Contains('\n') ?? false))
                errors.Add($"{name}: the subject and preview line are one line each.");
            if (string.IsNullOrWhiteSpace(language.Heading)) errors.Add($"{name}: the heading is empty.");
            if (string.IsNullOrWhiteSpace(language.BandLabel) || language.BandLabel.Length > 40)
                errors.Add($"{name}: the band label is 1 to 40 characters.");
            if (language.Blocks.Count == 0) errors.Add($"{name}: the layout has no blocks.");
            if (language.Blocks.Count > MaxBlocks) errors.Add($"{name}: the layout has more than {MaxBlocks} blocks.");
            for (var index = 0; index < language.Blocks.Count; index++)
                CheckBlock(language.Blocks[index], $"{name}, block {index + 1}", known, errors);

            var composed = Compose(language);
            foreach (var template in new[] { composed.Subject, composed.Preheader ?? "", composed.Band, composed.Heading, composed.Html, composed.Text })
            {
                var analysis = engine.Analyse(template);
                foreach (var error in analysis.Errors) errors.Add($"{name}: {error}");
                foreach (var helper in analysis.UnknownHelpers) errors.Add($"{name}: '{helper}' is not a helper; use date, datetime, label or money.");
                foreach (var field in analysis.Fields)
                {
                    if (!known.ContainsKey(field)) errors.Add($"{name}: '{field}' is not a field of {kind.Name}.");
                    else used.Add(field);
                }
            }
            foreach (var each in EachWithoutElse(language))
                errors.Add($"{name}: the repeat over '{each}' needs an empty-list text ({{{{else}}}} before {{{{/each}}}}).");
        }
        if (content.Languages.Select(x => x.Blocks.Count).Distinct().Count() > 1)
            warnings.Add("The languages have different numbers of blocks; check that each says the same.");
        return new MailTemplateCheck(errors.Count == 0, errors.Distinct().ToList(), warnings, used.ToList(),
            content.Languages.Count == 0 ? 0 : content.Languages[0].Subject.Length);
    }

    public RenderedMail Render(MailLanguageContent content, JsonElement payload, MailShell shell)
    {
        var composed = Compose(content);
        string Text(string template) => OneLine(engine.Render(template, payload, content.Language, TemplateOutput.Text));
        var subject = Text(composed.Subject);
        if (subject.Length == 0) subject = Text(composed.Heading);
        if (subject.Length > 300) subject = subject[..300];
        var preheader = composed.Preheader is null ? null : Text(composed.Preheader);
        var heading = Text(composed.Heading);
        var body = engine.Render(composed.Html, payload, content.Language, TemplateOutput.Html);
        var html = EmailLayout.Document(shell.CompanyName, Text(composed.Band), heading, body, shell.Footer,
            shell.CompanyLine, string.IsNullOrWhiteSpace(preheader) ? null : preheader, content.Language, shell.ClosingLine);
        var text = new StringBuilder(heading).Append("\n\n")
            .Append(TidyText(engine.Render(composed.Text, payload, content.Language, TemplateOutput.Text)))
            .Append(EmailLayout.TextFooter(shell.Footer, shell.CompanyLine, shell.ClosingLine)).ToString();
        return new RenderedMail(subject, preheader, html, text);
    }

    private sealed record Composed(string Subject, string? Preheader, string Band, string Heading, string Html, string Text);

    private static Composed Compose(MailLanguageContent content)
    {
        var html = new StringBuilder();
        var text = new StringBuilder();
        foreach (var block in content.Blocks)
        {
            var (blockHtml, blockText) = ComposeBlock(block);
            if (!string.IsNullOrWhiteSpace(block.When))
            {
                blockHtml = $"{{{{#if {block.When.Trim()}}}}}{blockHtml}{{{{/if}}}}";
                blockText = $"{{{{#if {block.When.Trim()}}}}}{blockText}{{{{/if}}}}";
            }
            html.Append(blockHtml);
            text.Append(blockText);
        }
        return new Composed(content.Subject, content.Preheader, content.BandLabel, content.Heading, html.ToString(), text.ToString());
    }

    private static (string Html, string Text) ComposeBlock(MailBlock block)
    {
        switch (block.Type)
        {
            case MailBlockType.Heading:
                return (EmailLayout.SubHeading(Inline(block.Text)), PlainInline(block.Text) + "\n\n");
            case MailBlockType.Paragraph:
            case MailBlockType.SignOff:
                return (EmailLayout.Paragraph(Inline(block.Text)), PlainInline(block.Text) + "\n\n");
            case MailBlockType.Table:
            {
                var columns = block.Columns ?? [];
                var each = (block.Each ?? "").Trim();
                var row = EmailLayout.TableRow(columns.Select(column => Inline(column.Text)).ToList());
                var empty = EmailLayout.TableNote(Math.Max(1, columns.Count), Inline(block.EmptyText));
                var tableHtml = EmailLayout.Table(columns.Select(column => EmailLayout.Encode(column.Header)).ToList(),
                    $"{{{{#each {each}}}}}{row}{{{{else}}}}{empty}{{{{/each}}}}");
                var line = string.Join(" · ", columns.Select(column => $"{column.Header}: {PlainInline(column.Text)}"));
                return (tableHtml, $"{{{{#each {each}}}}}- {line}\n{{{{else}}}}{PlainInline(block.EmptyText)}\n{{{{/each}}}}\n");
            }
            case MailBlockType.Panel:
            {
                var facts = block.Facts ?? [];
                var panel = EmailLayout.FactsPanel(facts.Select(fact => (fact.Label, Inline(fact.Text))));
                return (panel, string.Concat(facts.Select(fact => $"{fact.Label}: {PlainInline(fact.Text)}\n")) + "\n");
            }
            case MailBlockType.Button:
            {
                var link = $"{{{{{(block.Link ?? "").Trim()}}}}}";
                return (EmailLayout.Button(EmailLayout.Encode(block.Label), link) + EmailLayout.ButtonFallback(link),
                    $"{PlainInline(block.Label)}:\n{link}\n\n");
            }
            default:
                return ("", "");
        }
    }

    private static void CheckBlock(MailBlock block, string where, Dictionary<string, MailField> known, List<string> errors)
    {
        if (!MailBlockType.All.Contains(block.Type)) { errors.Add($"{where}: '{block.Type}' is not a block type."); return; }
        var texts = new List<string?> { block.Text, block.EmptyText, block.Label };
        texts.AddRange((block.Columns ?? []).SelectMany(x => new[] { x.Header, x.Text }));
        texts.AddRange((block.Facts ?? []).SelectMany(x => new[] { x.Label, x.Text }));
        if (texts.Any(x => x is { Length: > MaxBlockText })) errors.Add($"{where}: text is longer than {MaxBlockText} characters.");
        if (block.When is { } when && !string.IsNullOrWhiteSpace(when) && !known.ContainsKey(when.Trim()))
            errors.Add($"{where}: 'show when' uses '{when}', which is not a field.");
        switch (block.Type)
        {
            case MailBlockType.Heading or MailBlockType.Paragraph or MailBlockType.SignOff when string.IsNullOrWhiteSpace(block.Text):
                errors.Add($"{where}: the text is empty.");
                break;
            case MailBlockType.Table:
                if (block.Each is null || !known.TryGetValue(block.Each.Trim(), out var list) || !list.Repeats)
                    errors.Add($"{where}: a table repeats over a list field.");
                if (string.IsNullOrWhiteSpace(block.EmptyText)) errors.Add($"{where}: a table needs the sentence shown when the list is empty.");
                if (block.Columns is not { Count: >= 1 and <= 4 } || block.Columns.Any(x => string.IsNullOrWhiteSpace(x.Header) || string.IsNullOrWhiteSpace(x.Text)))
                    errors.Add($"{where}: a table has 1 to 4 columns, each with a heading and a value.");
                break;
            case MailBlockType.Panel:
                if (block.Facts is not { Count: >= 1 and <= 8 } || block.Facts.Any(x => string.IsNullOrWhiteSpace(x.Label) || string.IsNullOrWhiteSpace(x.Text)))
                    errors.Add($"{where}: a details panel has 1 to 8 rows, each with a label and a value.");
                break;
            case MailBlockType.Button:
                if (string.IsNullOrWhiteSpace(block.Label)) errors.Add($"{where}: the button needs a label.");
                if (block.Link is null || !block.Link.Trim().StartsWith("links.", StringComparison.Ordinal) || !known.ContainsKey(block.Link.Trim()))
                    errors.Add($"{where}: a button links to one of the product's own links.");
                break;
        }
        foreach (Match link in MarkLink().Matches(string.Join("\n", texts.Where(x => x is not null))))
        {
            var target = link.Groups[2].Value;
            var field = target.StartsWith("{{", StringComparison.Ordinal) && target.EndsWith("}}", StringComparison.Ordinal)
                ? target[2..^2].Trim() : null;
            if (field is null || !field.StartsWith("links.", StringComparison.Ordinal) || !known.ContainsKey(field))
                errors.Add($"{where}: links point only to the product's own links ({{{{links.…}}}}), not '{target}'.");
        }
    }

    /// <summary>Every <c>#each</c> in the author's text that closes without an <c>{{else}}</c> at its own level.</summary>
    private static IEnumerable<string> EachWithoutElse(MailLanguageContent content)
    {
        var texts = content.Blocks.SelectMany(block => new[] { block.Text, block.Label }
            .Concat((block.Columns ?? []).Select(x => x.Text)).Concat((block.Facts ?? []).Select(x => x.Text)))
            .Append(content.Subject).Append(content.Preheader).Append(content.Heading);
        foreach (var text in texts.Where(x => x is not null))
        {
            var open = new Stack<(string Name, string Path, bool Else)>();
            foreach (Match tag in Tag().Matches(text!))
            {
                var value = tag.Groups[1].Value.Trim();
                if (value.StartsWith('#'))
                {
                    var parts = value[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    open.Push((parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "", false));
                }
                else if (value == "else" && open.Count > 0) open.Push(open.Pop() with { Else = true });
                else if (value.StartsWith('/') && open.Count > 0)
                {
                    var closed = open.Pop();
                    if (closed is { Name: "each", Else: false }) yield return closed.Path;
                }
            }
        }
    }

    /// <summary>Author text as HTML: encoded, then the three marks and line breaks turned into tags.</summary>
    private static string Inline(string? text)
    {
        var encoded = EmailLayout.Encode((text ?? "").Trim());
        encoded = MarkLink().Replace(encoded, match =>
            $"<a href=\"{match.Groups[2].Value}\" style=\"color:{LinkColour}\">{match.Groups[1].Value}</a>");
        encoded = MarkBold().Replace(encoded, "<strong>$1</strong>");
        encoded = MarkItalic().Replace(encoded, "<em>$1</em>");
        return encoded.Replace("\r\n", "\n").Replace("\n", "<br>");
    }

    private static string PlainInline(string? text)
    {
        var plain = (text ?? "").Trim();
        plain = MarkLink().Replace(plain, "$1 ($2)");
        plain = MarkBold().Replace(plain, "$1");
        return MarkItalic().Replace(plain, "$1").Replace("\r\n", "\n");
    }

    private static string OneLine(string value) => Whitespace().Replace(value, " ").Trim();

    private static string TidyText(string value) => BlankLines().Replace(value.Replace("\r\n", "\n"), "\n\n").TrimStart();

    public static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "en" => "English",
        "de" => "Deutsch",
        "fr" => "Français",
        "es" => "Español",
        "it" => "Italiano",
        "nl" => "Nederlands",
        "hi" => "हिन्दी",
        _ => code,
    };

    [GeneratedRegex(@"^[a-z]{2}(-[A-Z]{2})?$")]
    private static partial Regex LanguageCode();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\(([^)\s]+)\)")]
    private static partial Regex MarkLink();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex MarkBold();

    [GeneratedRegex(@"(?<![*\w])\*(?!\s)([^*\r\n]+?)(?<!\s)\*(?![*\w])")]
    private static partial Regex MarkItalic();

    [GeneratedRegex(@"\{\{(.*?)\}\}")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}

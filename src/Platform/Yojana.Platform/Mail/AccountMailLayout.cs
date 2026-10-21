using System.Text;
using Yojana.Platform.Contracts.Mail;

namespace Yojana.Platform.Mail;

/// <summary>
/// What an account e-mail says, as plain text: the layout encodes every value, so no caller can put markup into a
/// message. <paramref name="Before"/> paragraphs come before the details panel, <paramref name="After"/> after the button.
/// <paramref name="CompanyLine"/> is the company's line from Settings › Company (<see cref="CompanyMailIdentity"/>); null
/// when the mail belongs to no single company.
/// </summary>
public sealed record AccountMailContent(
    string TenantName,
    string Category,
    string Heading,
    IReadOnlyList<string> Before,
    IReadOnlyList<(string Label, string Value)> Facts,
    (string Label, string Url)? Action,
    IReadOnlyList<string> After,
    string Footer,
    string? CompanyLine = null);

/// <summary>
/// Account e-mail in the approved look (PHASE-09 mockup set 4). The look itself lives in <see cref="EmailLayout"/>, shared
/// with customer mail, so this class only arranges an account message's parts.
/// </summary>
public static class AccountMailLayout
{
    public static (string Html, string Text) Render(AccountMailContent content)
    {
        string E(string value) => EmailLayout.Encode(value);
        var body = new StringBuilder();
        foreach (var paragraph in content.Before) body.Append(EmailLayout.Paragraph(E(paragraph)));
        if (content.Facts.Count > 0) body.Append(EmailLayout.FactsPanel(content.Facts.Select(fact => (fact.Label, E(fact.Value)))));
        if (content.Action is { } action) body.Append(EmailLayout.Button(action.Label, E(action.Url)));
        foreach (var paragraph in content.After) body.Append(EmailLayout.Paragraph(E(paragraph), size: 14));
        if (content.Action is { } link) body.Append(EmailLayout.ButtonFallback(E(link.Url)));

        var html = EmailLayout.Document(content.TenantName, content.Category, content.Heading, body.ToString(),
            content.Footer, content.CompanyLine);

        var text = new StringBuilder().Append(content.Heading).Append("\n\n");
        foreach (var paragraph in content.Before) text.Append(paragraph).Append("\n\n");
        foreach (var (label, value) in content.Facts) text.Append(label).Append(": ").Append(value).Append('\n');
        if (content.Facts.Count > 0) text.Append('\n');
        if (content.Action is { } textAction) text.Append(textAction.Label).Append(":\n").Append(textAction.Url).Append("\n\n");
        foreach (var paragraph in content.After) text.Append(paragraph).Append("\n\n");
        text.Append(EmailLayout.TextFooter(content.Footer, content.CompanyLine));
        return (html, text.ToString());
    }

    /// <summary>Up to two initials from the tenant's name ("Asterra Compute Works" → "AC").</summary>
    public static string Monogram(string name) => EmailLayout.Monogram(name);
}

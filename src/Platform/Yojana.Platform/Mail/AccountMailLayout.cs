using System.Net;
using System.Text;

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
/// The approved account e-mail look (PHASE-09 mockup set 4, owner-approved 2026-10-21): navy header band with the
/// tenant's monogram, a gold rule, a details panel, one action button and a formal footer. E-mail-safe on purpose:
/// tables and inline styles only, 600 px, no script and no remote images.
/// </summary>
public static class AccountMailLayout
{
    private const string Font = "-apple-system,'Segoe UI',Roboto,'Helvetica Neue',Arial,sans-serif";
    private const string Navy = "#0b1f3a";
    private const string Gold = "#c9a54a";
    private const string Rule = "#e2e8f0";

    public static (string Html, string Text) Render(AccountMailContent content)
    {
        string E(string value) => WebUtility.HtmlEncode(value);
        var body = new StringBuilder();
        foreach (var paragraph in content.Before) body.Append(Paragraph(E(paragraph)));
        if (content.Facts.Count > 0)
        {
            body.Append($"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin:8px 0 20px;background:#f8fafc;border:1px solid {Rule};border-radius:8px\"><tr><td style=\"padding:10px 20px\"><table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\">");
            foreach (var (label, value) in content.Facts)
                body.Append($"<tr><td style=\"padding:9px 16px 9px 0;font:600 11px {Font};color:#64748b;white-space:nowrap;vertical-align:top;text-transform:uppercase;letter-spacing:0.06em;width:34%\">{E(label)}</td><td style=\"padding:9px 0;font:14px/1.5 {Font};color:#0f172a\">{E(value)}</td></tr>");
            body.Append("</table></td></tr></table>");
        }
        if (content.Action is { } action)
        {
            body.Append($"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:26px 0 10px\"><tr><td style=\"border-radius:6px;background:{Navy}\"><a href=\"{E(action.Url)}\" style=\"display:inline-block;padding:13px 28px;font:600 14px {Font};color:#ffffff;text-decoration:none;border-radius:6px;letter-spacing:0.02em\">{E(action.Label)}</a></td></tr></table>");
        }
        foreach (var paragraph in content.After) body.Append(Paragraph(E(paragraph), size: 14));
        if (content.Action is { } link)
            body.Append(Paragraph($"Button not working? Paste this address into your browser:<br><span style=\"color:#1d4ed8;word-break:break-all\">{E(link.Url)}</span>", size: 12, colour: "#64748b"));

        var companyLine = content.CompanyLine is null ? string.Empty : E(content.CompanyLine) + "<br>";
        var html = $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light">
            <meta name="supported-color-schemes" content="light">
            <title>{E(content.Heading)}</title>
            </head>
            <body style="margin:0;padding:0;background:#eef1f5">
            <table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="background:#eef1f5">
            <tr><td align="center" style="padding:36px 12px">
              <table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="max-width:600px;background:#ffffff;border:1px solid #dde3ea;border-radius:10px;overflow:hidden">
                <tr><td style="background:{Navy};padding:22px 40px">
                  <table role="presentation" cellpadding="0" cellspacing="0" width="100%"><tr>
                    <td style="vertical-align:middle"><table role="presentation" cellpadding="0" cellspacing="0"><tr>
                      <td style="width:34px;height:34px;border:1px solid {Gold};border-radius:6px;text-align:center;vertical-align:middle;font:600 13px {Font};color:{Gold};letter-spacing:0.04em">{E(Monogram(content.TenantName))}</td>
                      <td style="padding-left:12px;font:600 15px {Font};color:#ffffff;letter-spacing:0.01em">{E(content.TenantName)}</td>
                    </tr></table></td>
                    <td align="right" style="vertical-align:middle;font:600 10px {Font};color:#9fb2cf;text-transform:uppercase;letter-spacing:0.14em">{E(content.Category)}</td>
                  </tr></table>
                </td></tr>
                <tr><td style="height:3px;background:{Gold};line-height:3px;font-size:0">&nbsp;</td></tr>
                <tr><td style="padding:40px 40px 30px">
                  <h1 style="margin:0 0 18px;font:600 24px/1.3 {Font};color:{Navy};letter-spacing:-0.015em">{E(content.Heading)}</h1>
                  {body}
                </td></tr>
                <tr><td style="padding:0 40px"><div style="border-top:1px solid {Rule};height:1px;line-height:1px;font-size:0">&nbsp;</div></td></tr>
                <tr><td style="padding:20px 40px 30px;font:12px/1.65 {Font};color:#64748b">{E(content.Footer)}<div style="margin-top:12px;color:#94a3b8">{companyLine}Delivered by Yojana. This is an automated message; please do not forward it.</div></td></tr>
              </table>
            </td></tr>
            </table>
            </body>
            </html>
            """;

        var text = new StringBuilder().Append(content.Heading).Append("\n\n");
        foreach (var paragraph in content.Before) text.Append(paragraph).Append("\n\n");
        foreach (var (label, value) in content.Facts) text.Append(label).Append(": ").Append(value).Append('\n');
        if (content.Facts.Count > 0) text.Append('\n');
        if (content.Action is { } textAction) text.Append(textAction.Label).Append(":\n").Append(textAction.Url).Append("\n\n");
        foreach (var paragraph in content.After) text.Append(paragraph).Append("\n\n");
        text.Append("-- \n").Append(content.Footer).Append('\n');
        if (content.CompanyLine is not null) text.Append(content.CompanyLine).Append('\n');
        return (html, text.ToString());
    }

    /// <summary>Up to two initials from the tenant's name ("Asterra Compute Works" → "AC").</summary>
    public static string Monogram(string name)
    {
        var initials = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.FirstOrDefault(char.IsLetterOrDigit)).Where(c => c != default).Take(2).ToArray();
        return initials.Length == 0 ? "Y" : new string(initials).ToUpperInvariant();
    }

    private static string Paragraph(string encoded, int size = 15, string colour = "#475569") =>
        $"<p style=\"margin:0 0 16px;font:{size}px/1.65 {Font};color:{colour}\">{encoded}</p>";
}

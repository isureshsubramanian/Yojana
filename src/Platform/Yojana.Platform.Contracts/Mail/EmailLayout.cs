using System.Net;
using System.Text;

namespace Yojana.Platform.Contracts.Mail;

/// <summary>
/// The one e-mail look (PHASE-09 mockup sets 4 and 5, owner-approved 2026-10-21): navy header band with the company's
/// monogram, a gold rule, details panels, one action button and a formal footer. Account mail (platform) and customer mail
/// (P9-T20, Serialisation) both build their bodies from these pieces, so the two cannot drift apart. E-mail-safe on
/// purpose: tables and inline styles only, 600 px, no script and no images (owner decision 2026-10-21: no logo either).
/// Pure functions: every method taking <c>string text</c> encodes it; methods taking <c>encodedHtml</c> expect markup the
/// caller produced on the server, never text from a browser.
/// </summary>
public static class EmailLayout
{
    public const string Font = "-apple-system,'Segoe UI',Roboto,'Helvetica Neue',Arial,sans-serif";
    public const string Navy = "#0b1f3a";
    public const string Gold = "#c9a54a";
    public const string Rule = "#e2e8f0";
    public const string Ink = "#0f172a";
    public const string Muted = "#64748b";
    public const string Body = "#475569";

    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");

    public static string Paragraph(string encodedHtml, int size = 15, string colour = Body) =>
        $"<p style=\"margin:0 0 16px;font:{size}px/1.65 {Font};color:{colour}\">{encodedHtml}</p>";

    public static string SubHeading(string text) =>
        $"<h2 style=\"margin:22px 0 10px;font:600 17px/1.35 {Font};color:{Navy}\">{Encode(text)}</h2>";

    /// <summary>The grey details panel: label column in small capitals, value column in body text.</summary>
    public static string FactsPanel(IEnumerable<(string Label, string EncodedValue)> facts)
    {
        var html = new StringBuilder($"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin:8px 0 20px;background:#f8fafc;border:1px solid {Rule};border-radius:8px\"><tr><td style=\"padding:10px 20px\"><table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\">");
        foreach (var (label, value) in facts)
            html.Append($"<tr><td style=\"padding:9px 16px 9px 0;font:600 11px {Font};color:{Muted};white-space:nowrap;vertical-align:top;text-transform:uppercase;letter-spacing:0.06em;width:34%\">{Encode(label)}</td><td style=\"padding:9px 0;font:14px/1.5 {Font};color:{Ink}\">{value}</td></tr>");
        return html.Append("</table></td></tr></table>").ToString();
    }

    /// <summary>A plain data table: a header row and body rows, each cell already encoded.</summary>
    public static string Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> encodedRows)
    {
        var html = new StringBuilder($"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin:8px 0 20px;border-collapse:collapse\"><tr>");
        foreach (var header in headers)
            html.Append($"<td style=\"padding:8px 10px 8px 0;border-bottom:2px solid {Rule};font:600 11px {Font};color:{Muted};text-transform:uppercase;letter-spacing:0.06em\">{Encode(header)}</td>");
        html.Append("</tr>");
        foreach (var row in encodedRows) html.Append(TableRow(row));
        return html.Append("</table>").ToString();
    }

    public static string TableRow(IReadOnlyList<string> encodedCells)
    {
        var html = new StringBuilder("<tr>");
        foreach (var cell in encodedCells)
            html.Append($"<td style=\"padding:9px 10px 9px 0;border-bottom:1px solid {Rule};font:14px/1.5 {Font};color:{Ink};vertical-align:top\">{cell}</td>");
        return html.Append("</tr>").ToString();
    }

    public static string Button(string label, string encodedUrl) =>
        $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:26px 0 10px\"><tr><td style=\"border-radius:6px;background:{Navy}\"><a href=\"{encodedUrl}\" style=\"display:inline-block;padding:13px 28px;font:600 14px {Font};color:#ffffff;text-decoration:none;border-radius:6px;letter-spacing:0.02em\">{Encode(label)}</a></td></tr></table>";

    public static string ButtonFallback(string encodedUrl) =>
        Paragraph($"Button not working? Paste this address into your browser:<br><span style=\"color:#1d4ed8;word-break:break-all\">{encodedUrl}</span>", size: 12, colour: Muted);

    /// <summary>
    /// The whole message around a body. <paramref name="preheader"/> is the line an inbox shows next to the subject;
    /// null leaves it out. <paramref name="companyLine"/> is the company's line from Settings › Company.
    /// </summary>
    public static string Document(string tenantName, string category, string heading, string bodyHtml, string footer,
        string? companyLine, string? preheader = null, string language = "en")
    {
        var company = companyLine is null ? string.Empty : Encode(companyLine) + "<br>";
        var hidden = preheader is null ? string.Empty
            : $"<div style=\"display:none;max-height:0;overflow:hidden;mso-hide:all;font-size:1px;line-height:1px;color:#eef1f5\">{Encode(preheader)}</div>\n";
        return $"""
            <!DOCTYPE html>
            <html lang="{Encode(language)}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light">
            <meta name="supported-color-schemes" content="light">
            <title>{Encode(heading)}</title>
            </head>
            <body style="margin:0;padding:0;background:#eef1f5">
            {hidden}<table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="background:#eef1f5">
            <tr><td align="center" style="padding:36px 12px">
              <table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="max-width:600px;background:#ffffff;border:1px solid #dde3ea;border-radius:10px;overflow:hidden">
                <tr><td style="background:{Navy};padding:22px 40px">
                  <table role="presentation" cellpadding="0" cellspacing="0" width="100%"><tr>
                    <td style="vertical-align:middle"><table role="presentation" cellpadding="0" cellspacing="0"><tr>
                      <td style="width:34px;height:34px;border:1px solid {Gold};border-radius:6px;text-align:center;vertical-align:middle;font:600 13px {Font};color:{Gold};letter-spacing:0.04em">{Encode(Monogram(tenantName))}</td>
                      <td style="padding-left:12px;font:600 15px {Font};color:#ffffff;letter-spacing:0.01em">{Encode(tenantName)}</td>
                    </tr></table></td>
                    <td align="right" style="vertical-align:middle;font:600 10px {Font};color:#9fb2cf;text-transform:uppercase;letter-spacing:0.14em">{Encode(category)}</td>
                  </tr></table>
                </td></tr>
                <tr><td style="height:3px;background:{Gold};line-height:3px;font-size:0">&nbsp;</td></tr>
                <tr><td style="padding:40px 40px 30px">
                  <h1 style="margin:0 0 18px;font:600 24px/1.3 {Font};color:{Navy};letter-spacing:-0.015em">{Encode(heading)}</h1>
                  {bodyHtml}
                </td></tr>
                <tr><td style="padding:0 40px"><div style="border-top:1px solid {Rule};height:1px;line-height:1px;font-size:0">&nbsp;</div></td></tr>
                <tr><td style="padding:20px 40px 30px;font:12px/1.65 {Font};color:{Muted}">{Encode(footer)}<div style="margin-top:12px;color:#94a3b8">{company}Delivered by Yojana. This is an automated message; please do not forward it.</div></td></tr>
              </table>
            </td></tr>
            </table>
            </body>
            </html>
            """;
    }

    /// <summary>The plain-text footer every message ends with.</summary>
    public static string TextFooter(string footer, string? companyLine) =>
        "-- \n" + footer + "\n" + (companyLine is null ? string.Empty : companyLine + "\n");

    /// <summary>Up to two initials from the company's name ("Asterra Compute Works" → "AC").</summary>
    public static string Monogram(string name)
    {
        var initials = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.FirstOrDefault(char.IsLetterOrDigit)).Where(c => c != default).Take(2).ToArray();
        return initials.Length == 0 ? "Y" : new string(initials).ToUpperInvariant();
    }
}

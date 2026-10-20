using System.Net;
using System.Text;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

public sealed class UnitCustomerDocument(WhatsCoveredQuery query, IPdfRenderer? renderer = null) : IUnitCustomerDocument
{
    public async Task<CustomerDocument?> CreateAsync(Guid unitId, string language, CancellationToken cancellationToken)
    {
        var data = await query.ReadAsync(unitId, cancellationToken);
        if (data is null) return null;
        var title = $"What's covered · {data.Serial}";
        var html = new StringBuilder("<!doctype html><html lang=\"").Append(E(language)).Append("\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>")
            .Append(E(title)).Append("</title>").Append(CertificateDocumentStyle.StyleTag).Append("</head><body><h1>").Append(E(title))
            .Append("</h1><p>").Append(E(data.ItemName)).Append(" · ").Append(E(data.CustomerName)).Append("</p><p>As of ").Append(data.AsOf.ToString("yyyy-MM-dd")).Append("</p>");
        foreach (var covered in new[] { true, false })
        {
            html.Append("<h2>").Append(covered ? "Covered" : "Not covered").Append("</h2><ul>");
            foreach (var item in data.Items.Where(x => x.Covered == covered))
            {
                html.Append("<li>").Append(E(item.CustomerWording));
                foreach (var grant in item.Grants)
                {
                    html.Append("<p>").Append(E(grant.Cover.FamilyName)).Append(" · ").Append(E(grant.Cover.PolicyName))
                        .Append(" v").Append(grant.Cover.PolicyVersion);
                    if (grant.Cover.Future) html.Append(" · from ").Append(grant.Cover.From?.ToString("yyyy-MM-dd"));
                    if (grant.Cover.From is null) html.Append(" · start not confirmed");
                    html.Append(" · until ").Append(grant.Cover.Until?.ToString("yyyy-MM-dd") ?? "end not recorded");
                    foreach (var u in grant.Cover.Usage) html.Append(" · ").Append(u.Remaining).Append(' ').Append(E(u.MeterType)).Append(" left / ").Append(u.Period == "contract_year" ? "contract year" : "term");
                    html.Append("</p>");
                }
                html.Append("</li>");
            }
            html.Append("</ul>");
        }
        html.Append("</body></html>");
        var document = html.ToString();
        var pdf = renderer is null ? null : await renderer.RenderAsync(new(document, new("A4", "portrait", 12, 12, 12, 12, null, null)), cancellationToken);
        return new(title, document, pdf, new Dictionary<string, string>
        {
            ["unit.serial"] = data.Serial, ["unit.item"] = data.ItemName, ["customer.name"] = data.CustomerName ?? "",
            ["sheet.title"] = title, ["sheet.as_of"] = data.AsOf.ToString("yyyy-MM-dd"),
        });
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
}

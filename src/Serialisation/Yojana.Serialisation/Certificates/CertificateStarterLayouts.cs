using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Certificates;

internal sealed record CertificateStarterDefinition(string Code, string Name, string Kind, string Form, int Version,
    string Body, string? Header = null, string? Footer = null);

/// <summary>Versioned product-owned layouts. Tenant copies contain their own HTML and never follow later revisions here.</summary>
internal static class CertificateStarterLayouts
{
    private const string Css = """
        <style>
        .certificate-sheet{font:10pt Arial,Helvetica,sans-serif;color:#172033;line-height:1.35;position:relative}
        .certificate-sheet h1{font-size:19pt;margin:0 0 1mm}.certificate-sheet h2{font-size:8.5pt;letter-spacing:.1em;text-transform:uppercase;margin:5mm 0 1.5mm;color:#475569}
        .certificate-head{min-height:24mm;padding-left:54mm;display:flex;justify-content:space-between;gap:8mm;border-bottom:1.5pt solid #172033;padding-bottom:4mm}
        .issuer{font-size:9pt}.issuer strong{display:block;font-size:12pt}.document-meta{text-align:right;min-width:62mm}.document-meta div{display:flex;justify-content:space-between;gap:5mm}.document-meta span:first-child{color:#64748b}
        table{width:100%;border-collapse:collapse;font-size:9pt}th,td{padding:1.6mm 2mm;border-bottom:.5pt solid #cbd5e1;text-align:left;vertical-align:top}th{font-size:7.5pt;text-transform:uppercase;letter-spacing:.06em;color:#475569;background:#f8fafc}
        .key{width:31%;font-weight:700;color:#475569}.mono{font-family:ui-monospace,SFMono-Regular,Consolas,monospace}.status-MISSING{color:#b91c1c;font-weight:800}.status-CAPTURED{color:#166534;font-weight:700}
        .section-grid{display:grid;grid-template-columns:1fr 1fr;gap:5mm}.avoid{break-inside:avoid}.integrity{display:grid;grid-template-columns:1fr 1fr;gap:6mm;border-top:1pt solid #172033;margin-top:5mm;padding-top:3mm;font-size:8pt}.integrity strong{display:block;text-transform:uppercase;letter-spacing:.07em}.fine{font-size:7.5pt;color:#64748b}.declaration{border:1pt solid #94a3b8;padding:3mm;margin-top:4mm;break-inside:avoid}
        .certificate-signature{display:grid;grid-template-columns:1fr 1fr;gap:6mm;margin-top:5mm;padding-top:3mm;font-size:8.5pt}.certificate-signature hr{display:none}.certificate-signature strong{text-transform:uppercase;letter-spacing:.07em}
        .numbered{table-layout:fixed}.numbered td,.numbered th{border:.7pt solid #334155}.box-number{display:block;font-size:7pt;color:#64748b;margin-bottom:1mm}.form-warning{border:1pt solid #b45309;color:#92400e;background:#fffbeb;padding:2mm 3mm;font-weight:700;text-align:center;margin-bottom:3mm}
        @media print{thead{display:table-header-group}tfoot{display:table-footer-group}tr,.avoid{break-inside:avoid}.certificate-sheet{print-color-adjust:exact;-webkit-print-color-adjust:exact}}
        </style>
        """;

    private const string Meta = """
        <div class="certificate-head"><div class="issuer"><strong>{{issuer.name}}</strong><span>{{issuer.address}}</span><br><span>{{issuer.registration_code}}</span></div>
        <div class="document-meta"><div><span>Tracking number</span><b class="mono">{{certificate.tracking_number}}</b></div><div><span>Version</span><b>{{certificate.version}}</b></div><div><span>Issued</span><b>{{certificate.issued_at}}</b></div><div><span>Supersedes</span><b class="mono">{{certificate.supersedes_tracking_number}}</b></div><div><span>Reason</span><b>{{certificate.supersede_reason}}</b></div></div></div>
        """;

    private const string Identity = """
        <h2>Identity</h2><table><thead><tr><th>Identifier</th><th>Value and lineage</th><th>Issuer / validity</th></tr></thead><tbody>
        {{#each identifiers}}<tr><td>{{type}}</td><td class="mono">{{value}}<div class="fine">Previous: {{replaces}} · Replaced by: {{replaced_by}}</div></td><td>{{issuer}}<div class="fine">{{valid_from}} – {{valid_to}}</div></td></tr>{{/each}}
        <tr><td class="key">Model and revision</td><td colspan="2">{{model.code}} · {{model.name}} · revision {{model.revision}}</td></tr>
        <tr><td class="key">Built</td><td colspan="2">{{build_date}} · {{facility}} · {{work_reference}}</td></tr>
        <tr><td class="key">Condition</td><td colspan="2">{{condition}} · refurbishment cycle {{refurbishment_cycle}} · {{refurbisher}}</td></tr></tbody></table>
        """;

    private const string AsBuilt = """
        <h2>As-built at release</h2><table><thead><tr><th>Slot</th><th>Part</th><th>Serial / lot</th><th>Release status</th></tr></thead><tbody>
        {{#each as_maintained}}<tr><td>{{slot}}</td><td>{{item_code}} · {{item_name}}</td><td class="mono">{{serial}} {{lot_reference}}</td><td>{{release_status}}<div class="fine">{{release_document}}</div></td></tr>{{/each}}
        {{#each required_serialized_components}}<tr class="status-{{status}}"><td>{{slot}}</td><td>{{item_code}} · {{item_name}}</td><td class="mono">{{captured_serials}}</td><td>{{status}} · expected {{expected_quantity}}</td></tr>{{/each}}
        </tbody></table>
        """;

    private const string Tests = """
        <h2>Tests and sanitisation</h2><table><thead><tr><th>Test</th><th>Result</th><th>Equipment / time</th></tr></thead><tbody>
        {{#each tests}}<tr><td>{{kind}}</td><td>{{result}}<div class="fine">{{measurements}}</div></td><td>{{equipment}}<div class="fine">{{tested_at}}</div></td></tr>{{/each}}
        <tr><td class="key">Sanitisation</td><td colspan="2">{{sanitisation_reference}}</td></tr><tr><td class="key">Firmware</td><td colspan="2">{{firmware}}</td></tr></tbody></table>
        """;

    private const string Integrity = """
        <div class="integrity avoid"><div><strong>Integrity</strong>Payload SHA-256<br><span class="mono">{{certificate.payload_hash}}</span></div><div><strong>Verify</strong><span class="mono">{{certificate.verify_reference}}</span><br><span class="fine">Signature name, meaning, time and method are printed below and bound to this payload.</span></div></div>
        """;

    private const string Footer = "{{issuer.name}} · controlled certificate · verify {{certificate.verify_reference}}";

    private static string Release(string title, string intro = "") => Css + "<article class=\"certificate-sheet\">" + Meta
        + $"<h1>{title}</h1>" + intro + Identity + AsBuilt + Tests + Integrity + "</article>";

    public static readonly IReadOnlyList<CertificateStarterDefinition> All =
    [
        new("builtin-generic-release", "Generic release record", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.GenericRelease, 2, Release("Release record"), Footer: Footer),
        new("builtin-coc", "Certificate of conformity", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.CertificateOfConformity, 2, Release("Certificate of conformity",
                "<div class=\"declaration\">The identified unit was built, inspected and released against its recorded model, revision and as-built configuration. This certificate reports the signed release facts; it is not a warranty statement.</div>"), Footer: Footer),
        new("builtin-production-history", "Production-history extract", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.DeviceHistoryExtract, 1, Release("Production-history extract",
                "<p class=\"fine\">Controlled extract of identity, production, inspection and release history.</p>"), Footer: Footer),
        new("builtin-8130-3-style", "8130-3-style release block", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.Release81303Style, 1, Airworthiness("8130-3-style release block"), Footer: Footer),
        new("builtin-easa-form-1-style", "EASA Form 1-style release block", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.EasaForm1Style, 1, Airworthiness("EASA Form 1-style release block"), Footer: Footer),
        new("builtin-battery-passport", "Battery passport", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.BatteryPassport, 1, Battery(), Footer: Footer),
        new("builtin-warranty", "Warranty certificate", CertificateDocumentKind.WarrantyCertificate,
            CertificateFormType.WarrantyCertificate, 2, Warranty(), Footer: Footer),
    ];

    private static string Airworthiness(string title) => Css + "<article class=\"certificate-sheet\"><div class=\"form-warning\">"
        + title + " · a rendering of the Yojana release record, not the regulated form itself</div>" + Meta
        + "<table class=\"numbered\"><tbody><tr><td><span class=\"box-number\">1. Approving authority</span>{{issuer.name}}</td><td><span class=\"box-number\">2. Tracking number</span>{{certificate.tracking_number}}</td></tr>"
        + "<tr><td><span class=\"box-number\">3. Organisation</span>{{issuer.name}}<br>{{issuer.address}}</td><td><span class=\"box-number\">4. Work reference</span>{{work_reference}}</td></tr>"
        + "<tr><td colspan=\"2\"><span class=\"box-number\">5–12. Item identification and status</span>" + Identity + AsBuilt + "</td></tr>"
        + "<tr><td colspan=\"2\"><span class=\"box-number\">13. Remarks, tests and release basis</span>" + Tests + "</td></tr></tbody></table>" + Integrity + "</article>";

    private static string Battery() => Css + "<article class=\"certificate-sheet\">" + Meta
        + "<h1>Battery passport</h1><p class=\"fine\">The predecessor identity below is the linked predecessor passport reference when the signed payload carries one.</p>"
        + Identity + "<h2>Linked predecessor passport</h2><table><thead><tr><th>Current identifier</th><th>Predecessor</th></tr></thead><tbody>"
        + "{{#each identifiers}}<tr><td class=\"mono\">{{value}}</td><td class=\"mono\">{{replaces}}</td></tr>{{/each}}</tbody></table>"
        + AsBuilt + Tests + Integrity + "</article>";

    private static string Warranty() => Css + "<article class=\"certificate-sheet\">" + Meta
        + "<h1>Warranty certificate</h1><h2>Owner snapshot</h2><table><tbody><tr><td class=\"key\">Owner</td><td>{{owner.name}} ({{owner.code}})</td></tr><tr><td class=\"key\">Owner kind</td><td>{{owner.kind}}</td></tr><tr><td class=\"key\">Effective from</td><td>{{owner.effective_from}}</td></tr></tbody></table>"
        + "<h2>Entitlements by coverage family</h2><table><thead><tr><th>Coverage family</th><th>Basis and dates</th><th>Policy / terms</th></tr></thead><tbody>"
        + "{{#each entitlements}}<tr><td>{{coverage_family}}<div class=\"fine\">{{type}}</div></td><td>{{start_basis}}<br>{{start_date}} – {{end_date}}</td><td>{{policy_code}} v{{policy_version}}<br>{{term_summary}}</td></tr>{{/each}}</tbody></table>"
        + Integrity + "</article>";
}

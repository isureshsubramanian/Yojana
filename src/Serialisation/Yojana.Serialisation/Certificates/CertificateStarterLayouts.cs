using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Certificates;

internal sealed record CertificateStarterDefinition(string Code, string Name, string Kind, string Form, int Version,
    string Body, string? Header = null, string? Footer = null);

/// <summary>Versioned product-owned layouts. Tenant copies contain their own HTML and never follow later revisions here.</summary>
internal static class CertificateStarterLayouts
{
    private const string Meta = """
        <div class="certificate-head"><div class="issuer"><strong>{{issuer.name}}</strong>{{#if issuer.address}}<span>{{issuer.address}}</span>{{/if}}{{#if issuer.registration_code}}<span>Registration {{issuer.registration_code}}</span>{{/if}}</div>
        <dl class="document-meta"><div><dt>Tracking number</dt><dd class="numeric">{{certificate.tracking_number}}</dd></div><div><dt>Version</dt><dd class="numeric">{{certificate.version}}</dd></div><div><dt>Issued</dt><dd class="numeric">{{datetime certificate.issued_at}}</dd></div>{{#if certificate.supersedes_tracking_number}}<div><dt>Supersedes</dt><dd class="numeric">{{certificate.supersedes_tracking_number}}</dd></div>{{/if}}{{#if certificate.supersede_reason}}<div><dt>Reason</dt><dd>{{label certificate.supersede_reason}}</dd></div>{{/if}}</dl></div>
        """;

    private const string Identity = """
        <h2>Identity</h2><table><thead><tr><th>Identifier</th><th>Value and lineage</th><th>Issuer / validity</th></tr></thead><tbody>
        {{#each identifiers}}<tr><td>{{label type}}</td><td class="numeric">{{value}}{{#if replaces}}<div class="fine">Previous: {{replaces}}</div>{{/if}}{{#if replaced_by}}<div class="fine">Replaced by: {{replaced_by}}</div>{{/if}}</td><td>{{#if issuer}}{{issuer}}{{/if}}{{#if valid_from}}<div class="fine">Valid from {{date valid_from}}{{#if valid_to}} to {{date valid_to}}{{/if}}</div>{{/if}}</td></tr>{{else}}<tr><td colspan="3" class="quiet">No identifiers recorded</td></tr>{{/each}}
        <tr><td class="key">Model</td><td colspan="2">{{#if model.name}}{{model.name}}{{else}}{{model.code}}{{/if}}{{#if model.name}}<span class="fine"> · {{model.code}}</span>{{/if}}{{#if model.revision}} · Revision {{model.revision}}{{/if}}</td></tr>
        {{#if build_date}}<tr><td class="key">Release date</td><td colspan="2">{{date build_date}}{{#if facility}} · {{facility}}{{/if}}{{#if work_reference}} · {{work_reference}}{{/if}}</td></tr>{{/if}}
        {{#if condition}}<tr><td class="key">Condition</td><td colspan="2">{{label condition}}{{#if refurbishment_cycle}} · Refurbishment cycle {{refurbishment_cycle}}{{/if}}{{#if refurbisher}} · {{refurbisher}}{{/if}}</td></tr>{{/if}}</tbody></table>
        """;

    private const string AsBuilt = """
        <h2>As-built</h2><h3>Serialised components</h3><table><thead><tr><th>Slot</th><th>Part</th><th>Serial number</th><th>Release status</th></tr></thead><tbody>
        {{#each as_maintained}}<tr><td>{{#if slot}}{{slot}}{{else}}—{{/if}}</td><td>{{#if item_name}}{{item_name}}{{else}}{{item_code}}{{/if}}{{#if item_name}}<div class="fine">{{item_code}}</div>{{/if}}</td><td class="numeric">{{#if serial}}{{serial}}{{else}}—{{/if}}</td><td>{{#if release_status}}{{label release_status}}{{else}}Not recorded{{/if}}{{#if release_document}}<div class="fine">{{release_document}}</div>{{/if}}</td></tr>{{else}}{{#unless required_serialized_components}}<tr><td colspan="4" class="quiet">No serialised components recorded</td></tr>{{/unless}}{{/each}}
        {{#each required_serialized_components}}<tr><td>{{slot}}</td><td>{{#if item_name}}{{item_name}}{{else}}{{item_code}}{{/if}}{{#if item_name}}<div class="fine">{{item_code}}</div>{{/if}}</td><td class="numeric">{{#each captured_serials}}{{this}}{{else}}—{{/each}}</td><td>{{label status}}{{#if expected_quantity}}<div class="fine">Expected quantity: {{expected_quantity}}</div>{{/if}}</td></tr>{{/each}}
        </tbody></table>
        {{#if full_bom_requested}}<h3>Other parts (full bill of materials)</h3>{{#if bom_synced_at}}<p class="fine">Bill of materials as synced from the ERP on {{date bom_synced_at}}</p><table><thead><tr><th>Line</th><th>Part</th><th>Description</th><th>Qty</th></tr></thead><tbody>{{#each bom_lines}}<tr><td>{{line_no}}</td><td>{{item_code}}</td><td>{{#if description}}{{description}}{{else}}—{{/if}}</td><td class="numeric">{{quantity}}</td></tr>{{else}}<tr><td colspan="4" class="quiet">No other inventory parts</td></tr>{{/each}}</tbody></table>{{else}}<p class="fine">Full bill of materials not available from the ERP at issue</p>{{/if}}{{/if}}
        """;

    private const string Tests = """
        <h2>Tests and sanitisation</h2><table><thead><tr><th>Test</th><th>Result</th><th>Equipment / time</th></tr></thead><tbody>
        {{#each tests}}<tr><td>{{label kind}}</td><td>{{label result}}</td><td>{{#if equipment}}{{equipment}}{{/if}}{{#if tested_at}}<div class="fine">{{datetime tested_at}}</div>{{/if}}</td></tr>{{else}}<tr><td colspan="3" class="quiet">No tests recorded</td></tr>{{/each}}
        {{#if sanitisation_reference}}<tr><td class="key">Sanitisation</td><td colspan="2">{{sanitisation_reference}}</td></tr>{{/if}}{{#if firmware}}<tr><td class="key">Firmware</td><td colspan="2">{{firmware}}</td></tr>{{/if}}</tbody></table>
        """;

    private const string Integrity = """
        <div class="integrity avoid"><div><strong>Integrity</strong>Payload SHA-256<br><span class="numeric hash">{{certificate.payload_hash}}</span></div><div><strong>Verify</strong><span class="numeric hash">{{certificate.verify_reference}}</span><br><span class="fine">Signature name, meaning, time and method are printed below and bound to this payload.</span></div></div>
        """;

    private const string Footer = "<div class=\"certificate-footer\">{{issuer.name}} · controlled certificate · verify {{certificate.verify_reference}}</div>";

    private static string Release(string title, string intro = "") => CertificateDocumentStyle.StyleTag + "<article class=\"certificate-sheet\">" + Meta
        + $"<h1>{title}</h1>" + intro + Identity + AsBuilt + Tests + Integrity + "</article>";

    public static readonly IReadOnlyList<CertificateStarterDefinition> All =
    [
        new("builtin-generic-release", "Generic release record", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.GenericRelease, 5, Release("Release record"), Footer: Footer),
        new("builtin-coc", "Certificate of conformity", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.CertificateOfConformity, 5, Release("Certificate of conformity",
                "<div class=\"declaration\">The identified unit was built, inspected and released against its recorded model, revision and as-built configuration. This certificate reports the signed release facts; it is not a warranty statement.</div>"), Footer: Footer),
        new("builtin-production-history", "Production-history extract", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.DeviceHistoryExtract, 4, Release("Production-history extract",
                "<p class=\"fine\">Controlled extract of identity, production, inspection and release history.</p>"), Footer: Footer),
        new("builtin-8130-3-style", "8130-3-style release block", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.Release81303Style, 4, Airworthiness("8130-3-style release block"), Footer: Footer),
        new("builtin-easa-form-1-style", "EASA Form 1-style release block", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.EasaForm1Style, 4, Airworthiness("EASA Form 1-style release block"), Footer: Footer),
        new("builtin-battery-passport", "Battery passport", CertificateDocumentKind.ReleaseRecord,
            CertificateFormType.BatteryPassport, 5, Battery(), Footer: Footer),
        new("builtin-warranty", "Warranty certificate", CertificateDocumentKind.WarrantyCertificate,
            CertificateFormType.WarrantyCertificate, 5, Warranty(), Footer: Footer),
    ];

    private static string Airworthiness(string title) => CertificateDocumentStyle.StyleTag + "<article class=\"certificate-sheet\"><div class=\"form-warning\">"
        + title + " · a rendering of the Yojana release record, not the regulated form itself</div>" + Meta
        + "<table class=\"numbered\"><tbody><tr><td><span class=\"box-number\">1. Approving authority</span>{{issuer.name}}</td><td><span class=\"box-number\">2. Tracking number</span>{{certificate.tracking_number}}</td></tr>"
        + "<tr><td><span class=\"box-number\">3. Organisation</span>{{issuer.name}}{{#if issuer.address}}<br>{{issuer.address}}{{/if}}{{#if issuer.registration_code}}<br>Registration {{issuer.registration_code}}{{/if}}</td><td><span class=\"box-number\">4. Work reference</span>{{#if work_reference}}{{work_reference}}{{else}}Not recorded{{/if}}</td></tr>"
        + "<tr><td colspan=\"2\"><span class=\"box-number\">5–12. Item identification and status</span>" + Identity + AsBuilt + "</td></tr>"
        + "<tr><td colspan=\"2\"><span class=\"box-number\">13. Remarks, tests and release basis</span>" + Tests + "</td></tr></tbody></table>" + Integrity + "</article>";

    private static string Battery() => CertificateDocumentStyle.StyleTag + "<article class=\"certificate-sheet\">" + Meta
        + "<h1>Battery passport</h1>"
        + Identity + "<h2>Linked predecessor passport</h2><table><thead><tr><th>Current identifier</th><th>Predecessor</th></tr></thead><tbody>"
        + "{{#each identifiers}}<tr><td>{{label type}}<div class=\"numeric\">{{value}}</div></td><td>{{#if replaces}}<span class=\"numeric\">{{replaces}}</span>{{else}}<span class=\"quiet\">No linked predecessor passport recorded</span>{{/if}}</td></tr>{{else}}<tr><td colspan=\"2\" class=\"quiet\">No identifier lineage recorded</td></tr>{{/each}}</tbody></table>"
        + "{{#if battery}}<h2>Battery details</h2><table><tbody><tr><td class=\"key\">Chemistry</td><td>{{#if battery.chemistry}}{{battery.chemistry}}{{else}}Not recorded{{/if}}</td></tr><tr><td class=\"key\">Rated capacity</td><td>{{#if battery.rated_capacity_kwh}}{{battery.rated_capacity_kwh}} kWh{{else}}Not recorded{{/if}}</td></tr><tr><td class=\"key\">State of health</td><td>{{#if battery.state_of_health_percent}}{{battery.state_of_health_percent}}%{{else}}Not recorded{{/if}}</td></tr><tr><td class=\"key\">Carbon footprint</td><td>{{#if battery.carbon_footprint_kg_co2e}}{{battery.carbon_footprint_kg_co2e}} kg CO2e{{else}}Not recorded{{/if}}</td></tr><tr><td class=\"key\">Manufactured</td><td>{{#if battery.manufactured_at}}{{battery.manufactured_at}}{{else}}Not recorded{{/if}}</td></tr><tr><td class=\"key\">Responsible operator</td><td>{{#if battery.responsible_operator}}{{battery.responsible_operator}}{{else}}Not recorded{{/if}}</td></tr></tbody></table>{{/if}}"
        + AsBuilt + Tests + Integrity + "</article>";

    private static string Warranty() => CertificateDocumentStyle.StyleTag + "<article class=\"certificate-sheet\">" + Meta
        + "<h1>Warranty certificate</h1><h2>Owner snapshot</h2><table><tbody><tr><td class=\"key\">Owner</td><td>{{owner.name}}</td></tr>{{#if owner.kind}}<tr><td class=\"key\">Owner type</td><td>{{label owner.kind}}</td></tr>{{/if}}{{#if owner.effective_from}}<tr><td class=\"key\">Effective from</td><td>{{date owner.effective_from}}</td></tr>{{/if}}</tbody></table>"
        + "<h2>Warranty cover</h2><table><thead><tr><th>Coverage</th><th>Basis and dates</th><th>Policy / terms</th></tr></thead><tbody>"
        + "{{#each entitlements}}<tr><td>{{label coverage_family}}{{#if type}}<div class=\"fine\">{{label type}}</div>{{/if}}</td><td>{{label start_basis}}{{#if start_date}}<br>{{date start_date}}{{#if end_date}} – {{date end_date}}{{/if}}{{/if}}</td><td>{{#if policy_name}}{{policy_name}}{{else}}{{policy_code}}{{/if}}{{#if policy_version}} · Version {{policy_version}}{{/if}}{{#if term_summary}}<br>{{label term_summary}}{{/if}}</td></tr>{{else}}<tr><td colspan=\"3\" class=\"quiet\">No current warranty entitlements recorded</td></tr>{{/each}}</tbody></table>"
        + Integrity + "</article>";
}

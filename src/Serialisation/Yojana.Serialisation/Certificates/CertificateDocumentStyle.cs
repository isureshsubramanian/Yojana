namespace Yojana.Serialisation.Certificates;

/// <summary>The one print-safe type system shared by built-in layouts, browser previews and PDF bodies.</summary>
public static class CertificateDocumentStyle
{
    public const string FontFamily = "Arial,Helvetica,sans-serif";

    // COUPLING (designer preview typography): web/src/certificates/designerCore.ts mirrors these four size tokens
    // because an unsaved draft is rendered entirely in the browser and cannot consume server CSS. Changing only one
    // side makes Design view differ from issued HTML/PDF, so keep the documented token values in step.
    public const string Css = """
        :root{--certificate-font-family:Arial,Helvetica,sans-serif;--certificate-size-title:18pt;--certificate-size-section:9pt;--certificate-size-body:9pt;--certificate-size-caption:7.5pt}
        html,body,.certificate-sheet{font-family:var(--certificate-font-family);font-size:var(--certificate-size-body)}
        .certificate-sheet{color:#172033;line-height:1.35;position:relative}
        .certificate-sheet h1{font-size:var(--certificate-size-title);margin:0 0 1mm}
        .certificate-sheet h2{font-size:var(--certificate-size-section);letter-spacing:.1em;text-transform:uppercase;margin:5mm 0 1.5mm;color:#475569}
        .certificate-head{min-height:24mm;display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:start;gap:8mm;border-bottom:1.5pt solid #172033;padding-bottom:4mm}
        .certificate-logo+.certificate-sheet .certificate-head{padding-left:54mm}
        .issuer,.document-meta{font-size:var(--certificate-size-body);line-height:1.35}
        .issuer strong,.issuer span{display:block}.issuer strong{font-size:var(--certificate-size-section)}
        .document-meta{display:grid;grid-template-columns:max-content minmax(32mm,max-content);align-items:baseline;column-gap:5mm;row-gap:.6mm;min-width:62mm}
        .document-meta div{display:contents}.document-meta dt,.document-meta dd{font:inherit;text-align:left;margin:0}.document-meta dt{color:#64748b}.document-meta dd{font-weight:400}
        table{width:100%;border-collapse:collapse;font-size:var(--certificate-size-body)}
        th,td{padding:1.6mm 2mm;border-bottom:.5pt solid #cbd5e1;text-align:left;vertical-align:top}
        th{font-size:var(--certificate-size-caption);text-transform:uppercase;letter-spacing:.06em;color:#475569;background:#f8fafc}
        .key{width:31%;font-weight:700;color:#475569}.numeric{font-variant-numeric:tabular-nums}.hash{overflow-wrap:anywhere}.quiet{color:#64748b;font-style:italic}
        .section-grid{display:grid;grid-template-columns:1fr 1fr;gap:5mm}.avoid{break-inside:avoid}
        .integrity{display:grid;grid-template-columns:1fr 1fr;gap:6mm;border-top:1pt solid #172033;margin-top:5mm;padding-top:3mm;font-size:var(--certificate-size-caption)}
        .integrity strong{display:block;text-transform:uppercase;letter-spacing:.07em}.fine{font-size:var(--certificate-size-caption);color:#64748b}
        .declaration{border:1pt solid #94a3b8;padding:3mm;margin-top:4mm;break-inside:avoid}
        .certificate-signature{display:grid;grid-template-columns:1fr 1fr;gap:6mm;margin-top:5mm;padding-top:3mm;font-size:var(--certificate-size-section)}
        .certificate-signature hr{display:none}.certificate-signature strong{text-transform:uppercase;letter-spacing:.07em}
        .numbered{table-layout:fixed}.numbered td,.numbered th{border:.7pt solid #334155}
        .box-number{display:block;font-size:var(--certificate-size-caption);color:#64748b;margin-bottom:1mm}
        .form-warning{border:1pt solid #b45309;color:#92400e;background:#fffbeb;padding:2mm 3mm;font-weight:700;text-align:center;margin-bottom:3mm}
        .certificate-footer{font-size:var(--certificate-size-caption);line-height:1.25;color:#64748b}
        .certificate-page-chrome{font-family:var(--certificate-font-family);font-size:var(--certificate-size-caption)}
        @media print{thead{display:table-header-group}tfoot{display:table-footer-group}tr,.avoid{break-inside:avoid}.certificate-sheet{print-color-adjust:exact;-webkit-print-color-adjust:exact}}
        """;

    public const string StyleTag = "<style>" + Css + "</style>";
}

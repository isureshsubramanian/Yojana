import type { TenantAsset } from "./types";

export function insertPlaceholder(text: string, field: string, start: number, end: number): { text: string; caret: number } {
  const token = `{{${field}}}`;
  const before = text.slice(0, Math.max(0, start));
  const after = text.slice(Math.max(start, end));
  return { text: before + token + after, caret: before.length + token.length };
}

export interface CertificatePreviewMargins { top: number; right: number; bottom: number; left: number }

const DEFAULT_MARGINS: CertificatePreviewMargins = { top: 15, right: 15, bottom: 15, left: 15 };

// COUPLING (certificate document typography): CertificateDocumentStyle.cs owns the issued HTML/PDF tokens mirrored
// here because an unsaved Design view has no server rendering. If either scale changes alone the design and issued
// views diverge; these are deliberately the same Arial/Helvetica family and four documented sizes.
const DOCUMENT_PREVIEW_CSS = `
  :root{--certificate-font-family:Arial,Helvetica,sans-serif;--certificate-size-title:18pt;--certificate-size-section:9pt;--certificate-size-body:9pt;--certificate-size-caption:7.5pt}
  html,body,.certificate-sheet{font-family:var(--certificate-font-family);font-size:var(--certificate-size-body)}
  body{margin:0;color:#172033;background:#fff}
`;

export function designPreviewHtml(layout: string, margins: CertificatePreviewMargins = DEFAULT_MARGINS): string {
  const safeAttributes = layout.replace(/<[^>]*>/g, (tag) => tag.replace(/\{\{[^{}]+\}\}/g, "preview-value"));
  const highlighted = renderDesignFragment(safeAttributes, 0).html;
  return `<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
    <style>${DOCUMENT_PREVIEW_CSS}body{padding:${mm(margins.top)} ${mm(margins.right)} ${mm(margins.bottom)} ${mm(margins.left)}}.placeholder{background:Highlight;color:HighlightText;border-radius:3px;padding:1px 3px}.template-block-label{display:block;width:max-content;max-width:100%;margin:5px 0 3px;padding:3px 7px;border:1px dashed color-mix(in srgb,Highlight 55%,CanvasText);border-radius:4px;background:color-mix(in srgb,Highlight 12%,Canvas);color:CanvasText;font-family:var(--certificate-font-family);font-weight:600;font-size:var(--certificate-size-caption);line-height:1.3}.template-block-else{opacity:.75}</style>
    </head><body>${highlighted}</body></html>`;
}

function mm(value: number): string {
  return `${Math.max(0, Number.isFinite(value) ? value : 0)}mm`;
}

interface DesignFragment { html: string; index: number; stop: "else" | "close" | null }

function renderDesignFragment(source: string, from: number, closing?: string): DesignFragment {
  let html = "";
  let index = from;
  while (index < source.length) {
    const start = source.indexOf("{{", index);
    if (start < 0) return { html: html + source.slice(index), index: source.length, stop: null };
    html += source.slice(index, start);
    const end = source.indexOf("}}", start + 2);
    if (end < 0) return { html: html + escapeHtml(source.slice(start)), index: source.length, stop: null };
    const expression = source.slice(start + 2, end).trim();
    index = end + 2;
    if (expression === "else" && closing) return { html, index, stop: "else" };
    if (expression === `/${closing}`) return { html, index, stop: "close" };
    if (expression.startsWith("#")) {
      const [helper = "", path = ""] = expression.slice(1).trim().split(/\s+/, 2);
      const body = renderDesignFragment(source, index, helper);
      index = body.index;
      let otherwise = "";
      if (body.stop === "else") {
        const alternate = renderDesignFragment(source, index, helper);
        index = alternate.index;
        otherwise = `<span class="template-block-label template-block-else">${helper === "each" ? "When the list is empty" : "Otherwise"}</span>${alternate.html}`;
      }
      html += `<span class="template-block-label">${blockLabel(helper, path)}</span>${body.html}${otherwise}`;
      continue;
    }
    if (expression.startsWith("/")) continue;
    const parts = expression.split(/\s+/);
    const helper = parts.length > 1 ? parts[0] : null;
    const field = helper ? parts.slice(1).join(" ") : expression;
    html += `<mark class="placeholder">${helper ? `${escapeHtml(helper)} · ` : ""}${escapeHtml(field)}</mark>`;
  }
  return { html, index, stop: null };
}

function blockLabel(helper: string, path: string): string {
  const field = humanLabel(path);
  if (helper === "each") return `Repeats for each ${singular(field)}`;
  if (helper === "unless") return `Shown only when ${field} is empty`;
  if (helper === "if") return `Shown only when ${field} has a value`;
  return `Template block ${helper} · ${field}`;
}

function humanLabel(path: string): string {
  return path.split(".").at(-1)?.replace(/_/g, " ") ?? path;
}

function singular(value: string): string {
  return value.endsWith("ies") ? value.slice(0, -3) + "y" : value.endsWith("s") ? value.slice(0, -1) : value;
}

export function assetPreviewSource(asset: Pick<TenantAsset, "assetId" | "mediaType"> | null): string | null {
  if (!asset || !["image/png", "image/jpeg", "image/svg+xml"].includes(asset.mediaType)) return null;
  return `/api/tenant-assets/${asset.assetId}/content`;
}

function escapeHtml(value: string): string {
  return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

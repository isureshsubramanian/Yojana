import type { TenantAsset } from "./types";

export function insertPlaceholder(text: string, field: string, start: number, end: number): { text: string; caret: number } {
  const token = `{{${field}}}`;
  const before = text.slice(0, Math.max(0, start));
  const after = text.slice(Math.max(start, end));
  return { text: before + token + after, caret: before.length + token.length };
}

export function designPreviewHtml(layout: string): string {
  const highlighted = layout.replace(/\{\{\s*([#/]?[^{}]+?)\s*\}\}/g, (_match, field: string) =>
    `<mark class="placeholder">${escapeHtml(field.trim())}</mark>`);
  return `<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
    <style>body{font-family:system-ui,sans-serif;margin:24px;color:CanvasText;background:Canvas}.placeholder{background:Highlight;color:HighlightText;border-radius:3px;padding:1px 3px}</style>
    </head><body>${highlighted}</body></html>`;
}

export function assetPreviewSource(asset: Pick<TenantAsset, "assetId" | "mediaType"> | null): string | null {
  if (!asset || !["image/png", "image/jpeg", "image/svg+xml"].includes(asset.mediaType)) return null;
  return `/api/tenant-assets/${asset.assetId}/content`;
}

function escapeHtml(value: string): string {
  return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

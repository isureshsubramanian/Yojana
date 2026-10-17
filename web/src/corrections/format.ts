import type { SelectionSource } from "../api/units";
import type { CorrectionKpiRow, IdentifierAssignmentInput, WorkbenchChoice } from "./types";

export const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export const ROOT_CAUSES: Record<string, { label: string; sub: string }> = {
  "1": { label: "Document serials differ from units", sub: "CorrectDocumentSerials" },
  "2": { label: "Replacement or exchange", sub: "business event, not an error" },
  "3": { label: "Refurbishment re-serialisation", sub: "business event, not an error" },
  "4": { label: "Typo or crossed pair", sub: "CorrectIdentifier · SwapIdentifiers" },
  "5": { label: "Wrong item or wrong order", sub: "CorrectItem · CorrectSourceDocument" },
  "6": { label: "Allocation change before birth", sub: "ReassignAllocation · VoidUnit · AssignIdentifiers" },
  "7": { label: "Warranty", sub: "entitlement commands" },
  unclassified: { label: "Unclassified requests", sub: '"None of these" · not a correction until re-filed' },
};

export function splitLines(text: string): string[] {
  return text.split(/[\n,;\t]+/).map((line) => line.trim()).filter(Boolean);
}

export function serialSelection(text: string, scheme: string): SelectionSource | null {
  const range = /^\s*(.+?)\s*(?:→|\.\.|\bto\b)\s*(.+?)\s*$/i.exec(text);
  if (range?.[1] && range[2]) return { kind: "serial_range", scheme: scheme.trim(), rangeStart: range[1].trim(), rangeEnd: range[2].trim() };
  const serials = splitLines(text).flatMap((line) => line.split(/\s+/)).filter(Boolean);
  return serials.length ? { kind: "pasted_list", serials } : null;
}

export function identifierLines(text: string, explicit: boolean): { lines: IdentifierAssignmentInput[]; errors: string[] } {
  const lines: IdentifierAssignmentInput[] = [];
  const errors: string[] = [];
  for (const [index, raw] of text.split(/\r?\n/).entries()) {
    const line = raw.trim();
    if (!line) continue;
    if (!explicit) { lines.push({ value: line }); continue; }
    const comma = line.indexOf(",");
    const unitId = comma < 0 ? "" : line.slice(0, comma).trim();
    const value = comma < 0 ? "" : line.slice(comma + 1).trim();
    if (!GUID.test(unitId) || !value) errors.push(`Line ${index + 1} needs a unit UUID, then a serial value.`);
    else lines.push({ unitId, value });
  }
  return { lines, errors };
}

export function latestBucket(rows: readonly CorrectionKpiRow[]): string | null {
  return rows.reduce<string | null>((latest, row) => latest === null || row.bucket > latest ? row.bucket : latest, null);
}

export function bucketRows(rows: readonly CorrectionKpiRow[], bucket: string | null): CorrectionKpiRow[] {
  return bucket ? rows.filter((row) => row.bucket === bucket) : [];
}

export function slugChoice(label: string, existing: readonly WorkbenchChoice[]): string {
  const base = label.toLowerCase().normalize("NFKD").replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "").slice(0, 48) || "custom_choice";
  const used = new Set(existing.map((choice) => choice.choice));
  if (!used.has(base)) return base;
  let suffix = 2;
  while (used.has(`${base}_${suffix}`)) suffix += 1;
  return `${base}_${suffix}`;
}

export function rootCauseLabel(dimension: string): string {
  const entry = ROOT_CAUSES[dimension];
  return entry ? `${dimension === "unclassified" ? "" : `${dimension} · `}${entry.label}` : dimension.replace(/_/g, " ");
}

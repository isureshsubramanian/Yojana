import type { Coverage, DisclosureRow, ImportRow, ReconciliationPage, XrefRow } from "./types";

export const acceptedImportFormats = ["csv", "xlsx", "xls"] as const;
export function acceptsImportFile(name: string): boolean { return acceptedImportFormats.some((format) => name.toLowerCase().endsWith(`.${format}`)); }
export function fileFormat(name: string): typeof acceptedImportFormats[number] | null { return acceptedImportFormats.find((format) => name.toLowerCase().endsWith(`.${format}`)) ?? null; }
export function splitValues(text: string, limit = 500): string[] { return text.split(/[\n,;]+/).map((value) => value.trim()).filter(Boolean).slice(0, limit); }
export function actionOptions(row: Pick<ImportRow, "comparisonPartition">): string[] { return row.comparisonPartition === "existing_collision" ? ["skip", "flag"] : ["assign", "skip", "flag"]; }
export function coverageFor(coverage: Coverage[], family: string): string { const row = coverage.find((item) => item.coverageFamily === family); return row?.expiry ?? row?.status ?? "—"; }
export function coverageLabel(coverage: Coverage[], family: string): string { const value = coverageFor(coverage, family); return /^\d{4}-\d{2}-\d{2}/.test(value) ? date(value) : human(value); }
export function reconciliationKpis(result: ReconciliationPage | null) { return result ? [result.total, result.owned, result.ownedByAnotherParty, result.notFound, result.invalid] : [0, 0, 0, 0, 0]; }
export function xrefIndent(row: Pick<XrefRow, "depth">): number { return Math.max(0, row.depth) * 18; }
export const fixedDisclosureFields = new Set(["party.data:public", "document.references:public", "service.history:public", "service.internal_notes:public", "service.cost:public", "party.data:owner", "service.internal_notes:owner", "service.cost:owner"]);
export function disclosureLocked(row: DisclosureRow): boolean { return fixedDisclosureFields.has(`${row.field}:${row.tier}`) || row.tier === "internal"; }
const acronyms = new Set(["csv", "id", "imei", "mac", "po", "qr", "rma", "sla", "udi", "uii", "vin", "xls", "xlsx"]);
export function human(value: string): string {
  const words = value.replace(/[._]/g, " ").split(" ").filter(Boolean);
  return words.map((word, index) => acronyms.has(word.toLowerCase()) ? word.toUpperCase()
    : index === 0 ? word[0]!.toUpperCase() + word.slice(1) : word).join(" ");
}
export function date(value: string | null): string { return value ? new Date(value).toLocaleDateString(undefined, { day: "2-digit", month: "short", year: "numeric" }) : "—"; }
export const publicLookupFields = ["itemCode", "itemName", "condition", "manufactureMonth", "warrantyStatus"] as const;
export const portalUnitSections = ["coverage", "service history", "certificates", "registration", "fault report"] as const;
export function internalSections(lookup: { ownership: unknown[]; serviceHistory: unknown[] }): number { return lookup.ownership.length + (lookup.serviceHistory.length ? 1 : 0); }
export function tone(value: string): "ok" | "warn" | "bad" | "info" | "neutral" { const v = value.toLowerCase(); if (["active", "applied", "found", "verified", "current", "issued", "archived"].some((x) => v.includes(x))) return "ok"; if (["invalid", "rejected", "expired", "collision", "not_found"].some((x) => v.includes(x))) return "bad"; if (["pending", "flag", "another", "inbox", "mapped"].some((x) => v.includes(x))) return "warn"; return "neutral"; }

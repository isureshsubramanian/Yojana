import type { EntitlementView, GapRow, PolicyIssue } from "./types";

export function entitlementTone(status: string): "ok" | "warn" | "bad" | "info" | "neutral" {
  switch (status) {
    case "active": return "ok";
    case "pending_evidence": case "suspended": return "warn";
    case "expired": case "closed": return "neutral";
    default: return "info";
  }
}

export function issueTone(issue: PolicyIssue): "bad" | "warn" { return issue.severity === "error" ? "bad" : "warn"; }

export function groupEntitlements(rows: EntitlementView[]): { live: EntitlementView[]; history: EntitlementView[] } {
  const live = rows.filter((row) => row.effectiveStatus !== "closed" && row.effectiveStatus !== "expired");
  const history = rows.filter((row) => !live.includes(row));
  return { live, history };
}

export function daysOld(iso: string, now = Date.now()): number {
  const time = Date.parse(iso);
  return Number.isFinite(time) ? Math.max(0, Math.floor((now - time) / 86_400_000)) : 0;
}

export function gapCause(row: GapRow): string { return row.missingInput ?? row.requirementStatus ?? "no_entitlement"; }

export function commaList(text: string): string[] {
  return [...new Set(text.split(/[\n,]+/).map((part) => part.trim()).filter(Boolean))];
}

export function nullableNumber(value: string): number | null {
  if (!value.trim()) return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

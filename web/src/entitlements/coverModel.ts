import type { CoverFamily, CoverFilters, CoverMoney } from "./coverTypes.ts";

export const coverKinds = ["added", "upgraded", "changed", "amended", "closed_early", "suspended"] as const;
export function changeLabel(kind: string): string { return ({ added: "Added", upgraded: "Upgraded", changed: "Changed", amended: "Amended", closed_early: "Closed early", suspended: "Suspended", same: "Same", ended: "Ended", transferred: "Transferred", resumed: "Resumed", unresolved: "Awaiting evidence", not_taken: "Not taken" } as Record<string, string>)[kind] ?? "Change"; }
export function moneyLabel(values: readonly CoverMoney[]): string { return values.length ? values.map((v) => `${v.currency} ${v.amount.toLocaleString(undefined, { maximumFractionDigits: 4 })}`).join(" · ") : "No amount recorded"; }
export function coverQuery(filters: CoverFilters, cursor?: string | null): string { const query = new URLSearchParams(); for (const [key, value] of Object.entries(filters)) if (value) query.set(key, value); if (cursor) query.set("cursor", cursor); return query.toString(); }
export function changedFamilies(rows: readonly CoverFamily[], onlyChanged: boolean): readonly CoverFamily[] { return onlyChanged ? rows.filter((r) => coverKinds.includes(r.change as typeof coverKinds[number])) : rows; }

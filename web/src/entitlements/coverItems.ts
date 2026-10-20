import { api } from "../api/client";
import { humanise } from "../units/format";

export interface PartsScope { partCategories: string[]; itemFamilies: string[] }
export interface PolicyCoverItem { code: string; type: "covered" | "excluded"; customerWording: string; internalNote: string | null; category: string | null; partsScope: PartsScope; causeCodes: string[]; sinceVersion?: number | null; tooLongForCustomerSheet?: boolean }
export interface PolicyItemPage { items: PolicyCoverItem[]; total: number; skip: number; take: number; version: string }
export interface ClaimCause { code: string; customerWording: string; active: boolean }
export interface CausePage { items: ClaimCause[]; total: number; skip: number; take: number; version: string }
export interface CoverUsage { meterType: string; limit: number; used: number; remaining: number; period: string }
export interface ItemCover { entitlementId: string; familyCode: string; familyName: string; policyName: string; policyVersion: number; from: string | null; until: string | null; asOf: string; future: boolean; usage: CoverUsage[]; policyCode?: string | null }
export interface Contribution { code: string; customerWording: string; internalNote: string | null; category: string | null; partsScope: PartsScope; cover: ItemCover; partsScopeDescription?: string }
export interface UnitCoverItem { key: string; customerWording: string; category: string | null; causeCodes: string[]; causeNames?: string[]; covered: boolean; grants: Contribution[]; excludes: Contribution[]; canBeCovered: string }
export interface WhatsCoveredPage { unitId: string; serial: string; itemName: string; customerName: string | null; asOf: string; covers: ItemCover[]; items: UnitCoverItem[]; coveredCount: number; excludedCount: number; total: number; skip: number; take: number; notice: string | null; customerEmailEnabled?: boolean; coveredTotal?: number; excludedTotal?: number }
export interface GuidedCover { cover: ItemCover; usage: { meterType: string; period: string; now: number; afterThisClaim: number | null }[]; items: Contribution[] }
export interface ClaimGuidance { causeCode: string; covered: boolean; grants: GuidedCover[]; excludes: Contribution[]; sayToCustomer: string; notice: string }
export const coverItemsApi = {
  unit: (id: string, type?: string, search = "", skip = 0, take = 7, signal?: AbortSignal) => api<WhatsCoveredPage>(`/api/units/${id}/whats-covered?${new URLSearchParams({ ...(type ? { type } : {}), search, skip: String(skip), take: String(take) })}`, { signal }),
  sections: (id: string, search = "", coveredSkip = 0, coveredTake = 7, excludedSkip = 0, excludedTake = 7, signal?: AbortSignal) => api<WhatsCoveredPage>(`/api/units/${id}/whats-covered?${new URLSearchParams({ sectionPages: "true", search, coveredSkip: String(coveredSkip), coveredTake: String(coveredTake), excludedSkip: String(excludedSkip), excludedTake: String(excludedTake) })}`, { signal }),
  guidance: (id: string, cause: string, signal?: AbortSignal) => api<ClaimGuidance>(`/api/units/${id}/claim-guidance?cause=${encodeURIComponent(cause)}`, { signal }),
  causes: async (signal?: AbortSignal): Promise<CausePage> => {
    let skip = 0; const items: ClaimCause[] = []; let page: CausePage;
    do { page = await api<CausePage>(`/api/claim-causes?skip=${skip}&take=100`, { signal }); items.push(...page.items); skip += page.items.length; }
    while (page.items.length > 0 && skip < page.total);
    return { ...page, items, skip: 0, take: items.length };
  },
  saveCauses: (items: ClaimCause[], expectedVersion: string) => api<CausePage>("/api/claim-causes", { method: "PUT", body: { items, expectedVersion } }),
  items: (id: string, type = "", skip = 0, signal?: AbortSignal) => api<PolicyItemPage>(`/api/warranty-policies/versions/${id}/items?${new URLSearchParams({ type, skip: String(skip), take: "25" })}`, { signal }),
};
export const coverLabel = (c: ItemCover) => `${c.familyName} · ${c.policyName} v${c.policyVersion}`;
export const usageLabel = (c: ItemCover) => c.usage.map((u) => `${u.remaining} ${humanise(u.meterType)} left / ${u.period === "contract_year" ? "contract year" : "term"}`).join(" · ");
export const unitTabs = ["history", "entitlements", "whats-covered", "cover", "identifiers", "custody", "asbuilt"] as const;
export type UnitTab = typeof unitTabs[number];
export function unitTab(value: string | null): UnitTab { return unitTabs.includes(value as UnitTab) ? value as UnitTab : "entitlements"; }

export function startClaimUrl(unitId: string, causeCodes: string[]): string {
  const query = new URLSearchParams({ unit: unitId });
  if (causeCodes.length === 1) query.set("cause", causeCodes[0]!);
  return `/claims/new?${query}`;
}
export function policyCoverUrl(cover: ItemCover): string | null {
  return cover.policyCode ? `/entitlements/policies?${new URLSearchParams({ policy: cover.policyCode, version: String(cover.policyVersion) })}` : null;
}

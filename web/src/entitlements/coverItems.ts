import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../api/client.ts";
import { humanise } from "../units/format.ts";

const text = z.string();
const nullableText = z.nullable(text);
const count = z.number();
export const partsScopeSchema = z.object({ partCategories: z.array(text), itemFamilies: z.array(text) });
export type PartsScope = z.infer<typeof partsScopeSchema>;
export const policyCoverItemSchema = z.object({ code: text, type: z.enum(["covered", "excluded"]), customerWording: text, internalNote: nullableText, category: nullableText, partsScope: partsScopeSchema, causeCodes: z.array(text), sinceVersion: z.optional(z.nullable(count)), tooLongForCustomerSheet: z.optional(z.boolean()) });
export type PolicyCoverItem = z.infer<typeof policyCoverItemSchema>;
export const policyCoverItemResponseSchema = z.object({ code: text, type: z.enum(["covered", "excluded"]), customerWording: text, internalNote: nullableText, category: nullableText, partsScope: partsScopeSchema, causeCodes: z.array(text), sinceVersion: z.nullable(count), tooLongForCustomerSheet: z.boolean() });
export const policyItemPageSchema = z.object({ items: z.array(policyCoverItemResponseSchema), total: count, skip: count, take: count, version: text });
export type PolicyItemPage = z.infer<typeof policyItemPageSchema>;
export const claimCauseSchema = z.object({ code: text, customerWording: text, active: z.boolean() });
export type ClaimCause = z.infer<typeof claimCauseSchema>;
export const causePageSchema = z.object({ items: z.array(claimCauseSchema), total: count, skip: count, take: count, version: text });
export type CausePage = z.infer<typeof causePageSchema>;
export const coverUsageSchema = z.object({ meterType: text, limit: count, used: count, remaining: count, period: text });
export type CoverUsage = z.infer<typeof coverUsageSchema>;
export const itemCoverSchema = z.object({ entitlementId: text, familyCode: text, familyName: text, policyName: text, policyVersion: count, from: nullableText, until: nullableText, asOf: text, future: z.boolean(), usage: z.array(coverUsageSchema), policyCode: nullableText });
export type ItemCover = z.infer<typeof itemCoverSchema>;
export const contributionSchema = z.object({ code: text, customerWording: text, internalNote: nullableText, category: nullableText, partsScope: partsScopeSchema, cover: itemCoverSchema, partsScopeDescription: text, causeCodes: z.nullable(z.array(text)) });
export type Contribution = z.infer<typeof contributionSchema>;
export const unitCoverItemSchema = z.object({ key: text, customerWording: text, category: nullableText, causeCodes: z.array(text), covered: z.boolean(), grants: z.array(contributionSchema), excludes: z.array(contributionSchema), canBeCovered: text, causeNames: z.nullable(z.array(text)) });
export type UnitCoverItem = z.infer<typeof unitCoverItemSchema>;
const coverOfferSchema = z.object({ policyCode: text, policyName: text, familyCode: text, until: nullableText });
export const whatsCoveredPageSchema = z.object({ unitId: text, serial: text, itemName: text, customerName: nullableText, asOf: text, covers: z.array(itemCoverSchema), items: z.array(unitCoverItemSchema), coveredCount: count, excludedCount: count, total: count, skip: count, take: count, notice: nullableText, customerEmailEnabled: z.boolean(), coveredTotal: count, excludedTotal: count, offers: z.nullable(z.array(coverOfferSchema)) });
export type WhatsCoveredPage = z.infer<typeof whatsCoveredPageSchema>;
const guidanceUsageSchema = z.object({ meterType: text, period: text, now: count, afterThisClaim: z.nullable(count) });
export const guidedCoverSchema = z.object({ cover: itemCoverSchema, usage: z.array(guidanceUsageSchema), items: z.array(contributionSchema), waitingPeriodUntil: nullableText });
export type GuidedCover = z.infer<typeof guidedCoverSchema>;
export const claimGuidanceSchema = z.object({ causeCode: text, covered: z.boolean(), grants: z.array(guidedCoverSchema), excludes: z.array(contributionSchema), sayToCustomer: text, notice: text });
export type ClaimGuidance = z.infer<typeof claimGuidanceSchema>;
const updateCausesRequestSchema = z.object({ items: z.array(claimCauseSchema), expectedVersion: text });
export const coverItemsApi = {
  unit: (id: string, type?: string, search = "", skip = 0, take = 7, signal?: AbortSignal) => apiParsed(`/api/units/${id}/whats-covered?${new URLSearchParams({ ...(type ? { type } : {}), search, skip: String(skip), take: String(take) })}`, whatsCoveredPageSchema, { signal }),
  sections: (id: string, search = "", coveredSkip = 0, coveredTake = 7, excludedSkip = 0, excludedTake = 7, signal?: AbortSignal) => apiParsed(`/api/units/${id}/whats-covered?${new URLSearchParams({ sectionPages: "true", search, coveredSkip: String(coveredSkip), coveredTake: String(coveredTake), excludedSkip: String(excludedSkip), excludedTake: String(excludedTake) })}`, whatsCoveredPageSchema, { signal }),
  guidance: (id: string, cause: string, claimDate: string, occurredDate: string, signal?: AbortSignal) => apiParsed(`/api/units/${id}/claim-guidance?${new URLSearchParams({ cause, ...(claimDate ? { claimDate } : {}), ...(occurredDate ? { occurredDate } : {}) })}`, claimGuidanceSchema, { signal }),
  causes: async (signal?: AbortSignal): Promise<CausePage> => {
    let skip = 0; const items: ClaimCause[] = []; let page: CausePage;
    do { page = await apiParsed(`/api/claim-causes?skip=${skip}&take=100`, causePageSchema, { signal }); items.push(...page.items); skip += page.items.length; }
    while (page.items.length > 0 && skip < page.total);
    return { ...page, items, skip: 0, take: items.length };
  },
  saveCauses: (items: ClaimCause[], expectedVersion: string) => apiParsed("/api/claim-causes", causePageSchema, { method: "PUT", body: checkedBody(updateCausesRequestSchema, { items, expectedVersion }) }),
  items: (id: string, type = "", skip = 0, signal?: AbortSignal) => apiParsed(`/api/warranty-policies/versions/${id}/items?${new URLSearchParams({ type, skip: String(skip), take: "25" })}`, policyItemPageSchema, { signal }),
};
export const coverLabel = (c: ItemCover) => `${c.familyName} · ${c.policyName} v${c.policyVersion}`;
export const usageLabel = (c: ItemCover) => c.usage.map((u) => `${u.remaining} ${humanise(u.meterType)} left / ${u.period === "contract_year" ? "contract year" : "term"}`).join(" · ");
export const unitTabs = ["history", "entitlements", "whats-covered", "cover", "questions", "identifiers", "custody", "asbuilt"] as const;
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

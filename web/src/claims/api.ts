import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import {
  claimAdjudicationPreviewSchema, claimCampaignOptionSchema, claimChangeResultSchema, claimDetailSchema,
  claimPageSchema, claimRiskDecisionSchema, claimRiskFlagDecisionResultSchema, claimRiskFlagPageSchema,
  claimRiskFlagReportRowSchema, claimRiskFlagRowSchema, claimRiskReviewPageSchema, claimRiskReviewSummarySchema,
  claimRiskRulesPolicySchema, claimSummarySchema, coverageFamilySchema, currencyInfoSchema, currencyUsageReportSchema,
  submitClaimResultSchema, submitClaimSchema, tenantCurrencySchema, type ClaimRiskDecision, type ClaimRiskRulesPolicy,
  type SubmitClaim,
} from "./types.ts";

export interface ClaimFilters {
  status: string; coverageSource: string; serviceCountry: string; recovery: string; q: string; campaignId: string;
}

export interface ClaimRiskFilters {
  status: "open" | "decided" | "all"; rule: string; from: string; to: string; cursor?: string | undefined; take: number;
}

const text = z.string();
const nullableText = z.nullable(text);
const adjudicateRequestSchema = z.object({ commandId: text, claimId: text, eventTime: text, denialReason: nullableText, denialNote: nullableText });
const requestEvidenceSchema = z.object({ commandId: text, claimId: text, note: text, eventTime: text, expectedVersion: text });
const claimActionSchema = z.object({ commandId: text, claimId: text, eventTime: text });
const currencyRequestSchema = z.object({ currency: text });
const riskDecisionRequestSchema = z.object({ decision: claimRiskDecisionSchema, reason: text });

function query(values: Record<string, string | number | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  const text = params.toString();
  return text ? `?${text}` : "";
}

export const claimsApi = {
  list: (filters: ClaimFilters, skip: number, take: number, signal?: AbortSignal) => apiParsed(`/api/claims/${query({ ...filters, skip, take })}`, claimPageSchema, { signal }),
  summary: (signal?: AbortSignal) => apiParsed("/api/claims/summary", claimSummarySchema, { signal }),
  detail: (claimId: string, signal?: AbortSignal) => apiParsed(`/api/claims/${claimId}`, claimDetailSchema, { signal }),
  preview: (claimId: string, signal?: AbortSignal) => apiParsed(`/api/claims/${claimId}/adjudication-preview`, claimAdjudicationPreviewSchema, { signal }),
  campaignOptions: (unitId: string, claimDate: string, signal?: AbortSignal) => apiParsed(`/api/claims/campaign-options${query({ unitId, claimDate })}`, z.array(claimCampaignOptionSchema), { signal }),
  submit: (body: SubmitClaim) => apiParsed("/api/claims/", submitClaimResultSchema, { method: "POST", body: z.parse(submitClaimSchema, body) }),
  adjudicate: (claimId: string, denialReason: string | null = null, denialNote: string | null = null) => { const body = { commandId: crypto.randomUUID(), claimId, eventTime: new Date().toISOString(), denialReason, denialNote }; return apiParsed(`/api/claims/${claimId}/adjudicate`, z.unknown(), { method: "POST", body: z.parse(adjudicateRequestSchema, body) }); },
  requestEvidence: (claimId: string, note: string, expectedVersion: string) => { const body = { commandId: crypto.randomUUID(), claimId, note, eventTime: new Date().toISOString(), expectedVersion }; return apiParsed(`/api/claims/${claimId}/request-evidence`, claimChangeResultSchema, { method: "POST", body: z.parse(requestEvidenceSchema, body) }); },
  settle: (claimId: string) => { const body = { commandId: crypto.randomUUID(), claimId, eventTime: new Date().toISOString() }; return apiParsed(`/api/claims/${claimId}/settle`, claimChangeResultSchema, { method: "POST", body: z.parse(claimActionSchema, body) }); },
  close: (claimId: string) => { const body = { commandId: crypto.randomUUID(), claimId, eventTime: new Date().toISOString() }; return apiParsed(`/api/claims/${claimId}/close`, claimChangeResultSchema, { method: "POST", body: z.parse(claimActionSchema, body) }); },
  currency: (signal?: AbortSignal) => apiParsed("/api/tenant/currency", tenantCurrencySchema, { signal }),
  currencyCatalogue: (signal?: AbortSignal) => apiParsed("/api/tenant/currency/catalogue", z.array(currencyInfoSchema), { signal }),
  saveCurrency: (currency: string) => apiParsed("/api/tenant/currency", tenantCurrencySchema, { method: "PUT", body: z.parse(currencyRequestSchema, { currency }) }),
  currencyReport: (signal?: AbortSignal) => apiParsed("/api/tenant/currency/report", currencyUsageReportSchema, { signal }),
  coverageFamilies: (signal?: AbortSignal) => apiParsed("/api/warranty-reference/coverage-families", z.array(coverageFamilySchema), { signal }),
  riskRules: (signal?: AbortSignal) => apiParsed("/api/claims/risk-rules", claimRiskRulesPolicySchema, { signal }),
  saveRiskRules: (body: ClaimRiskRulesPolicy) => apiParsed("/api/claims/risk-rules", claimRiskRulesPolicySchema, { method: "PUT", body: z.parse(claimRiskRulesPolicySchema, body) }),
  riskFlags: (filters: ClaimRiskFilters, signal?: AbortSignal) => apiParsed(`/api/claims/risk-flags${query({ ...filters })}`, claimRiskFlagPageSchema, { signal }),
  riskReview: (filters: ClaimRiskFilters, signal?: AbortSignal) => apiParsed(`/api/claims/risk-review${query({ ...filters })}`, claimRiskReviewPageSchema, { signal }),
  riskReviewSummary: (from: string, to: string, signal?: AbortSignal) => apiParsed(`/api/claims/risk-review/summary${query({ from, to })}`, claimRiskReviewSummarySchema, { signal }),
  claimRiskFlags: (claimId: string, signal?: AbortSignal) => apiParsed(`/api/claims/${claimId}/risk-flags`, z.array(claimRiskFlagRowSchema), { signal }),
  decideRiskFlag: (claimId: string, flagId: string, decision: ClaimRiskDecision, reason: string) =>
    apiParsed(`/api/claims/${claimId}/risk-flags/${flagId}/decision`, claimRiskFlagDecisionResultSchema, { method: "POST", body: z.parse(riskDecisionRequestSchema, { decision, reason }) }),
  riskReport: (from: string, to: string, signal?: AbortSignal) => apiParsed(`/api/claims/risk-flags/report${query({ from, to })}`, z.array(claimRiskFlagReportRowSchema), { signal }),
};


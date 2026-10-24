import { api } from "../api/client";
import type {
  ClaimAdjudicationPreview, ClaimCampaignOption, ClaimChangeResult, ClaimDetail, ClaimPage, ClaimRiskDecision,
  ClaimRiskFlagDecisionResult, ClaimRiskFlagPage, ClaimRiskFlagReportRow, ClaimRiskFlagRow, ClaimRiskReviewPage,
  ClaimRiskReviewSummary, ClaimRiskRulesPolicy,
  ClaimSummary, CoverageFamily, CurrencyInfo, SubmitClaim, SubmitClaimResult, TenantCurrency,
  CurrencyUsageReport,
} from "./types";

export interface ClaimFilters {
  status: string; coverageSource: string; serviceCountry: string; recovery: string; q: string; campaignId: string;
}

export interface ClaimRiskFilters {
  status: "open" | "decided" | "all"; rule: string; from: string; to: string; cursor?: string | undefined; take: number;
}

function query(values: Record<string, string | number | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  const text = params.toString();
  return text ? `?${text}` : "";
}

export const claimsApi = {
  list: (filters: ClaimFilters, skip: number, take: number, signal?: AbortSignal) => api<ClaimPage>(`/api/claims/${query({ ...filters, skip, take })}`, { signal }),
  summary: (signal?: AbortSignal) => api<ClaimSummary>("/api/claims/summary", { signal }),
  detail: (claimId: string, signal?: AbortSignal) => api<ClaimDetail>(`/api/claims/${claimId}`, { signal }),
  preview: (claimId: string, signal?: AbortSignal) => api<ClaimAdjudicationPreview>(`/api/claims/${claimId}/adjudication-preview`, { signal }),
  campaignOptions: (unitId: string, claimDate: string, signal?: AbortSignal) => api<ClaimCampaignOption[]>(`/api/claims/campaign-options${query({ unitId, claimDate })}`, { signal }),
  submit: (body: SubmitClaim) => api<SubmitClaimResult>("/api/claims/", { method: "POST", body }),
  adjudicate: (claimId: string, denialReason: string | null = null, denialNote: string | null = null) => api<unknown>(`/api/claims/${claimId}/adjudicate`, { method: "POST", body: { commandId: crypto.randomUUID(), claimId, eventTime: new Date().toISOString(), denialReason, denialNote } }),
  requestEvidence: (claimId: string, note: string, expectedVersion: string) => api<ClaimChangeResult>(`/api/claims/${claimId}/request-evidence`, { method: "POST", body: { commandId: crypto.randomUUID(), claimId, note, eventTime: new Date().toISOString(), expectedVersion } }),
  settle: (claimId: string) => api<ClaimChangeResult>(`/api/claims/${claimId}/settle`, { method: "POST", body: { commandId: crypto.randomUUID(), claimId, eventTime: new Date().toISOString() } }),
  close: (claimId: string) => api<ClaimChangeResult>(`/api/claims/${claimId}/close`, { method: "POST", body: { commandId: crypto.randomUUID(), claimId, eventTime: new Date().toISOString() } }),
  currency: (signal?: AbortSignal) => api<TenantCurrency>("/api/tenant/currency", { signal }),
  currencyCatalogue: (signal?: AbortSignal) => api<CurrencyInfo[]>("/api/tenant/currency/catalogue", { signal }),
  saveCurrency: (currency: string) => api<TenantCurrency>("/api/tenant/currency", { method: "PUT", body: { currency } }),
  currencyReport: (signal?: AbortSignal) => api<CurrencyUsageReport>("/api/tenant/currency/report", { signal }),
  coverageFamilies: (signal?: AbortSignal) => api<CoverageFamily[]>("/api/warranty-reference/coverage-families", { signal }),
  riskRules: (signal?: AbortSignal) => api<ClaimRiskRulesPolicy>("/api/claims/risk-rules", { signal }),
  saveRiskRules: (body: ClaimRiskRulesPolicy) => api<ClaimRiskRulesPolicy>("/api/claims/risk-rules", { method: "PUT", body }),
  riskFlags: (filters: ClaimRiskFilters, signal?: AbortSignal) => api<ClaimRiskFlagPage>(`/api/claims/risk-flags${query({ ...filters })}`, { signal }),
  riskReview: (filters: ClaimRiskFilters, signal?: AbortSignal) => api<ClaimRiskReviewPage>(`/api/claims/risk-review${query({ ...filters })}`, { signal }),
  riskReviewSummary: (from: string, to: string, signal?: AbortSignal) => api<ClaimRiskReviewSummary>(`/api/claims/risk-review/summary${query({ from, to })}`, { signal }),
  claimRiskFlags: (claimId: string, signal?: AbortSignal) => api<ClaimRiskFlagRow[]>(`/api/claims/${claimId}/risk-flags`, { signal }),
  decideRiskFlag: (claimId: string, flagId: string, decision: ClaimRiskDecision, reason: string) =>
    api<ClaimRiskFlagDecisionResult>(`/api/claims/${claimId}/risk-flags/${flagId}/decision`, { method: "POST", body: { decision, reason } }),
  riskReport: (from: string, to: string, signal?: AbortSignal) => api<ClaimRiskFlagReportRow[]>(`/api/claims/risk-flags/report${query({ from, to })}`, { signal }),
};


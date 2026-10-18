import { api } from "../api/client";
import type {
  ClaimAdjudicationPreview, ClaimCampaignOption, ClaimChangeResult, ClaimDetail, ClaimPage, ClaimSummary, CoverageFamily,
  CurrencyInfo, SubmitClaim, SubmitClaimResult, TenantCurrency,
} from "./types";

export interface ClaimFilters {
  status: string; coverageSource: string; serviceCountry: string; recovery: string; q: string; campaignId: string;
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
  saveCurrency: (currency: string, accepted: string[]) => api<TenantCurrency>("/api/tenant/currency", { method: "PUT", body: { currency, accepted } }),
  coverageFamilies: (signal?: AbortSignal) => api<CoverageFamily[]>("/api/warranty-reference/coverage-families", { signal }),
};


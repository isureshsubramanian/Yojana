import { apiParsed } from "../api/client.ts";
import { recoveryAnswerResult, recoveryClaimPage, recoveryClaimSummary, recoveryNotRaisedReport, recoveryPolicy, type RecoveryAnswer, type RecoveryPolicy } from "./types.ts";

function query(values: Record<string, string | number | undefined | null>): string {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") result.set(key, String(value));
  const text = result.toString();
  return text ? `?${text}` : "";
}

export const recoveryApi = {
  policy: (signal?: AbortSignal) => apiParsed("/api/claims/recovery-policy", recoveryPolicy, { signal }),
  savePolicy: (body: RecoveryPolicy) => apiParsed("/api/claims/recovery-policy", recoveryPolicy, { method: "PUT", body }),
  page: (filters: Record<string, string | number | undefined | null>, signal?: AbortSignal) => apiParsed(`/api/recovery-claims${query(filters)}`, recoveryClaimPage, { signal }),
  summary: (signal?: AbortSignal) => apiParsed("/api/recovery-claims/summary", recoveryClaimSummary, { signal }),
  notRaised: (days = 30, signal?: AbortSignal) => apiParsed(`/api/recovery-claims/not-raised${query({ days })}`, recoveryNotRaisedReport, { signal }),
  exportUrl: (filters: Record<string, string | undefined | null>, sort?: string, dir?: string) => `/api/recovery-claims/export${query({ ...filters, sort, dir })}`,
  answer: (recoveryClaimId: string, body: RecoveryAnswer) => apiParsed(`/api/recovery-claims/${recoveryClaimId}/approve`, recoveryAnswerResult, { method: "POST", body }),
};

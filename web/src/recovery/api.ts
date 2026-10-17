import { api } from "../api/client";
import type { RecoveryAnswer, RecoveryAnswerResult, RecoveryClaimPage, RecoveryClaimSummary, RecoveryNotRaisedReport } from "./types";

function query(values: Record<string, string | number | undefined | null>): string {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") result.set(key, String(value));
  const text = result.toString();
  return text ? `?${text}` : "";
}

export const recoveryApi = {
  page: (filters: Record<string, string | number | undefined | null>, signal?: AbortSignal) => api<RecoveryClaimPage>(`/api/recovery-claims${query(filters)}`, { signal }),
  summary: (signal?: AbortSignal) => api<RecoveryClaimSummary>("/api/recovery-claims/summary", { signal }),
  notRaised: (days = 30, signal?: AbortSignal) => api<RecoveryNotRaisedReport>(`/api/recovery-claims/not-raised${query({ days })}`, { signal }),
  exportUrl: (filters: Record<string, string | undefined | null>) => `/api/recovery-claims/export${query(filters)}`,
  answer: (recoveryClaimId: string, body: RecoveryAnswer) => api<RecoveryAnswerResult>(`/api/recovery-claims/${recoveryClaimId}/approve`, { method: "POST", body }),
};

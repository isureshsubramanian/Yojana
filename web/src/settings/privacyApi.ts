import { api } from "../api/client.ts";

export type ErasureStatus = "pending" | "running" | "completed" | "failed" | "refused";
export type ErasureSubjectKind = "Party" | "User";
export type ReviewAction = "Redact" | "Keep";

export interface ErasureBlocker {
  kind: string; recordId: string | null; reference: string; status: string; detail: string;
}
export interface ErasureStoreCount {
  store: string; kind: string; previewCount: number; erasedCount: number; action: string;
  status: "previewed" | "completed"; completedAt: string | null;
}
export interface ErasureRow {
  erasureId: string; subjectKind: ErasureSubjectKind; subjectId: string; token: string;
  requestReference: string; requestedBy: string; requestReceivedOn: string; status: ErasureStatus;
  requestedAt: string; completedAt: string | null; failureRule: string | null;
}
export interface ErasureSummary {
  completedLastTwelveMonths: number; inProgress: number; refused: number;
  oldestOpen: { receivedOn: string; dueOn: string; ageDays: number; warning: "none" | "due_soon" | "overdue" } | null;
}
export interface ErasurePage { items: ErasureRow[]; nextCursor: string | null; summary: ErasureSummary }
export interface ErasureDetail extends ErasureRow {
  reason: string; requestReceivedOn: string; startedAt: string | null; failedAt: string | null;
  retentionBasis: string; counts: ErasureStoreCount[]; failureDetail: string | null; blockers: ErasureBlocker[];
}
export interface ErasureReviewItem {
  itemKey: string; store: string; recordReference: string; field: string; excerpt: string | null;
}
export interface ErasurePreview {
  subjectKind: ErasureSubjectKind; subjectId: string; token: string;
  counts: { store: string; kind: string; count: number; action: string }[];
  reviewItems: ErasureReviewItem[]; accuracyNote: string;
}
export interface ErasureRequestInput {
  subjectKind: ErasureSubjectKind; subjectId: string; requestReference: string;
  requestReceivedOn: string; reason: string; subjectIsNaturalPerson: boolean;
}

const query = (cursor?: string) => {
  const values = new URLSearchParams({ take: "50" });
  if (cursor) values.set("cursor", cursor);
  return values.toString();
};

export const privacyApi = {
  page: (cursor?: string, signal?: AbortSignal) => api<ErasurePage>(`/api/privacy/erasures?${query(cursor)}`, { signal }),
  detail: (erasureId: string, signal?: AbortSignal) => api<ErasureDetail>(`/api/privacy/erasures/${erasureId}`, { signal }),
  preview: (request: ErasureRequestInput) => api<ErasurePreview>("/api/privacy/erasures/preview", { method: "POST", body: request }),
  confirm: (request: ErasureRequestInput & { confirmation: string; reviewDecisions: { itemKey: string; action: ReviewAction }[] }) =>
    api<ErasureDetail>("/api/privacy/erasures/confirm", { method: "POST", body: request }),
};

import { api } from "../api/client";
import type {
  CanonicalisationRun, MigrationBatch, MigrationBatchPage, MigrationOutcomeChoice, MigrationOutcomeSettings,
  MigrationProfile, MigrationProfileSamples, MigrationReviewDecision, MigrationReviewDecisionResponse,
  MigrationReviewDetail, MigrationReviewKind, MigrationReviewPage, MigrationReviewStatus, MigrationSource,
  MigrationSourcePage, MigrationSourceTestResult, StartMigrationBatchResponse,
} from "./migrationTypes";

const root = "/api/migration";
function query(values: Record<string, string | number | undefined>) {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") result.set(key, String(value));
  return `?${result}`;
}

export const migrationApi = {
  sources: (cursor?: string, signal?: AbortSignal) => api<MigrationSourcePage>(`${root}/sources${query({ cursor, take: 50 })}`, { signal }),
  licence: (sourceId: string, confirmed: boolean, contractReference: string | null, note: string | null) =>
    api<MigrationSource>(`${root}/sources/${sourceId}/licence`, { method: "POST", body: { confirmed, contractReference, note } }),
  testSource: (sourceId: string) => api<MigrationSourceTestResult>(`${root}/sources/${sourceId}/test`, { method: "POST" }),
  batches: (cursor?: string, signal?: AbortSignal) => api<MigrationBatchPage>(`${root}/batches${query({ cursor, take: 12 })}`, { signal }),
  batch: (batchId: string, signal?: AbortSignal) => api<MigrationBatch>(`${root}/batches/${batchId}`, { signal }),
  startBatch: (sourceId: string) => api<StartMigrationBatchResponse>(`${root}/batches`, { method: "POST", body: { sourceId } }),
  profile: (batchId: string, signal?: AbortSignal) => api<MigrationProfile>(`${root}/batches/${batchId}/profile`, { signal }),
  samples: (batchId: string, classCode: string, signal?: AbortSignal) => api<MigrationProfileSamples>(`${root}/batches/${batchId}/profile/${encodeURIComponent(classCode)}/samples`, { signal }),
  rerunProfile: (batchId: string) => api<{ runId: string; batchId: string; status: string }>(`${root}/batches/${batchId}/profile`, { method: "POST" }),
  outcomes: (signal?: AbortSignal) => api<MigrationOutcomeSettings>(`${root}/profiling/outcomes`, { signal }),
  saveOutcomes: (classes: MigrationOutcomeChoice[], expectedVersion: string) => api<MigrationOutcomeSettings>(`${root}/profiling/outcomes`, { method: "PUT", body: { classes, expectedVersion } }),
  canonicalisation: (batchId: string, signal?: AbortSignal) => api<CanonicalisationRun>(`${root}/batches/${batchId}/canonicalisation`, { signal }),
  runCanonicalisation: (batchId: string) => api<{ runId: string; batchId: string; status: string }>(`${root}/batches/${batchId}/canonicalise`, { method: "POST" }),
  review: (filters: { batchId: string; kind?: MigrationReviewKind | ""; family?: string; status?: MigrationReviewStatus | ""; cursor?: string | undefined; take?: number }, signal?: AbortSignal) =>
    api<MigrationReviewPage>(`${root}/review${query({ ...filters, take: filters.take ?? 25 })}`, { signal }),
  reviewDetail: (itemId: string, signal?: AbortSignal) => api<MigrationReviewDetail>(`${root}/review/${itemId}`, { signal }),
  decideReview: (itemId: string, decision: MigrationReviewDecision, keepSourceKey: string | null, reason: string, note?: string) =>
    api<MigrationReviewDecisionResponse>(`${root}/review/${itemId}/decision`, { method: "POST", body: { decision, keepSourceKey, reason, note: note?.trim() || null } }),
};

import { api } from "../api/client";
import type { ActionResult, AttentionCount, DeadLetterDetail, DeadLetterPage, ManualTask, ManualTaskPage } from "./types";

function query(values: Record<string, string | number | undefined>) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") params.set(key, String(value));
  const text = params.toString();
  return text ? `?${text}` : "";
}

export const integrationApi = {
  attention: (signal?: AbortSignal) => api<AttentionCount>("/api/integration/attention-count", { signal }),
  deadLetters: (filters: { status: string; connectionId?: string | undefined; reason?: string | undefined; recordType?: string | undefined; query?: string | undefined; cursor?: string | undefined; take?: number | undefined }, signal?: AbortSignal) =>
    api<DeadLetterPage>(`/api/integration/dead-letters${query(filters)}`, { signal }),
  deadLetter: (id: string, signal?: AbortSignal) => api<DeadLetterDetail>(`/api/integration/dead-letters/${id}`, { signal }),
  mapAndRetry: (id: string, entityType: string, externalId: string, moduleId: string) =>
    api<ActionResult>(`/api/integration/dead-letters/${id}/map-and-retry`, { method: "POST", body: { entityType, externalId, moduleId } }),
  createProvisionalAndRetry: (id: string, entityType: string, externalId: string) =>
    api<ActionResult>(`/api/integration/dead-letters/${id}/create-provisional-and-retry`, { method: "POST", body: { entityType, externalId } }),
  discard: (id: string, reason: string) => api<ActionResult>(`/api/integration/dead-letters/${id}/discard`, { method: "POST", body: { reason } }),
  manualTasks: (filters: { status: string; desk?: string | undefined; cursor?: string | undefined; take?: number | undefined }, signal?: AbortSignal) =>
    api<ManualTaskPage>(`/api/integration/manual-tasks${query(filters)}`, { signal }),
  manualTask: (id: string, signal?: AbortSignal) => api<ManualTask>(`/api/integration/manual-tasks/${id}`, { signal }),
  completeManualTask: (id: string, erpExternalId: string, allowUnverified: boolean) =>
    api<ManualTask>(`/api/integration/manual-tasks/${id}/complete`, { method: "POST", body: { erpExternalId, erpChangeMarker: null, allowUnverified } }),
};

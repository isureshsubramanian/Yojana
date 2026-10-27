import { apiParsed } from "../api/client.ts";
import { actionResult, attentionCount, deadLetterDetail, deadLetterPage, manualTask, manualTaskPage } from "./types.ts";

function query(values: Record<string, string | number | undefined>) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") params.set(key, String(value));
  const text = params.toString();
  return text ? `?${text}` : "";
}

export const integrationApi = {
  attention: (signal?: AbortSignal) => apiParsed("/api/integration/attention-count", attentionCount, { signal }),
  deadLetters: (filters: { status: string; connectionId?: string | undefined; reason?: string | undefined; recordType?: string | undefined; query?: string | undefined; cursor?: string | undefined; take?: number | undefined }, signal?: AbortSignal) =>
    apiParsed(`/api/integration/dead-letters${query(filters)}`, deadLetterPage, { signal }),
  deadLetter: (id: string, signal?: AbortSignal) => apiParsed(`/api/integration/dead-letters/${id}`, deadLetterDetail, { signal }),
  mapAndRetry: (id: string, entityType: string, externalId: string, moduleId: string) =>
    apiParsed(`/api/integration/dead-letters/${id}/map-and-retry`, actionResult, { method: "POST", body: { entityType, externalId, moduleId } }),
  createProvisionalAndRetry: (id: string, entityType: string, externalId: string) =>
    apiParsed(`/api/integration/dead-letters/${id}/create-provisional-and-retry`, actionResult, { method: "POST", body: { entityType, externalId } }),
  retry: (id: string) => apiParsed(`/api/integration/dead-letters/${id}/retry`, actionResult, { method: "POST" }),
  discard: (id: string, reason: string) => apiParsed(`/api/integration/dead-letters/${id}/discard`, actionResult, { method: "POST", body: { reason } }),
  manualTasks: (filters: { status: string; desk?: string | undefined; cursor?: string | undefined; take?: number | undefined }, signal?: AbortSignal) =>
    apiParsed(`/api/integration/manual-tasks${query(filters)}`, manualTaskPage, { signal }),
  manualTask: (id: string, signal?: AbortSignal) => apiParsed(`/api/integration/manual-tasks/${id}`, manualTask, { signal }),
  completeManualTask: (id: string, erpExternalId: string, allowUnverified: boolean) =>
    apiParsed(`/api/integration/manual-tasks/${id}/complete`, manualTask, { method: "POST", body: { erpExternalId, erpChangeMarker: null, allowUnverified } }),
};

import { apiParsed } from "../api/client.ts";
import { coexistenceConflictPage, coexistenceConflictView, goLiveRecordView, parallelRunDashboard, parallelRunView, run } from "./adminTypes.ts";

const root = "/api/integration";
const connection = (id: string) => `${root}/connections/${id}`;

function query(values: Record<string, string | number | undefined>) {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") result.set(key, String(value));
  const text = result.toString();
  return text ? `?${text}` : "";
}

export const parallelRunApi = {
  dashboard: (connectionId: string, signal?: AbortSignal) =>
    apiParsed(`${connection(connectionId)}/parallel-run/dashboard`, parallelRunDashboard, { signal }),
  start: (connectionId: string, days: 30 | 60 | 90, startDate: string) =>
    apiParsed(`${connection(connectionId)}/parallel-run`, parallelRunView, { method: "POST", body: { days, startDate } }),
  setMonthEndClose: (connectionId: string, closeOn: string) =>
    apiParsed(`${connection(connectionId)}/parallel-run/month-end-close`, parallelRunDashboard, { method: "PUT", body: { closeOn } }),
  planHandover: (connectionId: string, kind: string, plannedOn: string) =>
    apiParsed(`${connection(connectionId)}/parallel-run/handover/${encodeURIComponent(kind)}/plan`, parallelRunDashboard, { method: "PUT", body: { plannedOn } }),
  confirmHandover: (connectionId: string, kind: string, evidence: string) =>
    apiParsed(`${connection(connectionId)}/parallel-run/handover/${encodeURIComponent(kind)}/confirm`, parallelRunDashboard, { method: "POST", body: { evidence } }),
  confirmGoLiveItem: (connectionId: string, itemCode: string, evidence: string) =>
    apiParsed(`${connection(connectionId)}/go-live/${encodeURIComponent(itemCode)}/confirm`, parallelRunDashboard, { method: "POST", body: { evidence } }),
  issueGoLiveRecord: (connectionId: string) =>
    apiParsed(`${connection(connectionId)}/go-live/record`, goLiveRecordView, { method: "POST" }),
  goLiveRecord: (recordId: string, signal?: AbortSignal) =>
    apiParsed(`${root}/go-live/records/${recordId}`, goLiveRecordView, { signal }),
  goLiveCsvUrl: (recordId: string) => `${root}/go-live/records/${recordId}/csv`,
  conflicts: (connectionId: string, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${root}/coexistence/conflicts${query({ connectionId, status: "unresolved", cursor, sort: "detected", dir: "desc", take: 5 })}`, coexistenceConflictPage, { signal }),
  resolveConflict: (conflictId: string, decision: "keep_module" | "accept_remote", note: string) =>
    apiParsed(`${root}/coexistence/conflicts/${conflictId}/resolve`, coexistenceConflictView, { method: "POST", body: { decision, note } }),
  explainReconciliation: (runId: string, note: string) =>
    apiParsed(`${root}/reconciliation/runs/${runId}/explain`, run, { method: "POST", body: { note } }),
};

import { api } from "../api/client.ts";
import type {
  CoexistenceConflictPage, CoexistenceConflictView, GoLiveRecordView, ParallelRunDashboard, ParallelRunView, Run,
} from "./adminTypes";

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
    api<ParallelRunDashboard>(`${connection(connectionId)}/parallel-run/dashboard`, { signal }),
  start: (connectionId: string, days: 30 | 60 | 90, startDate: string) =>
    api<ParallelRunView>(`${connection(connectionId)}/parallel-run`, { method: "POST", body: { days, startDate } }),
  setMonthEndClose: (connectionId: string, closeOn: string) =>
    api<ParallelRunDashboard>(`${connection(connectionId)}/parallel-run/month-end-close`, { method: "PUT", body: { closeOn } }),
  planHandover: (connectionId: string, kind: string, plannedOn: string) =>
    api<ParallelRunDashboard>(`${connection(connectionId)}/parallel-run/handover/${encodeURIComponent(kind)}/plan`, { method: "PUT", body: { plannedOn } }),
  confirmHandover: (connectionId: string, kind: string, evidence: string) =>
    api<ParallelRunDashboard>(`${connection(connectionId)}/parallel-run/handover/${encodeURIComponent(kind)}/confirm`, { method: "POST", body: { evidence } }),
  confirmGoLiveItem: (connectionId: string, itemCode: string, evidence: string) =>
    api<ParallelRunDashboard>(`${connection(connectionId)}/go-live/${encodeURIComponent(itemCode)}/confirm`, { method: "POST", body: { evidence } }),
  issueGoLiveRecord: (connectionId: string) =>
    api<GoLiveRecordView>(`${connection(connectionId)}/go-live/record`, { method: "POST" }),
  goLiveRecord: (recordId: string, signal?: AbortSignal) =>
    api<GoLiveRecordView>(`${root}/go-live/records/${recordId}`, { signal }),
  goLiveCsvUrl: (recordId: string) => `${root}/go-live/records/${recordId}/csv`,
  conflicts: (connectionId: string, cursor?: string, signal?: AbortSignal) =>
    api<CoexistenceConflictPage>(`${root}/coexistence/conflicts${query({ connectionId, status: "unresolved", cursor, take: 5 })}`, { signal }),
  resolveConflict: (conflictId: string, decision: "keep_module" | "accept_remote", note: string) =>
    api<CoexistenceConflictView>(`${root}/coexistence/conflicts/${conflictId}/resolve`, { method: "POST", body: { decision, note } }),
  explainReconciliation: (runId: string, note: string) =>
    api<Run>(`${root}/reconciliation/runs/${runId}/explain`, { method: "POST", body: { note } }),
};

import { api } from "../api/client";
import type { AcceptanceItemResult, AcceptanceRun, ConnectorVerificationView, EdgeImportResult } from "./verificationTypes";

const root = (connectionId: string) => `/api/integration/connections/${connectionId}/verification`;

export const verificationApi = {
  overview: (connectionId: string, signal?: AbortSignal) => api<ConnectorVerificationView>(`${root(connectionId)}/`, { signal }),
  run: (connectionId: string, runId: string, signal?: AbortSignal) => api<AcceptanceRun>(`${root(connectionId)}/runs/${runId}`, { signal }),
  start: (connectionId: string, sandboxLabel: string) => api<AcceptanceRun>(`${root(connectionId)}/runs`, { method: "POST", body: { sandboxLabel } }),
  mark: (connectionId: string, runId: string, itemId: string, result: AcceptanceItemResult, note: string, evidence: string) =>
    api<AcceptanceRun>(`${root(connectionId)}/runs/${runId}/items/${itemId}`, { method: "PUT", body: { result, note: note || null, evidence: evidence || null } }),
  importResults: (connectionId: string, runId: string, resultFile: unknown) =>
    api<EdgeImportResult>(`${root(connectionId)}/runs/${runId}/imports`, { method: "POST", body: { resultFile } }),
  complete: (connectionId: string, runId: string) => api<AcceptanceRun>(`${root(connectionId)}/runs/${runId}/complete`, { method: "POST" }),
};

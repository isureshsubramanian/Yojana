import { apiParsed } from "../api/client.ts";
import { acceptanceRun, connectorVerificationView, edgeImportResult, type AcceptanceItemResult } from "./verificationTypes.ts";

const root = (connectionId: string) => `/api/integration/connections/${connectionId}/verification`;

export const verificationApi = {
  overview: (connectionId: string, signal?: AbortSignal) => apiParsed(`${root(connectionId)}/`, connectorVerificationView, { signal }),
  run: (connectionId: string, runId: string, signal?: AbortSignal) => apiParsed(`${root(connectionId)}/runs/${runId}`, acceptanceRun, { signal }),
  start: (connectionId: string, sandboxLabel: string) => apiParsed(`${root(connectionId)}/runs`, acceptanceRun, { method: "POST", body: { sandboxLabel } }),
  mark: (connectionId: string, runId: string, itemId: string, result: AcceptanceItemResult, note: string, evidence: string) =>
    apiParsed(`${root(connectionId)}/runs/${runId}/items/${itemId}`, acceptanceRun, { method: "PUT", body: { result, note: note || null, evidence: evidence || null } }),
  importResults: (connectionId: string, runId: string, resultFile: unknown) =>
    apiParsed(`${root(connectionId)}/runs/${runId}/imports`, edgeImportResult, { method: "POST", body: { resultFile } }),
  complete: (connectionId: string, runId: string) => apiParsed(`${root(connectionId)}/runs/${runId}/complete`, acceptanceRun, { method: "POST" }),
};

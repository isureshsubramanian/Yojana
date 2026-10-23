import { api } from "../api/client.ts";
import type {
  ConnectionAuthorisationView, ConsentResult, ConsentStart, CutoverChecklistView, DisableResult, ErpChangeCleanPeriodView,
  ErpChangeDailyCheckView, ErpChangeDivergencePage, ErpChangeDivergenceRow, ErpChangeRunbookView, MasterDataExceptionPage,
  MasterDataExceptionRow, ReconcileStockResult, ReminderSettingsView, ResolveStockDifferenceRequest, StockDifferencePage,
  StockDifferenceRow, StockResidualSignoffView,
} from "./erpChangeTypes";

const root = "/api/integration/erp-change";
const run = (runId: string) => `${root}/runs/${runId}`;

function query(values: Record<string, string | number | boolean | undefined>) {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") result.set(key, String(value));
  const text = result.toString();
  return text ? `?${text}` : "";
}

export interface ListFilter { kind?: string | undefined; status?: string | undefined; query?: string | undefined }
export interface StockFilter { reason?: string | undefined; liveCover?: boolean | undefined; status?: string | undefined; query?: string | undefined }

export const erpChangeApi = {
  active: (signal?: AbortSignal) => api<ErpChangeRunbookView>(`${root}/runs/active`, { signal }),
  detail: (runId: string, signal?: AbortSignal) => api<ErpChangeRunbookView>(run(runId), { signal }),
  start: (sourceConnectionId: string, newConnection: unknown) =>
    api<ErpChangeRunbookView>(`${root}/runs`, { method: "POST", body: { sourceConnectionId, newConnection } }),
  check: (runId: string) => api<ErpChangeRunbookView>(`${run(runId)}/check`, { method: "POST" }),
  advance: (runId: string) => api<ErpChangeRunbookView>(`${run(runId)}/advance`, { method: "POST" }),
  cancel: (runId: string, reason: string) => api<ErpChangeRunbookView>(`${run(runId)}/cancel`, { method: "POST", body: { reason } }),
  reverse: (runId: string) => api<ErpChangeRunbookView>(`${run(runId)}/reverse`, { method: "POST", body: {} }),

  masterData: (runId: string, filter: ListFilter, cursor?: string, signal?: AbortSignal) =>
    api<MasterDataExceptionPage>(`${run(runId)}/master-data/exceptions${query({ ...filter, cursor, take: 12 })}`, { signal }),
  remap: (runId: string) => api<unknown>(`${run(runId)}/master-data/remap`, { method: "POST" }),
  chooseCandidate: (runId: string, exceptionId: string, moduleId: string, note?: string) =>
    api<MasterDataExceptionRow>(`${run(runId)}/master-data/exceptions/${exceptionId}/choose-candidate`, { method: "POST", body: { moduleId, note } }),
  mapByHand: (runId: string, exceptionId: string, moduleId: string, note?: string) =>
    api<MasterDataExceptionRow>(`${run(runId)}/master-data/exceptions/${exceptionId}/map`, { method: "POST", body: { moduleId, note } }),
  notNeeded: (runId: string, exceptionId: string, note: string) =>
    api<MasterDataExceptionRow>(`${run(runId)}/master-data/exceptions/${exceptionId}/not-needed`, { method: "POST", body: { note } }),

  stock: (runId: string, filter: StockFilter, cursor?: string, signal?: AbortSignal) =>
    api<StockDifferencePage>(`${run(runId)}/stock/differences${query({ ...filter, cursor, take: 12 })}`, { signal }),
  reconcileStock: (runId: string) => api<ReconcileStockResult>(`${run(runId)}/stock/reconcile`, { method: "POST" }),
  resolveStock: (runId: string, differenceId: string, body: ResolveStockDifferenceRequest) =>
    api<StockDifferenceRow>(`${run(runId)}/stock/differences/${differenceId}/resolve`, { method: "POST", body }),
  signOffStock: (runId: string, note: string) => api<StockResidualSignoffView>(`${run(runId)}/stock/sign-off`, { method: "POST", body: { note } }),

  divergences: (runId: string, filter: ListFilter, cursor?: string, signal?: AbortSignal) =>
    api<ErpChangeDivergencePage>(`${run(runId)}/dual-run/divergences${query({ ...filter, cursor, take: 12 })}`, { signal }),
  startDualRun: (runId: string) => api<unknown>(`${run(runId)}/dual-run/start`, { method: "POST" }),
  dailyCheck: (runId: string) => api<ErpChangeDailyCheckView>(`${run(runId)}/dual-run/check`, { method: "POST" }),
  acceptSource: (runId: string, kind: string, divergenceId: string) =>
    api<ErpChangeDivergenceRow>(`${run(runId)}/dual-run/${kind}-divergences/${divergenceId}/accept-source`, { method: "POST" }),
  explain: (runId: string, kind: string, divergenceId: string, note: string) =>
    api<ErpChangeDivergenceRow>(`${run(runId)}/dual-run/${kind}-divergences/${divergenceId}/explain`, { method: "POST", body: { note } }),
  cleanPeriod: (signal?: AbortSignal) => api<ErpChangeCleanPeriodView>(`${root}/settings/clean-period`, { signal }),
  saveCleanPeriod: (runId: string, days: number, expectedVersion: string) =>
    api<ErpChangeCleanPeriodView>(`${run(runId)}/settings/clean-period`, { method: "PUT", body: { days, expectedVersion } }),

  cutover: (runId: string, signal?: AbortSignal) => api<CutoverChecklistView>(`${run(runId)}/cutover`, { signal }),
  beginCutover: (runId: string, targetOutboundMode: "reference_only" | "documents") =>
    api<CutoverChecklistView>(`${run(runId)}/cutover/start`, { method: "POST", body: { targetOutboundMode } }),
  recordValidation: (runId: string, commandType: string, method: "test_company" | "manual", evidence: string) =>
    api<CutoverChecklistView>(`${run(runId)}/cutover/validations`, { method: "POST", body: { commandType, method, evidence, passed: true } }),
  drain: (runId: string) => api<CutoverChecklistView>(`${run(runId)}/cutover/drain`, { method: "POST" }),
  enableTarget: (runId: string) => api<CutoverChecklistView>(`${run(runId)}/cutover/enable-target`, { method: "POST" }),
  confirmFirstOutbound: (runId: string, commandType: string, externalDocumentRef: string, evidence: string) =>
    api<CutoverChecklistView>(`${run(runId)}/cutover/confirm-first-outbound`, { method: "POST", body: { commandType, externalDocumentRef, evidence } }),
  reconcileAfterCutover: (runId: string) => api<CutoverChecklistView>(`${run(runId)}/cutover/reconcile`, { method: "POST" }),
  pendingActionsCsvUrl: (runId: string) => `${run(runId)}/cutover/pending-actions.csv`,
  archive: (runId: string) => api<unknown>(`${run(runId)}/archive`, { method: "POST" }),
};

const signIn = (connectionId: string) => `/api/integration/connections/${connectionId}/cx04`;

export const signInApi = {
  read: (connectionId: string, cursor?: string, signal?: AbortSignal) =>
    api<ConnectionAuthorisationView>(`${signIn(connectionId)}/authorisation${query({ cursor, take: 12 })}`, { signal }),
  begin: (connectionId: string) => api<ConsentStart>(`${signIn(connectionId)}/consent`, { method: "POST" }),
  complete: (connectionId: string, code: string, state: string) =>
    api<ConsentResult>(`${signIn(connectionId)}/callback`, { method: "POST", body: { code, state } }),
  disable: (connectionId: string, reason: string) =>
    api<DisableResult>(`${signIn(connectionId)}/disable`, { method: "POST", body: { reason, confirm: true } }),
  reminders: (connectionId: string, signal?: AbortSignal) => api<ReminderSettingsView>(`${signIn(connectionId)}/reminder-settings`, { signal }),
  saveReminders: (connectionId: string, daysBeforeExpiry: number[], recipientRoles: string[], expectedVersion: string) =>
    api<ReminderSettingsView>(`${signIn(connectionId)}/reminder-settings`, { method: "PUT", body: { daysBeforeExpiry, recipientRoles, expectedVersion } }),
};

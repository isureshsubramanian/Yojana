import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import { connectionAuthorisationView, consentResult, consentStart, cutoverChecklistView, disableResult, erpChangeCleanPeriodView, erpChangeDailyCheckView, erpChangeDivergencePage, erpChangeDivergenceRow, erpChangeMyWorkView, erpChangeNotificationSettingsView, erpChangeRunHistoryPage, erpChangeRunbookView, masterDataExceptionPage, masterDataExceptionRow, parkedOutboundPage, reconcileStockResult, reminderSettingsView, stockDifferencePage, stockDifferenceRow, stockResidualSignoffView, unitHistoryChangePage, type ResolveStockDifferenceRequest, type ErpChangeNotificationSettingsView, type UnitHistoryChangeKind } from "./erpChangeTypes.ts";

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
  active: (signal?: AbortSignal) => apiParsed(`${root}/runs/active`, erpChangeRunbookView, { signal }),
  history: (cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/runs${query({ cursor, take: 12 })}`, erpChangeRunHistoryPage, { signal }),
  detail: (runId: string, signal?: AbortSignal) => apiParsed(run(runId), erpChangeRunbookView, { signal }),
  unitHistoryChanges: (runId: string, kind: UnitHistoryChangeKind, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${run(runId)}/unit-history-changes${query({ kind, cursor, take: 12 })}`, unitHistoryChangePage, { signal }),
  start: (sourceConnectionId: string, newConnection: unknown) =>
    apiParsed(`${root}/runs`, erpChangeRunbookView, { method: "POST", body: { sourceConnectionId, newConnection } }),
  check: (runId: string) => apiParsed(`${run(runId)}/check`, erpChangeRunbookView, { method: "POST" }),
  advance: (runId: string) => apiParsed(`${run(runId)}/advance`, erpChangeRunbookView, { method: "POST" }),
  cancel: (runId: string, reason: string) => apiParsed(`${run(runId)}/cancel`, erpChangeRunbookView, { method: "POST", body: { reason } }),
  reverse: (runId: string, newConnection?: unknown) => apiParsed(`${run(runId)}/reverse`, erpChangeRunbookView, { method: "POST", body: newConnection === undefined ? {} : { newConnection } }),

  masterData: (runId: string, filter: ListFilter, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${run(runId)}/master-data/exceptions${query({ ...filter, cursor, take: 12 })}`, masterDataExceptionPage, { signal }),
  remap: (runId: string) => apiParsed(`${run(runId)}/master-data/remap`, z.unknown(), { method: "POST" }),
  chooseCandidate: (runId: string, exceptionId: string, moduleId: string, note?: string) =>
    apiParsed(`${run(runId)}/master-data/exceptions/${exceptionId}/choose-candidate`, masterDataExceptionRow, { method: "POST", body: { moduleId, note } }),
  mapByHand: (runId: string, exceptionId: string, moduleId: string, note?: string) =>
    apiParsed(`${run(runId)}/master-data/exceptions/${exceptionId}/map`, masterDataExceptionRow, { method: "POST", body: { moduleId, note } }),
  notNeeded: (runId: string, exceptionId: string, note: string) =>
    apiParsed(`${run(runId)}/master-data/exceptions/${exceptionId}/not-needed`, masterDataExceptionRow, { method: "POST", body: { note } }),

  stock: (runId: string, filter: StockFilter, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${run(runId)}/stock/differences${query({ ...filter, cursor, take: 12 })}`, stockDifferencePage, { signal }),
  reconcileStock: (runId: string) => apiParsed(`${run(runId)}/stock/reconcile`, reconcileStockResult, { method: "POST" }),
  resolveStock: (runId: string, differenceId: string, body: ResolveStockDifferenceRequest) =>
    apiParsed(`${run(runId)}/stock/differences/${differenceId}/resolve`, stockDifferenceRow, { method: "POST", body }),
  signOffStock: (runId: string, note: string) => apiParsed(`${run(runId)}/stock/sign-off`, stockResidualSignoffView, { method: "POST", body: { note } }),

  divergences: (runId: string, filter: ListFilter, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${run(runId)}/dual-run/divergences${query({ ...filter, cursor, take: 12 })}`, erpChangeDivergencePage, { signal }),
  startDualRun: (runId: string) => apiParsed(`${run(runId)}/dual-run/start`, z.unknown(), { method: "POST" }),
  dailyCheck: (runId: string) => apiParsed(`${run(runId)}/dual-run/check`, erpChangeDailyCheckView, { method: "POST" }),
  acceptSource: (runId: string, kind: string, divergenceId: string) =>
    apiParsed(`${run(runId)}/dual-run/${kind}-divergences/${divergenceId}/accept-source`, erpChangeDivergenceRow, { method: "POST", body: { commandId: crypto.randomUUID() } }),
  explain: (runId: string, kind: string, divergenceId: string, note: string) =>
    apiParsed(`${run(runId)}/dual-run/${kind}-divergences/${divergenceId}/explain`, erpChangeDivergenceRow, { method: "POST", body: { note } }),
  cleanPeriod: (signal?: AbortSignal) => apiParsed(`${root}/settings/clean-period`, erpChangeCleanPeriodView, { signal }),
  saveCleanPeriod: (runId: string, days: number, expectedVersion: string) =>
    apiParsed(`${run(runId)}/settings/clean-period`, erpChangeCleanPeriodView, { method: "PUT", body: { days, expectedVersion } }),
  myWork: (signal?: AbortSignal) => apiParsed(`${root}/my-work`, erpChangeMyWorkView, { signal }),
  notificationSettings: (signal?: AbortSignal) => apiParsed(`${root}/settings/notifications`, erpChangeNotificationSettingsView, { signal }),
  saveNotificationSettings: (value: ErpChangeNotificationSettingsView) => apiParsed(`${root}/settings/notifications`, erpChangeNotificationSettingsView, {
    method: "PUT", body: {
      digestEnabled: value.digestEnabled, digestHour: value.digestHour,
      masterDataRecipientRoles: value.masterDataRecipientRoles, stockRecipientRoles: value.stockRecipientRoles,
      expectedVersion: value.settingsVersion,
    },
  }),

  cutover: (runId: string, signal?: AbortSignal) => apiParsed(`${run(runId)}/cutover`, cutoverChecklistView, { signal }),
  parked: (runId: string, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${run(runId)}/cutover/parked${query({ cursor, take: 12 })}`, parkedOutboundPage, { signal }),
  beginCutover: (runId: string, targetOutboundMode: "reference_only" | "documents") =>
    apiParsed(`${run(runId)}/cutover/start`, cutoverChecklistView, { method: "POST", body: { targetOutboundMode } }),
  recordValidation: (runId: string, commandType: string, method: "test_company" | "manual", evidence: string) =>
    apiParsed(`${run(runId)}/cutover/validations`, cutoverChecklistView, { method: "POST", body: { commandType, method, evidence, passed: true } }),
  drain: (runId: string) => apiParsed(`${run(runId)}/cutover/drain`, cutoverChecklistView, { method: "POST" }),
  convertParked: (runId: string, outboundCommandId: string, reason: string) =>
    apiParsed(`${run(runId)}/cutover/parked-to-manual`, cutoverChecklistView, { method: "POST", body: { outboundCommandId, reason } }),
  enableTarget: (runId: string) => apiParsed(`${run(runId)}/cutover/enable-target`, cutoverChecklistView, { method: "POST" }),
  confirmFirstOutbound: (runId: string, commandType: string, externalDocumentRef: string, evidence: string) =>
    apiParsed(`${run(runId)}/cutover/confirm-first-outbound`, cutoverChecklistView, { method: "POST", body: { commandType, externalDocumentRef, evidence } }),
  reconcileAfterCutover: (runId: string) => apiParsed(`${run(runId)}/cutover/reconcile`, cutoverChecklistView, { method: "POST" }),
  pendingActionsCsvUrl: (runId: string) => `${run(runId)}/cutover/pending-actions.csv`,
  archive: (runId: string) => apiParsed(`${run(runId)}/archive`, z.unknown(), { method: "POST" }),
};

const signIn = (connectionId: string) => `/api/integration/connections/${connectionId}/cx04`;

export const signInApi = {
  read: (connectionId: string, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${signIn(connectionId)}/authorisation${query({ cursor, take: 12 })}`, connectionAuthorisationView, { signal }),
  begin: (connectionId: string) => apiParsed(`${signIn(connectionId)}/consent`, consentStart, { method: "POST" }),
  complete: (connectionId: string, code: string, state: string) =>
    apiParsed(`${signIn(connectionId)}/callback`, consentResult, { method: "POST", body: { code, state } }),
  disable: (connectionId: string, reason: string) =>
    apiParsed(`${signIn(connectionId)}/disable`, disableResult, { method: "POST", body: { reason, confirm: true } }),
  reminders: (connectionId: string, signal?: AbortSignal) => apiParsed(`${signIn(connectionId)}/reminder-settings`, reminderSettingsView, { signal }),
  saveReminders: (connectionId: string, daysBeforeExpiry: number[], recipientRoles: string[], expectedVersion: string) =>
    apiParsed(`${signIn(connectionId)}/reminder-settings`, reminderSettingsView, { method: "PUT", body: { daysBeforeExpiry, recipientRoles, expectedVersion } }),
};

import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import type { GridSort } from "../components";
import { approvalView, archiveResult, backfill, type Backfill, checklist, coexistenceSettings, type CoexistenceSettings, connectionDetail, type ConnectionEvent, connectionEvent, connectionPage, cursorPage, type EntityDay, entityDay, fieldMap, type FieldSource, fullHistoryEstimate, healthDetail, type LocationCheck, locationCheck, type RegistrationConnector, registrationConnector, type Relay, relay, relayIssued, type Run, run, serviceCertificate, syncControlResult, type SyncStart, tenantStatusPage, transportSettings, warrantyContractSettings, type WarrantyContractSettings } from "./adminTypes.ts";
const root = "/api/integration";
function query(values: Record<string, string | number | boolean | undefined>) { const q = new URLSearchParams(); for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") q.set(key, String(value)); return `?${q}`; }
const connection = (id: string) => `${root}/connections/${id}`;
type GridRequest = { sort: GridSort; pageSize: number };
export const adminApi = {
  connections: (cursor: string | undefined, archived: boolean, signal: AbortSignal | undefined, grid: GridRequest) => apiParsed(`${root}/connections${query({ cursor, archived, sort: grid.sort.key, dir: grid.sort.dir, take: grid.pageSize })}`, connectionPage, { signal }),
  connectionChoices: (cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/connections${query({ cursor, archived: false, take: 12 })}`, connectionPage, { signal }), // grid-gate: picker
  status: (cursor: string | undefined, signal: AbortSignal | undefined, grid: GridRequest) => apiParsed(`${root}/status${query({ cursor, sort: grid.sort.key, dir: grid.sort.dir, take: grid.pageSize })}`, tenantStatusPage, { signal }),
  registrationConnectors: (cursor: string | undefined, signal: AbortSignal | undefined, grid: GridRequest) => apiParsed(`${root}/connections/registration-connectors${query({ cursor, sort: grid.sort.key, dir: grid.sort.dir, take: grid.pageSize })}`, cursorPage(registrationConnector), { signal }),
  register: (body: unknown) => apiParsed(`${root}/connections`, z.object({ connectionId: z.string(), healthStatus: z.string() }), { method: "POST", body }),
  previewCertificate: (serviceUri: string) => apiParsed(`${root}/connections/fetch-certificate`, serviceCertificate, { method: "POST", body: { serviceUri } }),
  detail: (id: string, signal?: AbortSignal) => apiParsed(connection(id), connectionDetail, { signal }),
  smoke: (id: string) => apiParsed(`${connection(id)}/smoke-read`, z.unknown(), { method: "POST" }),
  saveOnboarding: (id: string, syncStart: SyncStart, serviceCertificateSha256: string | null) => apiParsed(`${connection(id)}/onboarding`, connectionDetail, { method: "PUT", body: { syncStart, serviceCertificateSha256 } }),
  fullHistoryEstimate: (id: string) => apiParsed(`${connection(id)}/full-history-estimate`, fullHistoryEstimate),
  fetchCertificate: (id: string) => apiParsed(`${connection(id)}/fetch-certificate`, serviceCertificate, { method: "POST" }),
  rebaseline: (id: string, entities: string[] | null, to: string, reason: string) => apiParsed(`${connection(id)}/rebaseline`, z.unknown(), { method: "POST", body: { entities, to, reason } }),
  archive: (id: string, force = false, reason?: string) => apiParsed(`${connection(id)}/archive`, archiveResult, { method: "POST", body: { force, reason } }),
  stopSync: (id: string, reason: string) => apiParsed(`${connection(id)}/stop-sync`, syncControlResult, { method: "POST", body: { reason } }),
  resumeSync: (id: string, note?: string) => apiParsed(`${connection(id)}/resume-sync`, syncControlResult, { method: "POST", body: { note: note?.trim() || null } }),
  health: (id: string, signal?: AbortSignal) => apiParsed(`${connection(id)}/health`, healthDetail, { signal }),
  events: (id: string, cursor: string | undefined, signal: AbortSignal | undefined, grid: GridRequest) => apiParsed(`${connection(id)}/events${query({ cursor, sort: grid.sort.key, dir: grid.sort.dir, take: grid.pageSize })}`, cursorPage(connectionEvent), { signal }),
  fieldMap: (id: string) => apiParsed(`${connection(id)}/cx01/field-map`, fieldMap),
  saveFieldMap: (id: string, fields: Record<string, FieldSource | null>) => apiParsed(`${connection(id)}/cx01/field-map`, fieldMap, { method: "PUT", body: { fields } }),
  approvals: (id: string) => apiParsed(`${connection(id)}/cx01/approval-handling`, approvalView),
  saveApprovals: (id: string, documentTypes: Record<string, { mode: string; evidence: string } | null>) => apiParsed(`${connection(id)}/cx01/approval-handling`, approvalView, { method: "PUT", body: { documentTypes } }),
  checklist: (id: string) => apiParsed(`${connection(id)}/onboarding-checklist`, checklist),
  confirm: (id: string, code: string, evidence: string) => apiParsed(`${connection(id)}/onboarding-checklist/${encodeURIComponent(code)}/confirm`, z.unknown(), { method: "POST", body: { evidence } }),
  warrantyContracts: () => apiParsed(`${root}/settings/warranty-contracts`, warrantyContractSettings),
  saveWarrantyContracts: (id: string, maintainedIn: WarrantyContractSettings["maintainedIn"], expectedVersion: string) => apiParsed(`${connection(id)}/warranty-contracts`, warrantyContractSettings, { method: "PUT", body: { maintainedIn, expectedVersion } }),
  coexistence: (id: string, signal?: AbortSignal) => apiParsed(`${connection(id)}/coexistence`, coexistenceSettings, { signal }),
  saveCoexistence: (id: string, value: CoexistenceSettings, nativeWarrantyObjectMode: CoexistenceSettings["nativeWarrantyObjectMode"]) => apiParsed(`${connection(id)}/coexistence`, coexistenceSettings, { method: "PUT", body: { nativeWarrantyObjectMode, watchedFields: value.watchedFields, explainingFields: value.explainingFields, driftScanEnabled: value.driftScanEnabled, pageSize: value.pageSize, maxPagesPerRun: value.maxPagesPerRun } }),
  relays: (id: string, cursor: string | undefined, signal: AbortSignal | undefined, grid: GridRequest) => apiParsed(`${connection(id)}/relays${query({ cursor, sort: grid.sort.key, dir: grid.sort.dir, take: grid.pageSize })}`, cursorPage(relay), { signal }),
  registerRelay: (id: string, displayName: string, productUrl: string) => apiParsed(`${connection(id)}/relays`, relayIssued, { method: "POST", body: { displayName, productUrl } }),
  revokeRelay: (id: string, relayId: string) => apiParsed(`${connection(id)}/relays/${relayId}/revoke`, z.unknown(), { method: "POST" }),
  transport: () => apiParsed(`${root}/settings/transport`, transportSettings),
  saveTransport: (body: { mode: string; credentialReference: string | null; expectedVersion: string }) => apiParsed(`${root}/settings/transport`, transportSettings, { method: "PUT", body }),
  transportCredential: (connectionString: string, rotationDueAt: string) => apiParsed(`${root}/settings/transport/credentials`, z.object({ credentialReference: z.string() }), { method: "POST", body: { connectionString, rotationDueAt } }),
  runs: (connectionId: string, status: string, cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/reconciliation/runs${query({ connectionId, status, cursor, take: 12 })}`, cursorPage(run), { signal }),
  run: (connectionId: string, rangeStart: string, rangeEnd: string) => apiParsed(`${root}/reconciliation/runs`, z.object({ runId: z.string() }), { method: "POST", body: { connectionId, rangeStart, rangeEnd } }),
  locationCheck: (connectionId: string) => apiParsed(`${root}/reconciliation/location-checks`, z.unknown(), { method: "POST", body: { connectionId } }),
  entityDays: (runId: string, cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/reconciliation/runs/${runId}/entity-days${query({ cursor, take: 12 })}`, cursorPage(entityDay), { signal }),
  locations: (runId: string, differences: boolean, cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/reconciliation/runs/${runId}/locations${query({ cursor, take: 12, differences })}`, cursorPage(locationCheck), { signal }),
  backfills: (connectionId: string, status: string, cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/backfills${query({ connectionId, status, cursor, take: 12 })}`, cursorPage(backfill), { signal }),
  backfill: (id: string, signal?: AbortSignal) => apiParsed(`${root}/backfills/${id}`, backfill, { signal }),
  startBackfill: (connectionId: string, entity: string, rangeStart: string, rangeEnd: string) => apiParsed(`${root}/backfills`, backfill, { method: "POST", body: { connectionId, entity, rangeStart, rangeEnd, pageSize: 100 } }),
  changeBackfill: (id: string, action: "pause" | "resume" | "cancel") => apiParsed(`${root}/backfills/${id}/${action}`, backfill, { method: "POST" }),
};

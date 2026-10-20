import { api } from "../api/client";
import type { ApprovalView, ArchiveResult, Backfill, Checklist, ConnectionDetail, ConnectionEvent, ConnectionPage, CursorPage, EntityDay, FieldMap, FieldSource, HealthDetail, LocationCheck, RegistrationConnector, Relay, RelayIssued, Run, TenantStatusPage, TransportSettings, WarrantyContractSettings } from "./adminTypes";
const root = "/api/integration";
function query(values: Record<string, string | number | boolean | undefined>) { const q = new URLSearchParams(); for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") q.set(key, String(value)); return `?${q}`; }
const connection = (id: string) => `${root}/connections/${id}`;
export const adminApi = {
  connections: (cursor?: string, archived = false, signal?: AbortSignal) => api<ConnectionPage>(`${root}/connections${query({ cursor, archived, take: 12 })}`, { signal }),
  status: (cursor?: string, signal?: AbortSignal) => api<TenantStatusPage>(`${root}/status${query({ cursor, take: 12 })}`, { signal }),
  registrationConnectors: (cursor?: string, signal?: AbortSignal) => api<CursorPage<RegistrationConnector>>(`${root}/connections/registration-connectors${query({ cursor, take: 12 })}`, { signal }),
  register: (body: unknown) => api<{ connectionId: string; healthStatus: string }>(`${root}/connections`, { method: "POST", body }),
  detail: (id: string, signal?: AbortSignal) => api<ConnectionDetail>(connection(id), { signal }),
  smoke: (id: string) => api(`${connection(id)}/smoke-read`, { method: "POST" }),
  archive: (id: string, force = false, reason?: string) => api<ArchiveResult>(`${connection(id)}/archive`, { method: "POST", body: { force, reason } }),
  health: (id: string, signal?: AbortSignal) => api<HealthDetail>(`${connection(id)}/health`, { signal }),
  events: (id: string, cursor?: string, signal?: AbortSignal) => api<CursorPage<ConnectionEvent>>(`${connection(id)}/events${query({ cursor, take: 12 })}`, { signal }),
  fieldMap: (id: string) => api<FieldMap>(`${connection(id)}/cx01/field-map`),
  saveFieldMap: (id: string, fields: Record<string, FieldSource | null>) => api<FieldMap>(`${connection(id)}/cx01/field-map`, { method: "PUT", body: { fields } }),
  approvals: (id: string) => api<ApprovalView>(`${connection(id)}/cx01/approval-handling`),
  saveApprovals: (id: string, documentTypes: Record<string, { mode: string; evidence: string } | null>) => api<ApprovalView>(`${connection(id)}/cx01/approval-handling`, { method: "PUT", body: { documentTypes } }),
  checklist: (id: string) => api<Checklist>(`${connection(id)}/onboarding-checklist`),
  confirm: (id: string, code: string, evidence: string) => api(`${connection(id)}/onboarding-checklist/${encodeURIComponent(code)}/confirm`, { method: "POST", body: { evidence } }),
  warrantyContracts: () => api<WarrantyContractSettings>(`${root}/settings/warranty-contracts`),
  saveWarrantyContracts: (id: string, maintainedIn: WarrantyContractSettings["maintainedIn"], expectedVersion: string) => api<WarrantyContractSettings>(`${connection(id)}/warranty-contracts`, { method: "PUT", body: { maintainedIn, expectedVersion } }),
  relays: (id: string, cursor?: string, signal?: AbortSignal) => api<CursorPage<Relay>>(`${connection(id)}/relays${query({ cursor, take: 12 })}`, { signal }),
  registerRelay: (id: string, displayName: string, productUrl: string) => api<RelayIssued>(`${connection(id)}/relays`, { method: "POST", body: { displayName, productUrl } }),
  revokeRelay: (id: string, relayId: string) => api(`${connection(id)}/relays/${relayId}/revoke`, { method: "POST" }),
  transport: () => api<TransportSettings>(`${root}/settings/transport`),
  saveTransport: (body: { mode: string; credentialReference: string | null; expectedVersion: string }) => api<TransportSettings>(`${root}/settings/transport`, { method: "PUT", body }),
  transportCredential: (connectionString: string, rotationDueAt: string) => api<{ credentialReference: string }>(`${root}/settings/transport/credentials`, { method: "POST", body: { connectionString, rotationDueAt } }),
  runs: (connectionId: string, status: string, cursor?: string, signal?: AbortSignal) => api<CursorPage<Run>>(`${root}/reconciliation/runs${query({ connectionId, status, cursor, take: 12 })}`, { signal }),
  run: (connectionId: string, rangeStart: string, rangeEnd: string) => api<{ runId: string }>(`${root}/reconciliation/runs`, { method: "POST", body: { connectionId, rangeStart, rangeEnd } }),
  locationCheck: (connectionId: string) => api(`${root}/reconciliation/location-checks`, { method: "POST", body: { connectionId } }),
  entityDays: (runId: string, cursor?: string, signal?: AbortSignal) => api<CursorPage<EntityDay>>(`${root}/reconciliation/runs/${runId}/entity-days${query({ cursor, take: 12 })}`, { signal }),
  locations: (runId: string, differences: boolean, cursor?: string, signal?: AbortSignal) => api<CursorPage<LocationCheck>>(`${root}/reconciliation/runs/${runId}/locations${query({ cursor, take: 12, differences })}`, { signal }),
  backfills: (connectionId: string, status: string, cursor?: string, signal?: AbortSignal) => api<CursorPage<Backfill>>(`${root}/backfills${query({ connectionId, status, cursor, take: 12 })}`, { signal }),
  backfill: (id: string, signal?: AbortSignal) => api<Backfill>(`${root}/backfills/${id}`, { signal }),
  startBackfill: (connectionId: string, entity: string, rangeStart: string, rangeEnd: string) => api<Backfill>(`${root}/backfills`, { method: "POST", body: { connectionId, entity, rangeStart, rangeEnd, pageSize: 100 } }),
  changeBackfill: (id: string, action: "pause" | "resume" | "cancel") => api<Backfill>(`${root}/backfills/${id}/${action}`, { method: "POST" }),
};

import { api } from "../api/client";
import { DEFAULT_LEGAL_ENTITY, newCommandId, now } from "../api/units";
import type { CompareResult, DisclosurePolicy, ImportJob, ImportPage, ImportRowPage, ImportRunResult, InstalledBasePage, InternalLookup, PortalContext, PortalLookup, PortalUnit, PublicLookup, ReconciliationPage, ReferenceOption, SerialListResult, ServiceHistoryPage, CertificatePage, WhereUsedResult, XrefPage, PortalRegistrationResult, PortalRmaResult } from "./types";

export function query(values: Record<string, string | number | boolean | null | undefined>): string { const params = new URLSearchParams(); for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value)); const text = params.toString(); return text ? `?${text}` : ""; }
export function exportUrl(path: string, format: "csv" | "xlsx"): string { return `${path}${path.includes("?") ? "&" : "?"}format=${format}`; }
export async function fileBytes(file: File): Promise<string> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 32_768)
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 32_768));
  return btoa(binary);
}

export const p7Api = {
  imports: (cursor?: string, signal?: AbortSignal) => api<ImportPage>(`/api/imports/${query({ cursor, take: 50 })}`, { signal }),
  importJob: (id: string, signal?: AbortSignal) => api<ImportJob>(`/api/imports/${id}`, { signal }),
  importRows: (id: string, cursor?: string, signal?: AbortSignal) => api<ImportRowPage>(`/api/imports/${id}/rows${query({ cursor, take: 100 })}`, { signal }),
  runImport: async (file: File, mode: "apply" | "compare" = "apply") => api<ImportRunResult>("/api/imports/run", { method: "POST", body: { commandId: newCommandId(), source: "user_upload", file: { fileName: file.name, format: file.name.split(".").pop()?.toLowerCase(), content: await fileBytes(file) }, eventTime: now(), legalEntityId: DEFAULT_LEGAL_ENTITY, mapping: { serial: "Serial", orderNumber: "Order" }, mappingName: "standard_serial_list", mode } }),
  compare: (jobAId: string, jobBId: string) => api<CompareResult>("/api/imports/compare", { method: "POST", body: { commandId: newCommandId(), jobAId, jobBId, selectionId: null } }),
  review: (jobId: string, overrides: { rowResultId: string; action: string }[]) => api(`/api/imports/${jobId}/review`, { method: "POST", body: { commandId: newCommandId(), jobId, overrides, eventTime: now(), legalEntityId: DEFAULT_LEGAL_ENTITY } }),
  references: (kind: "party" | "document", text: string, type?: string, signal?: AbortSignal) => api<{ results: ReferenceOption[] }>(`/api/serialisation/reference-search${query({ kind, type, q: text, take: 20 })}`, { signal }),
  reconcile: (partyRef: string, importJobId: string) => api<ReconciliationPage>("/api/customer-reconciliations", { method: "POST", body: { commandId: newCommandId(), partyRef, source: { importJobId }, assertedAt: now(), take: 50, after: null } }),
  reconciliation: (id: string, after?: string, signal?: AbortSignal) => api<ReconciliationPage>(`/api/customer-reconciliations/${id}${query({ after, take: 50 })}`, { signal }),
  verifyAssertion: (id: string, evidenceRef: string, evidenceDate: string) => api(`/api/customer-reconciliations/assertions/${id}/verify`, { method: "POST", body: { commandId: newCommandId(), evidenceRef, evidenceDate, legalEntityId: DEFAULT_LEGAL_ENTITY } }),
  rejectAssertion: (id: string, reason: string) => api(`/api/customer-reconciliations/assertions/${id}/reject`, { method: "POST", body: { commandId: newCommandId(), reason } }),
  publicLookup: (tenant: string, identifier: string, type?: string, signal?: AbortSignal) => api<PublicLookup>(`/api/public/portal/${encodeURIComponent(tenant)}/resolve${query({ identifier, type })}`, { signal }),
  portalContext: (signal?: AbortSignal) => api<PortalContext>("/api/portal/context", { signal }),
  portalLookup: (q: string, signal?: AbortSignal) => api<PortalLookup>(`/api/portal/lookup${query({ q })}`, { signal }),
  portalUnit: (unitId: string, signal?: AbortSignal) => api<PortalUnit>(`/api/portal/units/${unitId}`, { signal }),
  installedBase: (includeComponents: boolean, afterDeliveryDate?: string, afterUnitId?: string, signal?: AbortSignal) => api<InstalledBasePage>(`/api/portal/installed-base${query({ includeComponents, take: 50, afterDeliveryDate, afterUnitId })}`, { signal }),
  serviceHistory: (unitId: string, signal?: AbortSignal) => api<ServiceHistoryPage>(`/api/portal/units/${unitId}/service-history?take=50`, { signal }),
  certificates: (unitId: string, signal?: AbortSignal) => api<CertificatePage>(`/api/portal/units/${unitId}/certificates?take=50`, { signal }),
  registerUnit: async (unitId: string, evidenceAt: string, evidenceReference: string, attachment: File | null) => api<PortalRegistrationResult>(`/api/portal/units/${unitId}/registrations`, { method: "POST", body: { commandId: newCommandId(), evidenceKind: "installation", evidenceAt: new Date(`${evidenceAt}T12:00:00Z`).toISOString(), evidenceReference: evidenceReference.trim() || null, attachment: attachment ? { fileName: attachment.name, contentType: attachment.type || "application/octet-stream", content: await fileBytes(attachment) } : null } }),
  requestRma: (unitId: string, requestDate: string, serviceCountry: string, coverageFamily: string, description: string) => api<PortalRmaResult>(`/api/portal/units/${unitId}/rmas`, { method: "POST", body: { commandId: newCommandId(), requestDate, serviceCountry, coverageFamily, claimLineKind: "parts", description } }),
  whereUsed: (value: string) => api<WhereUsedResult>("/api/portal/where-used", { method: "POST", body: { value, identifierType: null } }),
  serialList: (values: string[]) => api<SerialListResult>("/api/portal/serial-list", { method: "POST", body: { serials: values.map((value) => ({ value, identifierType: null })) } }),
  internalLookup: (identifier: string, signal?: AbortSignal) => api<InternalLookup>(`/api/reports/internal-lookup${query({ identifier })}`, { signal }),
  selectionFromSerials: (serials: string[]) => api<{ selectionId: string; resolved: number; unknown: string[]; rejected: string[] }>("/api/selections", { method: "POST", body: { commandId: newCommandId(), source: { kind: "pasted_list", serials } } }),
  xref: (selectionId: string, after?: string, signal?: AbortSignal) => api<XrefPage>(`/api/reports/identifier-cross-reference${query({ selectionId, after, take: 100 })}`, { signal }),
  disclosure: (signal?: AbortSignal) => api<DisclosurePolicy>("/api/admin/portal/disclosure-policy", { signal }),
  saveDisclosure: (fields: DisclosurePolicy["fields"]) => api<DisclosurePolicy>("/api/admin/portal/disclosure-policy/fields", { method: "PUT", body: { fields } }),
  setPublic: (enabled: boolean, expectedVersion: string) => api<DisclosurePolicy>("/api/admin/portal/disclosure-policy/public-enabled", { method: "PUT", body: { enabled, expectedVersion } }),
};

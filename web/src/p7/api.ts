import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import { newCommandId, now } from "../api/units.ts";
import {
  certificatePageSchema, compareResultSchema, comparisonHistoryPageSchema, disclosurePolicySchema, documentRefSchema,
  disclosureRowSchema, feedMessageHistoryPageSchema, importFileHistoryPageSchema, importJobSchema, importPageSchema,
  importRowPageSchema, importRunResultSchema, installedBasePageSchema, internalLookupSchema, portalContextSchema,
  portalLookupSchema, portalRegistrationResultSchema, portalRmaResultSchema, portalUnitSchema, publicLookupSchema,
  reconciliationPageSchema, referenceOptionSchema, serialListResultSchema, serviceHistoryPageSchema,
  whereUsedResultSchema, xrefPageSchema, type DisclosurePolicy,
} from "./types.ts";

const text = z.string();
const nullableText = z.nullable(text);
const runImportRequestSchema = z.object({ commandId: text, source: z.literal("user_upload"), file: z.object({ fileName: text, format: text, content: text }), eventTime: text, mapping: z.object({ serial: text, orderNumber: text }), mappingName: text, mode: z.enum(["apply", "compare"]) });
const compareRequestSchema = z.object({ commandId: text, jobAId: text, jobBId: text, selectionId: z.null() });
const reviewRequestSchema = z.object({ commandId: text, jobId: text, overrides: z.array(z.object({ rowResultId: text, action: text })), eventTime: text });
const reconciliationRequestSchema = z.object({ commandId: text, partyRef: text, source: z.object({ importJobId: text }), assertedAt: text, take: z.number(), after: z.null() });
const verifyAssertionRequestSchema = z.object({ commandId: text, evidenceRef: text, evidenceDate: text, unitAtCustomerSite: z.boolean() });
const rejectAssertionRequestSchema = z.object({ commandId: text, reason: text });
const registrationRequestSchema = z.object({ commandId: text, evidenceKind: z.literal("installation"), evidenceAt: text, evidenceReference: nullableText, attachment: z.nullable(z.object({ fileName: text, contentType: text, content: text })) });
const rmaRequestSchema = z.object({ commandId: text, requestDate: text, serviceCountry: text, coverageFamily: text, claimLineKind: z.literal("parts"), description: text });
const portalSerialInputSchema = z.object({ value: text, identifierType: z.null() });
const serialListRequestSchema = z.object({ serials: z.array(portalSerialInputSchema) });
const selectionSourceSchema = z.object({ kind: text, document: z.nullable(documentRefSchema), partyRef: nullableText, itemId: nullableText, from: nullableText, to: nullableText, eventType: nullableText, serials: z.nullable(z.array(text)), scheme: nullableText, rangeStart: nullableText, rangeEnd: nullableText, importJobId: nullableText });
const selectionRequestSchema = z.object({ commandId: text, source: z.object({ kind: z.literal("pasted_list"), serials: z.array(text) }) });
const selectionResultSchema = z.object({ selectionId: text, resolved: z.number(), unknown: z.array(text), rejected: z.array(text), source: selectionSourceSchema });
const disclosureFieldsRequestSchema = z.object({ fields: z.array(disclosureRowSchema) });
const publicSettingRequestSchema = z.object({ enabled: z.boolean(), expectedVersion: text });

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
  imports: (cursor?: string, signal?: AbortSignal) => apiParsed(`/api/imports/${query({ cursor, take: 50 })}`, importPageSchema, { signal }),
  importHistory: (filters: Record<string, string | undefined>, cursor?: string, signal?: AbortSignal) => apiParsed(`/api/imports/history${query({ ...filters, cursor, take: 50 })}`, importFileHistoryPageSchema, { signal }),
  feedMessageHistory: (filters: Record<string, string | undefined>, cursor?: string, signal?: AbortSignal) => apiParsed(`/api/imports/history/feed-messages${query({ ...filters, cursor, take: 50 })}`, feedMessageHistoryPageSchema, { signal }),
  comparisonHistory: (filters: Record<string, string | undefined>, cursor?: string, signal?: AbortSignal) => apiParsed(`/api/imports/history/comparisons${query({ ...filters, cursor, take: 50 })}`, comparisonHistoryPageSchema, { signal }),
  importJob: (id: string, signal?: AbortSignal) => apiParsed(`/api/imports/${id}`, importJobSchema, { signal }),
  importRows: (id: string, cursor?: string, signal?: AbortSignal) => apiParsed(`/api/imports/${id}/rows${query({ cursor, take: 100 })}`, importRowPageSchema, { signal }),
  runImport: async (file: File, mode: "apply" | "compare" = "apply") => { const body = { commandId: newCommandId(), source: "user_upload" as const, file: { fileName: file.name, format: file.name.split(".").pop()?.toLowerCase(), content: await fileBytes(file) }, eventTime: now(), mapping: { serial: "Serial", orderNumber: "Order" }, mappingName: "standard_serial_list", mode }; return apiParsed("/api/imports/run", importRunResultSchema, { method: "POST", body: z.parse(runImportRequestSchema, body) }); },
  compare: (jobAId: string, jobBId: string) => apiParsed("/api/imports/compare", compareResultSchema, { method: "POST", body: z.parse(compareRequestSchema, { commandId: newCommandId(), jobAId, jobBId, selectionId: null }) }),
  review: (jobId: string, overrides: { rowResultId: string; action: string }[]) => { const body = { commandId: newCommandId(), jobId, overrides, eventTime: now() }; return apiParsed(`/api/imports/${jobId}/review`, z.unknown(), { method: "POST", body: z.parse(reviewRequestSchema, body) }); },
  references: (kind: "party" | "document", text: string, type?: string, signal?: AbortSignal) => apiParsed(`/api/serialisation/reference-search${query({ kind, type, q: text, take: 20 })}`, z.object({ results: z.array(referenceOptionSchema) }), { signal }),
  reconcile: (partyRef: string, importJobId: string) => { const body = { commandId: newCommandId(), partyRef, source: { importJobId }, assertedAt: now(), take: 50, after: null }; return apiParsed("/api/customer-reconciliations", reconciliationPageSchema, { method: "POST", body: z.parse(reconciliationRequestSchema, body) }); },
  reconciliation: (id: string, after?: string, signal?: AbortSignal) => apiParsed(`/api/customer-reconciliations/${id}${query({ after, take: 50 })}`, reconciliationPageSchema, { signal }),
  verifyAssertion: (id: string, evidenceRef: string, evidenceDate: string, unitAtCustomerSite: boolean) => { const body = { commandId: newCommandId(), evidenceRef, evidenceDate, unitAtCustomerSite }; return apiParsed(`/api/customer-reconciliations/assertions/${id}/verify`, z.unknown(), { method: "POST", body: z.parse(verifyAssertionRequestSchema, body) }); },
  rejectAssertion: (id: string, reason: string) => apiParsed(`/api/customer-reconciliations/assertions/${id}/reject`, z.unknown(), { method: "POST", body: z.parse(rejectAssertionRequestSchema, { commandId: newCommandId(), reason }) }),
  publicLookup: (tenant: string, identifier: string, type?: string, signal?: AbortSignal) => apiParsed(`/api/public/portal/${encodeURIComponent(tenant)}/resolve${query({ identifier, type })}`, publicLookupSchema, { signal }),
  portalContext: (signal?: AbortSignal) => apiParsed("/api/portal/context", portalContextSchema, { signal }),
  portalLookup: (q: string, signal?: AbortSignal) => apiParsed(`/api/portal/lookup${query({ q })}`, portalLookupSchema, { signal }),
  portalUnit: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/portal/units/${unitId}`, portalUnitSchema, { signal }),
  installedBase: (includeComponents: boolean, afterDeliveryDate?: string, afterUnitId?: string, signal?: AbortSignal) => apiParsed(`/api/portal/installed-base${query({ includeComponents, take: 50, afterDeliveryDate, afterUnitId })}`, installedBasePageSchema, { signal }),
  serviceHistory: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/portal/units/${unitId}/service-history?take=50`, serviceHistoryPageSchema, { signal }),
  certificates: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/portal/units/${unitId}/certificates?take=50`, certificatePageSchema, { signal }),
  registerUnit: async (unitId: string, evidenceAt: string, evidenceReference: string, attachment: File | null) => { const body = { commandId: newCommandId(), evidenceKind: "installation" as const, evidenceAt: new Date(`${evidenceAt}T12:00:00Z`).toISOString(), evidenceReference: evidenceReference.trim() || null, attachment: attachment ? { fileName: attachment.name, contentType: attachment.type || "application/octet-stream", content: await fileBytes(attachment) } : null }; return apiParsed(`/api/portal/units/${unitId}/registrations`, portalRegistrationResultSchema, { method: "POST", body: z.parse(registrationRequestSchema, body) }); },
  requestRma: (unitId: string, requestDate: string, serviceCountry: string, coverageFamily: string, description: string) => { const body = { commandId: newCommandId(), requestDate, serviceCountry, coverageFamily, claimLineKind: "parts" as const, description }; return apiParsed(`/api/portal/units/${unitId}/rmas`, portalRmaResultSchema, { method: "POST", body: z.parse(rmaRequestSchema, body) }); },
  whereUsed: (value: string) => apiParsed("/api/portal/where-used", whereUsedResultSchema, { method: "POST", body: z.parse(portalSerialInputSchema, { value, identifierType: null }) }),
  serialList: (values: string[]) => apiParsed("/api/portal/serial-list", serialListResultSchema, { method: "POST", body: z.parse(serialListRequestSchema, { serials: values.map((value) => ({ value, identifierType: null })) }) }),
  internalLookup: (identifier: string, signal?: AbortSignal) => apiParsed(`/api/reports/internal-lookup${query({ identifier })}`, internalLookupSchema, { signal }),
  selectionFromSerials: (serials: string[]) => { const body = { commandId: newCommandId(), source: { kind: "pasted_list" as const, serials } }; return apiParsed("/api/selections", selectionResultSchema, { method: "POST", body: z.parse(selectionRequestSchema, body) }); },
  xref: (selectionId: string, after?: string, signal?: AbortSignal) => apiParsed(`/api/reports/identifier-cross-reference${query({ selectionId, after, take: 100 })}`, xrefPageSchema, { signal }),
  disclosure: (signal?: AbortSignal) => apiParsed("/api/admin/portal/disclosure-policy", disclosurePolicySchema, { signal }),
  saveDisclosure: (fields: DisclosurePolicy["fields"]) => apiParsed("/api/admin/portal/disclosure-policy/fields", disclosurePolicySchema, { method: "PUT", body: z.parse(disclosureFieldsRequestSchema, { fields }) }),
  setPublic: (enabled: boolean, expectedVersion: string) => apiParsed("/api/admin/portal/disclosure-policy/public-enabled", disclosurePolicySchema, { method: "PUT", body: z.parse(publicSettingRequestSchema, { enabled, expectedVersion }) }),
};

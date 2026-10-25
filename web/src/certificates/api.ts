import * as z from "zod/mini";
import { ApiError, apiParsed, checkedBody, parseResponse } from "../api/client.ts";
import { newCommandId, type SelectionSource } from "../api/units.ts";
import {
  certificateDetailSchema, certificateListRowSchema, certificateRunResultSchema,
  certificateSelectionPreviewRequestSchema, certificateSelectionPreviewSchema, certificateTemplateDetailSchema,
  certificateTemplateSummarySchema, certificateTemplateVersionSchema, certificateVersionChainSchema,
  fieldCatalogueEntrySchema, issueCertificateRequestSchema, labelTemplateDetailSchema, labelTemplateSummarySchema,
  pageSchema, templateValidationSchema, tenantAssetSchema, type CertificateRunResult,
  type CertificateSelectionPreviewRequest, type IssueCertificateRequest, type TenantAsset,
} from "./types.ts";

const text = z.string();
const nullableText = z.nullable(text);
const documentRefSchema = z.object({ type: text, id: text, line: z.optional(z.nullable(z.number())) });
const selectionSourceRequestSchema = z.object({
  kind: text, document: z.optional(z.nullable(documentRefSchema)), partyRef: z.optional(nullableText),
  itemId: z.optional(nullableText), from: z.optional(nullableText), to: z.optional(nullableText),
  eventType: z.optional(nullableText), serials: z.optional(z.nullable(z.array(text))), scheme: z.optional(nullableText),
  rangeStart: z.optional(nullableText), rangeEnd: z.optional(nullableText), importJobId: z.optional(nullableText),
});
const selectionSourceResponseSchema = z.object({
  kind: text, document: z.nullable(documentRefSchema), partyRef: nullableText, itemId: nullableText, from: nullableText,
  to: nullableText, eventType: nullableText, serials: z.nullable(z.array(text)), scheme: nullableText,
  rangeStart: nullableText, rangeEnd: nullableText, importJobId: nullableText,
});
const resolveSelectionRequestSchema = z.object({ commandId: text, source: selectionSourceRequestSchema });
const resolveSelectionSetResultSchema = z.object({ selectionId: text, resolved: z.number(), unknown: z.array(text), rejected: z.array(text), source: selectionSourceResponseSchema });
const saveDraftResultSchema = z.object({ version: z.nullable(certificateTemplateVersionSchema), validation: templateValidationSchema });
const certificateTemplatePreviewSchema = z.object({ bytes: text, mediaType: text, validation: templateValidationSchema });
const sampleUnitRequestSchema = z.object({ sampleUnitId: text });
const problemSchema = z.object({ detail: z.optional(text), title: z.optional(text), rule: z.optional(text) });
export const certificateListPageSchema = pageSchema(certificateListRowSchema);

export const certificateApi = {
  list: (params: URLSearchParams, signal?: AbortSignal) => apiParsed(`/api/certificates/?${params}`, certificateListPageSchema, { signal }),
  detail: (id: string, signal?: AbortSignal) => apiParsed(`/api/certificates/${id}`, certificateDetailSchema, { signal }),
  chain: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/certificates/units/${unitId}/versions`, certificateVersionChainSchema, { signal }),
  templates: (signal?: AbortSignal) => apiParsed("/api/certificate-templates/", z.array(certificateTemplateSummarySchema), { signal }),
  template: (id: string, signal?: AbortSignal) => apiParsed(`/api/certificate-templates/${id}`, certificateTemplateDetailSchema, { signal }),
  fields: (kind: string, signal?: AbortSignal) => apiParsed(`/api/certificate-templates/field-catalogue/${kind}`, z.array(fieldCatalogueEntrySchema), { signal }),
  createTemplate: (body: unknown) => apiParsed("/api/certificate-templates/", certificateTemplateDetailSchema, { method: "POST", body }),
  saveDraft: (id: string, body: unknown) => apiParsed(`/api/certificate-templates/${id}/versions/draft`, saveDraftResultSchema, { method: "POST", body }),
  validate: (id: string, body: unknown) => apiParsed(`/api/certificate-templates/${id}/validate`, templateValidationSchema, { method: "POST", body }),
  activate: (id: string, versionId: string) => apiParsed(`/api/certificate-templates/${id}/versions/${versionId}/activate`, certificateTemplateVersionSchema, { method: "POST", body: {} }),
  retire: (id: string, versionId: string) => apiParsed(`/api/certificate-templates/${id}/versions/${versionId}/retire`, certificateTemplateVersionSchema, { method: "POST", body: {} }),
  preview: (id: string, versionId: string, sampleUnitId: string) => apiParsed(`/api/certificate-templates/${id}/versions/${versionId}/preview`, certificateTemplatePreviewSchema, { method: "POST", body: checkedBody(sampleUnitRequestSchema, { sampleUnitId }) }),
  labels: (signal?: AbortSignal) => apiParsed("/api/labels/templates?take=200", z.array(labelTemplateSummarySchema), { signal }),
  label: (id: string, signal?: AbortSignal) => apiParsed(`/api/labels/templates/${id}`, labelTemplateDetailSchema, { signal }),
  assets: (signal?: AbortSignal) => apiParsed("/api/tenant-assets/", z.array(tenantAssetSchema), { signal }),
  async uploadAsset(file: File, legalEntityId: string | null): Promise<TenantAsset> {
    const body = new FormData(); body.append("commandId", crypto.randomUUID()); body.append("file", file); if (legalEntityId) body.append("legalEntityId", legalEntityId);
    const response = await fetch("/api/tenant-assets/", { method: "POST", credentials: "same-origin", body });
    if (!response.ok) {
      const fallback = `Something went wrong (HTTP ${response.status})`;
      try {
        const problem = z.parse(problemSchema, await response.json());
        const title = problem.title ?? null;
        const detail = problem.detail ?? null;
        const rule = problem.rule ?? null;
        throw new ApiError(response.status, detail ?? title ?? rule ?? fallback, title, detail, rule);
      } catch (reason) {
        if (reason instanceof ApiError) throw reason;
        throw new ApiError(response.status, fallback);
      }
    }
    return parseResponse("/api/tenant-assets/", tenantAssetSchema, await response.json());
  },
};


export const certificateIssueApi = {
  resolve: (source: SelectionSource) => { const body = { commandId: newCommandId(), source }; return apiParsed("/api/selections/", resolveSelectionSetResultSchema, { method: "POST", body: checkedBody(resolveSelectionRequestSchema, body) }); },
  preview: (request: CertificateSelectionPreviewRequest, signal?: AbortSignal) => apiParsed("/api/certificates/selection/preview", certificateSelectionPreviewSchema, { method: "POST", body: checkedBody(certificateSelectionPreviewRequestSchema, request), signal }),
  issue: (request: IssueCertificateRequest) => apiParsed("/api/certificates/selection/issue", certificateRunResultSchema, { method: "POST", body: checkedBody(issueCertificateRequestSchema, request) }),
};

export function downloadArchive(result: CertificateRunResult): void {
  const binary = atob(result.archive);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
  const url = URL.createObjectURL(new Blob([bytes], { type: result.archiveMediaType }));
  const link = document.createElement("a"); link.href = url; link.download = result.archiveFileName; link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}

import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../api/client.ts";
import { newCommandId, now } from "../api/units.ts";
import {
  labelBatchPrintResultSchema, labelBindableFieldSchema, labelBindingSchema, labelPolicySchema, labelPrintJobPageSchema,
  labelPrintJobSchema, labelPrintJobSummarySchema, labelPrintResultSchema, labelTemplateCatalogueRowSchema,
  labelTemplateDetailSchema, labelTemplatePreviewSchema,
} from "./types.ts";

function query(params: Record<string, string | number | undefined | null>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== undefined && value !== null && value !== "") search.set(key, String(value));
  const result = search.toString();
  return result ? `?${result}` : "";
}

const text = z.string();
const nullableText = z.nullable(text);
export const saveDraftInputSchema = z.object({
  templateVersionId: nullableText, effectiveFrom: text, effectiveTo: nullableText,
  layoutKind: z.enum(["zpl", "html"]), layout: text, staticText: z.array(text), bindings: z.array(labelBindingSchema),
  symbology: z.enum(["code128", "datamatrix", "qr", "code39"]), previewSampleUnitId: nullableText,
  regulated: z.boolean(),
});
export type SaveDraftInput = z.infer<typeof saveDraftInputSchema>;
const createTemplateRequestSchema = z.object({ commandId: text, name: text, appliesTo: text, appliesToValue: text });
const createTemplateResultSchema = z.object({ templateId: text, replayed: z.boolean() });
const saveDraftRequestSchema = z.extend(saveDraftInputSchema, { commandId: text });
const saveDraftResultSchema = z.object({ templateId: text, templateVersionId: text, version: z.number(), legalReview: z.boolean(), replayed: z.boolean() });
const previewRequestSchema = z.object({ sampleUnitId: nullableText });
const commandRequestSchema = z.object({ commandId: text });
const activateRequestSchema = z.object({ commandId: text, activatedAt: text });
const retireRequestSchema = z.object({ commandId: text, retiredAt: text });
const versionChangedSchema = z.object({ templateVersionId: text, status: text, legalReview: z.boolean(), replayed: z.boolean() });
const printRequestSchema = z.object({ commandId: text, unitId: text, templateId: nullableText, identifierId: z.null(), printerRef: text, printAt: text });
const batchPrintRequestSchema = z.object({ commandId: text, templateVersionId: nullableText, printerRef: text, serials: z.array(text) });
const reprintRequestSchema = z.object({ commandId: text, printerRef: nullableText, reprintedAt: text });

export const labelsApi = {
  templates: (signal?: AbortSignal) => apiParsed("/api/labels/templates?take=200", z.array(labelTemplateCatalogueRowSchema), { signal }),
  template: (templateId: string, signal?: AbortSignal) => apiParsed(`/api/labels/templates/${templateId}`, labelTemplateDetailSchema, { signal }),
  bindableFields: (signal?: AbortSignal) => apiParsed("/api/labels/bindable-fields", z.array(labelBindableFieldSchema), { signal }),
  createTemplate: (name: string, appliesTo: string, appliesToValue: string) => { const body = { commandId: newCommandId(), name, appliesTo, appliesToValue }; return apiParsed("/api/labels/templates", createTemplateResultSchema, {
    method: "POST", body: checkedBody(createTemplateRequestSchema, body),
  }); },
  saveDraft: (templateId: string, draft: SaveDraftInput) => { const body = { commandId: newCommandId(), ...draft }; return apiParsed(`/api/labels/templates/${templateId}/versions/draft`, saveDraftResultSchema, {
    method: "POST", body: checkedBody(saveDraftRequestSchema, body),
  }); },
  preview: (versionId: string, sampleUnitId: string | null) => apiParsed(`/api/labels/versions/${versionId}/preview`, labelTemplatePreviewSchema, {
    method: "POST", body: checkedBody(previewRequestSchema, { sampleUnitId }),
  }),
  requestReview: (versionId: string) => lifecycle(versionId, "legal-review/request"),
  signReview: (versionId: string) => lifecycle(versionId, "legal-review/sign"),
  approve: (versionId: string) => lifecycle(versionId, "approve"),
  activate: (versionId: string) => apiParsed(`/api/labels/versions/${versionId}/activate`, versionChangedSchema, {
    method: "POST", body: checkedBody(activateRequestSchema, { commandId: newCommandId(), activatedAt: now() }),
  }),
  retire: (versionId: string) => apiParsed(`/api/labels/versions/${versionId}/retire`, versionChangedSchema, {
    method: "POST", body: checkedBody(retireRequestSchema, { commandId: newCommandId(), retiredAt: now() }),
  }),
  settings: (signal?: AbortSignal) => apiParsed("/api/labels/settings", labelPolicySchema, { signal }),
  saveSettings: (policy: z.infer<typeof labelPolicySchema>) => apiParsed("/api/labels/settings", labelPolicySchema, { method: "PUT", body: checkedBody(labelPolicySchema, policy) }),
  jobs: (params: { status?: string | undefined; find?: string | undefined; printer?: string | undefined; kind?: "manual" | "reprint" | "event" | undefined; from?: string | undefined; to?: string | undefined; skip?: number | undefined; take?: number | undefined }, signal?: AbortSignal) =>
    apiParsed(`/api/labels/print-jobs${query(params)}`, labelPrintJobPageSchema, { signal }),
  summary: (from: string, to: string, signal?: AbortSignal) => apiParsed(`/api/labels/print-jobs/summary${query({ from, to })}`, labelPrintJobSummarySchema, { signal }),
  printers: (from: string, to: string, signal?: AbortSignal) => apiParsed(`/api/labels/print-jobs/printers${query({ from, to })}`, z.array(text), { signal }),
  job: (jobId: string, signal?: AbortSignal) => apiParsed(`/api/labels/print-jobs/${jobId}`, labelPrintJobSchema, { signal }),
  print: (unitId: string, printerRef: string, templateId: string | null = null) => { const body = { commandId: newCommandId(), unitId, templateId, identifierId: null, printerRef, printAt: now() }; return apiParsed("/api/labels/print-jobs", labelPrintResultSchema, {
    method: "POST", body: checkedBody(printRequestSchema, body),
  }); },
  printBatch: (serials: string[], printerRef: string, templateVersionId: string | null) => { const body = { commandId: newCommandId(), templateVersionId, printerRef, serials }; return apiParsed("/api/labels/print-jobs/batch", labelBatchPrintResultSchema, {
    method: "POST", body: checkedBody(batchPrintRequestSchema, body),
  }); },
  reprint: (jobId: string, printerRef: string | null) => { const body = { commandId: newCommandId(), printerRef, reprintedAt: now() }; return apiParsed(`/api/labels/print-jobs/${jobId}/reprint`, labelPrintResultSchema, {
    method: "POST", body: checkedBody(reprintRequestSchema, body),
  }); },
};

function lifecycle(versionId: string, action: string) {
  return apiParsed(`/api/labels/versions/${versionId}/${action}`, versionChangedSchema, { method: "POST", body: checkedBody(commandRequestSchema, { commandId: newCommandId() }) });
}

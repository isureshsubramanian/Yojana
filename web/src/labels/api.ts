import { api } from "../api/client";
import { newCommandId, now } from "../api/units";
import type {
  LabelBatchPrintResult, LabelBindableField, LabelBinding, LabelPolicy, LabelPrintJob, LabelPrintJobPage, LabelPrintJobSummary, LabelPrintResult,
  LabelTemplateCatalogueRow, LabelTemplateDetail, LabelTemplatePreview,
} from "./types";

function query(params: Record<string, string | number | undefined | null>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== undefined && value !== null && value !== "") search.set(key, String(value));
  const result = search.toString();
  return result ? `?${result}` : "";
}

export interface SaveDraftInput {
  templateVersionId: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
  layoutKind: "zpl" | "html";
  layout: string;
  staticText: string[];
  bindings: LabelBinding[];
  symbology: "code128" | "datamatrix" | "qr" | "code39";
  previewSampleUnitId: string | null;
  regulated: boolean;
}

export const labelsApi = {
  templates: (signal?: AbortSignal) => api<LabelTemplateCatalogueRow[]>("/api/labels/templates?take=200", { signal }),
  template: (templateId: string, signal?: AbortSignal) => api<LabelTemplateDetail>(`/api/labels/templates/${templateId}`, { signal }),
  bindableFields: (signal?: AbortSignal) => api<LabelBindableField[]>("/api/labels/bindable-fields", { signal }),
  createTemplate: (name: string, appliesTo: string, appliesToValue: string) => api<{ templateId: string; replayed: boolean }>("/api/labels/templates", {
    method: "POST", body: { commandId: newCommandId(), name, appliesTo, appliesToValue },
  }),
  saveDraft: (templateId: string, draft: SaveDraftInput) => api<{ templateId: string; templateVersionId: string; version: number; legalReview: boolean; replayed: boolean }>(`/api/labels/templates/${templateId}/versions/draft`, {
    method: "POST", body: { commandId: newCommandId(), ...draft },
  }),
  preview: (versionId: string, sampleUnitId: string | null) => api<LabelTemplatePreview>(`/api/labels/versions/${versionId}/preview`, {
    method: "POST", body: { sampleUnitId },
  }),
  requestReview: (versionId: string) => lifecycle(versionId, "legal-review/request"),
  signReview: (versionId: string) => lifecycle(versionId, "legal-review/sign"),
  approve: (versionId: string) => lifecycle(versionId, "approve"),
  activate: (versionId: string) => api(`/api/labels/versions/${versionId}/activate`, {
    method: "POST", body: { commandId: newCommandId(), activatedAt: now() },
  }),
  retire: (versionId: string) => api(`/api/labels/versions/${versionId}/retire`, {
    method: "POST", body: { commandId: newCommandId(), retiredAt: now() },
  }),
  settings: (signal?: AbortSignal) => api<LabelPolicy>("/api/labels/settings", { signal }),
  saveSettings: (policy: LabelPolicy) => api<LabelPolicy>("/api/labels/settings", { method: "PUT", body: policy }),
  jobs: (params: { status?: string | undefined; find?: string | undefined; printer?: string | undefined; kind?: "manual" | "reprint" | "event" | undefined; from?: string | undefined; to?: string | undefined; skip?: number | undefined; take?: number | undefined }, signal?: AbortSignal) =>
    api<LabelPrintJobPage>(`/api/labels/print-jobs${query(params)}`, { signal }),
  summary: (from: string, to: string, signal?: AbortSignal) => api<LabelPrintJobSummary>(`/api/labels/print-jobs/summary${query({ from, to })}`, { signal }),
  printers: (from: string, to: string, signal?: AbortSignal) => api<string[]>(`/api/labels/print-jobs/printers${query({ from, to })}`, { signal }),
  job: (jobId: string, signal?: AbortSignal) => api<LabelPrintJob>(`/api/labels/print-jobs/${jobId}`, { signal }),
  print: (unitId: string, printerRef: string, templateId: string | null = null) => api<LabelPrintResult>("/api/labels/print-jobs", {
    method: "POST", body: { commandId: newCommandId(), unitId, templateId, identifierId: null, printerRef, printAt: now() },
  }),
  printBatch: (serials: string[], printerRef: string, templateVersionId: string | null) => api<LabelBatchPrintResult>("/api/labels/print-jobs/batch", {
    method: "POST", body: { commandId: newCommandId(), templateVersionId, printerRef, serials },
  }),
  reprint: (jobId: string, printerRef: string | null) => api<LabelPrintResult>(`/api/labels/print-jobs/${jobId}/reprint`, {
    method: "POST", body: { commandId: newCommandId(), printerRef, reprintedAt: now() },
  }),
};

function lifecycle(versionId: string, action: string) {
  return api(`/api/labels/versions/${versionId}/${action}`, { method: "POST", body: { commandId: newCommandId() } });
}

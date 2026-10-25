import * as z from "zod/mini";
import { apiParsed, ApiError } from "../api/client.ts";
import { deltaExportUrl } from "./migrationModel.ts";
import { canonicalFamilyCatalogue, canonicalisationRun, currencyInfo, linkOverridePage, mappingSettings, migrationApply, migrationApplyRefusalPage, migrationBatch, migrationBatchPage, migrationDeltaFindingPage, migrationLoadDetail, migrationLoadPage, migrationLoadReadiness, migrationMap, migrationOutcomeSettings, migrationPlanGapPage, migrationProfile, migrationProfileSamples, migrationReconstruction, migrationReviewDecisionResponse, migrationReviewDetail, migrationReviewPage, migrationSource, migrationSourcePage, migrationSourceTestResult, migrationSyncControlResult, migrationValidation, policyCatalogue, rollBackMigrationLoadResponse, signoffRecord, startMigrationApplyResponse, startMigrationBatchResponse, startValidationResponse, templateKeywordRules, templateMappingDetail, templateMappingSummaryView, templatePatternPage, touchedMigrationLoadUnitPage, validationCheck, validationCheckDetail, validationDifferencePage, validationSample, validationSamplePage, validationSettings, type MigrationOutcomeChoice, type MigrationReviewDecision, type MigrationReviewKind, type MigrationReviewStatus, type TemplateKeywordRule, type TemplateMappingState, type ValidationSampleOutcome, type ValidationSignatureRole, type LinkOverrideUploadResponse, type MappingSettings } from "./migrationTypes.ts";

const root = "/api/migration";
function query(values: Record<string, string | number | boolean | undefined>) {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") result.set(key, String(value));
  const text = result.toString();
  return text ? `?${text}` : "";
}

async function uploadCsv(path: string, file: File): Promise<LinkOverrideUploadResponse> {
  const body = new FormData(); body.append("file", file);
  const response = await fetch(path, { method: "POST", credentials: "same-origin", headers: { accept: "application/json" }, body });
  if (!response.ok) {
    let problem: { title?: string; detail?: string; rule?: string } = {};
    try { problem = await response.json() as typeof problem; } catch { /* The status still gives a useful refusal. */ }
    throw new ApiError(response.status, problem.detail ?? problem.title ?? `Something went wrong (HTTP ${response.status})`, problem.title ?? null, problem.detail ?? null, problem.rule ?? null);
  }
  return await response.json() as LinkOverrideUploadResponse;
}

export const migrationApi = {
  sources: (cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/sources${query({ cursor, take: 50 })}`, migrationSourcePage, { signal }),
  licence: (sourceId: string, confirmed: boolean, contractReference: string | null, note: string | null) =>
    apiParsed(`${root}/sources/${sourceId}/licence`, migrationSource, { method: "POST", body: { confirmed, contractReference, note } }),
  testSource: (sourceId: string) => apiParsed(`${root}/sources/${sourceId}/test`, migrationSourceTestResult, { method: "POST" }),
  batches: (cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/batches${query({ cursor, take: 12 })}`, migrationBatchPage, { signal }),
  batch: (batchId: string, signal?: AbortSignal) => apiParsed(`${root}/batches/${batchId}`, migrationBatch, { signal }),
  startBatch: (sourceId: string) => apiParsed(`${root}/batches`, startMigrationBatchResponse, { method: "POST", body: { sourceId } }),
  profile: (batchId: string, signal?: AbortSignal) => apiParsed(`${root}/batches/${batchId}/profile`, migrationProfile, { signal }),
  samples: (batchId: string, classCode: string, signal?: AbortSignal) => apiParsed(`${root}/batches/${batchId}/profile/${encodeURIComponent(classCode)}/samples`, migrationProfileSamples, { signal }),
  rerunProfile: (batchId: string) => apiParsed(`${root}/batches/${batchId}/profile`, z.object({ runId: z.string(), batchId: z.string(), status: z.string() }), { method: "POST" }),
  outcomes: (signal?: AbortSignal) => apiParsed(`${root}/profiling/outcomes`, migrationOutcomeSettings, { signal }),
  saveOutcomes: (classes: MigrationOutcomeChoice[], expectedVersion: string) => apiParsed(`${root}/profiling/outcomes`, migrationOutcomeSettings, { method: "PUT", body: { classes, expectedVersion } }),
  canonicalisation: (batchId: string, signal?: AbortSignal) => apiParsed(`${root}/batches/${batchId}/canonicalisation`, canonicalisationRun, { signal }),
  runCanonicalisation: (batchId: string) => apiParsed(`${root}/batches/${batchId}/canonicalise`, z.object({ runId: z.string(), batchId: z.string(), status: z.string() }), { method: "POST" }),
  reconstruction: (batchId: string, signal?: AbortSignal) => apiParsed(`${root}/batches/${batchId}/reconstruction`, migrationReconstruction, { signal }),
  map: (batchId: string, signal?: AbortSignal) => apiParsed(`${root}/batches/${batchId}/map`, migrationMap, { signal }),
  mapGaps: (batchId: string, kind?: string, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${root}/batches/${batchId}/map/gaps${query({ kind, cursor, take: 25 })}`, migrationPlanGapPage, { signal }),
  apply: (batchId: string, rehearsal: boolean) => apiParsed(`${root}/batches/${batchId}/apply`, startMigrationApplyResponse, { method: "POST", body: { rehearsal } }),
  load: (batchId: string, signal?: AbortSignal) => apiParsed(`${root}/batches/${batchId}/apply`, migrationApply, { signal }),
  loadReadiness: (batchId: string, signal?: AbortSignal) => apiParsed(`${root}/batches/${batchId}/load-readiness`, migrationLoadReadiness, { signal }),
  startDelta: (batchId: string) => apiParsed(`${root}/batches/${batchId}/delta`, startMigrationApplyResponse, { method: "POST" }),
  loads: (sourceId: string, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${root}/loads${query({ sourceId, cursor, take: 12 })}`, migrationLoadPage, { signal }),
  loadSummary: (loadId: string, signal?: AbortSignal) => apiParsed(`${root}/loads/${loadId}`, migrationLoadDetail, { signal }),
  deltaFindings: (loadId: string, filters: { kind?: string | undefined; itemCode?: string | undefined; serial?: string | undefined; cursor?: string | undefined } = {}, signal?: AbortSignal) =>
    apiParsed(`${root}/loads/${loadId}/delta${query({ ...filters, take: 100 })}`, migrationDeltaFindingPage, { signal }),
  deltaExportUrl: (loadId: string, format: "csv" | "xlsx", filters: { kind?: string; itemCode?: string; serial?: string }) =>
    deltaExportUrl(loadId, format, filters),
  loadRefusals: (loadId: string, filters: { reason?: string | undefined; itemCode?: string | undefined; serial?: string | undefined; cursor?: string | undefined } = {}, signal?: AbortSignal) =>
    apiParsed(`${root}/loads/${loadId}/refusals${query({ ...filters, take: 50 })}`, migrationApplyRefusalPage, { signal }),
  loadRefusalsExportUrl: (loadId: string, filters: { reason?: string; itemCode?: string; serial?: string }) =>
    `${root}/loads/${loadId}/refusals/export.csv${query(filters)}`,
  touchedLoadUnits: (loadId: string, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${root}/loads/${loadId}/rollback/touched-units${query({ cursor, take: 50 })}`, touchedMigrationLoadUnitPage, { signal }),
  rollbackLoad: (loadId: string) => apiParsed(`${root}/loads/${loadId}/rollback`, rollBackMigrationLoadResponse, { method: "POST" }),
  resumeSync: (connectionId: string) => apiParsed(`/api/integration/connections/${connectionId}/resume-sync`, migrationSyncControlResult, { method: "POST", body: { note: "Resumed after the final migration load" } }),
  review: (filters: { batchId: string; kind?: MigrationReviewKind | ""; family?: string; status?: MigrationReviewStatus | ""; find?: string; cursor?: string | undefined; take?: number }, signal?: AbortSignal) =>
    apiParsed(`${root}/review${query({ ...filters, take: filters.take ?? 25 })}`, migrationReviewPage, { signal }),
  reviewExportUrl: (filters: { batchId: string; kind?: MigrationReviewKind | ""; family?: string; status?: MigrationReviewStatus | ""; find?: string }) =>
    `${root}/review/export.csv${query(filters)}`,
  reviewDetail: (itemId: string, signal?: AbortSignal) => apiParsed(`${root}/review/${itemId}`, migrationReviewDetail, { signal }),
  decideReview: (itemId: string, decision: MigrationReviewDecision, keepSourceKey: string | null, reason: string, note?: string) =>
    apiParsed(`${root}/review/${itemId}/decision`, migrationReviewDecisionResponse, { method: "POST", body: { decision, keepSourceKey, reason, note: note?.trim() || null } }),
  canonicalFamilyCatalogue: (signal?: AbortSignal) => apiParsed(`${root}/canonicalisation/families/catalogue`, canonicalFamilyCatalogue, { signal }),
  templateSummary: (signal?: AbortSignal) => apiParsed(`${root}/templates/summary`, templateMappingSummaryView, { signal }),
  templatePatterns: (filters: { state?: TemplateMappingState | ""; deletedInSource?: boolean; cursor?: string; take?: number }, signal?: AbortSignal) =>
    apiParsed(`${root}/templates${query({ ...filters, take: filters.take ?? 25 })}`, templatePatternPage, { signal }),
  templateDetail: (templateKey: string, signal?: AbortSignal) =>
    apiParsed(`${root}/templates/${encodeURIComponent(templateKey)}`, templateMappingDetail, { signal }),
  templateCatalogue: (signal?: AbortSignal) => apiParsed(`${root}/templates/catalogue`, policyCatalogue, { signal }),
  proposeTemplates: (templateKeys?: string[]) => apiParsed(`${root}/templates/propose`, z.object({ requested: z.number(), proposed: z.number(), unchanged: z.number(), superseded: z.number() }), { method: "POST", body: { templateKeys: templateKeys ?? null } }),
  updateTemplate: (templateKey: string, blocks: { familyCode: string; policyId: string | null }[], expectedVersion: string) =>
    apiParsed(`${root}/templates/${encodeURIComponent(templateKey)}`, templateMappingDetail, { method: "PUT", body: { blocks, expectedVersion } }),
  approveTemplate: (templateKey: string) => apiParsed(`${root}/templates/${encodeURIComponent(templateKey)}/approve`, templateMappingDetail, { method: "POST" }),
  approveTemplatePattern: (patternKey: string) => apiParsed(`${root}/templates/approve-pattern`, z.object({ patternKey: z.string(), approved: z.number() }), { method: "POST", body: { patternKey } }),
  templateKeywordRules: (signal?: AbortSignal) => apiParsed(`${root}/templates/keyword-rules`, templateKeywordRules, { signal }),
  saveTemplateKeywordRules: (rules: TemplateKeywordRule[], expectedVersion: string) => apiParsed(`${root}/templates/keyword-rules`, templateKeywordRules, { method: "PUT", body: { rules, expectedVersion } }),
  mappingSettings: (signal?: AbortSignal) => apiParsed(`${root}/mapping/settings`, mappingSettings, { signal }),
  saveMappingSettings: (settings: MappingSettings) => apiParsed(`${root}/mapping/settings`, mappingSettings, { method: "PUT", body: {
    allowWaitingReviews: settings.allowWaitingReviews, cutoverDate: settings.cutoverDate,
    conditionMap: settings.conditionMap, linkDefaults: settings.linkDefaults,
    cardCoverPolicyId: settings.cardCoverPolicyId, serialMasterCoverPolicyId: settings.serialMasterCoverPolicyId,
    expectedVersion: settings.version,
  } }),
  currencyCatalogue: (signal?: AbortSignal) => apiParsed("/api/tenant/currency/catalogue", z.array(currencyInfo), { signal }),
  linkOverrides: (batchId: string | undefined, unused: boolean | undefined, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${root}/link-overrides${query({ batchId, unused: unused || undefined, cursor, take: 12 })}`, linkOverridePage, { signal }),
  linkExportUrl: (batchId: string) => `${root}/batches/${batchId}/links/export.csv`,
  uploadLinkOverrides: (file: File) => uploadCsv(`${root}/link-overrides/upload`, file),
  removeLinkOverrides: () => apiParsed(`${root}/link-overrides`, z.object({ removed: z.number() }), { method: "DELETE", body: { confirm: true } }),
  validation: (batchId: string, signal?: AbortSignal) => apiParsed(`${root}/batches/${batchId}/validation`, migrationValidation, { signal }),
  validationExportUrl: (batchId: string, format: "csv" | "xlsx") => `${root}/batches/${batchId}/validation/export.${format}`,
  runValidation: (batchId: string, rehearsal: boolean) => apiParsed(`${root}/batches/${batchId}/validate`, startValidationResponse, { method: "POST", body: { rehearsal } }),
  validationCheck: (checkId: string, signal?: AbortSignal) => apiParsed(`${root}/validation/checks/${checkId}`, validationCheckDetail, { signal }),
  validationDifferences: (checkId: string, cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/validation/checks/${checkId}/differences${query({ cursor, take: 25 })}`, validationDifferencePage, { signal }),
  explainValidationCheck: (checkId: string, explanation: string) => apiParsed(`${root}/validation/checks/${checkId}/explain`, validationCheck, { method: "POST", body: { explanation } }),
  signValidationCheck: (checkId: string, role: ValidationSignatureRole) => apiParsed(`${root}/validation/checks/${checkId}/sign`, validationCheck, { method: "POST", body: { role } }),
  validationSample: (runId: string, cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/validation/runs/${runId}/sample${query({ cursor, take: 10 })}`, validationSamplePage, { signal }),
  traceValidationSample: (sampleId: string, outcome: ValidationSampleOutcome, note: string | null) => apiParsed(`${root}/validation/sample/${sampleId}/trace`, validationSample, { method: "POST", body: { outcome, note } }),
  validationSettings: (signal?: AbortSignal) => apiParsed(`${root}/validation/settings`, validationSettings, { signal }),
  saveValidationSettings: (historicalTolerancePercent: number, sampleSize: number, ageBandYears: number[], expectedVersion: string) => apiParsed(`${root}/validation/settings`, validationSettings, { method: "PUT", body: { historicalTolerancePercent, sampleSize, ageBandYears, expectedVersion } }),
  createSignoff: (runId: string) => apiParsed(`${root}/validation/runs/${runId}/signoff-record`, signoffRecord, { method: "POST" }),
  signoffExportUrl: (recordId: string) => `${root}/signoff-records/${recordId}/export.csv`,
};

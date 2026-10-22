import { api } from "../api/client";
import type {
  CanonicalFamilyCatalogue, CanonicalisationRun, MigrationBatch, MigrationBatchPage, MigrationOutcomeChoice, MigrationOutcomeSettings,
  MigrationProfile, MigrationProfileSamples, MigrationReviewDecision, MigrationReviewDecisionResponse,
  MigrationApply, MigrationApplyRefusalPage, MigrationMap, MigrationReconstruction, RollBackMigrationLoadResponse,
  MigrationReviewDetail, MigrationReviewKind, MigrationReviewPage, MigrationReviewStatus, MigrationSource,
  MigrationSourcePage, MigrationSourceTestResult, PolicyCatalogue, StartMigrationBatchResponse,
  TemplateKeywordRule, TemplateKeywordRules, TemplateMappingDetail, TemplateMappingState,
  TemplateMappingSummaryView, TemplatePatternPage, MigrationValidation, SignoffRecord,
  StartMigrationApplyResponse, StartValidationResponse, ValidationCheck, ValidationCheckDetail, ValidationDifferencePage,
  ValidationSample, ValidationSampleOutcome, ValidationSamplePage, ValidationSettings, ValidationSignatureRole,
} from "./migrationTypes";

const root = "/api/migration";
function query(values: Record<string, string | number | boolean | undefined>) {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") result.set(key, String(value));
  return `?${result}`;
}

export const migrationApi = {
  sources: (cursor?: string, signal?: AbortSignal) => api<MigrationSourcePage>(`${root}/sources${query({ cursor, take: 50 })}`, { signal }),
  licence: (sourceId: string, confirmed: boolean, contractReference: string | null, note: string | null) =>
    api<MigrationSource>(`${root}/sources/${sourceId}/licence`, { method: "POST", body: { confirmed, contractReference, note } }),
  testSource: (sourceId: string) => api<MigrationSourceTestResult>(`${root}/sources/${sourceId}/test`, { method: "POST" }),
  batches: (cursor?: string, signal?: AbortSignal) => api<MigrationBatchPage>(`${root}/batches${query({ cursor, take: 12 })}`, { signal }),
  batch: (batchId: string, signal?: AbortSignal) => api<MigrationBatch>(`${root}/batches/${batchId}`, { signal }),
  startBatch: (sourceId: string) => api<StartMigrationBatchResponse>(`${root}/batches`, { method: "POST", body: { sourceId } }),
  profile: (batchId: string, signal?: AbortSignal) => api<MigrationProfile>(`${root}/batches/${batchId}/profile`, { signal }),
  samples: (batchId: string, classCode: string, signal?: AbortSignal) => api<MigrationProfileSamples>(`${root}/batches/${batchId}/profile/${encodeURIComponent(classCode)}/samples`, { signal }),
  rerunProfile: (batchId: string) => api<{ runId: string; batchId: string; status: string }>(`${root}/batches/${batchId}/profile`, { method: "POST" }),
  outcomes: (signal?: AbortSignal) => api<MigrationOutcomeSettings>(`${root}/profiling/outcomes`, { signal }),
  saveOutcomes: (classes: MigrationOutcomeChoice[], expectedVersion: string) => api<MigrationOutcomeSettings>(`${root}/profiling/outcomes`, { method: "PUT", body: { classes, expectedVersion } }),
  canonicalisation: (batchId: string, signal?: AbortSignal) => api<CanonicalisationRun>(`${root}/batches/${batchId}/canonicalisation`, { signal }),
  runCanonicalisation: (batchId: string) => api<{ runId: string; batchId: string; status: string }>(`${root}/batches/${batchId}/canonicalise`, { method: "POST" }),
  reconstruction: (batchId: string, signal?: AbortSignal) => api<MigrationReconstruction>(`${root}/batches/${batchId}/reconstruction`, { signal }),
  map: (batchId: string, signal?: AbortSignal) => api<MigrationMap>(`${root}/batches/${batchId}/map`, { signal }),
  apply: (batchId: string, rehearsal: boolean) => api<StartMigrationApplyResponse>(`${root}/batches/${batchId}/apply`, { method: "POST", body: { rehearsal } }),
  load: (batchId: string, signal?: AbortSignal) => api<MigrationApply>(`${root}/batches/${batchId}/apply`, { signal }),
  loadRefusals: (loadId: string, cursor?: string, signal?: AbortSignal) => api<MigrationApplyRefusalPage>(`${root}/loads/${loadId}/refusals${query({ cursor, take: 50 })}`, { signal }),
  rollbackLoad: (loadId: string) => api<RollBackMigrationLoadResponse>(`${root}/loads/${loadId}/rollback`, { method: "POST" }),
  review: (filters: { batchId: string; kind?: MigrationReviewKind | ""; family?: string; status?: MigrationReviewStatus | ""; find?: string; cursor?: string | undefined; take?: number }, signal?: AbortSignal) =>
    api<MigrationReviewPage>(`${root}/review${query({ ...filters, take: filters.take ?? 25 })}`, { signal }),
  reviewExportUrl: (filters: { batchId: string; kind?: MigrationReviewKind | ""; family?: string; status?: MigrationReviewStatus | ""; find?: string }) =>
    `${root}/review/export.csv${query(filters)}`,
  reviewDetail: (itemId: string, signal?: AbortSignal) => api<MigrationReviewDetail>(`${root}/review/${itemId}`, { signal }),
  decideReview: (itemId: string, decision: MigrationReviewDecision, keepSourceKey: string | null, reason: string, note?: string) =>
    api<MigrationReviewDecisionResponse>(`${root}/review/${itemId}/decision`, { method: "POST", body: { decision, keepSourceKey, reason, note: note?.trim() || null } }),
  canonicalFamilyCatalogue: (signal?: AbortSignal) => api<CanonicalFamilyCatalogue>(`${root}/canonicalisation/families/catalogue`, { signal }),
  templateSummary: (signal?: AbortSignal) => api<TemplateMappingSummaryView>(`${root}/templates/summary`, { signal }),
  templatePatterns: (filters: { state?: TemplateMappingState | ""; deletedInSource?: boolean; cursor?: string; take?: number }, signal?: AbortSignal) =>
    api<TemplatePatternPage>(`${root}/templates${query({ ...filters, take: filters.take ?? 25 })}`, { signal }),
  templateDetail: (templateKey: string, signal?: AbortSignal) =>
    api<TemplateMappingDetail>(`${root}/templates/${encodeURIComponent(templateKey)}`, { signal }),
  templateCatalogue: (signal?: AbortSignal) => api<PolicyCatalogue>(`${root}/templates/catalogue`, { signal }),
  proposeTemplates: (templateKeys?: string[]) => api<{ requested: number; proposed: number; unchanged: number; superseded: number }>(`${root}/templates/propose`, { method: "POST", body: { templateKeys: templateKeys ?? null } }),
  updateTemplate: (templateKey: string, blocks: { familyCode: string; policyId: string | null }[], expectedVersion: string) =>
    api<TemplateMappingDetail>(`${root}/templates/${encodeURIComponent(templateKey)}`, { method: "PUT", body: { blocks, expectedVersion } }),
  approveTemplate: (templateKey: string) => api<TemplateMappingDetail>(`${root}/templates/${encodeURIComponent(templateKey)}/approve`, { method: "POST" }),
  approveTemplatePattern: (patternKey: string) => api<{ patternKey: string; approved: number }>(`${root}/templates/approve-pattern`, { method: "POST", body: { patternKey } }),
  templateKeywordRules: (signal?: AbortSignal) => api<TemplateKeywordRules>(`${root}/templates/keyword-rules`, { signal }),
  saveTemplateKeywordRules: (rules: TemplateKeywordRule[], expectedVersion: string) => api<TemplateKeywordRules>(`${root}/templates/keyword-rules`, { method: "PUT", body: { rules, expectedVersion } }),
  validation: (batchId: string, signal?: AbortSignal) => api<MigrationValidation>(`${root}/batches/${batchId}/validation`, { signal }),
  validationExportUrl: (batchId: string, format: "csv" | "xlsx") => `${root}/batches/${batchId}/validation/export.${format}`,
  runValidation: (batchId: string, rehearsal: boolean) => api<StartValidationResponse>(`${root}/batches/${batchId}/validate`, { method: "POST", body: { rehearsal } }),
  validationCheck: (checkId: string, signal?: AbortSignal) => api<ValidationCheckDetail>(`${root}/validation/checks/${checkId}`, { signal }),
  validationDifferences: (checkId: string, cursor?: string, signal?: AbortSignal) => api<ValidationDifferencePage>(`${root}/validation/checks/${checkId}/differences${query({ cursor, take: 25 })}`, { signal }),
  explainValidationCheck: (checkId: string, explanation: string) => api<ValidationCheck>(`${root}/validation/checks/${checkId}/explain`, { method: "POST", body: { explanation } }),
  signValidationCheck: (checkId: string, role: ValidationSignatureRole) => api<ValidationCheck>(`${root}/validation/checks/${checkId}/sign`, { method: "POST", body: { role } }),
  validationSample: (runId: string, cursor?: string, signal?: AbortSignal) => api<ValidationSamplePage>(`${root}/validation/runs/${runId}/sample${query({ cursor, take: 10 })}`, { signal }),
  traceValidationSample: (sampleId: string, outcome: ValidationSampleOutcome, note: string | null) => api<ValidationSample>(`${root}/validation/sample/${sampleId}/trace`, { method: "POST", body: { outcome, note } }),
  validationSettings: (signal?: AbortSignal) => api<ValidationSettings>(`${root}/validation/settings`, { signal }),
  saveValidationSettings: (historicalTolerancePercent: number, sampleSize: number, ageBandYears: number[], expectedVersion: string) => api<ValidationSettings>(`${root}/validation/settings`, { method: "PUT", body: { historicalTolerancePercent, sampleSize, ageBandYears, expectedVersion } }),
  createSignoff: (runId: string) => api<SignoffRecord>(`${root}/validation/runs/${runId}/signoff-record`, { method: "POST" }),
  signoffExportUrl: (recordId: string) => `${root}/signoff-records/${recordId}/export.csv`,
};

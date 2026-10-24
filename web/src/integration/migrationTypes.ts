import type { CursorPage } from "./adminTypes";

export interface MigrationSource {
  sourceId: string;
  displayLabel: string;
  sourceSystem: string;
  runtime: string;
  route: string;
  databaseName: string;
  schemaName: string;
  linkedServerName: string | null;
  commandTimeoutSeconds: number;
  hasConnectionString: boolean;
  licencePosition: string;
  licenceConfirmedByName: string | null;
  licenceConfirmedAt: string | null;
  licenceContractReference: string | null;
  licenceNote: string | null;
  createdAt: string;
  updatedAt: string;
}

export type MigrationSourcePage = CursorPage<MigrationSource>;

// table is the source's own name, shown only as data under the readable label the server supplies.
export interface MigrationSourceTestTable { table: string; rowCount: number | null; error: string | null; role: string; label: string | null }
export interface MigrationSourceTestResult { sourceId: string; tables: MigrationSourceTestTable[] }

export interface MigrationBatchTable {
  table: string;
  role: string;
  label: string;
  rowsRead: number;
  inserted: number;
  updated: number;
  unchanged: number;
  absent: number;
  durationMilliseconds: number;
}

export interface MigrationBatch {
  batchId: string;
  displayNumber: string;
  sourceId: string;
  sourceSystem: string;
  kind: string;
  requestedByName: string;
  requestedAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  status: string;
  failure: string | null;
  tables: MigrationBatchTable[] | null;
}

export type MigrationBatchPage = CursorPage<MigrationBatch>;
export interface StartMigrationBatchResponse { batchId: string; status: string; failure: string | null }

export interface MigrationProfileClass {
  code: string;
  name: string;
  outcome: MigrationOutcome;
  outcomeLabel: string;
  count: number | null;
  change: number | null;
  note: string | null;
}

export interface MigrationProfile {
  runId: string;
  batchId: string;
  startedAt: string | null;
  finishedAt: string | null;
  status: string;
  classes: MigrationProfileClass[];
}

export interface MigrationProfileSample {
  sourceKey: string;
  serial: string | null;
  item: string | null;
  facts: Record<string, string | null>;
}

export interface MigrationProfileSamples { runId: string; classCode: string; samples: MigrationProfileSample[] }

export type MigrationOutcome = "stop_load" | "needs_person" | "fixed_by_rule" | "loaded_with_note";
export interface MigrationOutcomeChoice { classCode: string; outcome: MigrationOutcome }
export interface MigrationOutcomeSettings { classes: MigrationOutcomeChoice[]; version: string }

export type MigrationReviewKind = "same_serial_one_item" | "same_serial_different_items" | "orphan_equipment_card" | "serial_in_user_field" | "history_needs_review";
export type MigrationReviewStatus = "waiting" | "decided" | "carried_over";
export type MigrationReviewDecision = "merge_into" | "keep_apart" | "fix_in_incumbent" | "accept_as_recorded" | "exclude_from_history";

export interface CanonicalisationGap { kind: MigrationReviewKind; reason: string }
export interface MigrationReviewCount { key: string; count: number }
export interface CanonicalisationRun {
  runId: string;
  batchId: string;
  startedAt: string | null;
  finishedAt: string | null;
  status: string;
  failure: string | null;
  candidateRowsTotal: number;
  candidateRowsExact: number;
  candidateRowsTrimmedUpper: number;
  candidateRowsFamilyNormalised: number;
  candidateRowsMerged: number;
  waitingReviewItems: number;
  waitingSameItemReviewItems: number;
  waitingAcrossItemsReviewItems: number;
  waitingReviewItemsByKind: MigrationReviewCount[];
  decidedReviewItems: number;
  decidedReviewItemsByDecision: MigrationReviewCount[];
  carriedOverReviewItems: number;
  unverifiedDates: number;
  gaps: CanonicalisationGap[];
}

export interface ReconstructionCount { key: string; count: number }
export interface MigrationReconstruction {
  runId: string;
  batchId: string;
  canonicalisationRunId: string;
  startedAt: string | null;
  finishedAt: string | null;
  status: string;
  failure: string | null;
  candidatesReconstructed: number;
  candidatesSkippedByDisposition: number;
  eventsByMovementKind: ReconstructionCount[];
  historyDepth: ReconstructionCount[];
  cancellationPairsRemoved: number;
  queuedForReviewByReason: ReconstructionCount[];
}

export interface MappingCount { key: string; count: number }
export interface MigrationGapCount { kind: string; label: string; count: number }
export interface MigrationPlanGap {
  gapId: string;
  planUnitId: string | null;
  itemCode: string | null;
  itemName: string | null;
  serial: string | null;
  kind: string;
  label: string;
  reason: string;
  sourceKey: string | null;
}
export interface MigrationPlanGapPage { items: MigrationPlanGap[]; nextCursor: string | null }
export interface MigrationMap {
  runId: string;
  batchId: string;
  reconstructionRunId: string;
  cutoverDate: string;
  startedAt: string | null;
  finishedAt: string | null;
  status: string;
  failure: string | null;
  unitsByOrigin: MappingCount[];
  unitsByLocationState: MappingCount[];
  identifiersByIssuer: MappingCount[];
  eventsByProvenance: MappingCount[];
  ownership: MappingCount[];
  replacementLinks: number;
  entitlementsByFamily: MappingCount[];
  entitlementsByDateSource: MappingCount[];
  expiredEntitlements: number;
  gapsByKind: MigrationGapCount[];
  excludedCandidates: number;
  waitingCandidatesLeftOut: number;
}

export interface ConditionMappingRule { cardStatus: string | null; lastMovementKind: string | null; grade: string }
export interface ReplacementLinkValues {
  direction: string;
  replacementReason: string;
  state: string;
  replacementConditionAllowed: string;
  pricePolicy: string;
  differenceAmount: number;
  currency: string;
}
export interface MappingPolicyChoice {
  policyId: string;
  policyCode: string;
  version: number;
  policyName: string;
  familyCode: string;
}
export interface MappingSettings {
  allowWaitingReviews: boolean;
  cutoverDate: string | null;
  conditionMap: ConditionMappingRule[];
  linkDefaults: ReplacementLinkValues;
  cardCoverPolicyId: string | null;
  serialMasterCoverPolicyId: string | null;
  cardCoverPolicy: MappingPolicyChoice | null;
  serialMasterCoverPolicy: MappingPolicyChoice | null;
  approvedPublishedPolicies: MappingPolicyChoice[];
  version: string;
}
export interface LinkOverride {
  sourceKey: string;
  direction: string | null;
  replacementReason: string | null;
  state: string | null;
  replacementConditionAllowed: string | null;
  pricePolicy: string | null;
  differenceAmount: number | null;
  currency: string | null;
  uploadedAt: string;
  uploadedByName: string;
  uploadFileName: string;
  unused: boolean;
}
export interface LinkOverridePage {
  items: LinkOverride[];
  nextCursor: string | null;
  totalCount: number;
  unusedCount: number;
}
export interface LinkOverrideUploadError { row: number; column: string; reason: string }
export interface LinkOverrideUploadResponse {
  added: number;
  changed: number;
  removed: number;
  unchanged: number;
  errorTotal: number;
  errors: LinkOverrideUploadError[];
}
export interface CurrencyInfo { code: string; name: string; symbol: string; minorDigits: number }

export interface StartMigrationApplyResponse {
  loadId: string;
  batchId: string;
  mapRunId: string;
  rehearsal: boolean;
  status: string;
}

export interface MigrationLoadActionReason { code: string; message: string }
export interface MigrationLoadAction { allowed: boolean; reasons: MigrationLoadActionReason[] }
export interface MigrationDeltaLoadAction extends MigrationLoadAction { baseLoadId: string | null; baseLabel: string | null }
export interface MigrationLoadReadiness {
  rehearsal: MigrationLoadAction;
  final: MigrationLoadAction;
  delta: MigrationDeltaLoadAction;
}

export type MigrationLoadKind = "rehearsal" | "final" | "delta";
export interface MigrationLoadUndo { allowed: boolean; reason: string | null }
export interface MigrationLoadHandOver {
  connectionId: string;
  connectionName: string;
  from: string | null;
  syncState: string;
  note: string | null;
}

export interface MigrationSyncControlResult {
  connectionId: string;
  syncState: string;
  notifications: "pending" | "held" | "not_held" | "not_supported";
  waitingOutbound: number;
  failureCode: string | null;
  warning: string | null;
}

export interface MigrationLoadSummary {
  loadId: string;
  label: string;
  kind: MigrationLoadKind;
  status: string;
  batchId: string;
  batchDisplayNumber: string;
  baseLoadId: string | null;
  baseLabel: string | null;
  requestedByName: string;
  requestedAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  applied: number;
  refused: number;
  alreadyInModule: number;
  coverReplaced: number;
  coverRefused: number;
  ownerDiffers: number;
  goneFromSource: number;
  eventsWritten: number;
  entitlementsWritten: number;
  replacementLinksWritten: number;
  rolledBackAt: string | null;
  rolledBackByName: string | null;
  undo: MigrationLoadUndo;
  handOver: MigrationLoadHandOver | null;
}

export interface MigrationLoadDetail extends MigrationLoadSummary {
  batchStartedAt: string | null;
  batchFinishedAt: string | null;
}

export interface MigrationLoadPage { items: MigrationLoadSummary[]; nextCursor: string | null }

export interface MigrationApply {
  loadId: string;
  batchId: string;
  mapRunId: string;
  rehearsal: boolean;
  status: string;
  requestedByName: string;
  requestedAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  failure: string | null;
  unitsInPlan: number;
  applied: number;
  alreadyApplied: number;
  refused: number;
  eventsWritten: number;
  entitlementsWritten: number;
  replacementLinksWritten: number;
  certificatesRecorded: number;
  rolledBackBy: string | null;
  rolledBackAt: string | null;
  gapReportRunId: string | null;
  coverGaps: number | null;
  gapsByKind: MigrationGapCount[];
  label: string;
  handOver: MigrationLoadHandOver | null;
  undo: MigrationLoadUndo;
}

export interface MigrationDeltaCount { kind: string; label: string; count: number }
export interface MigrationDeltaFinding {
  itemId: string;
  kind: string;
  kindLabel: string;
  itemCode: string | null;
  serial: string | null;
  detail: string | null;
  unitId: string | null;
  conflictId: string | null;
}
export interface MigrationDeltaFindingPage {
  counts: MigrationDeltaCount[];
  total: number;
  loadTotal: number;
  items: MigrationDeltaFinding[];
  nextCursor: string | null;
}

export interface MigrationApplyRefusal {
  refusalId: string;
  planUnitId: string;
  itemCode: string;
  serial: string | null;
  reasons: string[];
}
export interface MigrationApplyRefusalCount { reason: string; label: string; count: number }
export interface MigrationApplyRefusalPage {
  items: MigrationApplyRefusal[];
  nextCursor: string | null;
  total: number;
  loadTotal: number;
  countsByReason: MigrationApplyRefusalCount[];
}

export interface TouchedMigrationLoadUnit { serial: string }
export interface TouchedMigrationLoadUnitPage { items: TouchedMigrationLoadUnit[]; nextCursor: string | null; total: number }

export interface RollBackMigrationLoadResponse {
  loadId: string;
  outcome: string;
  unitsRemoved: number;
  eventsRemoved: number;
  touchedUnitCount: number;
  touchedUnits: TouchedMigrationLoadUnit[];
  touchedNextCursor: string | null;
  entitlementsRemoved: number;
  replacementLinksRemoved: number;
  certificatesRemoved: number;
}

export interface CanonicalFamilyCatalogueItem { code: string; label: string }
export interface CanonicalFamilyCatalogue { families: CanonicalFamilyCatalogueItem[] }

export interface MigrationReviewRow {
  itemId: string;
  serialAsStored: string | null;
  itemCode: string | null;
  itemName: string | null;
  family: string | null;
  kind: MigrationReviewKind;
  ruleTried: string;
  candidateSummary: string;
  suggestion: MigrationReviewDecision | null;
  suggestedKeepSourceKey: string | null;
  suggestedKeepLabel: string | null;
  suggestionReason: string | null;
  hint: string | null;
  status: MigrationReviewStatus;
  carriedOver: boolean;
}

export interface MigrationReviewPage { items: MigrationReviewRow[]; nextCursor: string | null }

export interface MigrationCandidateLineage {
  sourceKey: string;
  serialAsStored: string | null;
  itemCode: string;
  itemName: string | null;
  transactions: number;
  firstReceipt: string | null;
  lastDelivery: string | null;
  lastDeliveryParty: string | null;
  hasEquipmentCard: boolean;
  cardNumber: string | null;
  hasContract: boolean;
  contractNumber: string | null;
  receiptDocumentNumber: number | null;
  receiptDocumentLine: number | null;
  disposition: string;
}

export interface MigrationReviewDecisionView {
  decisionId: string;
  decision: MigrationReviewDecision;
  keepSourceKey: string | null;
  reason: string;
  note: string | null;
  decidedByName: string;
  decidedAt: string;
  appliedToThisBatch: boolean;
}

export interface MigrationReviewDetail {
  itemId: string;
  kind: MigrationReviewKind;
  status: MigrationReviewStatus;
  serialAsStored: string | null;
  itemCode: string | null;
  itemName: string | null;
  ruleTried: string;
  candidates: MigrationCandidateLineage[];
  suggestion: MigrationReviewDecision | null;
  suggestedKeepSourceKey: string | null;
  suggestionReason: string | null;
  hint: string | null;
  earlierDecisions: MigrationReviewDecisionView[];
}

export interface MigrationReviewDecisionResponse {
  decisionId: string;
  itemId: string;
  decision: MigrationReviewDecision;
  status: MigrationReviewStatus;
  decidedAt: string;
}

export type TemplateMappingState = "waiting" | "needs_person" | "approved" | "waiting_for_policy_publication";
export type TemplateBlockResolution = "existing_policy" | "policy_to_create";

export interface TemplateSourceTerms {
  durationValue: number | null;
  durationUnit: string;
  durationUnitVerified: boolean;
  partsPct: number | null;
  labourPct: number | null;
  travelPct: number | null;
  responseHours: number | null;
  responseSource: string | null;
  coverageHours: string | null;
  contractType: string | null;
}

export interface TemplateUsageCounts { contracts: number; serials: number; activeContracts: number; activeSerials: number }
export interface TemplateMappingSummary {
  templateKey: string; templateName: string; state: TemplateMappingState; needsPerson: boolean;
  reviewReason: string | null; deletedInSource: boolean; sourceReference: string | null;
  sourceTerms: TemplateSourceTerms; usage: TemplateUsageCounts;
}
export interface TemplatePattern {
  patternKey: string; patternLabel: string; state: TemplateMappingState; templateCount: number;
  usage: TemplateUsageCounts; templates: TemplateMappingSummary[];
}
export interface TemplatePatternPage { items: TemplatePattern[]; nextCursor: string | null }
export interface TemplateStateSummary { templates: number; patterns: number; activeContracts: number }
export interface TemplateMappingSummaryView {
  templates: number; patterns: number; activeContracts: number; approved: TemplateStateSummary;
  waitingForApproval: TemplateStateSummary; needsPerson: TemplateStateSummary;
  waitingForPolicyPublication: TemplateStateSummary; policiesToCreate: number;
}
export interface TemplateWording { kind: string; text: string }
export interface TemplateBlock {
  familyCode: string; familyName: string; stacking: boolean; resolution: TemplateBlockResolution;
  policyId: string | null; policyCode: string | null; policyVersion: number | null; policyName: string | null;
  policyStatus: string | null; proposedTerms: TemplateSourceTerms;
}
export interface TemplateMappingDetail extends TemplateMappingSummary {
  sourceSystem: string; templateSourceKey: string; extractionBatchId: string; extractedAt: string;
  patternKey: string; patternLabel: string; wordingToReview: TemplateWording[]; blocks: TemplateBlock[];
  approvedBy: string | null; approvedAt: string | null; version: string;
}
export interface PolicyCatalogueFamily { familyCode: string; familyName: string; stacking: boolean; active: boolean }
export interface PolicyCataloguePolicy {
  familyCode: string; policyId: string; policyCode: string; version: number; policyName: string;
  terms: { durationValue: number; durationUnit: string; partsPct: number; labourPct: number; travelPct: number; serviceMode: string; responseHours: number | null; coverageHours: string | null };
}
export interface PolicyCatalogue { families: PolicyCatalogueFamily[]; policies: PolicyCataloguePolicy[] }
export interface TemplateKeywordRule { keyword: string; familyCode: string }
export interface TemplateKeywordRules { rules: TemplateKeywordRule[]; version: string }

export type ValidationResult = "pass" | "explained" | "fail" | "hard_fail" | "report" | "warning";
export type ValidationSignatureRole = "preparer" | "reviewer";
export type ValidationSampleOutcome = "traced" | "mismatch";

export interface ValidationSignature {
  role: ValidationSignatureRole;
  actorName: string;
  signedAt: string;
  superseded: boolean;
  supersededAt: string | null;
}

export interface ValidationCheck {
  checkId: string;
  code: string;
  label: string;
  limitKind: string;
  sourceTotal: number | null;
  targetTotal: number | null;
  difference: number | null;
  result: ValidationResult;
  explanation: string | null;
  explainedByName: string | null;
  explainedAt: string | null;
  howCounted: string;
  ranAt: string;
  durationMilliseconds: number;
  signatures: ValidationSignature[];
}

export interface MigrationValidation {
  runId: string;
  batchId: string;
  mapRunId: string;
  targetKind: string;
  rehearsal: boolean;
  rehearsalNumber: number | null;
  status: string;
  startedAt: string | null;
  finishedAt: string | null;
  loadDate: string | null;
  signoffRecordId: string | null;
  signoffRehearsal: boolean | null;
  signoffCreatedAt: string | null;
  signoffCreatedByName: string | null;
  hasFinalSignoff: boolean;
  failureCode: string | null;
  failureDetail: string | null;
  checksTotal: number;
  passed: number;
  explained: number;
  failed: number;
  hardFailed: number;
  signedChecks: number;
  units: number;
  events: number;
  eventsShortPercent: number | null;
  sampleTraced: number;
  sampleTotal: number;
  hardFailSummary: string | null;
  gapsByKind: MigrationGapCount[];
  checks: ValidationCheck[];
}

export interface ValidationCheckPart { code: string; label: string; value: number }
export interface ValidationCheckDetail {
  check: ValidationCheck;
  parts: ValidationCheckPart[];
  signatureHistory: ValidationSignature[];
}

export interface ValidationDifference {
  differenceId: string;
  kind: string;
  itemCode: string | null;
  warehouse: string | null;
  serial: string | null;
  sourceValue: number;
  targetValue: number;
  difference: number;
  detail: string | null;
}
export interface ValidationDifferencePage { items: ValidationDifference[]; nextCursor: string | null }

export interface ValidationSample {
  sampleId: string;
  serial: string;
  itemCode: string;
  itemFamily: string;
  ageBand: string;
  sourceTrailJson: string;
  targetTrailJson: string;
  outcome: ValidationSampleOutcome | null;
  note: string | null;
  tracedByName: string | null;
  tracedAt: string | null;
}
export interface ValidationSamplePage { items: ValidationSample[]; nextCursor: string | null }

export interface ValidationSettings {
  historicalTolerancePercent: number;
  sampleSize: number;
  ageBandYears: number[];
  version: string;
}

export interface StartValidationResponse { runId: string; batchId: string; status: string; targetKind: string }
export interface SignoffLine {
  position: number;
  reportName: string;
  sourceTotal: number | null;
  targetTotal: number | null;
  difference: number | null;
  explanation: string | null;
  result: ValidationResult;
  preparerName: string;
  preparedAt: string;
  reviewerName: string;
  reviewedAt: string;
}
export interface SignoffRecord {
  recordId: string;
  runId: string;
  rehearsal: boolean;
  createdAt: string;
  createdByName: string;
  lines: SignoffLine[];
}

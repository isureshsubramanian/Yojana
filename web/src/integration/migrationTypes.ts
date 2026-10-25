import * as z from "zod/mini";
import { cursorPage } from "./adminTypes.ts";

export const migrationLoadKind = z.enum(["rehearsal", "final", "delta"]);
export type MigrationLoadKind = z.infer<typeof migrationLoadKind>;
export const migrationOutcome = z.enum(["stop_load", "needs_person", "fixed_by_rule", "loaded_with_note"]);
export type MigrationOutcome = z.infer<typeof migrationOutcome>;
export const migrationReviewDecision = z.enum(["merge_into", "keep_apart", "fix_in_incumbent", "accept_as_recorded",
  "exclude_from_history"]);
export type MigrationReviewDecision = z.infer<typeof migrationReviewDecision>;
export const migrationReviewKind = z.enum(["same_serial_one_item", "same_serial_different_items",
  "orphan_equipment_card", "serial_in_user_field", "history_needs_review"]);
export type MigrationReviewKind = z.infer<typeof migrationReviewKind>;
export const migrationReviewStatus = z.enum(["waiting", "decided", "carried_over"]);
export type MigrationReviewStatus = z.infer<typeof migrationReviewStatus>;
export const templateBlockResolution = z.enum(["existing_policy", "policy_to_create"]);
export type TemplateBlockResolution = z.infer<typeof templateBlockResolution>;
export const templateMappingState = z.enum(["waiting", "needs_person", "approved", "waiting_for_policy_publication"]);
export type TemplateMappingState = z.infer<typeof templateMappingState>;
export const validationResult = z.enum(["pass", "explained", "fail", "hard_fail", "report", "warning"]);
export type ValidationResult = z.infer<typeof validationResult>;
export const validationSampleOutcome = z.enum(["traced", "mismatch"]);
export type ValidationSampleOutcome = z.infer<typeof validationSampleOutcome>;
export const validationSignatureRole = z.enum(["preparer", "reviewer"]);
export type ValidationSignatureRole = z.infer<typeof validationSignatureRole>;
export const migrationSource = z.object({ sourceId: z.string(), displayLabel: z.string(), sourceSystem: z.string(),
  runtime: z.string(), route: z.string(), databaseName: z.string(), schemaName: z.string(),
  linkedServerName: z.nullable(z.string()), commandTimeoutSeconds: z.number(), hasConnectionString: z.boolean(),
  licencePosition: z.string(), licenceConfirmedByName: z.nullable(z.string()),
  licenceConfirmedAt: z.nullable(z.string()), licenceContractReference: z.nullable(z.string()),
  licenceNote: z.nullable(z.string()), createdAt: z.string(), updatedAt: z.string() });
export type MigrationSource = z.infer<typeof migrationSource>;
export const migrationSourceTestTable = z.object({ table: z.string(), rowCount: z.nullable(z.number()),
  error: z.nullable(z.string()), role: z.string(), label: z.nullable(z.string()) });
export type MigrationSourceTestTable = z.infer<typeof migrationSourceTestTable>;
export const migrationSourceTestResult = z.object({ sourceId: z.string(), tables: z.array(migrationSourceTestTable) });
export type MigrationSourceTestResult = z.infer<typeof migrationSourceTestResult>;
export const migrationBatchTable = z.object({ table: z.string(), role: z.string(), label: z.string(),
  rowsRead: z.number(), inserted: z.number(), updated: z.number(), unchanged: z.number(), absent: z.number(),
  durationMilliseconds: z.number() });
export type MigrationBatchTable = z.infer<typeof migrationBatchTable>;
export const migrationBatch = z.object({ batchId: z.string(), displayNumber: z.string(), sourceId: z.string(),
  sourceSystem: z.string(), kind: z.string(), requestedByName: z.string(), requestedAt: z.string(),
  startedAt: z.nullable(z.string()), finishedAt: z.nullable(z.string()), status: z.string(),
  failure: z.nullable(z.string()), tables: z.nullable(z.array(migrationBatchTable)) });
export type MigrationBatch = z.infer<typeof migrationBatch>;
export const startMigrationBatchResponse = z.object({ batchId: z.string(), status: z.string(),
  failure: z.nullable(z.string()) });
export type StartMigrationBatchResponse = z.infer<typeof startMigrationBatchResponse>;
export const migrationProfileClass = z.object({ code: z.string(), name: z.string(), outcome: migrationOutcome,
  outcomeLabel: z.string(), count: z.nullable(z.number()), change: z.nullable(z.number()),
  note: z.nullable(z.string()) });
export type MigrationProfileClass = z.infer<typeof migrationProfileClass>;
export const migrationProfile = z.object({ runId: z.string(), batchId: z.string(), startedAt: z.nullable(z.string()),
  finishedAt: z.nullable(z.string()), status: z.string(), classes: z.array(migrationProfileClass) });
export type MigrationProfile = z.infer<typeof migrationProfile>;
export const migrationProfileSample = z.object({ sourceKey: z.string(), serial: z.nullable(z.string()),
  item: z.nullable(z.string()), facts: z.record(z.string(), z.nullable(z.string())) });
export type MigrationProfileSample = z.infer<typeof migrationProfileSample>;
export const migrationProfileSamples = z.object({ runId: z.string(), classCode: z.string(),
  samples: z.array(migrationProfileSample) });
export type MigrationProfileSamples = z.infer<typeof migrationProfileSamples>;
export const migrationOutcomeChoice = z.object({ classCode: z.string(), outcome: migrationOutcome });
export type MigrationOutcomeChoice = z.infer<typeof migrationOutcomeChoice>;
export const migrationOutcomeSettings = z.object({ classes: z.array(migrationOutcomeChoice), version: z.string() });
export type MigrationOutcomeSettings = z.infer<typeof migrationOutcomeSettings>;
export const canonicalisationGap = z.object({ kind: migrationReviewKind, reason: z.string() });
export type CanonicalisationGap = z.infer<typeof canonicalisationGap>;
export const migrationReviewCount = z.object({ key: z.string(), count: z.number() });
export type MigrationReviewCount = z.infer<typeof migrationReviewCount>;
export const canonicalisationRun = z.object({ runId: z.string(), batchId: z.string(),
  startedAt: z.nullable(z.string()), finishedAt: z.nullable(z.string()), status: z.string(),
  failure: z.nullable(z.string()), candidateRowsTotal: z.number(), candidateRowsExact: z.number(),
  candidateRowsTrimmedUpper: z.number(), candidateRowsFamilyNormalised: z.number(), candidateRowsMerged: z.number(),
  waitingReviewItems: z.number(), waitingSameItemReviewItems: z.number(), waitingAcrossItemsReviewItems: z.number(),
  waitingReviewItemsByKind: z.array(migrationReviewCount), decidedReviewItems: z.number(),
  decidedReviewItemsByDecision: z.array(migrationReviewCount), carriedOverReviewItems: z.number(),
  unverifiedDates: z.number(), gaps: z.array(canonicalisationGap) });
export type CanonicalisationRun = z.infer<typeof canonicalisationRun>;
export const reconstructionCount = z.object({ key: z.string(), count: z.number() });
export type ReconstructionCount = z.infer<typeof reconstructionCount>;
export const migrationReconstruction = z.object({ runId: z.string(), batchId: z.string(),
  canonicalisationRunId: z.string(), startedAt: z.nullable(z.string()), finishedAt: z.nullable(z.string()),
  status: z.string(), failure: z.nullable(z.string()), candidatesReconstructed: z.number(),
  candidatesSkippedByDisposition: z.number(), eventsByMovementKind: z.array(reconstructionCount),
  historyDepth: z.array(reconstructionCount), cancellationPairsRemoved: z.number(),
  queuedForReviewByReason: z.array(reconstructionCount) });
export type MigrationReconstruction = z.infer<typeof migrationReconstruction>;
export const mappingCount = z.object({ key: z.string(), count: z.number() });
export type MappingCount = z.infer<typeof mappingCount>;
export const migrationGapCount = z.object({ kind: z.string(), label: z.string(), count: z.number() });
export type MigrationGapCount = z.infer<typeof migrationGapCount>;
export const migrationPlanGap = z.object({ gapId: z.string(), planUnitId: z.nullable(z.string()),
  itemCode: z.nullable(z.string()), itemName: z.nullable(z.string()), serial: z.nullable(z.string()), kind: z.string(),
  label: z.string(), reason: z.string(), sourceKey: z.nullable(z.string()) });
export type MigrationPlanGap = z.infer<typeof migrationPlanGap>;
export const migrationPlanGapPage = z.object({ items: z.array(migrationPlanGap), nextCursor: z.nullable(z.string()) });
export type MigrationPlanGapPage = z.infer<typeof migrationPlanGapPage>;
export const migrationMap = z.object({ runId: z.string(), batchId: z.string(), reconstructionRunId: z.string(),
  cutoverDate: z.string(), startedAt: z.nullable(z.string()), finishedAt: z.nullable(z.string()), status: z.string(),
  failure: z.nullable(z.string()), unitsByOrigin: z.array(mappingCount), unitsByLocationState: z.array(mappingCount),
  identifiersByIssuer: z.array(mappingCount), eventsByProvenance: z.array(mappingCount),
  ownership: z.array(mappingCount), replacementLinks: z.number(), entitlementsByFamily: z.array(mappingCount),
  entitlementsByDateSource: z.array(mappingCount), expiredEntitlements: z.number(),
  gapsByKind: z.array(migrationGapCount), excludedCandidates: z.number(), waitingCandidatesLeftOut: z.number() });
export type MigrationMap = z.infer<typeof migrationMap>;
export const conditionMappingRule = z.object({ cardStatus: z.nullable(z.string()),
  lastMovementKind: z.nullable(z.string()), grade: z.string() });
export type ConditionMappingRule = z.infer<typeof conditionMappingRule>;
export const replacementLinkValues = z.object({ direction: z.string(), replacementReason: z.string(),
  state: z.string(), replacementConditionAllowed: z.string(), pricePolicy: z.string(), differenceAmount: z.number(),
  currency: z.string() });
export type ReplacementLinkValues = z.infer<typeof replacementLinkValues>;
export const mappingPolicyChoice = z.object({ policyId: z.string(), policyCode: z.string(), version: z.number(),
  policyName: z.string(), familyCode: z.string() });
export type MappingPolicyChoice = z.infer<typeof mappingPolicyChoice>;
export const mappingSettings = z.object({ allowWaitingReviews: z.boolean(), cutoverDate: z.nullable(z.string()),
  conditionMap: z.array(conditionMappingRule), linkDefaults: replacementLinkValues,
  cardCoverPolicyId: z.nullable(z.string()), serialMasterCoverPolicyId: z.nullable(z.string()),
  cardCoverPolicy: z.nullable(mappingPolicyChoice), serialMasterCoverPolicy: z.nullable(mappingPolicyChoice),
  approvedPublishedPolicies: z.nullable(z.array(mappingPolicyChoice)), version: z.string() });
export type MappingSettings = z.infer<typeof mappingSettings>;
export const linkOverride = z.object({ sourceKey: z.string(), direction: z.nullable(z.string()),
  replacementReason: z.nullable(z.string()), state: z.nullable(z.string()),
  replacementConditionAllowed: z.nullable(z.string()), pricePolicy: z.nullable(z.string()),
  differenceAmount: z.nullable(z.number()), currency: z.nullable(z.string()), uploadedAt: z.string(),
  uploadedByName: z.string(), uploadFileName: z.string(), unused: z.boolean() });
export type LinkOverride = z.infer<typeof linkOverride>;
export const linkOverridePage = z.object({ items: z.array(linkOverride), nextCursor: z.nullable(z.string()),
  totalCount: z.number(), unusedCount: z.number() });
export type LinkOverridePage = z.infer<typeof linkOverridePage>;
export const linkOverrideUploadError = z.object({ row: z.number(), column: z.string(), reason: z.string() });
export type LinkOverrideUploadError = z.infer<typeof linkOverrideUploadError>;
export const linkOverrideUploadResponse = z.object({ added: z.number(), changed: z.number(), removed: z.number(),
  unchanged: z.number(), errorTotal: z.number(), errors: z.array(linkOverrideUploadError) });
export type LinkOverrideUploadResponse = z.infer<typeof linkOverrideUploadResponse>;
export const currencyInfo = z.object({ code: z.string(), name: z.string(), symbol: z.string(),
  minorDigits: z.number() });
export type CurrencyInfo = z.infer<typeof currencyInfo>;
export const startMigrationApplyResponse = z.object({ loadId: z.string(), batchId: z.string(), mapRunId: z.string(),
  rehearsal: z.boolean(), status: z.string() });
export type StartMigrationApplyResponse = z.infer<typeof startMigrationApplyResponse>;
export const migrationLoadActionReason = z.object({ code: z.string(), message: z.string() });
export type MigrationLoadActionReason = z.infer<typeof migrationLoadActionReason>;
export const migrationLoadAction = z.object({ allowed: z.boolean(), reasons: z.array(migrationLoadActionReason) });
export type MigrationLoadAction = z.infer<typeof migrationLoadAction>;
export const migrationDeltaLoadAction = z.extend(migrationLoadAction, { baseLoadId: z.nullable(z.string()),
  baseLabel: z.nullable(z.string()) });
export type MigrationDeltaLoadAction = z.infer<typeof migrationDeltaLoadAction>;
export const migrationLoadReadiness = z.object({ rehearsal: migrationLoadAction, final: migrationLoadAction,
  delta: migrationDeltaLoadAction });
export type MigrationLoadReadiness = z.infer<typeof migrationLoadReadiness>;
export const migrationLoadUndo = z.object({ allowed: z.boolean(), reason: z.nullable(z.string()) });
export type MigrationLoadUndo = z.infer<typeof migrationLoadUndo>;
export const migrationLoadHandOver = z.object({ connectionId: z.string(), connectionName: z.string(),
  from: z.nullable(z.string()), syncState: z.string(), note: z.nullable(z.string()) });
export type MigrationLoadHandOver = z.infer<typeof migrationLoadHandOver>;
export const migrationSyncControlResult = z.object({ connectionId: z.string(), syncState: z.string(),
  notifications: z.enum(["pending", "held", "not_held", "not_supported"]), waitingOutbound: z.number(),
  failureCode: z.nullable(z.string()), warning: z.nullable(z.string()) });
export type MigrationSyncControlResult = z.infer<typeof migrationSyncControlResult>;
export const migrationLoadSummary = z.object({ loadId: z.string(), label: z.string(), kind: migrationLoadKind,
  status: z.string(), batchId: z.string(), batchDisplayNumber: z.string(), baseLoadId: z.nullable(z.string()),
  baseLabel: z.nullable(z.string()), requestedByName: z.string(), requestedAt: z.string(),
  startedAt: z.nullable(z.string()), finishedAt: z.nullable(z.string()), applied: z.number(), refused: z.number(),
  alreadyInModule: z.number(), coverReplaced: z.number(), coverRefused: z.number(), ownerDiffers: z.number(),
  goneFromSource: z.number(), eventsWritten: z.number(), entitlementsWritten: z.number(),
  replacementLinksWritten: z.number(), rolledBackAt: z.nullable(z.string()), rolledBackByName: z.nullable(z.string()),
  undo: migrationLoadUndo, handOver: z.nullable(migrationLoadHandOver) });
export type MigrationLoadSummary = z.infer<typeof migrationLoadSummary>;
export const migrationLoadDetail = z.extend(migrationLoadSummary, { batchStartedAt: z.nullable(z.string()),
  batchFinishedAt: z.nullable(z.string()) });
export type MigrationLoadDetail = z.infer<typeof migrationLoadDetail>;
export const migrationLoadPage = z.object({ items: z.array(migrationLoadSummary), nextCursor: z.nullable(z.string()) });
export type MigrationLoadPage = z.infer<typeof migrationLoadPage>;
export const migrationApply = z.object({ loadId: z.string(), batchId: z.string(), mapRunId: z.string(),
  rehearsal: z.boolean(), status: z.string(), requestedByName: z.string(), requestedAt: z.string(),
  startedAt: z.nullable(z.string()), finishedAt: z.nullable(z.string()), failure: z.nullable(z.string()),
  unitsInPlan: z.number(), applied: z.number(), alreadyApplied: z.number(), refused: z.number(),
  eventsWritten: z.number(), entitlementsWritten: z.number(), replacementLinksWritten: z.number(),
  certificatesRecorded: z.number(), rolledBackBy: z.nullable(z.string()), rolledBackAt: z.nullable(z.string()),
  gapReportRunId: z.nullable(z.string()), coverGaps: z.nullable(z.number()),
  gapsByKind: z.nullable(z.array(migrationGapCount)), label: z.nullable(z.string()),
  handOver: z.nullable(migrationLoadHandOver), undo: z.nullable(migrationLoadUndo) });
export type MigrationApply = z.infer<typeof migrationApply>;
export const migrationDeltaCount = z.object({ kind: z.string(), label: z.string(), count: z.number() });
export type MigrationDeltaCount = z.infer<typeof migrationDeltaCount>;
export const migrationDeltaFinding = z.object({ itemId: z.string(), kind: z.string(), kindLabel: z.string(),
  itemCode: z.nullable(z.string()), serial: z.nullable(z.string()), detail: z.nullable(z.string()),
  unitId: z.nullable(z.string()), conflictId: z.nullable(z.string()) });
export type MigrationDeltaFinding = z.infer<typeof migrationDeltaFinding>;
export const migrationDeltaFindingPage = z.object({ counts: z.array(migrationDeltaCount), total: z.number(),
  loadTotal: z.number(), items: z.array(migrationDeltaFinding), nextCursor: z.nullable(z.string()) });
export type MigrationDeltaFindingPage = z.infer<typeof migrationDeltaFindingPage>;
export const migrationApplyRefusal = z.object({ refusalId: z.string(), planUnitId: z.string(), itemCode: z.string(),
  serial: z.nullable(z.string()), reasons: z.array(z.string()) });
export type MigrationApplyRefusal = z.infer<typeof migrationApplyRefusal>;
export const migrationApplyRefusalCount = z.object({ reason: z.string(), label: z.string(), count: z.number() });
export type MigrationApplyRefusalCount = z.infer<typeof migrationApplyRefusalCount>;
export const migrationApplyRefusalPage = z.object({ items: z.array(migrationApplyRefusal),
  nextCursor: z.nullable(z.string()), total: z.number(), loadTotal: z.number(),
  countsByReason: z.array(migrationApplyRefusalCount) });
export type MigrationApplyRefusalPage = z.infer<typeof migrationApplyRefusalPage>;
export const touchedMigrationLoadUnit = z.object({ serial: z.string() });
export type TouchedMigrationLoadUnit = z.infer<typeof touchedMigrationLoadUnit>;
export const touchedMigrationLoadUnitPage = z.object({ items: z.array(touchedMigrationLoadUnit),
  nextCursor: z.nullable(z.string()), total: z.number() });
export type TouchedMigrationLoadUnitPage = z.infer<typeof touchedMigrationLoadUnitPage>;
export const rollBackMigrationLoadResponse = z.object({ loadId: z.string(), outcome: z.string(),
  unitsRemoved: z.number(), eventsRemoved: z.number(), touchedUnitCount: z.number(),
  touchedUnits: z.array(touchedMigrationLoadUnit), touchedNextCursor: z.nullable(z.string()),
  entitlementsRemoved: z.number(), replacementLinksRemoved: z.number(), certificatesRemoved: z.number() });
export type RollBackMigrationLoadResponse = z.infer<typeof rollBackMigrationLoadResponse>;
export const canonicalFamilyCatalogueItem = z.object({ code: z.string(), label: z.string() });
export type CanonicalFamilyCatalogueItem = z.infer<typeof canonicalFamilyCatalogueItem>;
export const canonicalFamilyCatalogue = z.object({ families: z.array(canonicalFamilyCatalogueItem) });
export type CanonicalFamilyCatalogue = z.infer<typeof canonicalFamilyCatalogue>;
export const migrationReviewRow = z.object({ itemId: z.string(), serialAsStored: z.nullable(z.string()),
  itemCode: z.nullable(z.string()), itemName: z.nullable(z.string()), family: z.nullable(z.string()),
  kind: migrationReviewKind, ruleTried: z.string(), candidateSummary: z.string(),
  suggestion: z.nullable(migrationReviewDecision), suggestedKeepSourceKey: z.nullable(z.string()),
  suggestedKeepLabel: z.nullable(z.string()), suggestionReason: z.nullable(z.string()), hint: z.nullable(z.string()),
  status: migrationReviewStatus, carriedOver: z.boolean() });
export type MigrationReviewRow = z.infer<typeof migrationReviewRow>;
export const migrationReviewPage = z.object({ items: z.array(migrationReviewRow), nextCursor: z.nullable(z.string()) });
export type MigrationReviewPage = z.infer<typeof migrationReviewPage>;
export const migrationCandidateLineage = z.object({ sourceKey: z.string(), serialAsStored: z.nullable(z.string()),
  itemCode: z.string(), itemName: z.nullable(z.string()), transactions: z.number(),
  firstReceipt: z.nullable(z.string()), lastDelivery: z.nullable(z.string()),
  lastDeliveryParty: z.nullable(z.string()), hasEquipmentCard: z.boolean(), cardNumber: z.nullable(z.string()),
  hasContract: z.boolean(), contractNumber: z.nullable(z.string()), receiptDocumentNumber: z.nullable(z.number()),
  receiptDocumentLine: z.nullable(z.number()), disposition: z.string() });
export type MigrationCandidateLineage = z.infer<typeof migrationCandidateLineage>;
export const migrationReviewDecisionView = z.object({ decisionId: z.string(), decision: migrationReviewDecision,
  keepSourceKey: z.nullable(z.string()), reason: z.string(), note: z.nullable(z.string()), decidedByName: z.string(),
  decidedAt: z.string(), appliedToThisBatch: z.boolean() });
export type MigrationReviewDecisionView = z.infer<typeof migrationReviewDecisionView>;
export const migrationReviewDetail = z.object({ itemId: z.string(), kind: migrationReviewKind,
  status: migrationReviewStatus, serialAsStored: z.nullable(z.string()), itemCode: z.nullable(z.string()),
  itemName: z.nullable(z.string()), ruleTried: z.string(), candidates: z.array(migrationCandidateLineage),
  suggestion: z.nullable(migrationReviewDecision), suggestedKeepSourceKey: z.nullable(z.string()),
  suggestionReason: z.nullable(z.string()), hint: z.nullable(z.string()),
  earlierDecisions: z.array(migrationReviewDecisionView) });
export type MigrationReviewDetail = z.infer<typeof migrationReviewDetail>;
export const migrationReviewDecisionResponse = z.object({ decisionId: z.string(), itemId: z.string(),
  decision: migrationReviewDecision, status: migrationReviewStatus, decidedAt: z.string() });
export type MigrationReviewDecisionResponse = z.infer<typeof migrationReviewDecisionResponse>;
export const templateSourceTerms = z.object({ durationValue: z.nullable(z.number()), durationUnit: z.string(),
  durationUnitVerified: z.boolean(), partsPct: z.nullable(z.number()), labourPct: z.nullable(z.number()),
  travelPct: z.nullable(z.number()), responseHours: z.nullable(z.number()), responseSource: z.nullable(z.string()),
  coverageHours: z.nullable(z.string()), contractType: z.nullable(z.string()) });
export type TemplateSourceTerms = z.infer<typeof templateSourceTerms>;
export const templateUsageCounts = z.object({ contracts: z.number(), serials: z.number(), activeContracts: z.number(),
  activeSerials: z.number() });
export type TemplateUsageCounts = z.infer<typeof templateUsageCounts>;
export const templateMappingSummary = z.object({ templateKey: z.string(), templateName: z.string(),
  state: templateMappingState, needsPerson: z.boolean(), reviewReason: z.nullable(z.string()),
  deletedInSource: z.boolean(), sourceReference: z.nullable(z.string()), sourceTerms: templateSourceTerms,
  usage: templateUsageCounts });
export type TemplateMappingSummary = z.infer<typeof templateMappingSummary>;
export const templatePattern = z.object({ patternKey: z.string(), patternLabel: z.string(),
  state: templateMappingState, templateCount: z.number(), usage: templateUsageCounts,
  templates: z.array(templateMappingSummary) });
export type TemplatePattern = z.infer<typeof templatePattern>;
export const templatePatternPage = z.object({ items: z.array(templatePattern), nextCursor: z.nullable(z.string()) });
export type TemplatePatternPage = z.infer<typeof templatePatternPage>;
export const templateStateSummary = z.object({ templates: z.number(), patterns: z.number(),
  activeContracts: z.number() });
export type TemplateStateSummary = z.infer<typeof templateStateSummary>;
export const templateMappingSummaryView = z.object({ templates: z.number(), patterns: z.number(),
  activeContracts: z.number(), approved: templateStateSummary, waitingForApproval: templateStateSummary,
  needsPerson: templateStateSummary, waitingForPolicyPublication: templateStateSummary, policiesToCreate: z.number() });
export type TemplateMappingSummaryView = z.infer<typeof templateMappingSummaryView>;
export const templateWording = z.object({ kind: z.string(), text: z.string() });
export type TemplateWording = z.infer<typeof templateWording>;
export const templateBlock = z.object({ familyCode: z.string(), familyName: z.string(), stacking: z.boolean(),
  resolution: templateBlockResolution, policyId: z.nullable(z.string()), policyCode: z.nullable(z.string()),
  policyVersion: z.nullable(z.number()), policyName: z.nullable(z.string()), policyStatus: z.nullable(z.string()),
  proposedTerms: templateSourceTerms });
export type TemplateBlock = z.infer<typeof templateBlock>;
export const templateMappingDetail = z.extend(templateMappingSummary, { sourceSystem: z.string(),
  templateSourceKey: z.string(), extractionBatchId: z.string(), extractedAt: z.string(), patternKey: z.string(),
  patternLabel: z.string(), wordingToReview: z.array(templateWording), blocks: z.array(templateBlock),
  approvedBy: z.nullable(z.string()), approvedAt: z.nullable(z.string()), version: z.string() });
export type TemplateMappingDetail = z.infer<typeof templateMappingDetail>;
export const policyCatalogueFamily = z.object({ familyCode: z.string(), familyName: z.string(), stacking: z.boolean(),
  active: z.boolean() });
export type PolicyCatalogueFamily = z.infer<typeof policyCatalogueFamily>;
export const policyCataloguePolicy = z.object({ familyCode: z.string(), policyId: z.string(), policyCode: z.string(),
  version: z.number(), policyName: z.string(), terms: z.object({ durationValue: z.number(), durationUnit: z.string(),
  partsPct: z.number(), labourPct: z.number(), travelPct: z.number(), serviceMode: z.string(),
  responseHours: z.nullable(z.number()), coverageHours: z.nullable(z.string()) }) });
export type PolicyCataloguePolicy = z.infer<typeof policyCataloguePolicy>;
export const policyCatalogue = z.object({ families: z.array(policyCatalogueFamily),
  policies: z.array(policyCataloguePolicy) });
export type PolicyCatalogue = z.infer<typeof policyCatalogue>;
export const templateKeywordRule = z.object({ keyword: z.string(), familyCode: z.string() });
export type TemplateKeywordRule = z.infer<typeof templateKeywordRule>;
export const templateKeywordRules = z.object({ rules: z.array(templateKeywordRule), version: z.string() });
export type TemplateKeywordRules = z.infer<typeof templateKeywordRules>;
export const validationSignature = z.object({ role: validationSignatureRole, actorName: z.string(),
  signedAt: z.string(), superseded: z.boolean(), supersededAt: z.nullable(z.string()) });
export type ValidationSignature = z.infer<typeof validationSignature>;
export const validationCheck = z.object({ checkId: z.string(), code: z.string(), label: z.string(),
  limitKind: z.string(), sourceTotal: z.nullable(z.number()), targetTotal: z.nullable(z.number()),
  difference: z.nullable(z.number()), result: validationResult, explanation: z.nullable(z.string()),
  explainedByName: z.nullable(z.string()), explainedAt: z.nullable(z.string()), howCounted: z.string(),
  ranAt: z.string(), durationMilliseconds: z.number(), signatures: z.array(validationSignature) });
export type ValidationCheck = z.infer<typeof validationCheck>;
export const migrationValidation = z.object({ runId: z.string(), batchId: z.string(), mapRunId: z.string(),
  targetKind: z.string(), rehearsal: z.boolean(), rehearsalNumber: z.nullable(z.number()), status: z.string(),
  startedAt: z.nullable(z.string()), finishedAt: z.nullable(z.string()), loadDate: z.nullable(z.string()),
  signoffRecordId: z.nullable(z.string()), signoffRehearsal: z.nullable(z.boolean()),
  signoffCreatedAt: z.nullable(z.string()), signoffCreatedByName: z.nullable(z.string()), hasFinalSignoff: z.boolean(),
  failureCode: z.nullable(z.string()), failureDetail: z.nullable(z.string()), checksTotal: z.number(),
  passed: z.number(), explained: z.number(), failed: z.number(), hardFailed: z.number(), signedChecks: z.number(),
  units: z.number(), events: z.number(), eventsShortPercent: z.nullable(z.number()), sampleTraced: z.number(),
  sampleTotal: z.number(), hardFailSummary: z.nullable(z.string()), gapsByKind: z.array(migrationGapCount),
  checks: z.array(validationCheck) });
export type MigrationValidation = z.infer<typeof migrationValidation>;
export const validationCheckPart = z.object({ code: z.string(), label: z.string(), value: z.number() });
export type ValidationCheckPart = z.infer<typeof validationCheckPart>;
export const validationCheckDetail = z.object({ check: validationCheck, parts: z.array(validationCheckPart),
  signatureHistory: z.array(validationSignature) });
export type ValidationCheckDetail = z.infer<typeof validationCheckDetail>;
export const validationDifference = z.object({ differenceId: z.string(), kind: z.string(),
  itemCode: z.nullable(z.string()), warehouse: z.nullable(z.string()), serial: z.nullable(z.string()),
  sourceValue: z.number(), targetValue: z.number(), difference: z.number(), detail: z.nullable(z.string()) });
export type ValidationDifference = z.infer<typeof validationDifference>;
export const validationDifferencePage = z.object({ items: z.array(validationDifference),
  nextCursor: z.nullable(z.string()) });
export type ValidationDifferencePage = z.infer<typeof validationDifferencePage>;
export const validationSample = z.object({ sampleId: z.string(), serial: z.string(), itemCode: z.string(),
  itemFamily: z.string(), ageBand: z.string(), sourceTrailJson: z.string(), targetTrailJson: z.string(),
  outcome: z.nullable(validationSampleOutcome), note: z.nullable(z.string()), tracedByName: z.nullable(z.string()),
  tracedAt: z.nullable(z.string()) });
export type ValidationSample = z.infer<typeof validationSample>;
export const validationSamplePage = z.object({ items: z.array(validationSample), nextCursor: z.nullable(z.string()) });
export type ValidationSamplePage = z.infer<typeof validationSamplePage>;
export const validationSettings = z.object({ historicalTolerancePercent: z.number(), sampleSize: z.number(),
  ageBandYears: z.array(z.number()), version: z.string() });
export type ValidationSettings = z.infer<typeof validationSettings>;
export const startValidationResponse = z.object({ runId: z.string(), batchId: z.string(), status: z.string(),
  targetKind: z.string() });
export type StartValidationResponse = z.infer<typeof startValidationResponse>;
export const signoffLine = z.object({ position: z.number(), reportName: z.string(),
  sourceTotal: z.nullable(z.number()), targetTotal: z.nullable(z.number()), difference: z.nullable(z.number()),
  explanation: z.nullable(z.string()), result: validationResult, preparerName: z.string(), preparedAt: z.string(),
  reviewerName: z.string(), reviewedAt: z.string() });
export type SignoffLine = z.infer<typeof signoffLine>;
export const signoffRecord = z.object({ recordId: z.string(), runId: z.string(), rehearsal: z.boolean(),
  createdAt: z.string(), createdByName: z.string(), lines: z.array(signoffLine) });
export type SignoffRecord = z.infer<typeof signoffRecord>;

export const migrationSourcePage = cursorPage(migrationSource);
export type MigrationSourcePage = z.infer<typeof migrationSourcePage>;

// table is the source's own name, shown only as data under the readable label the server supplies.

export const migrationBatchPage = cursorPage(migrationBatch);
export type MigrationBatchPage = z.infer<typeof migrationBatchPage>;


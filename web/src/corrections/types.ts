import * as z from "zod/mini";
import {
  commandRefusal,
  correctionAllocationPreview,
  correctionAsBuiltRequirementPreview,
  correctionCertificatePreview,
  correctionCommissionPreview,
  correctionEntitlementPreview,
  correctionEventPreview,
  correctionExemptionPreview,
  correctionPreview,
  correctionPrintJobPreview,
  documentRef,
  documentRefInput,
  documentRefRequest,
  selectionSourceInput,
  selectionSourceRequest,
  unitDetail,
  unitOutcome,
} from "./schemas.ts";

const text = z.string();
const number = z.number();
const nullableText = z.nullable(text);

// Common correction preview contracts are shared with workbenchApi through one explicit schema boundary.
export {
  commandRefusal,
  correctionAllocationPreview,
  correctionAsBuiltRequirementPreview,
  correctionCertificatePreview,
  correctionCommissionPreview,
  correctionEntitlementPreview,
  correctionEventPreview,
  correctionExemptionPreview,
  correctionPreview,
  correctionPrintJobPreview,
  unitOutcome,
};
export type CommandRefusal = z.infer<typeof commandRefusal>;
export type UnitOutcome = z.infer<typeof unitOutcome>;
export type CorrectionEventPreview = z.infer<typeof correctionEventPreview>;
export type CorrectionEntitlementPreview = z.infer<typeof correctionEntitlementPreview>;
export type CorrectionCertificatePreview = z.infer<typeof correctionCertificatePreview>;
export type CorrectionPrintJobPreview = z.infer<typeof correctionPrintJobPreview>;
export type CorrectionAllocationPreview = z.infer<typeof correctionAllocationPreview>;
export type CorrectionPreview = z.infer<typeof correctionPreview>;

export const correctionKpiTarget = z.object({ rootCauseCategory: number, maximumPerThousand: number });
export const correctionKpiTargetPolicy = z.object({ rootCauses: z.array(correctionKpiTarget) });
export const correctionKpiTargetPolicyRequest = z.looseObject({
  rootCauses: z.array(z.looseObject({ rootCauseCategory: number, maximumPerThousand: number })),
});
export const correctionKpiBaseline = z.object({
  status: text, historyDays: number, daysRemaining: number, startedOn: nullableText, targetsEffectiveOn: nullableText,
});
export const correctionKpiRow = z.object({
  bucket: text,
  dimension: text,
  corrections: number,
  unitsCorrected: number,
  unitsShipped: number,
  perThousand: z.nullable(number),
  reversedLater: number,
  targetPerThousand: z.nullable(number),
});
export const unclassifiedRequestRate = z.object({
  bucket: text, ratePercent: number, unclassifiedRequests: number, corrections: number, alert: z.boolean(),
});
export const unclassifiedRequestAlert = z.object({
  thresholdPercent: number,
  ratePercent: number,
  unclassifiedRequests: number,
  corrections: number,
  alert: z.boolean(),
  months: z.nullable(z.array(unclassifiedRequestRate)),
});
export const correctionKpiReport = z.object({
  from: text,
  to: text,
  breakdown: text,
  itemId: nullableText,
  baseline: correctionKpiBaseline,
  rows: z.array(correctionKpiRow),
  unclassifiedAlert: z.nullable(unclassifiedRequestAlert),
});
export type CorrectionKpiTarget = z.infer<typeof correctionKpiTarget>;
export type CorrectionKpiTargetPolicy = z.infer<typeof correctionKpiTargetPolicy>;
export type CorrectionKpiBaseline = z.infer<typeof correctionKpiBaseline>;
export type CorrectionKpiRow = z.infer<typeof correctionKpiRow>;
export type UnclassifiedRequestRate = z.infer<typeof unclassifiedRequestRate>;
export type UnclassifiedRequestAlert = z.infer<typeof unclassifiedRequestAlert>;
export type CorrectionKpiReport = z.infer<typeof correctionKpiReport>;

export const reassignAllocation = z.object({
  commandId: text,
  fromProductionOrder: documentRefInput,
  toProductionOrder: documentRefInput,
  mode: text,
  fromSerials: selectionSourceInput,
  toSerials: z.nullable(selectionSourceInput),
  reason: text,
  eventTime: text,
  actorRef: text,
  legalEntityId: z.optional(text),
  note: nullableText,
});
export const reassignAllocationRequest = z.looseObject({
  commandId: text,
  fromProductionOrder: documentRefRequest,
  toProductionOrder: documentRefRequest,
  mode: text,
  fromSerials: selectionSourceRequest,
  toSerials: z.nullable(selectionSourceRequest),
  reason: text,
  eventTime: text,
  actorRef: text,
  legalEntityId: z.optional(text),
  note: nullableText,
});
export const reassignAllocationResult = z.object({
  correctionId: text, eventId: nullableText, preview: correctionPreview, replayed: z.boolean(),
});
export type ReassignAllocation = z.infer<typeof reassignAllocation>;
export type ReassignAllocationResult = z.infer<typeof reassignAllocationResult>;

export const correctionApprovalDecision = z.object({
  status: text,
  commitRoles: z.array(text),
  approverRoles: z.array(text),
  reviewTasks: z.array(text),
  refusal: z.nullable(commandRefusal),
});
export const mergeUnitsAlternative = z.object({
  resolution: text, available: z.boolean(), summary: text, refusal: z.nullable(commandRefusal),
});
export const previewMergeUnits = z.object({
  firstUnitId: text, secondUnitId: text, eventTime: text, survivorUnitId: nullableText,
});
export const previewMergeUnitsRequest = z.looseObject({
  firstUnitId: text, secondUnitId: text, eventTime: text, survivorUnitId: nullableText,
});
export const mergeUnitsPreviewResult = z.object({
  recommendedSurvivorUnitId: text,
  survivorUnitId: text,
  absorbedUnitId: text,
  preview: correctionPreview,
  approval: correctionApprovalDecision,
  alternatives: z.array(mergeUnitsAlternative),
});
export const mergeUnits = z.object({
  commandId: text,
  survivorUnitId: text,
  absorbedUnitId: text,
  reason: text,
  eventTime: text,
  actorRef: text,
  legalEntityId: z.optional(text),
  resolution: text,
  vendorSerialValue: nullableText,
  note: nullableText,
});
export const mergeUnitsRequest = z.looseObject({
  commandId: text,
  survivorUnitId: text,
  absorbedUnitId: text,
  reason: text,
  eventTime: text,
  actorRef: text,
  legalEntityId: z.optional(text),
  resolution: text,
  vendorSerialValue: nullableText,
  note: nullableText,
});
export const mergeUnitsResult = z.object({
  survivorUnitId: text,
  absorbedUnitId: text,
  resolution: text,
  correctionId: text,
  rowsRepointed: number,
  reversed: z.boolean(),
  replayed: z.boolean(),
});
export type CorrectionApprovalDecision = z.infer<typeof correctionApprovalDecision>;
export type MergeUnitsAlternative = z.infer<typeof mergeUnitsAlternative>;
export type PreviewMergeUnits = z.infer<typeof previewMergeUnits>;
export type MergeUnitsPreviewResult = z.infer<typeof mergeUnitsPreviewResult>;
export type MergeUnits = z.infer<typeof mergeUnits>;
export type MergeUnitsResult = z.infer<typeof mergeUnitsResult>;

export const identifierAssignmentInput = z.object({ value: text, unitId: z.optional(nullableText) });
const identifierAssignmentInputRequest = z.looseObject({ value: text, unitId: z.optional(nullableText) });
export const assignIdentifiers = z.object({
  commandId: text,
  productionOrder: documentRefInput,
  mode: text,
  lines: z.array(identifierAssignmentInput),
  reason: text,
  eventTime: text,
  actorRef: text,
  legalEntityId: z.optional(text),
  issuerPartyRef: nullableText,
  qualifier: nullableText,
  note: nullableText,
});
export const assignIdentifiersRequest = z.looseObject({
  commandId: text,
  productionOrder: documentRefRequest,
  mode: text,
  lines: z.array(identifierAssignmentInputRequest),
  reason: text,
  eventTime: text,
  actorRef: text,
  legalEntityId: z.optional(text),
  issuerPartyRef: nullableText,
  qualifier: nullableText,
  note: nullableText,
});
export const identifierAssignmentLineResult = z.object({
  index: number,
  requestedUnitId: nullableText,
  unitId: nullableText,
  rawValue: text,
  normalisedValue: nullableText,
  outcome: text,
  reason: nullableText,
  conflictingUnitId: nullableText,
  identifierId: nullableText,
  warnings: z.nullable(z.array(text)),
});
export const assignIdentifiersPreview = z.object({
  preview: correctionPreview,
  lines: z.array(identifierAssignmentLineResult),
  remainingCount: number,
  awaitingVendorSerialUnitIds: z.array(text),
  reversal: z.boolean(),
});
export const assignIdentifiersResult = z.object({
  correctionId: text,
  assigned: number,
  lines: z.array(identifierAssignmentLineResult),
  remainingCount: number,
  awaitingVendorSerialUnitIds: z.array(text),
  replayed: z.boolean(),
  reversal: z.boolean(),
});
export const awaitingVendorSerialUnit = z.object({
  unitId: text, itemId: text, provisionalIdentifier: text, allocatedAt: text,
});
export const awaitingVendorSerialReport = z.object({
  productionOrder: documentRef, count: number, units: z.array(awaitingVendorSerialUnit),
});
export type IdentifierAssignmentInput = z.infer<typeof identifierAssignmentInput>;
export type AssignIdentifiers = z.infer<typeof assignIdentifiers>;
export type IdentifierAssignmentLineResult = z.infer<typeof identifierAssignmentLineResult>;
export type AssignIdentifiersPreview = z.infer<typeof assignIdentifiersPreview>;
export type AssignIdentifiersResult = z.infer<typeof assignIdentifiersResult>;
export type AwaitingVendorSerialUnit = z.infer<typeof awaitingVendorSerialUnit>;
export type AwaitingVendorSerialReport = z.infer<typeof awaitingVendorSerialReport>;

export const workbenchChoiceOption = z.object({
  value: text, label: text, command: text, defaultReason: text, rootCause: number,
});
export const workbenchNarrowingQuestion = z.object({
  key: text, prompt: text, options: z.array(workbenchChoiceOption),
});
export const workbenchChoice = z.object({
  choice: text,
  label: text,
  area: text,
  helpText: text,
  commands: z.array(text),
  questions: z.array(workbenchNarrowingQuestion),
  defaultReason: text,
  rootCause: number,
  searchWords: z.nullable(z.array(text)),
  order: number,
  initiateRoles: z.nullable(z.array(text)),
  commitRoles: z.nullable(z.array(text)),
  status: text,
  standard: z.boolean(),
  fallback: z.boolean(),
});
const workbenchChoiceOptionRequest = z.looseObject({
  value: text, label: text, command: text, defaultReason: text, rootCause: number,
});
const workbenchNarrowingQuestionRequest = z.looseObject({
  key: text, prompt: text, options: z.array(workbenchChoiceOptionRequest),
});
const workbenchChoiceRequest = z.looseObject({
  choice: text,
  label: text,
  area: text,
  helpText: text,
  commands: z.array(text),
  questions: z.array(workbenchNarrowingQuestionRequest),
  defaultReason: text,
  rootCause: number,
  searchWords: z.nullable(z.array(text)),
  order: number,
  initiateRoles: z.nullable(z.array(text)),
  commitRoles: z.nullable(z.array(text)),
  status: text,
  standard: z.boolean(),
  fallback: z.boolean(),
});
export const workbenchChoiceConfiguration = z.object({
  version: number,
  choices: z.array(workbenchChoice),
  unclassifiedAlertPercent: number,
  updatedBy: text,
  updatedAt: text,
});
export const workbenchChoiceConfigurationVersion = z.object({
  configuration: workbenchChoiceConfiguration, validFrom: text, validTo: text,
});
export const workbenchChoiceSettingsView = z.object({
  current: workbenchChoiceConfiguration, history: z.array(workbenchChoiceConfigurationVersion),
});
export const updateWorkbenchChoices = z.object({
  choices: z.array(workbenchChoice), unclassifiedAlertPercent: number,
});
export const updateWorkbenchChoicesRequest = z.looseObject({
  choices: z.array(workbenchChoiceRequest), unclassifiedAlertPercent: number,
});
export type WorkbenchChoiceOption = z.infer<typeof workbenchChoiceOption>;
export type WorkbenchNarrowingQuestion = z.infer<typeof workbenchNarrowingQuestion>;
export type WorkbenchChoice = z.infer<typeof workbenchChoice>;
export type WorkbenchChoiceConfiguration = z.infer<typeof workbenchChoiceConfiguration>;
export type WorkbenchChoiceConfigurationVersion = z.infer<typeof workbenchChoiceConfigurationVersion>;
export type WorkbenchChoiceSettingsView = z.infer<typeof workbenchChoiceSettingsView>;
export type UpdateWorkbenchChoices = z.infer<typeof updateWorkbenchChoices>;

export const mergeRecordView = z.object({ first: unitDetail, second: unitDetail });
export type MergeRecordView = z.infer<typeof mergeRecordView>;

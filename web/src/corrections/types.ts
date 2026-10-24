import type { DocumentRef, SelectionSource, UnitDetail } from "../api/units";

export interface CommandRefusal { rule: string; message: string; unblockingStep: string | null }
export interface UnitOutcome { unitId: string; serial: string | null; outcome: string; reason: string | null; detail: string | null; unblockingStep: string | null }
export interface CorrectionEventPreview { unitId: string; eventType: string; effectiveAt: string; summary: string | null }
export interface CorrectionEntitlementPreview { unitId: string; change: string; entitlementId: string | null; summary: string | null }
export interface CorrectionCertificatePreview { unitId: string; certificateType: string; action: string; reason: string }
export interface CorrectionPrintJobPreview { unitId: string; jobType: string; reason: string }
export interface CorrectionAllocationPreview {
  pair: number; fromSerial: string; exchangeSerial: string | null; fromSerialRangeId: string; exchangeSerialRangeId: string | null;
  fromProductionOrder: DocumentRef; toProductionOrder: DocumentRef; outcome: string; rule: string | null; detail: string | null; unblockingStep: string | null;
}
export interface CorrectionPreview {
  selectionId: string | null; command: string; unitCount: number; change: number; skip: number; block: number;
  units: UnitOutcome[]; unitEvents: CorrectionEventPreview[]; entitlementChanges: CorrectionEntitlementPreview[];
  certificateVersions: CorrectionCertificatePreview[]; printJobs: CorrectionPrintJobPreview[]; refusal: CommandRefusal | null;
  allocationReassignments: CorrectionAllocationPreview[] | null;
}

export interface CorrectionKpiTarget { rootCauseCategory: number; maximumPerThousand: number }
export interface CorrectionKpiTargetPolicy { rootCauses: CorrectionKpiTarget[] }
export interface CorrectionKpiBaseline { status: string; historyDays: number; daysRemaining: number; startedOn: string | null; targetsEffectiveOn: string | null }
export interface CorrectionKpiRow { bucket: string; dimension: string; corrections: number; unitsCorrected: number; unitsShipped: number; perThousand: number | null; reversedLater: number; targetPerThousand: number | null }
export interface UnclassifiedRequestRate { bucket: string; ratePercent: number; unclassifiedRequests: number; corrections: number; alert: boolean }
export interface UnclassifiedRequestAlert { thresholdPercent: number; ratePercent: number; unclassifiedRequests: number; corrections: number; alert: boolean; months: UnclassifiedRequestRate[] | null }
export interface CorrectionKpiReport { from: string; to: string; breakdown: string; itemId: string | null; baseline: CorrectionKpiBaseline; rows: CorrectionKpiRow[]; unclassifiedAlert: UnclassifiedRequestAlert | null }

export interface ReassignAllocation {
  commandId: string; fromProductionOrder: DocumentRef; toProductionOrder: DocumentRef; mode: string; fromSerials: SelectionSource;
  toSerials: SelectionSource | null; reason: string; eventTime: string; actorRef: string; legalEntityId?: string; note: string | null;
}
export interface ReassignAllocationResult { correctionId: string; eventId: string | null; preview: CorrectionPreview; replayed: boolean }

export interface CorrectionApprovalDecision { status: string; commitRoles: string[]; approverRoles: string[]; reviewTasks: string[]; refusal: CommandRefusal | null }
export interface MergeUnitsAlternative { resolution: string; available: boolean; summary: string; refusal: CommandRefusal | null }
export interface PreviewMergeUnits { firstUnitId: string; secondUnitId: string; eventTime: string; survivorUnitId: string | null }
export interface MergeUnitsPreviewResult { recommendedSurvivorUnitId: string; survivorUnitId: string; absorbedUnitId: string; preview: CorrectionPreview; approval: CorrectionApprovalDecision; alternatives: MergeUnitsAlternative[] }
export interface MergeUnits { commandId: string; survivorUnitId: string; absorbedUnitId: string; reason: string; eventTime: string; actorRef: string; legalEntityId?: string; resolution: string; vendorSerialValue: string | null; note: string | null }
export interface MergeUnitsResult { survivorUnitId: string; absorbedUnitId: string; resolution: string; correctionId: string; rowsRepointed: number; reversed: boolean; replayed: boolean }

export interface IdentifierAssignmentInput { value: string; unitId?: string | null }
export interface AssignIdentifiers { commandId: string; productionOrder: DocumentRef; mode: string; lines: IdentifierAssignmentInput[]; reason: string; eventTime: string; actorRef: string; legalEntityId?: string; issuerPartyRef: string | null; qualifier: string | null; note: string | null }
export interface IdentifierAssignmentLineResult { index: number; requestedUnitId: string | null; unitId: string | null; rawValue: string; normalisedValue: string | null; outcome: string; reason: string | null; conflictingUnitId: string | null; identifierId: string | null; warnings: string[] | null }
export interface AssignIdentifiersPreview { preview: CorrectionPreview; lines: IdentifierAssignmentLineResult[]; remainingCount: number; awaitingVendorSerialUnitIds: string[]; reversal: boolean }
export interface AssignIdentifiersResult { correctionId: string; assigned: number; lines: IdentifierAssignmentLineResult[]; remainingCount: number; awaitingVendorSerialUnitIds: string[]; replayed: boolean; reversal: boolean }
export interface AwaitingVendorSerialUnit { unitId: string; itemId: string; provisionalIdentifier: string; allocatedAt: string }
export interface AwaitingVendorSerialReport { productionOrder: DocumentRef; count: number; units: AwaitingVendorSerialUnit[] }

export interface WorkbenchChoiceOption { value: string; label: string; command: string; defaultReason: string; rootCause: number }
export interface WorkbenchNarrowingQuestion { key: string; prompt: string; options: WorkbenchChoiceOption[] }
export interface WorkbenchChoice {
  choice: string; label: string; area: string; helpText: string; commands: string[]; questions: WorkbenchNarrowingQuestion[];
  defaultReason: string; rootCause: number; searchWords: string[] | null; order: number; initiateRoles: string[] | null;
  commitRoles: string[] | null; status: string; standard: boolean; fallback: boolean;
}
export interface WorkbenchChoiceConfiguration { version: number; choices: WorkbenchChoice[]; unclassifiedAlertPercent: number; updatedBy: string; updatedAt: string }
export interface WorkbenchChoiceConfigurationVersion { configuration: WorkbenchChoiceConfiguration; validFrom: string; validTo: string }
export interface WorkbenchChoiceSettingsView { current: WorkbenchChoiceConfiguration; history: WorkbenchChoiceConfigurationVersion[] }
export interface UpdateWorkbenchChoices { choices: WorkbenchChoice[]; unclassifiedAlertPercent: number }

export interface MergeRecordView { first: UnitDetail; second: UnitDetail }

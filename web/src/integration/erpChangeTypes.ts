import * as z from "zod/mini";
// Read records of the ERP change endpoints (Integration/ErpChange/ErpChangeContracts.cs). Actor fields are names the
// server resolved through the people directory; the client never shows an actor id.

export const stockResolutionCommand = z.enum(["correct_identifier", "merge_units", "change_custody"]);
export type StockResolutionCommand = z.infer<typeof stockResolutionCommand>;
export const unitHistoryChangeKind = z.enum(["unexpected", "deliberate"]);
export type UnitHistoryChangeKind = z.infer<typeof unitHistoryChangeKind>;
export const erpChangeConnectionView = z.object({ connectionId: z.string(), name: z.string(),
  connectorCode: z.string(), status: z.string(), health: z.string(), readOnly: z.boolean(), outboundMode: z.string() });
export type ErpChangeConnectionView = z.infer<typeof erpChangeConnectionView>;
export const erpChangeStepCheckView = z.object({ step: z.number(), passed: z.boolean(), counts: z.record(z.string(),
  z.number()), detail: z.string(), checkedAt: z.string(), checkedBy: z.string() });
export type ErpChangeStepCheckView = z.infer<typeof erpChangeStepCheckView>;
export const erpChangeTransitionView = z.object({ fromStep: z.number(), toStep: z.number(), kind: z.string(),
  occurredAt: z.string(), actorRef: z.string(), reason: z.nullable(z.string()) });
export type ErpChangeTransitionView = z.infer<typeof erpChangeTransitionView>;
export const dualRunSummaryView = z.object({ startedAt: z.nullable(z.string()), minimumDays: z.number(),
  elapsedDays: z.number(), cleanDaysRequired: z.number(), consecutiveCleanDays: z.number(),
  openDivergences: z.number(), agedOpenDivergences: z.number(), lastCheckedOn: z.nullable(z.string()) });
export type DualRunSummaryView = z.infer<typeof dualRunSummaryView>;
export const cutoverChecklistItemView = z.object({ code: z.string(), state: z.string(), detail: z.string(),
  evidence: z.nullable(z.string()), completedBy: z.nullable(z.string()), completedAt: z.nullable(z.string()) });
export type CutoverChecklistItemView = z.infer<typeof cutoverChecklistItemView>;
export const cutoverChecklistView = z.object({ runId: z.string(), cutoverAt: z.nullable(z.string()),
  items: z.array(cutoverChecklistItemView), canAdvance: z.boolean() });
export type CutoverChecklistView = z.infer<typeof cutoverChecklistView>;
export const parkedOutboundRow = z.object({ outboundCommandId: z.string(), documentKind: z.string(),
  documentReference: z.string(), unitCount: z.number(), parkedReason: z.string(), parkedAt: z.string(),
  ownedByManualTask: z.boolean() });
export type ParkedOutboundRow = z.infer<typeof parkedOutboundRow>;
export const parkedOutboundPage = z.object({ items: z.array(parkedOutboundRow), nextCursor: z.nullable(z.string()) });
export type ParkedOutboundPage = z.infer<typeof parkedOutboundPage>;
export const archiveStateView = z.object({ sourceArchived: z.boolean(), archivedAt: z.nullable(z.string()),
  activeConnections: z.number(), retainedMappings: z.number(), retainedInboundMessages: z.number() });
export type ArchiveStateView = z.infer<typeof archiveStateView>;
export const erpChangeRunbookView = z.object({ runId: z.string(), status: z.string(), currentStep: z.number(),
  source: erpChangeConnectionView, target: erpChangeConnectionView, startedBy: z.string(), startedAt: z.string(),
  closedBy: z.nullable(z.string()), closedAt: z.nullable(z.string()), closeReason: z.nullable(z.string()),
  checks: z.array(erpChangeStepCheckView), transitions: z.array(erpChangeTransitionView), unitsCompared: z.number(),
  openDifferences: z.number(), coveredOpenDifferences: z.number(), resolvedDifferences: z.number(),
  openMasterDataExceptions: z.number(), dualRun: z.nullable(dualRunSummaryView),
  cutoverChecklist: z.nullable(z.array(cutoverChecklistItemView)), archive: z.nullable(archiveStateView),
  unitHistoriesChanged: z.number(), deliberateUnitHistoriesChanged: z.number(),
  unitHistoryCheckedAt: z.nullable(z.string()) });
export type ErpChangeRunbookView = z.infer<typeof erpChangeRunbookView>;
export const erpChangeRunHistoryRow = z.object({ runId: z.string(), sourceName: z.string(), targetName: z.string(),
  startedAt: z.string(), finishedAt: z.nullable(z.string()), outcome: z.string(), startedBy: z.string(),
  currentStep: z.number(), currentStepName: z.string() });
export type ErpChangeRunHistoryRow = z.infer<typeof erpChangeRunHistoryRow>;
export const erpChangeRunHistoryPage = z.object({ items: z.array(erpChangeRunHistoryRow),
  nextCursor: z.nullable(z.string()) });
export type ErpChangeRunHistoryPage = z.infer<typeof erpChangeRunHistoryPage>;
export const unitHistoryChangeRow = z.object({ unitId: z.string(), identifier: z.string(), kind: unitHistoryChangeKind,
  eventsAdded: z.number(), lastRecordedAt: z.string() });
export type UnitHistoryChangeRow = z.infer<typeof unitHistoryChangeRow>;
export const unitHistoryChangePage = z.object({ items: z.array(unitHistoryChangeRow),
  nextCursor: z.nullable(z.string()), kind: unitHistoryChangeKind, total: z.number(), unexpectedTotal: z.number(),
  deliberateTotal: z.number(), checkedAt: z.nullable(z.string()) });
export type UnitHistoryChangePage = z.infer<typeof unitHistoryChangePage>;
export const erpChangeDivergenceRow = z.object({ divergenceId: z.string(), kind: z.string(), subjectType: z.string(),
  subjectId: z.nullable(z.string()), subjectRef: z.string(), sourceValue: z.string(), targetValue: z.string(),
  firstSeenAt: z.string(), lastSeenAt: z.string(), reviewDueAt: z.string(), status: z.string(),
  resolution: z.nullable(z.string()), resolutionNote: z.nullable(z.string()),
  resolutionCommandId: z.nullable(z.string()), resolvedBy: z.nullable(z.string()),
  resolvedAt: z.nullable(z.string()) });
export type ErpChangeDivergenceRow = z.infer<typeof erpChangeDivergenceRow>;
export const erpChangeDivergencePage = z.object({ items: z.array(erpChangeDivergenceRow),
  nextCursor: z.nullable(z.string()), open: z.number(), agedOpen: z.number(), resolved: z.number() });
export type ErpChangeDivergencePage = z.infer<typeof erpChangeDivergencePage>;
export const erpChangeCleanPeriodView = z.object({ days: z.number(), settingsVersion: z.string() });
export type ErpChangeCleanPeriodView = z.infer<typeof erpChangeCleanPeriodView>;
export const erpChangeNotificationSettingsView = z.object({ digestEnabled: z.boolean(), digestHour: z.number(),
  masterDataRecipientRoles: z.array(z.string()), stockRecipientRoles: z.array(z.string()),
  settingsVersion: z.string() });
export type ErpChangeNotificationSettingsView = z.infer<typeof erpChangeNotificationSettingsView>;
export const erpChangeMyWorkView = z.object({ runId: z.string(), currentStep: z.number(), sourceName: z.string(),
  targetName: z.string(), masterDataExceptions: z.number(), coveredStockDifferences: z.number(),
  otherStockDifferencesAwaitingSignOff: z.number(), stockTypeDivergences: z.number(), screenPath: z.string(),
  total: z.number() });
export type ErpChangeMyWorkView = z.infer<typeof erpChangeMyWorkView>;
export const erpChangeDailyCheckView = z.object({ businessDate: z.string(), newDivergences: z.number(),
  agedOpenDivergences: z.number(), clean: z.boolean(), consecutiveCleanDays: z.number(), checkedAt: z.string(),
  checkedBy: z.string() });
export type ErpChangeDailyCheckView = z.infer<typeof erpChangeDailyCheckView>;
export const masterDataCandidate = z.object({ moduleId: z.string(), label: z.string() });
export type MasterDataCandidate = z.infer<typeof masterDataCandidate>;
export const masterDataExceptionRow = z.object({ exceptionId: z.string(), kind: z.string(),
  targetExternalId: z.string(), displayName: z.string(), taxId: z.nullable(z.string()),
  country: z.nullable(z.string()), problem: z.string(), candidates: z.array(masterDataCandidate),
  liveUnitsAffected: z.number(), status: z.string(), resolvedModuleId: z.nullable(z.string()),
  resolvedBy: z.nullable(z.string()), resolvedAt: z.nullable(z.string()), resolutionNote: z.nullable(z.string()) });
export type MasterDataExceptionRow = z.infer<typeof masterDataExceptionRow>;
export const masterDataExceptionPage = z.object({ items: z.array(masterDataExceptionRow),
  nextCursor: z.nullable(z.string()), itemsMapped: z.number(), partiesMapped: z.number(), openExceptions: z.number(),
  ambiguous: z.number() });
export type MasterDataExceptionPage = z.infer<typeof masterDataExceptionPage>;
export const stockDifferenceRow = z.object({ differenceId: z.string(), unitId: z.nullable(z.string()),
  itemId: z.nullable(z.string()), identifier: z.string(), itemName: z.nullable(z.string()), reason: z.string(),
  moduleLocation: z.nullable(z.string()), sourceLocation: z.nullable(z.string()),
  targetLocation: z.nullable(z.string()), moduleStatus: z.nullable(z.string()), sourceStatus: z.nullable(z.string()),
  targetStatus: z.nullable(z.string()), hasLiveEntitlement: z.boolean(), warrantyEnd: z.nullable(z.string()),
  suggestedResolution: z.string(), status: z.string(), resolutionCommand: z.nullable(z.string()),
  resolutionCommandId: z.nullable(z.string()), resolvedBy: z.nullable(z.string()), resolvedAt: z.nullable(z.string()),
  comparedAt: z.string() });
export type StockDifferenceRow = z.infer<typeof stockDifferenceRow>;
export const stockReasonCount = z.object({ reason: z.string(), open: z.number(), resolved: z.number(),
  coveredOpen: z.number() });
export type StockReasonCount = z.infer<typeof stockReasonCount>;
export const stockDifferencePage = z.object({ items: z.array(stockDifferenceRow), nextCursor: z.nullable(z.string()),
  compared: z.number(), open: z.number(), coveredOpen: z.number(), resolved: z.number(),
  reasonCounts: z.array(stockReasonCount) });
export type StockDifferencePage = z.infer<typeof stockDifferencePage>;
export const reconcileStockResult = z.object({ runId: z.string(), unitsCompared: z.number(), differences: z.number(),
  coveredOpen: z.number(), resolvedByNormalisation: z.number(), comparedAt: z.string() });
export type ReconcileStockResult = z.infer<typeof reconcileStockResult>;
export const stockResidualSignoffView = z.object({ signoffId: z.string(), residualCount: z.number(), note: z.string(),
  signedOffBy: z.string(), signedOffAt: z.string() });
export type StockResidualSignoffView = z.infer<typeof stockResidualSignoffView>;
export const resolveStockDifferenceRequest = z.object({ command: stockResolutionCommand, commandId: z.string(),
  identifierType: z.optional(z.string()), newIdentifier: z.optional(z.string()),
  survivorUnitId: z.optional(z.string()), absorbedUnitId: z.optional(z.string()), locationId: z.optional(z.string()),
  locationKind: z.optional(z.string()), note: z.optional(z.string()) });
export type ResolveStockDifferenceRequest = z.infer<typeof resolveStockDifferenceRequest>;
export const signInHistoryItem = z.object({ sequence: z.number(), kind: z.string(), by: z.string(),
  occurredAt: z.string(), result: z.string(), count: z.number(), reason: z.optional(z.nullable(z.string())),
  daysBeforeExpiry: z.optional(z.nullable(z.number())), recipientRoles: z.optional(z.nullable(z.array(z.string()))) });
export type SignInHistoryItem = z.infer<typeof signInHistoryItem>;
export const connectionAuthorisationView = z.object({ status: z.string(),
  refreshChainExpiresAt: z.nullable(z.string()), refreshExpiryMode: z.string(), refreshLifetimeDays: z.number(),
  lastCallAt: z.nullable(z.string()), reminderDays: z.array(z.number()), reminderRecipientRoles: z.array(z.string()),
  remindersSent: z.array(z.number()), nextReminderDays: z.nullable(z.number()), signedInBy: z.nullable(z.string()),
  signedInAt: z.nullable(z.string()), disabledBy: z.nullable(z.string()), disabledAt: z.nullable(z.string()),
  disabledReason: z.nullable(z.string()), waitingOutbound: z.number(), licenceTier: z.string(),
  licensedRequestsPerMinute: z.number(), governedRequestsPerMinute: z.number(), requestHeadroomPerMinute: z.number(),
  canDisable: z.boolean(), requiresFreshConsent: z.boolean(), history: z.object({ items: z.array(signInHistoryItem),
  nextCursor: z.nullable(z.string()) }) });
export type ConnectionAuthorisationView = z.infer<typeof connectionAuthorisationView>;
export const consentStart = z.object({ consentUrl: z.string(), expiresAt: z.string() });
export type ConsentStart = z.infer<typeof consentStart>;
export const consentResult = z.object({ status: z.string(), refreshChainExpiresAt: z.string() });
export type ConsentResult = z.infer<typeof consentResult>;
export const disableResult = z.object({ status: z.string(), tokensDeleted: z.boolean(),
  remoteSignOutAttempted: z.boolean(), disabledAt: z.string(), reason: z.string() });
export type DisableResult = z.infer<typeof disableResult>;
export const reminderSettingsView = z.object({ daysBeforeExpiry: z.array(z.number()),
  recipientRoles: z.array(z.string()), channels: z.array(z.string()), settingsVersion: z.string() });
export type ReminderSettingsView = z.infer<typeof reminderSettingsView>;

// Sign-in of a connection that uses delegated tokens (ConnectionAuthorisationView, ConnectionLifecycleAdapter.cs).

// Read records of the ERP change endpoints (Integration/ErpChange/ErpChangeContracts.cs). Actor fields are names the
// server resolved through the people directory; the client never shows an actor id.

export interface ErpChangeConnectionView { connectionId: string; name: string; connectorCode: string; status: string; health: string; readOnly: boolean; outboundMode: string }
export interface ErpChangeStepCheckView { step: number; passed: boolean; counts: Record<string, number>; detail: string; checkedAt: string; checkedBy: string }
export interface ErpChangeTransitionView { fromStep: number; toStep: number; kind: string; occurredAt: string; actorRef: string; reason: string | null }
export interface DualRunSummaryView { startedAt: string | null; minimumDays: number; elapsedDays: number; cleanDaysRequired: number; consecutiveCleanDays: number; openDivergences: number; agedOpenDivergences: number; lastCheckedOn: string | null }
export interface CutoverChecklistItemView { code: string; state: "waiting" | "passed" | "failed" | string; detail: string; evidence: string | null; completedBy: string | null; completedAt: string | null }
export interface CutoverChecklistView { runId: string; cutoverAt: string | null; items: CutoverChecklistItemView[]; canAdvance: boolean }
export interface ArchiveStateView { sourceArchived: boolean; archivedAt: string | null; activeConnections: number; retainedMappings: number; retainedInboundMessages: number }
export interface ErpChangeRunbookView {
  runId: string; status: "active" | "cancelled" | "completed" | string; currentStep: number;
  source: ErpChangeConnectionView; target: ErpChangeConnectionView; startedBy: string; startedAt: string;
  closedBy: string | null; closedAt: string | null; closeReason: string | null;
  checks: ErpChangeStepCheckView[]; transitions: ErpChangeTransitionView[];
  unitsCompared: number; openDifferences: number; coveredOpenDifferences: number; resolvedDifferences: number; openMasterDataExceptions: number;
  dualRun: DualRunSummaryView | null; cutoverChecklist: CutoverChecklistItemView[] | null; archive: ArchiveStateView | null;
}

export interface ErpChangeDivergenceRow {
  divergenceId: string; kind: "stock" | "document" | string; subjectType: string; subjectId: string | null; subjectRef: string;
  sourceValue: string; targetValue: string; firstSeenAt: string; lastSeenAt: string; reviewDueAt: string;
  status: "open" | "resolved" | string; resolution: string | null; resolutionNote: string | null; resolutionCommandId: string | null;
  resolvedBy: string | null; resolvedAt: string | null;
}
export interface ErpChangeDivergencePage { items: ErpChangeDivergenceRow[]; nextCursor: string | null; open: number; agedOpen: number; resolved: number }
export interface ErpChangeCleanPeriodView { days: number; settingsVersion: string }
export interface ErpChangeNotificationSettingsView {
  digestEnabled: boolean; digestHour: number; masterDataRecipientRoles: string[]; stockRecipientRoles: string[]; settingsVersion: string;
}
export interface ErpChangeMyWorkView {
  runId: string; currentStep: number; sourceName: string; targetName: string; masterDataExceptions: number;
  coveredStockDifferences: number; otherStockDifferencesAwaitingSignOff: number; stockTypeDivergences: number;
  screenPath: string; total: number;
}
export interface ErpChangeDailyCheckView { businessDate: string; newDivergences: number; agedOpenDivergences: number; clean: boolean; consecutiveCleanDays: number; checkedAt: string; checkedBy: string }

export interface MasterDataCandidate { moduleId: string; label: string }
export interface MasterDataExceptionRow {
  exceptionId: string; kind: "item" | "party" | string; targetExternalId: string; displayName: string; taxId: string | null; country: string | null;
  problem: "unmatched" | "ambiguous" | "missing_in_target" | string; candidates: MasterDataCandidate[]; liveUnitsAffected: number;
  status: "open" | "mapped" | "not_needed" | string; resolvedModuleId: string | null; resolvedBy: string | null; resolvedAt: string | null; resolutionNote: string | null;
}
export interface MasterDataExceptionPage { items: MasterDataExceptionRow[]; nextCursor: string | null; itemsMapped: number; partiesMapped: number; openExceptions: number; ambiguous: number }

export interface StockDifferenceRow {
  differenceId: string; unitId: string | null; itemId: string | null; identifier: string; itemName: string | null;
  reason: "missing_in_b" | "extra_in_b" | "location_differs" | "serial_differs_by_normalisation" | string;
  moduleLocation: string | null; sourceLocation: string | null; targetLocation: string | null;
  moduleStatus: string | null; sourceStatus: string | null; targetStatus: string | null;
  hasLiveEntitlement: boolean; warrantyEnd: string | null; suggestedResolution: string; status: "open" | "resolved" | string;
  resolutionCommand: string | null; resolutionCommandId: string | null; resolvedBy: string | null; resolvedAt: string | null; comparedAt: string;
}
export interface StockDifferencePage { items: StockDifferenceRow[]; nextCursor: string | null; compared: number; open: number; coveredOpen: number; resolved: number }
export interface ReconcileStockResult { runId: string; unitsCompared: number; differences: number; coveredOpen: number; resolvedByNormalisation: number; comparedAt: string }
export interface StockResidualSignoffView { signoffId: string; residualCount: number; note: string; signedOffBy: string; signedOffAt: string }
export type StockResolutionCommand = "correct_identifier" | "merge_units" | "change_custody";
export interface ResolveStockDifferenceRequest {
  command: StockResolutionCommand; commandId: string; identifierType?: string | undefined; newIdentifier?: string | undefined;
  survivorUnitId?: string | undefined; absorbedUnitId?: string | undefined; locationId?: string | undefined; locationKind?: string | undefined; note?: string | undefined;
}

// Sign-in of a connection that uses delegated tokens (ConnectionAuthorisationView, ConnectionLifecycleAdapter.cs).
export interface SignInHistoryItem { sequence: number; kind: "signed_in" | "disabled" | "access_renewed" | "expiry_reminder" | "sign_in_required" | "sync_paused" | string; by: string; occurredAt: string; result: string; count: number; reason?: string | null; daysBeforeExpiry?: number | null; recipientRoles?: string[] | null }
export interface ConnectionAuthorisationView {
  status: "reconsent_required" | "consent_pending" | "active" | "disabled" | string; refreshChainExpiresAt: string | null;
  refreshExpiryMode: "absolute" | "sliding" | string; refreshLifetimeDays: number; lastCallAt: string | null;
  reminderDays: number[]; reminderRecipientRoles: string[]; remindersSent: number[]; nextReminderDays: number | null;
  signedInBy: string | null; signedInAt: string | null; disabledBy: string | null; disabledAt: string | null; disabledReason: string | null;
  waitingOutbound: number; licenceTier: string; licensedRequestsPerMinute: number; governedRequestsPerMinute: number; requestHeadroomPerMinute: number;
  canDisable: boolean; requiresFreshConsent: boolean; history: { items: SignInHistoryItem[]; nextCursor: string | null };
}
export interface ConsentStart { consentUrl: string; expiresAt: string }
export interface ConsentResult { status: string; refreshChainExpiresAt: string }
export interface DisableResult { status: string; tokensDeleted: boolean; remoteSignOutAttempted: boolean; disabledAt: string; reason: string }
export interface ReminderSettingsView { daysBeforeExpiry: number[]; recipientRoles: string[]; channels: string[]; settingsVersion: string }

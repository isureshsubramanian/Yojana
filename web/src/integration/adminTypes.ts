export interface CursorPage<T> { items: T[]; nextCursor: string | null }
export interface ConnectionKpis { connections: number; live: number; paused: number; receivedToday: number; receivedLast30Days: number; waitingToSend: number; openDeadLetters: number; openManualTasks: number; connectionsNeedingAttention: number; relayConnected: number }
export interface Health { status: string; isLive: boolean; notificationState: string; checkedUpTo: string | null; lastInboundAt: string | null; receivedToday: number; receivedLast30Days: number; waitingToSend: number; oldestWaitingAt: string | null; openDeadLetters: number; openManualTasks: number; lastDailyCheckResult: string | null; lastDailyCheckAt: string | null; dailyCheckDifferences: number; callsUsed: number; callsCap: number | null; lastErrorCode: string | null; lastErrorMessage: string | null; updatedAt: string; relayConnected: boolean }
export interface Connection { connectionId: string; connectionName: string; connectorCode: string; connectorName: string; status: string; isLive: boolean; outboundMode: string; readOnly: boolean; acceptancePassed: boolean; health: Health }
export interface ConnectionPage extends CursorPage<Connection> { kpis: ConnectionKpis }
export interface TenantStatus { connectionId: string; connectionName: string; status: string; isLive: boolean; checkedUpTo: string | null; waitingToSend: number; messagesNeedingAttention: number; openManualTasks: number; updatedAt: string }
export interface TenantStatusPage extends CursorPage<TenantStatus> { kpis: ConnectionKpis }
export interface RecordHealth { sourceEntity: string | null; recordType: string; notificationState: string; watermark: string | null; checkedUpTo: string | null; behindSeconds: number | null; intervalSeconds: number; overlapSeconds: number; receivedToday: number; needAttention: number; lastErrorCode: string | null; lastErrorDetail: string | null }
export interface HealthDetail { connectionName: string; health: Health; recordTypes: RecordHealth[]; handOver?: { from: string; text: string } | null }
export interface ConnectionEvent { sequence: number; kind: string; occurredAt: string; actorName: string; summary: string }
export interface ChecklistItem { code: string; label: string; detail: string; required: boolean; evidencePrompt: string | null; status: string; evidence: string | null; confirmedAt: string | null; manualConfirmation: boolean }
export interface Checklist { displayName: string; customerCostDisclosure: string; items: ChecklistItem[]; requiredItemsConfirmed: boolean }
export interface WarrantyContractSettings { maintainedIn: "module" | "incumbent_until_cutover"; version: string }
export interface CoexistenceSettings { connectionId: string; nativeWarrantyObjectMode: "frozen" | "pushed"; pushedModeAvailable: boolean; driftScanEnabled: boolean; watchedFields: string[]; explainingFields: string[]; pageSize: number; maxPagesPerRun: number }
export interface Credential { purpose: string; isSet: boolean; createdAt: string | null; expiresAt: string | null; replaceBy: string | null; retiredAt: string | null }
export interface ConnectionFeatures { tenantSideExtension: boolean; interactiveSignIn: boolean }
export interface SyncStart { mode: "today" | "date" | "full_history"; date: string | null; masterDataFull: boolean }
export interface ServiceCertificate { subject: string; issuer: string; validFrom: string; validTo: string; sha256Fingerprint: string }
export interface FullHistoryEstimate { status: "known" | "unknown"; records: number | null; entities: Record<string, number> | null }
export interface ConnectionDetail { lifecycleStatus: string; reconciliationWindowDays: number; reconciliationToday: string; connection: Connection; createdAt: string; archivedAt: string | null; features: ConnectionFeatures; capabilities: { canonicalMessage: string; supportLevel: string }[]; settings: { outboundMode: string; readOnly: boolean; nativeWarrantyObjectMode: string; warrantyContractsMaintainedIn: "module" | "incumbent_until_cutover"; orderingWaitMinutes: number; syncStart: SyncStart | null; serviceCertificateSha256: string | null; throttle: { requestsPerSecond: number | null; requestsPerMinute: number | null; requestsPerDay: number | null; burst: number; maxConcurrentCalls: number; retryAfterCeiling: string; alertThresholdPercent: number } | null; degradationRules: { ruleNumber: number; enabled: boolean; settingsJson: string }[] }; credentials: Credential[]; checklist: Checklist }
export interface RegistrationField { code: string; label: string; type: string; defaultValue: string | null }
export interface RegistrationConnector { code: string; displayName: string; form: { settings: RegistrationField[]; credentialPurpose: string; secretFields: RegistrationField[] } }
export interface ArchiveResult { archived: boolean; preconditions: { waitingOutbox: number; openDeadLetters: number; runningBackfills: number; openManualTasks: number; blocked: boolean }; cleanupErrorCode: string | null }
export interface FieldSource { field: string | null; trueValues?: string[] | null; valueMap?: Record<string, string> | null }
export interface FieldMap { fields: { code: string; label: string; kind: number; effective: FieldSource; warning: string | null; surveyedFields: string[] }[] }
export interface ApprovalView { templatesKnown: boolean; documentTypes: { collection: string; documentType: string; draftSupported: boolean; activeTemplates: string[]; effectiveMode: string; warning: string | null; configured: { evidence: string | null; acceptedAt: string | null } | null }[] }
export interface Relay { relayId: string; displayName: string; status: string; lastSeenAt: string | null; agentVersion: string | null; hostName: string | null }
export interface RelayIssued { secret: string; configuration: Record<string, unknown> }
export interface TransportSettings { route: { mode: string; credentialReference: string | null }; drainingRoutes: { mode: string }[]; version: string }
export interface Run { runId: string; connectionId: string; connectionName: string; rangeStart: string; rangeEnd: string; trigger: string; status: string; startedAt: string; completedAt: string | null; mismatchCount: number; locationMismatchCount: number; requestedByName: string; explainedAt: string | null; explainedByName: string | null; explanationNote: string | null }
export interface EntityDay { resultId: string; entity: string; businessDate: string; sourceCount: number; moduleCount: number; sourceQuantity: number; moduleQuantity: number; sourceDistinctSerials: number; moduleDistinctSerials: number; sourceKeyFieldHash: string; moduleKeyFieldHash: string; status: string; backfillJobId: string | null; attentionItemId: string | null }
export interface LocationCheck { resultId: string; itemCode: string; itemName: string | null; locationCode: string; locationName: string | null; sourceOnHand: number; moduleUnitCount: number; status: string }
export interface Backfill { backfillId: string; connectionId: string; connectionName: string; entity: string; rangeStart: string; rangeEnd: string; status: string; processedMessages: number; processedPages: number; totalMessages: number | null; progressPercent: number | null; estimatedRemaining: string | null; createdAt: string; startedAt: string | null; lastPageAt: string | null; awaitingReconciliationAt: string | null; completedAt: string | null; lastFailure: string | null; savedCursor: string | null; pageSize: number }

export interface ParallelRunRefusal { code: string; message: string }
export interface ParallelRunSyncLag { nowSeconds: number | null; targetSeconds: number; shareWithinTargetToday: number | null; messagesToday: number }
export interface ParallelRunConflictSummary { open: number; oldestDetectedAt: string | null; autoResolved: number }
export interface ParallelRunFailedMessages { open: number; olderThanSevenDays: number }
export interface ParallelRunReconciliationSummary { runs: number; clean: number; explained: number; mismatch: number; failed: number; lastExplainedOn: string | null }
export interface HandoverStepView {
  order: number; kind: string; label: string; description: string; state: "planned" | "next" | "module_owns" | "after_close";
  plannedOn: string | null; ownedSince: string | null; evidence: string | null; confirmedByName: string | null; confirmedAt: string | null;
  facts: string[]; canConfirm: boolean; confirmRefusals: ParallelRunRefusal[];
}
export interface GoLiveItemView {
  code: string; label: string; source: "measured" | "confirmed"; status: "done" | "open" | "fail"; detail: string;
  evidence: string | null; confirmedByName: string | null; confirmedAt: string | null;
}
export interface GoLiveView {
  done: number; total: number; issuedAt: string | null; recordId: string | null; canIssue: boolean;
  issueRefusals: ParallelRunRefusal[]; items: GoLiveItemView[];
}
export interface ParallelRunDashboard {
  connectionId: string; connectionLabel: string; status: "not_started" | "in_progress" | "ready"; days: number | null;
  startDate: string | null; endDate: string | null; dayNumber: number | null; daysLeft: number | null; monthEndCloseOn: string | null;
  cutoverAt: string | null; syncLag: ParallelRunSyncLag; conflicts: ParallelRunConflictSummary;
  failedMessages: ParallelRunFailedMessages; reconciliation: ParallelRunReconciliationSummary;
  handover: HandoverStepView[]; goLive: GoLiveView;
}
export interface ParallelRunChecklistItem {
  code: string; label: string; kind: string; status: string; detail: string; evidence: string | null; confirmedBy: string | null; confirmedAt: string | null;
}
export interface ParallelRunView {
  connectionId: string; connectionLabel: string; status: string; days: number | null; startDate: string | null;
  earliestDecommissionDate: string | null; recommendedDays: number; items: ParallelRunChecklistItem[];
}
export interface GoLiveRecordView {
  recordId: string; connectionId: string; connectionLabel: string; issuedAt: string; issuedByName: string;
  items: GoLiveItemView[]; handover: HandoverStepView[];
}

export interface CoexistenceConflictView {
  conflictId: string; connectionId: string; connectionLabel: string; recordType: string; recordKey: string; recordLabel: string;
  kind: string; fieldName: string; baselineValue: string | null; remoteValue: string | null; status: string;
  detectedAt: string; lastSeenAt: string; incumbentChangedAt: string | null; decision: string | null; decisionNote: string | null;
  decidedBy: string | null; decidedAt: string | null; manualTaskId: string | null; closedAt: string | null;
  deciderRoleLabel?: string;
  moduleValueLabel?: string | null;
}
export interface CoexistenceConflictPage { items: CoexistenceConflictView[]; nextCursor: string | null }

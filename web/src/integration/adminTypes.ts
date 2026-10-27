import * as z from "zod/mini";
export const cursorPage = <T extends z.ZodMiniType>(item: T) => z.object({ items: z.array(item),
  nextCursor: z.nullable(z.string()) });
export const connectionKpis = z.object({ connections: z.number(), live: z.number(), paused: z.number(),
  receivedToday: z.number(), receivedLast30Days: z.number(), waitingToSend: z.number(), openDeadLetters: z.number(),
  openManualTasks: z.number(), connectionsNeedingAttention: z.number(), relayConnected: z.number() });
export type ConnectionKpis = z.infer<typeof connectionKpis>;
export const health = z.object({ status: z.string(), isLive: z.boolean(), notificationState: z.string(),
  checkedUpTo: z.nullable(z.string()), lastInboundAt: z.nullable(z.string()), receivedToday: z.number(),
  receivedLast30Days: z.number(), waitingToSend: z.number(), oldestWaitingAt: z.nullable(z.string()),
  openDeadLetters: z.number(), openManualTasks: z.number(), lastDailyCheckResult: z.nullable(z.string()),
  lastDailyCheckAt: z.nullable(z.string()), dailyCheckDifferences: z.number(), callsUsed: z.number(),
  callsCap: z.nullable(z.number()), lastErrorCode: z.nullable(z.string()), lastErrorMessage: z.nullable(z.string()),
  updatedAt: z.string(), relayConnected: z.boolean() });
export type Health = z.infer<typeof health>;
export const syncState = z.object({ state: z.enum(["running", "stopped"]), stoppedAt: z.nullable(z.string()),
  stoppedByName: z.nullable(z.string()), stopReason: z.nullable(z.string()), resumedAt: z.nullable(z.string()),
  catchingUp: z.boolean(), notifications: z.enum(["pending", "held", "not_held", "not_supported"]),
  notificationFailureCode: z.nullable(z.string()), waitingOutbound: z.number() });
export type SyncState = z.infer<typeof syncState>;
export const connection = z.object({ connectionId: z.string(), connectionName: z.string(), connectorCode: z.string(),
  connectorName: z.string(), status: z.string(), isLive: z.boolean(), outboundMode: z.string(), readOnly: z.boolean(),
  acceptancePassed: z.boolean(), health: health, sync: syncState });
export type Connection = z.infer<typeof connection>;
export const tenantStatus = z.object({ connectionId: z.string(), connectionName: z.string(), status: z.string(),
  isLive: z.boolean(), checkedUpTo: z.nullable(z.string()), waitingToSend: z.number(),
  messagesNeedingAttention: z.number(), openManualTasks: z.number(), updatedAt: z.string(), sync: syncState });
export type TenantStatus = z.infer<typeof tenantStatus>;
export const recordHealth = z.object({ sourceEntity: z.nullable(z.string()), recordType: z.string(),
  notificationState: z.string(), watermark: z.nullable(z.string()), checkedUpTo: z.nullable(z.string()),
  behindSeconds: z.nullable(z.number()), intervalSeconds: z.number(), overlapSeconds: z.number(),
  receivedToday: z.number(), needAttention: z.number(), lastErrorCode: z.nullable(z.string()),
  lastErrorDetail: z.nullable(z.string()) });
export type RecordHealth = z.infer<typeof recordHealth>;
export const healthDetail = z.object({ connectionName: z.string(), health: health, recordTypes: z.array(recordHealth),
  handOver: z.optional(z.nullable(z.object({ from: z.string(), text: z.string() }))), sync: syncState });
export type HealthDetail = z.infer<typeof healthDetail>;
export const connectionEvent = z.object({ sequence: z.number(), kind: z.string(), occurredAt: z.string(),
  actorName: z.string(), summary: z.string() });
export type ConnectionEvent = z.infer<typeof connectionEvent>;
export const checklistItem = z.object({ code: z.string(), label: z.string(), detail: z.string(), required: z.boolean(),
  evidencePrompt: z.nullable(z.string()), status: z.string(), evidence: z.nullable(z.string()),
  confirmedAt: z.nullable(z.string()), manualConfirmation: z.boolean() });
export type ChecklistItem = z.infer<typeof checklistItem>;
export const checklist = z.object({ displayName: z.string(), customerCostDisclosure: z.string(),
  items: z.array(checklistItem), requiredItemsConfirmed: z.boolean() });
export type Checklist = z.infer<typeof checklist>;
export const warrantyContractSettings = z.object({ maintainedIn: z.enum(["module", "incumbent_until_cutover"]),
  version: z.string() });
export type WarrantyContractSettings = z.infer<typeof warrantyContractSettings>;
export const coexistenceSettings = z.object({ connectionId: z.string(), nativeWarrantyObjectMode: z.enum(["frozen",
  "pushed"]), pushedModeAvailable: z.boolean(), driftScanEnabled: z.boolean(), watchedFields: z.array(z.string()),
  explainingFields: z.array(z.string()), pageSize: z.number(), maxPagesPerRun: z.number() });
export type CoexistenceSettings = z.infer<typeof coexistenceSettings>;
export const credential = z.object({ purpose: z.string(), isSet: z.boolean(), createdAt: z.nullable(z.string()),
  expiresAt: z.nullable(z.string()), replaceBy: z.nullable(z.string()), retiredAt: z.nullable(z.string()) });
export type Credential = z.infer<typeof credential>;
export const connectionFeatures = z.object({ tenantSideExtension: z.boolean(), interactiveSignIn: z.boolean() });
export type ConnectionFeatures = z.infer<typeof connectionFeatures>;
export const syncStart = z.object({ mode: z.enum(["today", "date", "full_history"]), date: z.nullable(z.string()),
  masterDataFull: z.boolean() });
export type SyncStart = z.infer<typeof syncStart>;
export const serviceCertificate = z.object({ subject: z.string(), issuer: z.string(), validFrom: z.string(),
  validTo: z.string(), sha256Fingerprint: z.string() });
export type ServiceCertificate = z.infer<typeof serviceCertificate>;
export const fullHistoryEstimate = z.object({ status: z.enum(["known", "unknown"]), records: z.nullable(z.number()),
  entities: z.nullable(z.record(z.string(), z.number())) });
export type FullHistoryEstimate = z.infer<typeof fullHistoryEstimate>;
export const connectionDetail = z.object({ lifecycleStatus: z.string(), reconciliationWindowDays: z.number(),
  reconciliationToday: z.string(), connection: connection, createdAt: z.string(), archivedAt: z.nullable(z.string()),
  features: z.nullable(connectionFeatures), sync: syncState,
  capabilities: z.array(z.object({ canonicalMessage: z.string(), supportLevel: z.string() })),
  settings: z.object({ outboundMode: z.string(), readOnly: z.boolean(), nativeWarrantyObjectMode: z.string(),
  warrantyContractsMaintainedIn: z.enum(["module", "incumbent_until_cutover"]), orderingWaitMinutes: z.number(),
  syncStart: z.nullable(syncStart), serviceCertificateSha256: z.nullable(z.string()),
  throttle: z.nullable(z.object({ requestsPerSecond: z.nullable(z.number()), requestsPerMinute: z.nullable(z.number()),
  requestsPerDay: z.nullable(z.number()), burst: z.number(), maxConcurrentCalls: z.number(),
  retryAfterCeiling: z.string(), alertThresholdPercent: z.number() })),
  degradationRules: z.array(z.object({ ruleNumber: z.number(), enabled: z.boolean(), settingsJson: z.string() })) }),
  credentials: z.array(credential), checklist: checklist });
export type ConnectionDetail = z.infer<typeof connectionDetail>;
export const syncControlResult = z.object({ connectionId: z.string(), syncState: z.string(),
  notifications: syncState.shape.notifications, waitingOutbound: z.number(), failureCode: z.nullable(z.string()),
  warning: z.nullable(z.string()) });
export type SyncControlResult = z.infer<typeof syncControlResult>;
export const registrationField = z.object({ code: z.string(), label: z.string(), type: z.string(),
  defaultValue: z.nullable(z.string()),
  /** Choices of a "select" field; null for every other type. */
  options: z.optional(z.nullable(z.array(z.object({ value: z.string(), label: z.string() })))) });
export type RegistrationField = z.infer<typeof registrationField>;
export const registrationConnector = z.object({ code: z.string(), displayName: z.string(),
  form: z.object({ settings: z.array(registrationField), credentialPurpose: z.string(),
  secretFields: z.array(registrationField) }) });
export type RegistrationConnector = z.infer<typeof registrationConnector>;
export const archiveResult = z.object({ archived: z.boolean(), preconditions: z.object({ waitingOutbox: z.number(),
  openDeadLetters: z.number(), runningBackfills: z.number(), openManualTasks: z.number(), blocked: z.boolean() }),
  cleanupErrorCode: z.nullable(z.string()) });
export type ArchiveResult = z.infer<typeof archiveResult>;
export const fieldSource = z.object({ field: z.nullable(z.string()),
  trueValues: z.optional(z.nullable(z.array(z.string()))), valueMap: z.optional(z.nullable(z.record(z.string(),
  z.string()))) });
export type FieldSource = z.infer<typeof fieldSource>;
export const fieldMap = z.object({ fields: z.array(z.object({ code: z.string(), label: z.string(), kind: z.number(),
  effective: fieldSource, warning: z.nullable(z.string()), surveyedFields: z.array(z.string()) })) });
export type FieldMap = z.infer<typeof fieldMap>;
export const currencyMap = z.object({ connectionId: z.string(), surveyObservedAt: z.nullable(z.string()),
  currencies: z.array(z.object({ erpCode: z.string(), name: z.string(), documentsCode: z.nullable(z.string()),
  suggestedIsoCode: z.nullable(z.string()), mappedIsoCode: z.nullable(z.string()) })) });
export type CurrencyMap = z.infer<typeof currencyMap>;
export const approvalView = z.object({ templatesKnown: z.boolean(),
  documentTypes: z.array(z.object({ collection: z.string(), documentType: z.string(), draftSupported: z.boolean(),
  activeTemplates: z.array(z.string()), effectiveMode: z.string(), warning: z.nullable(z.string()),
  configured: z.nullable(z.object({ evidence: z.nullable(z.string()), acceptedAt: z.nullable(z.string()) })) })) });
export type ApprovalView = z.infer<typeof approvalView>;
export const relay = z.object({ relayId: z.string(), displayName: z.string(), status: z.string(),
  lastSeenAt: z.nullable(z.string()), agentVersion: z.nullable(z.string()), hostName: z.nullable(z.string()) });
export type Relay = z.infer<typeof relay>;
export const relayIssued = z.object({ secret: z.string(), configuration: z.record(z.string(), z.unknown()) });
export type RelayIssued = z.infer<typeof relayIssued>;
export const transportSettings = z.object({ route: z.object({ mode: z.string(),
  credentialReference: z.nullable(z.string()) }), drainingRoutes: z.array(z.object({ mode: z.string() })),
  version: z.string() });
export type TransportSettings = z.infer<typeof transportSettings>;
export const run = z.object({ runId: z.string(), connectionId: z.string(), connectionName: z.string(),
  rangeStart: z.string(), rangeEnd: z.string(), trigger: z.string(), status: z.string(), startedAt: z.string(),
  completedAt: z.nullable(z.string()), mismatchCount: z.number(), locationMismatchCount: z.number(),
  requestedByName: z.string(), explainedAt: z.nullable(z.string()), explainedByName: z.nullable(z.string()),
  explanationNote: z.nullable(z.string()) });
export type Run = z.infer<typeof run>;
export const entityDay = z.object({ resultId: z.string(), entity: z.string(), businessDate: z.string(),
  sourceCount: z.number(), moduleCount: z.number(), sourceQuantity: z.number(), moduleQuantity: z.number(),
  sourceDistinctSerials: z.number(), moduleDistinctSerials: z.number(), sourceKeyFieldHash: z.string(),
  moduleKeyFieldHash: z.string(), status: z.string(), backfillJobId: z.nullable(z.string()),
  attentionItemId: z.nullable(z.string()) });
export type EntityDay = z.infer<typeof entityDay>;
export const locationCheck = z.object({ resultId: z.string(), itemCode: z.string(), itemName: z.nullable(z.string()),
  locationCode: z.string(), locationName: z.nullable(z.string()), sourceOnHand: z.number(),
  moduleUnitCount: z.number(), status: z.string() });
export type LocationCheck = z.infer<typeof locationCheck>;
export const backfill = z.object({ backfillId: z.string(), connectionId: z.string(), connectionName: z.string(),
  entity: z.string(), rangeStart: z.string(), rangeEnd: z.string(), status: z.string(), processedMessages: z.number(),
  processedPages: z.number(), totalMessages: z.nullable(z.number()), progressPercent: z.nullable(z.number()),
  estimatedRemaining: z.nullable(z.string()), createdAt: z.string(), startedAt: z.nullable(z.string()),
  lastPageAt: z.nullable(z.string()), awaitingReconciliationAt: z.nullable(z.string()),
  completedAt: z.nullable(z.string()), lastFailure: z.nullable(z.string()), savedCursor: z.nullable(z.string()),
  pageSize: z.number() });
export type Backfill = z.infer<typeof backfill>;
export const parallelRunRefusal = z.object({ code: z.string(), message: z.string() });
export type ParallelRunRefusal = z.infer<typeof parallelRunRefusal>;
export const parallelRunSyncLag = z.object({ nowSeconds: z.nullable(z.number()), targetSeconds: z.number(),
  shareWithinTargetToday: z.nullable(z.number()), messagesToday: z.number() });
export type ParallelRunSyncLag = z.infer<typeof parallelRunSyncLag>;
export const parallelRunConflictSummary = z.object({ open: z.number(), oldestDetectedAt: z.nullable(z.string()),
  autoResolved: z.number() });
export type ParallelRunConflictSummary = z.infer<typeof parallelRunConflictSummary>;
export const parallelRunFailedMessages = z.object({ open: z.number(), olderThanSevenDays: z.number() });
export type ParallelRunFailedMessages = z.infer<typeof parallelRunFailedMessages>;
export const parallelRunReconciliationSummary = z.object({ runs: z.number(), clean: z.number(), explained: z.number(),
  mismatch: z.number(), failed: z.number(), lastExplainedOn: z.nullable(z.string()) });
export type ParallelRunReconciliationSummary = z.infer<typeof parallelRunReconciliationSummary>;
export const handoverStepView = z.object({ order: z.number(), kind: z.string(), label: z.string(),
  description: z.string(), state: z.enum(["planned", "next", "module_owns", "after_close"]),
  plannedOn: z.nullable(z.string()), ownedSince: z.nullable(z.string()), evidence: z.nullable(z.string()),
  confirmedByName: z.nullable(z.string()), confirmedAt: z.nullable(z.string()), facts: z.array(z.string()),
  canConfirm: z.boolean(), confirmRefusals: z.array(parallelRunRefusal) });
export type HandoverStepView = z.infer<typeof handoverStepView>;
export const goLiveItemView = z.object({ code: z.string(), label: z.string(), source: z.enum(["measured",
  "confirmed"]), status: z.enum(["done", "open", "fail"]), detail: z.string(), evidence: z.nullable(z.string()),
  confirmedByName: z.nullable(z.string()), confirmedAt: z.nullable(z.string()) });
export type GoLiveItemView = z.infer<typeof goLiveItemView>;
export const goLiveView = z.object({ done: z.number(), total: z.number(), issuedAt: z.nullable(z.string()),
  recordId: z.nullable(z.string()), canIssue: z.boolean(), issueRefusals: z.array(parallelRunRefusal),
  items: z.array(goLiveItemView) });
export type GoLiveView = z.infer<typeof goLiveView>;
export const parallelRunDashboard = z.object({ connectionId: z.string(), connectionLabel: z.string(),
  status: z.enum(["not_started", "in_progress", "ready"]), days: z.nullable(z.number()),
  startDate: z.nullable(z.string()), endDate: z.nullable(z.string()), dayNumber: z.nullable(z.number()),
  daysLeft: z.nullable(z.number()), monthEndCloseOn: z.nullable(z.string()), cutoverAt: z.nullable(z.string()),
  syncLag: parallelRunSyncLag, conflicts: parallelRunConflictSummary, failedMessages: parallelRunFailedMessages,
  reconciliation: parallelRunReconciliationSummary, handover: z.array(handoverStepView), goLive: goLiveView });
export type ParallelRunDashboard = z.infer<typeof parallelRunDashboard>;
export const parallelRunChecklistItem = z.object({ code: z.string(), label: z.string(), kind: z.string(),
  status: z.string(), detail: z.string(), evidence: z.nullable(z.string()), confirmedBy: z.nullable(z.string()),
  confirmedAt: z.nullable(z.string()) });
export type ParallelRunChecklistItem = z.infer<typeof parallelRunChecklistItem>;
export const parallelRunView = z.object({ connectionId: z.string(), connectionLabel: z.string(), status: z.string(),
  days: z.nullable(z.number()), startDate: z.nullable(z.string()), earliestDecommissionDate: z.nullable(z.string()),
  recommendedDays: z.number(), items: z.array(parallelRunChecklistItem) });
export type ParallelRunView = z.infer<typeof parallelRunView>;
export const goLiveRecordView = z.object({ recordId: z.string(), connectionId: z.string(), connectionLabel: z.string(),
  issuedAt: z.string(), issuedByName: z.string(), items: z.array(goLiveItemView),
  handover: z.array(handoverStepView) });
export type GoLiveRecordView = z.infer<typeof goLiveRecordView>;
export const coexistenceConflictView = z.object({ conflictId: z.string(), connectionId: z.string(),
  connectionLabel: z.string(), recordType: z.string(), recordKey: z.string(), recordLabel: z.string(),
  kind: z.string(), fieldName: z.string(), baselineValue: z.nullable(z.string()), remoteValue: z.nullable(z.string()),
  status: z.string(), detectedAt: z.string(), lastSeenAt: z.string(), incumbentChangedAt: z.nullable(z.string()),
  decision: z.nullable(z.string()), decisionNote: z.nullable(z.string()), decidedBy: z.nullable(z.string()),
  decidedAt: z.nullable(z.string()), manualTaskId: z.nullable(z.string()), closedAt: z.nullable(z.string()),
  deciderRoleLabel: z.optional(z.string()), moduleValueLabel: z.optional(z.nullable(z.string())) });
export type CoexistenceConflictView = z.infer<typeof coexistenceConflictView>;
export const coexistenceConflictPage = z.object({ items: z.array(coexistenceConflictView),
  nextCursor: z.nullable(z.string()) });
export type CoexistenceConflictPage = z.infer<typeof coexistenceConflictPage>;
export interface CursorPage<T> { items: T[]; nextCursor: string | null }
// Hidden coupling: `sync` is required below although the C# contracts declare it nullable. The server builds every
// connection view through ConnectionQueries.SyncStatesAsync, which always supplies it; if that changes, make `sync`
// nullable here and handle null on the Integration screens.
export const connectionPage = z.extend(cursorPage(connection), { kpis: connectionKpis });
export type ConnectionPage = z.infer<typeof connectionPage>;
export const tenantStatusPage = z.extend(cursorPage(tenantStatus), { kpis: connectionKpis });
export type TenantStatusPage = z.infer<typeof tenantStatusPage>;


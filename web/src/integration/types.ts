export interface DeadLetterRow {
  deadLetterItemId: string; connectionId: string; connectionLabel: string; direction: string;
  recordType: string; recordReference: string | null; reasonCode: string; failure: string; status: string; attemptCount: number;
  createdAt: string; updatedAt: string;
}

export interface DeadLetterSummary {
  open: number; retried: number; discarded: number; unknownItem: number; serialNotFound: number; partyNotLinked: number; couldNotSend: number;
  resolvedLast30Days: number; retriedLast30Days: number; discardedLast30Days: number;
}

export interface DeadLetterPage { items: DeadLetterRow[]; nextCursor: string | null; total: number; summary: DeadLetterSummary }
export interface DeadLetterEvent { deadLetterEventId: string; action: string; reason: string | null; detailsJson: string; occurredAt: string }

export interface DeadLetterDetail extends DeadLetterRow {
  sourceKind: string; sourceId: string; rawPayload: string; canonicalAttempt: string;
  resolvedAt: string | null; events: DeadLetterEvent[]; manualTask: ManualTask | null;
}

export interface ManualTask {
  manualOutboundTaskId: string; deadLetterItemId: string; outboundCommandId: string; connectionId: string;
  connectionLabel: string; commandType: string; desk: string; draft: string; status: string; createdAt: string;
  erpExternalId: string | null; erpChangeMarker: string | null; completedBy: string | null; completedAt: string | null;
}

export interface DeskCount { desk: string; label: string; count: number }
export interface ManualTaskPage { items: ManualTask[]; nextCursor: string | null; total: number; desks: DeskCount[] }
export interface AttentionCount { needsAttention: number }
export interface ActionResult { deadLetterItemId: string; status: string; moduleId: string | null }

export interface DraftLine { [key: string]: unknown }
export interface ManualDraft {
  title?: string; subtitle?: string; reason?: string; reference?: string;
  fields?: Record<string, unknown>; lines?: DraftLine[]; currency?: string; total?: number;
  [key: string]: unknown;
}

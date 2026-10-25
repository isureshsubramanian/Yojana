import * as z from "zod/mini";
export const deadLetterRow = z.object({ deadLetterItemId: z.string(), connectionId: z.string(),
  connectionLabel: z.string(), direction: z.string(), recordType: z.string(), recordReference: z.nullable(z.string()),
  reasonCode: z.string(), failure: z.string(), status: z.string(), attemptCount: z.number(), createdAt: z.string(),
  updatedAt: z.string() });
export type DeadLetterRow = z.infer<typeof deadLetterRow>;
export const deadLetterSummary = z.object({ open: z.number(), retried: z.number(), discarded: z.number(),
  unknownItem: z.number(), serialNotFound: z.number(), partyNotLinked: z.number(), couldNotSend: z.number(),
  resolvedLast30Days: z.number(), retriedLast30Days: z.number(), discardedLast30Days: z.number() });
export type DeadLetterSummary = z.infer<typeof deadLetterSummary>;
export const deadLetterPage = z.object({ items: z.array(deadLetterRow), nextCursor: z.nullable(z.string()),
  total: z.number(), summary: deadLetterSummary });
export type DeadLetterPage = z.infer<typeof deadLetterPage>;
export const deadLetterEvent = z.object({ deadLetterEventId: z.string(), action: z.string(),
  reason: z.nullable(z.string()), detailsJson: z.string(), occurredAt: z.string() });
export type DeadLetterEvent = z.infer<typeof deadLetterEvent>;
export const manualTask = z.object({ manualOutboundTaskId: z.string(), deadLetterItemId: z.nullable(z.string()),
  outboundCommandId: z.nullable(z.string()), connectionId: z.string(), connectionLabel: z.string(),
  commandType: z.string(), desk: z.string(), draft: z.string(), status: z.string(), createdAt: z.string(),
  erpExternalId: z.nullable(z.string()), erpChangeMarker: z.nullable(z.string()), completedBy: z.nullable(z.string()),
  completedAt: z.nullable(z.string()) });
export type ManualTask = z.infer<typeof manualTask>;
// The detail record carries no recordReference or attemptCount (only list rows do), so they are left out.
export const deadLetterDetail = z.extend(z.omit(deadLetterRow, { recordReference: true, attemptCount: true }), {
  sourceKind: z.string(), sourceId: z.string(), rawPayload: z.string(), canonicalAttempt: z.string(), resolvedAt: z.nullable(z.string()),
  events: z.array(deadLetterEvent), manualTask: z.nullable(manualTask) });
export type DeadLetterDetail = z.infer<typeof deadLetterDetail>;
export const deskCount = z.object({ desk: z.string(), label: z.string(), count: z.number() });
export type DeskCount = z.infer<typeof deskCount>;
export const manualTaskPage = z.object({ items: z.array(manualTask), nextCursor: z.nullable(z.string()),
  total: z.number(), desks: z.array(deskCount) });
export type ManualTaskPage = z.infer<typeof manualTaskPage>;
export const attentionCount = z.object({ needsAttention: z.number() });
export type AttentionCount = z.infer<typeof attentionCount>;
export const actionResult = z.object({ deadLetterItemId: z.string(), status: z.string(),
  moduleId: z.nullable(z.string()) });
export type ActionResult = z.infer<typeof actionResult>;

export interface DraftLine { [key: string]: unknown }
export interface ManualDraft {
  title?: string; subtitle?: string; reason?: string; reference?: string;
  fields?: Record<string, unknown>; lines?: DraftLine[]; currency?: string; total?: number;
  [key: string]: unknown;
}

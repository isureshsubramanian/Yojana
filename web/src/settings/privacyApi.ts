import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../api/client.ts";

export const reviewAction = z.enum(["Redact", "Keep"]);
export type ReviewAction = z.infer<typeof reviewAction>;

export const erasureStatus = z.enum(["pending", "running", "completed", "failed", "refused"]);
export type ErasureStatus = z.infer<typeof erasureStatus>;
export const erasureSubjectKind = z.enum(["Party", "User"]);
export type ErasureSubjectKind = z.infer<typeof erasureSubjectKind>;
export const erasureBlocker = z.object({ kind: z.string(), recordId: z.nullable(z.string()), reference: z.string(),
  status: z.string(), detail: z.string() });
export type ErasureBlocker = z.infer<typeof erasureBlocker>;
export const erasureStoreCount = z.object({ store: z.string(), kind: z.string(), previewCount: z.number(),
  erasedCount: z.number(), action: z.string(), status: z.enum(["previewed", "completed"]),
  completedAt: z.nullable(z.string()) });
export type ErasureStoreCount = z.infer<typeof erasureStoreCount>;
export const erasureRow = z.object({ erasureId: z.string(), subjectKind: erasureSubjectKind, subjectId: z.string(),
  token: z.string(), requestReference: z.string(), requestedBy: z.string(), requestReceivedOn: z.string(),
  status: erasureStatus, requestedAt: z.string(), completedAt: z.nullable(z.string()),
  failureRule: z.nullable(z.string()) });
export type ErasureRow = z.infer<typeof erasureRow>;
export const erasureSummary = z.object({ completedLastTwelveMonths: z.number(), inProgress: z.number(),
  refused: z.number(), oldestOpen: z.nullable(z.object({ receivedOn: z.string(), dueOn: z.string(),
  ageDays: z.number(), warning: z.enum(["none", "due_soon", "overdue"]) })) });
export type ErasureSummary = z.infer<typeof erasureSummary>;
export const erasurePage = z.object({ items: z.array(erasureRow), nextCursor: z.nullable(z.string()),
  summary: erasureSummary });
export type ErasurePage = z.infer<typeof erasurePage>;
export const erasureDetail = z.extend(erasureRow, { reason: z.string(), requestReceivedOn: z.string(),
  startedAt: z.nullable(z.string()), failedAt: z.nullable(z.string()), retentionBasis: z.string(),
  counts: z.array(erasureStoreCount), failureDetail: z.nullable(z.string()), blockers: z.array(erasureBlocker) });
export type ErasureDetail = z.infer<typeof erasureDetail>;
export const erasureReviewItem = z.object({ itemKey: z.string(), store: z.string(), recordReference: z.string(),
  field: z.string(), excerpt: z.nullable(z.string()) });
export type ErasureReviewItem = z.infer<typeof erasureReviewItem>;
export const erasurePreview = z.object({ subjectKind: erasureSubjectKind, subjectId: z.string(), token: z.string(),
  counts: z.array(z.object({ store: z.string(), kind: z.string(), count: z.number(), action: z.string() })),
  reviewItems: z.array(erasureReviewItem), accuracyNote: z.string() });
export type ErasurePreview = z.infer<typeof erasurePreview>;
export const erasureRequestInput = z.object({ subjectKind: erasureSubjectKind, subjectId: z.string(),
  requestReference: z.string(), requestReceivedOn: z.string(), reason: z.string(),
  subjectIsNaturalPerson: z.boolean() });
export type ErasureRequestInput = z.infer<typeof erasureRequestInput>;
export const erasureConfirmInput = z.extend(erasureRequestInput, { confirmation: z.string(),
  reviewDecisions: z.array(z.object({ itemKey: z.string(), action: reviewAction })) });
export type ErasureConfirmInput = z.infer<typeof erasureConfirmInput>;

const query = (cursor?: string) => {
  const values = new URLSearchParams({ take: "50" });
  if (cursor) values.set("cursor", cursor);
  return values.toString();
};

export const privacyApi = {
  page: (cursor?: string, signal?: AbortSignal) => apiParsed(`/api/privacy/erasures?${query(cursor)}`, erasurePage, { signal }),
  detail: (erasureId: string, signal?: AbortSignal) => apiParsed(`/api/privacy/erasures/${erasureId}`, erasureDetail, { signal }),
  preview: (request: ErasureRequestInput) => apiParsed("/api/privacy/erasures/preview", erasurePreview, { method: "POST", body: checkedBody(erasureRequestInput, request) }),
  confirm: (request: ErasureConfirmInput) =>
    apiParsed("/api/privacy/erasures/confirm", erasureDetail, { method: "POST", body: checkedBody(erasureConfirmInput, request) }),
};

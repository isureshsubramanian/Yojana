import { api } from "../api/client";
import type { AddresseePage, EntryDraftRequest, EntryFieldChange, EntryReviewPage, EntryTagChoices, KnowledgeEntry, KnowledgeEntryPage, KnowledgeScreenReferencePage, MyQuestionPage, QuestionBellSummary, QuestionNotificationMode, QuestionNotificationPreference, QuestionPage, QuestionThread, Revision, SearchPage } from "./types";

const root = "/api/questions";
const qs = (values: Record<string, string | number | boolean | null | undefined>) => {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  return `?${params}`;
};

export const questionsApi = {
  onRecord: (recordKind: string, recordId: string, status?: string, cursor?: string, take = 50, signal?: AbortSignal) => api<QuestionPage>(`${root}${qs({ recordKind, recordId, status, cursor, take })}`, { signal }),
  thread: (id: string, signal?: AbortSignal) => api<QuestionThread>(`${root}/${id}`, { signal }),
  ask: (body: { recordId: string; recordKind: string; title: string; body: string; directedToUser: string | null; directedToRole: string | null }) => api<{ questionId: string }>(root, { method: "POST", body }),
  reply: (id: string, body: string) => api(`${root}/${id}/replies`, { method: "POST", body: { body } }),
  editQuestion: (id: string, body: string) => api(`${root}/${id}`, { method: "PUT", body: { body } }),
  editReply: (id: string, replyId: string, body: string) => api(`${root}/${id}/replies/${replyId}`, { method: "PUT", body: { body } }),
  accept: (id: string, replyId: string) => api(`${root}/${id}/accept`, { method: "POST", body: { replyId } }),
  close: (id: string, reason: string) => api(`${root}/${id}/close`, { method: "POST", body: { reason } }),
  reopen: (id: string) => api(`${root}/${id}/reopen`, { method: "POST" }),
  createEntry: (id: string) => api<{ entryId: string; status: string; currentVersion: number }>(`${root}/${id}/entry`, { method: "POST" }),
  revisions: (id: string, replyId?: string, signal?: AbortSignal) => api<Revision[]>(replyId ? `${root}/${id}/replies/${replyId}/revisions` : `${root}/${id}/revisions`, { signal }),
  addressees: (search?: string, cursor?: string, signal?: AbortSignal) => api<AddresseePage>(`${root}/addressees${qs({ search, cursor, take: 50 })}`, { signal }),
  search: (query: string, answeredOnly = false, cursor?: string, signal?: AbortSignal) => api<SearchPage>(`${root}/search${qs({ query, answeredOnly, cursor, take: 6 })}`, { signal }),
  mine: (filters: { scope: string; status: string; recordKind: string; age: string; search: string }, cursor?: string, signal?: AbortSignal) => api<MyQuestionPage>(`${root}/mine${qs({ ...filters, cursor, take: 12 })}`, { signal }),
  bell: (signal?: AbortSignal) => api<QuestionBellSummary>(`${root}/notifications/bell?take=5`, { signal }),
  notificationPreference: (signal?: AbortSignal) => api<QuestionNotificationPreference>(`${root}/notifications/preference`, { signal }),
  updateNotificationPreference: (mode: QuestionNotificationMode | null) => api<QuestionNotificationPreference>(`${root}/notifications/preference`, { method: "PUT", body: { mode } }),
};

const entriesRoot = "/api/knowledge/entries";
export const knowledgeApi = {
  browse: (filters: { query: string; role: string; task: string }, cursor?: string, signal?: AbortSignal) => api<KnowledgeEntryPage>(`${entriesRoot}${qs({ ...filters, cursor, take: 12 })}`, { signal }),
  entry: (id: string, signal?: AbortSignal) => api<KnowledgeEntry>(`${entriesRoot}/${id}`, { signal }),
  review: (filter: "pending" | "needs_review", cursor?: string, signal?: AbortSignal) => api<EntryReviewPage>(`${entriesRoot}/review${qs({ filter, cursor, take: 20 })}`, { signal }),
  tagChoices: (signal?: AbortSignal) => api<EntryTagChoices>(`${entriesRoot}/tag-choices`, { signal }),
  changes: (id: string, signal?: AbortSignal) => api<EntryFieldChange[]>(`${entriesRoot}/${id}/changes-since-last-round`, { signal }),
  save: (id: string, body: EntryDraftRequest) => api(`${entriesRoot}/${id}`, { method: "PUT", body }),
  submit: (id: string) => api(`${entriesRoot}/${id}/submit`, { method: "POST" }),
  approve: (id: string, note: string) => api(`${entriesRoot}/${id}/approve`, { method: "POST", body: { note: note || null } }),
  sendBack: (id: string, note: string) => api(`${entriesRoot}/${id}/send-back`, { method: "POST", body: { note } }),
  retire: (id: string, note: string) => api(`${entriesRoot}/${id}/retire`, { method: "POST", body: { note } }),
  stillCorrect: (id: string) => api(`${entriesRoot}/${id}/still-correct`, { method: "POST" }),
  screens: (cursor?: string, signal?: AbortSignal) => api<KnowledgeScreenReferencePage>(`/api/knowledge/screens${qs({ cursor, take: 50 })}`, { signal }),
  markScreenChanged: (screenReference: string, reason: string) => api(`/api/knowledge/screens/changed`, { method: "POST", body: { screenReference, reason } }),
};

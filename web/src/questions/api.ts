import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../api/client";
import { addresseePage, entryDraftRequest, trainingWelcome, entryFieldChange, entryReviewPage, entryTagChoices, knowledgeEntry, knowledgeEntryPage, knowledgeReport, knowledgeScreenReferencePage, myQuestionPage, policyVersionChoicePage, questionBellSummary, questionNotificationPreference, questionPage, questionThread, referenceChoicePage, revision, searchPage, trainingPath, trainingPathList, trainingTeam, type EntryDraftRequest, type KnowledgeReportFilters, type QuestionNotificationMode } from "./types";

const root = "/api/questions";
const qs = (values: Record<string, string | number | boolean | null | undefined>) => {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  return `?${params}`;
};

export const questionsApi = {
  onRecord: (recordKind: string, recordId: string, status?: string, cursor?: string, take = 50, signal?: AbortSignal) => apiParsed(`${root}${qs({ recordKind, recordId, status, cursor, take })}`, questionPage, { signal }),
  thread: (id: string, signal?: AbortSignal) => apiParsed(`${root}/${id}`, questionThread, { signal }),
  ask: (body: { recordId: string; recordKind: string; title: string; body: string; directedToUser: string | null; directedToRole: string | null }) => apiParsed(root, z.object({ questionId: z.string() }), { method: "POST", body }),
  reply: (id: string, body: string) => apiParsed(`${root}/${id}/replies`, z.object({ replyId: z.string() }), { method: "POST", body: { body } }),
  attach: (id: string, file: File, replyId?: string) => { const body = new FormData(); body.append("file", file); return apiParsed(`${root}/${id}${replyId ? `/replies/${replyId}` : ""}/attachments`, z.unknown(), { method: "POST", body }); },
  attachmentUrl: (id: string, fileId: string) => `${root}/${id}/attachments/${fileId}`,
  editQuestion: (id: string, body: string) => apiParsed(`${root}/${id}`, z.unknown(), { method: "PUT", body: { body } }),
  editReply: (id: string, replyId: string, body: string) => apiParsed(`${root}/${id}/replies/${replyId}`, z.unknown(), { method: "PUT", body: { body } }),
  accept: (id: string, replyId: string) => apiParsed(`${root}/${id}/accept`, z.unknown(), { method: "POST", body: { replyId } }),
  close: (id: string, reason: string) => apiParsed(`${root}/${id}/close`, z.unknown(), { method: "POST", body: { reason } }),
  reopen: (id: string) => apiParsed(`${root}/${id}/reopen`, z.unknown(), { method: "POST" }),
  createEntry: (id: string) => apiParsed(`${root}/${id}/entry`, z.object({ entryId: z.string(), status: z.string(), currentVersion: z.number() }), { method: "POST" }),
  revisions: (id: string, replyId?: string, signal?: AbortSignal) => apiParsed(replyId ? `${root}/${id}/replies/${replyId}/revisions` : `${root}/${id}/revisions`, z.array(revision), { signal }),
  addressees: (search?: string, cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/addressees${qs({ search, cursor, take: 50 })}`, addresseePage, { signal }),
  search: (query: string, answeredOnly = false, cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/search${qs({ query, answeredOnly, cursor, take: 6 })}`, searchPage, { signal }),
  mine: (filters: { scope: string; status: string; recordKind: string; age: string; search: string }, cursor?: string, signal?: AbortSignal) => apiParsed(`${root}/mine${qs({ ...filters, cursor, take: 12 })}`, myQuestionPage, { signal }),
  bell: (signal?: AbortSignal) => apiParsed(`${root}/notifications/bell?take=5`, questionBellSummary, { signal }),
  notificationPreference: (signal?: AbortSignal) => apiParsed(`${root}/notifications/preference`, questionNotificationPreference, { signal }),
  updateNotificationPreference: (mode: QuestionNotificationMode | null) => apiParsed(`${root}/notifications/preference`, questionNotificationPreference, { method: "PUT", body: { mode } }),
};

const entriesRoot = "/api/knowledge/entries";
export const knowledgeApi = {
  browse: (filters: { query: string; role: string; task: string }, cursor?: string, signal?: AbortSignal) => apiParsed(`${entriesRoot}${qs({ ...filters, cursor, take: 12 })}`, knowledgeEntryPage, { signal }),
  entry: (id: string, signal?: AbortSignal) => apiParsed(`${entriesRoot}/${id}`, knowledgeEntry, { signal }),
  review: (filter: "pending" | "needs_review", cursor?: string, signal?: AbortSignal) => apiParsed(`${entriesRoot}/review${qs({ filter, cursor, take: 20 })}`, entryReviewPage, { signal }),
  tagChoices: (signal?: AbortSignal) => apiParsed(`${entriesRoot}/tag-choices`, entryTagChoices, { signal }),
  changes: (id: string, signal?: AbortSignal) => apiParsed(`${entriesRoot}/${id}/changes-since-last-round`, z.array(entryFieldChange), { signal }),
  save: (id: string, body: EntryDraftRequest) => apiParsed(`${entriesRoot}/${id}`, z.unknown(), { method: "PUT", body: checkedBody(entryDraftRequest, body) }),
  submit: (id: string) => apiParsed(`${entriesRoot}/${id}/submit`, z.unknown(), { method: "POST" }),
  approve: (id: string, note: string) => apiParsed(`${entriesRoot}/${id}/approve`, z.unknown(), { method: "POST", body: { note: note || null } }),
  sendBack: (id: string, note: string) => apiParsed(`${entriesRoot}/${id}/send-back`, z.unknown(), { method: "POST", body: { note } }),
  retire: (id: string, note: string) => apiParsed(`${entriesRoot}/${id}/retire`, z.unknown(), { method: "POST", body: { note } }),
  stillCorrect: (id: string) => apiParsed(`${entriesRoot}/${id}/still-correct`, z.unknown(), { method: "POST" }),
  screens: (cursor?: string, signal?: AbortSignal) => apiParsed(`/api/knowledge/screens${qs({ cursor, take: 50 })}`, knowledgeScreenReferencePage, { signal }),
  markScreenChanged: (screenReference: string, reason: string) => apiParsed(`/api/knowledge/screens/changed`, z.unknown(), { method: "POST", body: { screenReference, reason } }),
  policyVersionChoices: (search?: string, cursor?: string, signal?: AbortSignal) => apiParsed(`/api/knowledge/references/policy-versions${qs({ search, cursor, take: 25 })}`, policyVersionChoicePage, { signal }),
  screenChoices: (search?: string, cursor?: string, signal?: AbortSignal) => apiParsed(`/api/knowledge/references/screens${qs({ search, cursor, take: 25 })}`, referenceChoicePage, { signal }),
};

const trainingRoot = "/api/knowledge/training-paths";
export const trainingApi = {
  list: (signal?: AbortSignal) => apiParsed(trainingRoot, trainingPathList, { signal }),
  detail: (role: string, signal?: AbortSignal) => apiParsed(`${trainingRoot}/${encodeURIComponent(role)}`, trainingPath, { signal }),
  save: (role: string, introduction: string, entryIds: string[], expectedVersion: number) => apiParsed(`${trainingRoot}/${encodeURIComponent(role)}`, z.unknown(), { method: "PUT", body: { introduction, entryIds, expectedVersion } }),
  markRead: (role: string, entryId: string) => apiParsed(`${trainingRoot}/${encodeURIComponent(role)}/entries/${entryId}/read`, z.unknown(), { method: "POST" }),
  team: (role: string, cursor?: string, signal?: AbortSignal) => apiParsed(`${trainingRoot}/${encodeURIComponent(role)}/team${qs({ cursor, take: 25 })}`, trainingTeam, { signal }),
  welcome: (signal?: AbortSignal) => apiParsed(`${trainingRoot}/welcome`, z.optional(trainingWelcome), { signal }),
  dismissWelcome: (pathId: string) => apiParsed(`${trainingRoot}/welcome/${pathId}/dismiss`, z.unknown(), { method: "POST" }),
};

const reportsRoot = "/api/knowledge/reports";
export const reportsApi = {
  report: (filters: KnowledgeReportFilters, signal?: AbortSignal) => apiParsed(`${reportsRoot}${qs({ period: filters.period, directedRole: filters.directedRole, recordKind: filters.recordKind, topic: filters.topic })}`, knowledgeReport, { signal }),
  exportUrl: (filters: KnowledgeReportFilters) => `${reportsRoot}/export${qs({ period: filters.period, directedRole: filters.directedRole, recordKind: filters.recordKind, topic: filters.topic })}`,
};

import { api } from "../api/client";
import type { AddresseePage, MyQuestionPage, QuestionPage, QuestionThread, Revision, SearchPage } from "./types";

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
  revisions: (id: string, replyId?: string, signal?: AbortSignal) => api<Revision[]>(replyId ? `${root}/${id}/replies/${replyId}/revisions` : `${root}/${id}/revisions`, { signal }),
  addressees: (search?: string, cursor?: string, signal?: AbortSignal) => api<AddresseePage>(`${root}/addressees${qs({ search, cursor, take: 50 })}`, { signal }),
  search: (query: string, answeredOnly = false, cursor?: string, signal?: AbortSignal) => api<SearchPage>(`${root}/search${qs({ query, answeredOnly, cursor, take: 6 })}`, { signal }),
  mine: (filters: { scope: string; status: string; recordKind: string; age: string; search: string }, cursor?: string, signal?: AbortSignal) => api<MyQuestionPage>(`${root}/mine${qs({ ...filters, cursor, take: 12 })}`, { signal }),
};

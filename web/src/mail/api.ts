import { api } from "../api/client.ts";

// Shapes of the customer e-mail endpoints (P9-T20, CustomerMail.cs in Serialisation.Contracts). The server renders every
// message and decides every rule; these screens edit block layouts, show what the server rendered and send commands.

export interface MailTableColumn { header: string; text: string }
export interface MailPanelFact { label: string; text: string }
export interface MailBlock {
  type: string; text?: string | null; when?: string | null; each?: string | null; emptyText?: string | null;
  columns?: MailTableColumn[] | null; facts?: MailPanelFact[] | null; label?: string | null; link?: string | null;
}
export interface MailLanguageContent { language: string; subject: string; preheader: string | null; bandLabel: string; heading: string; blocks: MailBlock[] }
export interface MailTemplateContent { languages: MailLanguageContent[]; attachPdf: boolean; askForNote: boolean }
export interface MailField { path: string; label: string; group: string; repeats: boolean; itemOf: string | null; format: string | null }
export interface MailKindView { kind: string; name: string; recordLabel: string; starterName: string; starterDescription: string; hasPdf: boolean; fields: MailField[] }
export interface MailTemplateCheck { passed: boolean; errors: string[]; warnings: string[]; fieldsUsed: string[]; subjectLength: number }

export interface MailTemplateRow {
  templateId: string; name: string; description: string | null; kind: string; kindName: string; startedFrom: string | null;
  languages: string[]; publishedVersion: number | null; publishedAt: string | null; draftVersion: number | null;
  waitingForReview: boolean; retired: boolean; lastChangeAt: string; lastChangeBy: string; sent30Days: number; defaultFor: string[];
}
export interface MailTemplateKpis { published: number; publishedKinds: number; drafts: number; waitingForReview: number; sent30Days: number; deliveredShare: number | null; languages: string[] }
export interface MailTemplatePage { rows: MailTemplateRow[]; total: number; skip: number; take: number; kpis: MailTemplateKpis; kinds: MailKindView[] }
export interface MailTemplateVersionView {
  versionId: string; version: number; status: string; content: MailTemplateContent; createdAt: string; createdBy: string;
  updatedAt: string; updatedBy: string; publishedBy: string | null; publishedAt: string | null; submittedForReview: boolean; expectedVersion: string;
}
export interface MailSampleRecord { recordId: string; label: string }
export interface MailTemplateDetail {
  templateId: string; name: string; description: string | null; kind: string; startedFrom: string | null;
  versions: MailTemplateVersionView[]; defaultFor: string[]; kindView: MailKindView; canPublish: boolean; sampleRecords: MailSampleRecord[];
}
export interface MailTemplateSaveResult { template: MailTemplateDetail | null; check: MailTemplateCheck }
export interface MailPreviewView { subject: string; preheader: string | null; html: string; text: string; check: MailTemplateCheck; attachmentName: string | null; recordLabel: string | null }
export interface MailQueuedView { messageIds: string[] }

export interface CustomerContact { name: string; address: string; role: string | null }
export interface ComposeTemplateChoice { templateId: string; versionId: string; name: string; version: number; publishedAt: string; languages: string[]; defaultFor: string[]; askForNote: boolean; attachPdf: boolean }
export interface CoverageMailComposeView {
  unitId: string; serial: string; itemName: string; customerName: string | null; contacts: CustomerContact[]; templates: ComposeTemplateChoice[];
  customerLanguage: string | null; senderName: string; replyTo: string | null; fromAddress: string; signedByName: string; signedByTeam: string | null; attachmentName: string;
}
export interface MailRecipientInput { address: string; name?: string | null; confirmed?: boolean }
export interface SendCoverageMail { commandId: string; templateVersionId: string; language: string; to: MailRecipientInput[]; cc: MailRecipientInput[]; note: string | null; signedBy: "me" | "team"; copyToMeOnly: boolean }

export interface SentMailRow {
  messageId: string; queuedAt: string; to: string; toName: string | null; ccCount: number; customerName: string | null; subject: string;
  templateName: string; templateVersion: number; language: string; kind: string; kindName: string; aboutLabel: string; unitId: string | null;
  sentBy: string; automatic: boolean; status: string; attempts: number; nextAttemptAt: string | null; lastError: string | null;
}
export interface SentMailKpis { total: number; sent: number; retrying: number; failed: number; deliveredShare: number | null; mostSentKind: string | null; mostSentCount: number }
export interface MailKindName { kind: string; name: string }
export interface SentMailPage { rows: SentMailRow[]; total: number; nextCursor: string | null; kpis: SentMailKpis; kinds: MailKindName[] }
export interface SentMailEvent { at: string; what: string; detail: string | null }
export interface SentMailDetail { row: SentMailRow; cc: MailRecipientInput[]; html: string; text: string; attachmentName: string | null; hasNote: boolean; transport: string | null; sentAt: string | null; timeline: SentMailEvent[] }
export interface SentMailFilter { status?: string; kind?: string; sentBy?: string; unitId?: string; q?: string }

const query = (values: Record<string, string | number | undefined | null>) => {
  const parameters = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") parameters.set(key, String(value));
  const text = parameters.toString();
  return text ? `?${text}` : "";
};
const options = (signal?: AbortSignal) => signal ? { signal } : undefined;

export const mailApi = {
  templates: (status: string | undefined, signal?: AbortSignal) => api<MailTemplatePage>(`/api/mail-templates${query({ status, take: 100 })}`, options(signal)),
  template: (id: string, signal?: AbortSignal) => api<MailTemplateDetail>(`/api/mail-templates/${id}`, options(signal)),
  create: (kind: string, name: string, copyFromTemplateId?: string | null) =>
    api<MailTemplateDetail>("/api/mail-templates", { method: "POST", body: { kind, name, copyFromTemplateId: copyFromTemplateId ?? null } }),
  saveDraft: (id: string, versionId: string | null, expectedVersion: string | null, content: MailTemplateContent, submitForReview = false) =>
    api<MailTemplateSaveResult>(`/api/mail-templates/${id}/draft`, { method: "PUT", body: { versionId, expectedVersion, content, submitForReview } }),
  publish: (id: string, versionId: string) => api<MailTemplateSaveResult>(`/api/mail-templates/${id}/versions/${versionId}/publish`, { method: "POST" }),
  retire: (id: string) => api<MailTemplateDetail>(`/api/mail-templates/${id}/retire`, { method: "POST" }),
  setDefault: (id: string, languages: string[]) => api<MailTemplateDetail>(`/api/mail-templates/${id}/default`, { method: "PUT", body: { languages } }),
  preview: (id: string, recordId: string | null, language: string, content: MailTemplateContent, note: string | null) =>
    api<MailPreviewView>(`/api/mail-templates/${id}/preview`, { method: "POST", body: { recordId, language, content, note } }),
  testSend: (id: string, recordId: string | null, language: string, content: MailTemplateContent) =>
    api<MailQueuedView>(`/api/mail-templates/${id}/test-send`, { method: "POST", body: { recordId, language, content } }),

  compose: (unitId: string, signal?: AbortSignal) => api<CoverageMailComposeView>(`/api/units/${unitId}/coverage-mail`, options(signal)),
  composePreview: (unitId: string, templateVersionId: string, language: string, recipientName: string | null, note: string | null, signedBy: string, signal?: AbortSignal) =>
    api<MailPreviewView>(`/api/units/${unitId}/coverage-mail/preview`, { method: "POST", body: { templateVersionId, language, recipientName, note, signedBy }, signal }),
  send: (unitId: string, command: SendCoverageMail) => api<MailQueuedView>(`/api/units/${unitId}/coverage-mail/send`, { method: "POST", body: command }),

  sent: (filter: SentMailFilter, cursor: string | null, signal?: AbortSignal) =>
    api<SentMailPage>(`/api/customer-mail${query({ ...filter, cursor, take: 25 })}`, options(signal)),
  aboutUnit: (unitId: string, signal?: AbortSignal) => api<SentMailPage>(`/api/customer-mail${query({ unitId, take: 5 })}`, options(signal)),
  sentMessage: (id: string, signal?: AbortSignal) => api<SentMailDetail>(`/api/customer-mail/${id}`, options(signal)),
  exportUrl: (filter: SentMailFilter) => `/api/customer-mail/export${query({ format: "csv", status: filter.status, kind: filter.kind, sentBy: filter.sentBy, q: filter.q })}`,
};

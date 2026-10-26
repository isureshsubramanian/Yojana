import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../api/client.ts";

// Shapes of the customer e-mail endpoints (P9-T20, CustomerMail.cs in Serialisation.Contracts). The server renders every
// message and decides every rule; these screens edit block layouts, show what the server rendered and send commands.

export const mailTableColumn = z.object({ header: z.string(), text: z.string() });
export type MailTableColumn = z.infer<typeof mailTableColumn>;
export const mailPanelFact = z.object({ label: z.string(), text: z.string() });
export type MailPanelFact = z.infer<typeof mailPanelFact>;
export const mailBlock = z.object({ type: z.string(), text: z.optional(z.nullable(z.string())),
  when: z.optional(z.nullable(z.string())), each: z.optional(z.nullable(z.string())),
  emptyText: z.optional(z.nullable(z.string())), columns: z.optional(z.nullable(z.array(mailTableColumn))),
  facts: z.optional(z.nullable(z.array(mailPanelFact))), label: z.optional(z.nullable(z.string())),
  link: z.optional(z.nullable(z.string())) });
export type MailBlock = z.infer<typeof mailBlock>;
export const mailLanguageContent = z.object({ language: z.string(), subject: z.string(),
  preheader: z.nullable(z.string()), bandLabel: z.string(), heading: z.string(), blocks: z.array(mailBlock) });
export type MailLanguageContent = z.infer<typeof mailLanguageContent>;
export const mailTemplateContent = z.object({ languages: z.array(mailLanguageContent), attachPdf: z.boolean(),
  askForNote: z.boolean() });
export type MailTemplateContent = z.infer<typeof mailTemplateContent>;
export const mailField = z.object({ path: z.string(), label: z.string(), group: z.string(), repeats: z.boolean(),
  itemOf: z.nullable(z.string()), format: z.nullable(z.string()) });
export type MailField = z.infer<typeof mailField>;
export const mailKindView = z.object({ kind: z.string(), name: z.string(), recordLabel: z.string(),
  starterName: z.string(), starterDescription: z.string(), hasPdf: z.boolean(), fields: z.array(mailField) });
export type MailKindView = z.infer<typeof mailKindView>;
export const mailTemplateCheck = z.object({ passed: z.boolean(), errors: z.array(z.string()),
  warnings: z.array(z.string()), fieldsUsed: z.array(z.string()), subjectLength: z.number() });
export type MailTemplateCheck = z.infer<typeof mailTemplateCheck>;
export const mailTemplateRow = z.object({ templateId: z.string(), name: z.string(),
  description: z.nullable(z.string()), kind: z.string(), kindName: z.string(), startedFrom: z.nullable(z.string()),
  languages: z.array(z.string()), publishedVersion: z.nullable(z.number()), publishedAt: z.nullable(z.string()),
  draftVersion: z.nullable(z.number()), waitingForReview: z.boolean(), retired: z.boolean(), lastChangeAt: z.string(),
  lastChangeBy: z.string(), sent30Days: z.number(), defaultFor: z.array(z.string()) });
export type MailTemplateRow = z.infer<typeof mailTemplateRow>;
export const mailTemplateKpis = z.object({ published: z.number(), publishedKinds: z.number(), drafts: z.number(),
  waitingForReview: z.number(), sent30Days: z.number(), deliveredShare: z.nullable(z.number()),
  languages: z.array(z.string()) });
export type MailTemplateKpis = z.infer<typeof mailTemplateKpis>;
export const mailTemplatePage = z.object({ rows: z.array(mailTemplateRow), total: z.number(), skip: z.number(),
  take: z.number(), kpis: mailTemplateKpis, kinds: z.array(mailKindView) });
export type MailTemplatePage = z.infer<typeof mailTemplatePage>;
export const mailTemplateVersionView = z.object({ versionId: z.string(), version: z.number(), status: z.string(),
  content: mailTemplateContent, createdAt: z.string(), createdBy: z.string(), updatedAt: z.string(),
  updatedBy: z.string(), publishedBy: z.nullable(z.string()), publishedAt: z.nullable(z.string()),
  submittedForReview: z.boolean(), expectedVersion: z.string() });
export type MailTemplateVersionView = z.infer<typeof mailTemplateVersionView>;
export const mailSampleRecord = z.object({ recordId: z.string(), label: z.string() });
export type MailSampleRecord = z.infer<typeof mailSampleRecord>;
export const mailTemplateDetail = z.object({ templateId: z.string(), name: z.string(),
  description: z.nullable(z.string()), kind: z.string(), startedFrom: z.nullable(z.string()),
  versions: z.array(mailTemplateVersionView), defaultFor: z.array(z.string()), kindView: mailKindView,
  canPublish: z.boolean(), sampleRecords: z.array(mailSampleRecord) });
export type MailTemplateDetail = z.infer<typeof mailTemplateDetail>;
export const mailTemplateSaveResult = z.object({ template: z.nullable(mailTemplateDetail), check: mailTemplateCheck });
export type MailTemplateSaveResult = z.infer<typeof mailTemplateSaveResult>;
export const mailPreviewView = z.object({ subject: z.string(), preheader: z.nullable(z.string()), html: z.string(),
  text: z.string(), check: mailTemplateCheck, attachmentName: z.nullable(z.string()),
  recordLabel: z.nullable(z.string()) });
export type MailPreviewView = z.infer<typeof mailPreviewView>;
export const mailQueuedView = z.object({ messageIds: z.array(z.string()) });
export type MailQueuedView = z.infer<typeof mailQueuedView>;
export const customerContact = z.object({ name: z.string(), address: z.string(), role: z.nullable(z.string()) });
export type CustomerContact = z.infer<typeof customerContact>;
export const composeTemplateChoice = z.object({ templateId: z.string(), versionId: z.string(), name: z.string(),
  version: z.number(), publishedAt: z.string(), languages: z.array(z.string()), defaultFor: z.array(z.string()),
  askForNote: z.boolean(), attachPdf: z.boolean() });
export type ComposeTemplateChoice = z.infer<typeof composeTemplateChoice>;
export const coverageMailComposeView = z.object({ unitId: z.string(), serial: z.string(), itemName: z.string(),
  customerName: z.nullable(z.string()), contacts: z.array(customerContact), templates: z.array(composeTemplateChoice),
  customerLanguage: z.nullable(z.string()), senderName: z.string(), replyTo: z.nullable(z.string()),
  fromAddress: z.string(), signedByName: z.string(), signedByTeam: z.nullable(z.string()),
  attachmentName: z.string() });
export type CoverageMailComposeView = z.infer<typeof coverageMailComposeView>;
export const mailRecipientInput = z.object({ address: z.string(), name: z.optional(z.nullable(z.string())),
  confirmed: z.optional(z.boolean()) });
export type MailRecipientInput = z.infer<typeof mailRecipientInput>;
export const sendCoverageMail = z.object({ commandId: z.string(), templateVersionId: z.string(), language: z.string(),
  to: z.array(mailRecipientInput), cc: z.array(mailRecipientInput), note: z.nullable(z.string()),
  signedBy: z.enum(["me", "team"]), copyToMeOnly: z.boolean() });
export type SendCoverageMail = z.infer<typeof sendCoverageMail>;
export const sentMailRow = z.object({ messageId: z.string(), queuedAt: z.string(), to: z.string(),
  toName: z.nullable(z.string()), ccCount: z.number(), customerName: z.nullable(z.string()), subject: z.string(),
  templateName: z.string(), templateVersion: z.number(), language: z.string(), kind: z.string(), kindName: z.string(),
  aboutLabel: z.string(), unitId: z.nullable(z.string()), sentBy: z.string(), automatic: z.boolean(),
  status: z.string(), attempts: z.number(), nextAttemptAt: z.nullable(z.string()), lastError: z.nullable(z.string()) });
export type SentMailRow = z.infer<typeof sentMailRow>;
export const sentMailKpis = z.object({ total: z.number(), sent: z.number(), retrying: z.number(), failed: z.number(),
  deliveredShare: z.nullable(z.number()), mostSentKind: z.nullable(z.string()), mostSentCount: z.number() });
export type SentMailKpis = z.infer<typeof sentMailKpis>;
export const mailKindName = z.object({ kind: z.string(), name: z.string() });
export type MailKindName = z.infer<typeof mailKindName>;
export const sentMailPage = z.object({ rows: z.array(sentMailRow), total: z.number(),
  nextCursor: z.nullable(z.string()), kpis: sentMailKpis, kinds: z.array(mailKindName) });
export type SentMailPage = z.infer<typeof sentMailPage>;
export const sentMailEvent = z.object({ at: z.string(), what: z.string(), detail: z.nullable(z.string()) });
export type SentMailEvent = z.infer<typeof sentMailEvent>;
export const sentMailDetail = z.object({ row: sentMailRow, cc: z.array(mailRecipientInput), html: z.string(),
  text: z.string(), attachmentName: z.nullable(z.string()), hasNote: z.boolean(), transport: z.nullable(z.string()),
  sentAt: z.nullable(z.string()), timeline: z.array(sentMailEvent) });
export type SentMailDetail = z.infer<typeof sentMailDetail>;

export interface SentMailFilter { status?: string; kind?: string; sentBy?: string; unitId?: string; q?: string }

const query = (values: Record<string, string | number | undefined | null>) => {
  const parameters = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") parameters.set(key, String(value));
  const text = parameters.toString();
  return text ? `?${text}` : "";
};
const options = (signal?: AbortSignal) => signal ? { signal } : undefined;

export const mailApi = {
  templates: (status: string | undefined, signal?: AbortSignal) => apiParsed(`/api/mail-templates${query({ status, take: 100 })}`, mailTemplatePage, options(signal)),
  template: (id: string, signal?: AbortSignal) => apiParsed(`/api/mail-templates/${id}`, mailTemplateDetail, options(signal)),
  create: (kind: string, name: string, copyFromTemplateId?: string | null) =>
    apiParsed("/api/mail-templates", mailTemplateDetail, { method: "POST", body: { kind, name, copyFromTemplateId: copyFromTemplateId ?? null } }),
  saveDraft: (id: string, versionId: string | null, expectedVersion: string | null, content: MailTemplateContent, submitForReview = false) =>
    apiParsed(`/api/mail-templates/${id}/draft`, mailTemplateSaveResult, { method: "PUT", body: { versionId, expectedVersion, content, submitForReview } }),
  publish: (id: string, versionId: string) => apiParsed(`/api/mail-templates/${id}/versions/${versionId}/publish`, mailTemplateSaveResult, { method: "POST" }),
  retire: (id: string) => apiParsed(`/api/mail-templates/${id}/retire`, mailTemplateDetail, { method: "POST" }),
  setDefault: (id: string, languages: string[]) => apiParsed(`/api/mail-templates/${id}/default`, mailTemplateDetail, { method: "PUT", body: { languages } }),
  preview: (id: string, recordId: string | null, language: string, content: MailTemplateContent, note: string | null) =>
    apiParsed(`/api/mail-templates/${id}/preview`, mailPreviewView, { method: "POST", body: { recordId, language, content, note } }),
  testSend: (id: string, recordId: string | null, language: string, content: MailTemplateContent) =>
    apiParsed(`/api/mail-templates/${id}/test-send`, mailQueuedView, { method: "POST", body: { recordId, language, content } }),

  compose: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}/coverage-mail`, coverageMailComposeView, options(signal)),
  composePreview: (unitId: string, templateVersionId: string, language: string, recipientName: string | null, note: string | null, signedBy: string, signal?: AbortSignal) =>
    apiParsed(`/api/units/${unitId}/coverage-mail/preview`, mailPreviewView, { method: "POST", body: { templateVersionId, language, recipientName, note, signedBy }, signal }),
  send: (unitId: string, command: SendCoverageMail) => apiParsed(`/api/units/${unitId}/coverage-mail/send`, mailQueuedView, { method: "POST", body: checkedBody(sendCoverageMail, command) }),

  sent: (filter: SentMailFilter, cursor: string | null, signal?: AbortSignal) =>
    apiParsed(`/api/customer-mail${query({ ...filter, cursor, take: 25 })}`, sentMailPage, options(signal)),
  aboutUnit: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/customer-mail${query({ unitId, take: 5 })}`, sentMailPage, options(signal)),
  sentMessage: (id: string, signal?: AbortSignal) => apiParsed(`/api/customer-mail/${id}`, sentMailDetail, options(signal)),
  exportUrl: (filter: SentMailFilter, sort?: string, dir?: string) => `/api/customer-mail/export${query({ format: "csv", status: filter.status, kind: filter.kind, sentBy: filter.sentBy, q: filter.q, sort, dir })}`,
};

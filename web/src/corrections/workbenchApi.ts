// The correction workbench's contract with /api/workbench (P3-T16, T16b). Shapes mirror Contracts/Workbench.cs and
// SelectionSets.cs; the server owns every rule, so the screen only shows what these calls return.
import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import {
  commandRefusal,
  correctionCertificatePreview,
  correctionEntitlementPreview,
  correctionEventPreview,
  correctionPreview,
  correctionPrintJobPreview,
  documentRef,
  documentRefInput,
  documentRefRequest,
  episodeRow,
  historyRow,
  identifierRow,
  unitDetail,
  unitOutcome,
} from "./schemas.ts";
import { workbenchChoice as settingsWorkbenchChoice } from "./types.ts";

const text = z.string();
const number = z.number();
const nullableText = z.nullable(text);

const workbenchEntryResponse = z.object({
  kind: text,
  value: nullableText,
  identifierType: nullableText,
  unitId: nullableText,
  document: z.nullable(documentRef),
  selectionId: nullableText,
});
export const workbenchEntry = z.object({
  kind: text,
  value: z.optional(nullableText),
  identifierType: z.optional(nullableText),
  unitId: z.optional(nullableText),
  document: z.optional(z.nullable(documentRefInput)),
  selectionId: z.optional(nullableText),
});
const workbenchEntryRequest = z.looseObject({
  kind: text,
  value: z.optional(nullableText),
  identifierType: z.optional(nullableText),
  unitId: z.optional(nullableText),
  document: z.optional(z.nullable(documentRefRequest)),
  selectionId: z.optional(nullableText),
});
export const workbenchCandidate = z.object({
  unitId: text,
  resolvedUnitId: text,
  mergedAlias: z.boolean(),
  lifecycleStatus: text,
  itemId: nullableText,
  displayIdentifier: nullableText,
  matchKind: nullableText,
});
export const workbenchResolution = z.object({
  entry: workbenchEntryResponse,
  candidates: z.array(workbenchCandidate),
  selectionId: nullableText,
  resolution: nullableText,
});
export type WorkbenchEntry = z.infer<typeof workbenchEntry>;
export type WorkbenchCandidate = z.infer<typeof workbenchCandidate>;
export type WorkbenchResolution = z.infer<typeof workbenchResolution>;

export const workbenchChoice = settingsWorkbenchChoice;
export const workbenchChoiceSuggestion = z.object({ choice: text, rule: text, label: text });
export const workbenchChoiceGroup = z.object({ area: text, choices: z.array(workbenchChoice) });
export const workbenchPicker = z.object({
  search: nullableText,
  suggestions: z.array(workbenchChoiceSuggestion),
  groups: z.array(workbenchChoiceGroup),
  fallback: workbenchChoice,
});
export type WorkbenchChoiceOption = z.infer<typeof workbenchChoice>["questions"][number]["options"][number];
export type WorkbenchNarrowingQuestion = z.infer<typeof workbenchChoice>["questions"][number];
export type WorkbenchChoice = z.infer<typeof workbenchChoice>;
export type WorkbenchChoiceSuggestion = z.infer<typeof workbenchChoiceSuggestion>;
export type WorkbenchChoiceGroup = z.infer<typeof workbenchChoiceGroup>;
export type WorkbenchPicker = z.infer<typeof workbenchPicker>;

const workbenchAnswersResponse = z.object({
  command: text, arguments: z.unknown(), narrowing: z.nullable(z.record(text, text)),
});
export const workbenchAnswers = z.object({
  command: text, arguments: z.unknown(), narrowing: z.optional(z.nullable(z.record(text, text))),
});
const workbenchAnswersRequest = z.looseObject({
  command: text, arguments: z.unknown(), narrowing: z.optional(z.nullable(z.record(text, text))),
});
const workbenchEvidenceRefResponse = z.object({ kind: text, ref: text, note: nullableText });
export const workbenchEvidenceRef = z.object({ kind: text, ref: text, note: z.optional(nullableText) });
const workbenchEvidenceRefRequest = z.looseObject({ kind: text, ref: text, note: z.optional(nullableText) });
const workbenchTargetResponse = z.object({
  kind: text, unitId: nullableText, document: z.nullable(documentRef), selectionId: nullableText,
});
export const workbenchTarget = z.object({
  kind: text,
  unitId: z.optional(nullableText),
  document: z.optional(z.nullable(documentRefInput)),
  selectionId: z.optional(nullableText),
});
const workbenchTargetRequest = z.looseObject({
  kind: text,
  unitId: z.optional(nullableText),
  document: z.optional(z.nullable(documentRefRequest)),
  selectionId: z.optional(nullableText),
});
export const workbenchPreviewRequest = z.object({
  commandId: text,
  choice: text,
  answers: workbenchAnswers,
  target: workbenchTarget,
  reason: text,
  note: z.optional(nullableText),
  evidence: z.optional(z.nullable(z.array(workbenchEvidenceRef))),
});
const workbenchPreviewRequestBody = z.looseObject({
  commandId: text,
  choice: text,
  answers: workbenchAnswersRequest,
  target: workbenchTargetRequest,
  reason: text,
  note: z.optional(nullableText),
  evidence: z.optional(z.nullable(z.array(workbenchEvidenceRefRequest))),
});
export type WorkbenchAnswers = z.infer<typeof workbenchAnswers>;
export type WorkbenchEvidenceRef = z.infer<typeof workbenchEvidenceRef>;
export type WorkbenchTarget = z.infer<typeof workbenchTarget>;
export type WorkbenchPreviewRequest = z.infer<typeof workbenchPreviewRequest>;

export {
  commandRefusal,
  correctionCertificatePreview,
  correctionEntitlementPreview,
  correctionEventPreview,
  correctionPreview,
  correctionPrintJobPreview,
  unitOutcome,
};
export type UnitOutcome = z.infer<typeof unitOutcome>;
export type CommandRefusal = z.infer<typeof commandRefusal>;
export type CorrectionEventPreview = z.infer<typeof correctionEventPreview>;
export type CorrectionEntitlementPreview = z.infer<typeof correctionEntitlementPreview>;
export type CorrectionCertificatePreview = z.infer<typeof correctionCertificatePreview>;
export type CorrectionPrintJobPreview = z.infer<typeof correctionPrintJobPreview>;
export type CorrectionPreview = z.infer<typeof correctionPreview>;
export const workbenchPreviewResult = z.object({
  choice: text, command: text, preview: correctionPreview, previewHash: text,
});
export type WorkbenchPreviewResult = z.infer<typeof workbenchPreviewResult>;

export const workbenchApproval = z.object({
  userRef: text, roles: z.array(text), approvedAt: text, note: nullableText,
});
export const workbenchRequestRow = z.object({
  requestId: text,
  commandId: text,
  choice: text,
  command: text,
  target: workbenchTargetResponse,
  answers: workbenchAnswersResponse,
  reason: text,
  note: nullableText,
  evidence: z.array(workbenchEvidenceRefResponse),
  preview: correctionPreview,
  previewHash: text,
  status: text,
  initiatedBy: text,
  initiatorRoles: z.array(text),
  initiatedAt: text,
  requiredApproverRoles: z.array(text),
  approvals: z.array(workbenchApproval),
  rejectedBy: nullableText,
  rejectedAt: nullableText,
  decisionNote: nullableText,
  committedBy: nullableText,
  committedAt: nullableText,
  result: z.nullable(z.unknown()),
  unclassifiedDescription: nullableText,
  classifiedBy: nullableText,
  classifiedAt: nullableText,
  escalatedBy: nullableText,
  escalatedAt: nullableText,
});
export const workbenchRequestPage = z.object({
  items: z.array(workbenchRequestRow), total: number, skip: number, take: number,
});
export type WorkbenchApproval = z.infer<typeof workbenchApproval>;
export type WorkbenchRequestRow = z.infer<typeof workbenchRequestRow>;
export type WorkbenchRequestPage = z.infer<typeof workbenchRequestPage>;

export const workbenchItemAssignment = z.object({
  assignmentId: text, itemId: text, modelVersionId: nullableText, validFrom: text, validTo: nullableText, reason: text,
});
export const workbenchEntitlement = z.object({
  entitlementId: text,
  coverageFamily: text,
  type: text,
  direction: text,
  status: text,
  startDate: nullableText,
  endDate: nullableText,
  supersedesEntitlementId: nullableText,
  parentEntitlementId: nullableText,
  coverageFamilyName: nullableText,
});
export const workbenchAssemblyLink = z.object({
  linkId: text,
  parentUnitId: text,
  childUnitId: nullableText,
  childItemId: nullableText,
  relation: text,
  slot: nullableText,
  installedAt: text,
  removedAt: nullableText,
  perspective: text,
});
export const workbenchMergeHistory = z.object({
  correctionId: text,
  survivorUnitId: text,
  absorbedUnitId: text,
  mergedAt: text,
  reversedByCorrectionId: nullableText,
  reversedAt: nullableText,
});
export const workbenchCorrectionHistory = z.object({
  correctionId: text,
  command: text,
  reasonCode: text,
  initiatedVia: text,
  initiatingRole: text,
  actorRef: text,
  eventTime: text,
  recordedTime: text,
  correctsCorrectionId: nullableText,
  note: nullableText,
});
export const workbenchUnitLineage = z.object({
  unitId: text,
  state: unitDetail,
  identifiers: z.array(identifierRow),
  itemAssignments: z.array(workbenchItemAssignment),
  episodes: z.array(episodeRow),
  entitlements: z.array(workbenchEntitlement),
  assembly: z.array(workbenchAssemblyLink),
  merges: z.array(workbenchMergeHistory),
  corrections: z.array(workbenchCorrectionHistory),
  lastEvents: z.array(historyRow),
});
export type WorkbenchItemAssignment = z.infer<typeof workbenchItemAssignment>;
export type WorkbenchEntitlement = z.infer<typeof workbenchEntitlement>;
export type WorkbenchAssemblyLink = z.infer<typeof workbenchAssemblyLink>;
export type WorkbenchMergeHistory = z.infer<typeof workbenchMergeHistory>;
export type WorkbenchCorrectionHistory = z.infer<typeof workbenchCorrectionHistory>;
export type WorkbenchUnitLineage = z.infer<typeof workbenchUnitLineage>;

const resolveWorkbenchEntry = z.looseObject({ resolutionId: text, entry: workbenchEntryRequest, actorRef: text });
const fileUnclassifiedWorkbenchRequest = z.looseObject({
  target: workbenchTargetRequest, description: text, evidence: z.array(workbenchEvidenceRefRequest),
});
const approveWorkbenchRequest = z.looseObject({ note: nullableText });
const rejectWorkbenchRequest = z.looseObject({ note: text });

/** Request states the approver can still act on. */
export const OPEN_STATUSES = ["awaiting_approval", "approved"];

export const workbenchApi = {
  resolve: (entry: WorkbenchEntry, signal?: AbortSignal): Promise<WorkbenchResolution> => apiParsed("/api/workbench/resolve", workbenchResolution, { method: "POST", body: z.parse(resolveWorkbenchEntry, { resolutionId: crypto.randomUUID(), entry, actorRef: "" }), signal }),
  lineage: (unitId: string, signal?: AbortSignal): Promise<WorkbenchUnitLineage> => apiParsed(`/api/workbench/units/${unitId}/lineage`, workbenchUnitLineage, { signal }),
  picker: (unitId: string | null, q: string, signal?: AbortSignal) => {
    const query = new URLSearchParams();
    if (unitId) query.set("unitId", unitId);
    if (q.trim()) query.set("q", q.trim());
    return apiParsed(`/api/workbench/picker?${query}`, workbenchPicker, { signal });
  },
  choices: (signal?: AbortSignal): Promise<WorkbenchChoice[]> => apiParsed("/api/workbench/choices", z.array(workbenchChoice), { signal }),
  reasons: (command: string, signal?: AbortSignal): Promise<string[]> => apiParsed(`/api/workbench/routes/${encodeURIComponent(command)}/reasons`, z.array(text), { signal }),
  preview: (request: WorkbenchPreviewRequest, signal?: AbortSignal): Promise<WorkbenchPreviewResult> => apiParsed("/api/workbench/preview", workbenchPreviewResult, { method: "POST", body: z.parse(workbenchPreviewRequestBody, request), signal }),
  initiate: (requestId: string, request: WorkbenchPreviewRequest): Promise<WorkbenchRequestRow> => apiParsed(`/api/workbench/requests/${requestId}`, workbenchRequestRow, { method: "POST", body: z.parse(workbenchPreviewRequestBody, request) }),
  fileUnclassified: (requestId: string, target: WorkbenchTarget, description: string, evidence: WorkbenchEvidenceRef[]) =>
    apiParsed(`/api/workbench/requests/${requestId}/unclassified`, workbenchRequestRow, { method: "POST", body: z.parse(fileUnclassifiedWorkbenchRequest, { target, description, evidence }) }),
  request: (requestId: string, signal?: AbortSignal): Promise<WorkbenchRequestRow> => apiParsed(`/api/workbench/requests/${requestId}`, workbenchRequestRow, { signal }),
  inbox: (skip: number, take: number, signal?: AbortSignal): Promise<WorkbenchRequestPage> => apiParsed(`/api/workbench/inbox?skip=${skip}&take=${take}`, workbenchRequestPage, { signal }),
  approve: (requestId: string, note: string | null): Promise<WorkbenchRequestRow> => apiParsed(`/api/workbench/requests/${requestId}/approve`, workbenchRequestRow, { method: "POST", body: z.parse(approveWorkbenchRequest, { note }) }),
  reject: (requestId: string, note: string): Promise<WorkbenchRequestRow> => apiParsed(`/api/workbench/requests/${requestId}/reject`, workbenchRequestRow, { method: "POST", body: z.parse(rejectWorkbenchRequest, { note }) }),
  commit: (requestId: string): Promise<WorkbenchRequestRow> => apiParsed(`/api/workbench/requests/${requestId}/commit`, workbenchRequestRow, { method: "POST" }),
};

/** "unit.identifiers_swapped" rows grouped by event type, the "Will be recorded" table of both screens. */
export function recordedRows(preview: CorrectionPreview): { what: string; detail: string; count: number }[] {
  const rows = new Map<string, { what: string; detail: string; count: number }>();
  const add = (what: string, detail: string) => {
    const row = rows.get(what);
    if (row) row.count += 1;
    else rows.set(what, { what, detail, count: 1 });
  };
  preview.unitEvents.forEach((e) => add(e.eventType, e.summary ?? ""));
  preview.entitlementChanges.forEach((e) => add(`entitlement · ${e.change}`, e.summary ?? ""));
  preview.certificateVersions.forEach((c) => add(`${c.certificateType} · ${c.action}`, c.reason));
  preview.printJobs.forEach((p) => add(`print job · ${p.jobType}`, p.reason));
  return [...rows.values()];
}

/** Commands whose arguments live on a dedicated screen; the workbench hands the user over instead of re-implementing them. */
export const DEDICATED_SCREENS: Record<string, { path: string; label: string }> = {
  "replacement.ship": { path: "/replacements/new", label: "Ship warranty replacement" },
  "refurbishment.release": { path: "/refurbishments/:id", label: "Release refurbishment" },
  merge_units: { path: "/corrections/merge", label: "Merge units" },
  reassign_allocation: { path: "/corrections/reassign-allocation", label: "Reassign allocation" },
  assign_identifiers: { path: "/corrections/vendor-serials", label: "Assign vendor serials" },
  "entitlement.amend": { path: "/entitlements/bulk?operation=entitlement.amend", label: "Amend entitlement" },
  "entitlement.supersede": { path: "/entitlements/bulk?operation=entitlement.supersede", label: "Supersede entitlement" },
  "entitlement.attach_extended": { path: "/entitlements/bulk?operation=entitlement.attach_extended", label: "Attach extended or add-on" },
  "entitlement.close": { path: "/entitlements/bulk?operation=entitlement.close", label: "Close entitlement" },
  "entitlement.backfill": { path: "/entitlements/bulk?operation=entitlement.backfill", label: "Backfill entitlements" },
};

// Readable names for commands whose plain wording differs from their code; other codes read as words ("swap_identifiers" → "Swap identifiers").
const COMMAND_NAMES: Record<string, string> = {
  correct_identifier: "Correct an identifier",
  correct_item: "Correct the item",
  correct_source_document: "Correct the source document",
  reassign_allocation: "Reassign an allocation",
  void_unit: "Void a unit",
  correct_as_built: "Correct as-built parts",
  late_serialization: "Serialise an item late",
  correct_replacement_link: "Correct a replacement link",
  "entitlement.amend": "Amend an entitlement",
};

/** The command's name as a person reads it, never the internal code. */
export function commandName(command: string): string {
  const known = COMMAND_NAMES[command];
  if (known) return known;
  const words = command.replace(/[._]/g, " ").trim().toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

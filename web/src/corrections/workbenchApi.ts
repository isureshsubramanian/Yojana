// The correction workbench's contract with /api/workbench (P3-T16, T16b). Shapes mirror Contracts/Workbench.cs and
// SelectionSets.cs; the server owns every rule, so the screen only shows what these calls return.
import { api } from "../api/client";
import type { DocumentRef, EpisodeRow, HistoryRow, IdentifierRow, UnitDetail } from "../api/units";

export interface WorkbenchEntry { kind: string; value?: string | null; identifierType?: string | null; unitId?: string | null; document?: DocumentRef | null; selectionId?: string | null }
export interface WorkbenchCandidate { unitId: string; resolvedUnitId: string; mergedAlias: boolean; lifecycleStatus: string; itemId: string | null; displayIdentifier: string | null; matchKind: string | null }
export interface WorkbenchResolution { entry: WorkbenchEntry; candidates: WorkbenchCandidate[]; selectionId: string | null; resolution: string | null }

export interface WorkbenchChoiceOption { value: string; label: string; command: string; defaultReason: string; rootCause: number }
export interface WorkbenchNarrowingQuestion { key: string; prompt: string; options: WorkbenchChoiceOption[] }
export interface WorkbenchChoice { choice: string; label: string; area: string; helpText: string; commands: string[]; questions: WorkbenchNarrowingQuestion[]; defaultReason: string; rootCause: number; searchWords: string[] | null; order: number; initiateRoles: string[] | null; commitRoles: string[] | null; status: string; standard: boolean; fallback: boolean }
export interface WorkbenchChoiceSuggestion { choice: string; rule: string; label: string }
export interface WorkbenchChoiceGroup { area: string; choices: WorkbenchChoice[] }
export interface WorkbenchPicker { search: string | null; suggestions: WorkbenchChoiceSuggestion[]; groups: WorkbenchChoiceGroup[]; fallback: WorkbenchChoice }

export interface WorkbenchAnswers { command: string; arguments: unknown; narrowing?: Record<string, string> | null }
export interface WorkbenchEvidenceRef { kind: string; ref: string; note?: string | null }
export interface WorkbenchTarget { kind: string; unitId?: string | null; document?: DocumentRef | null; selectionId?: string | null }
export interface WorkbenchPreviewRequest { commandId: string; choice: string; answers: WorkbenchAnswers; target: WorkbenchTarget; reason: string; note?: string | null; evidence?: WorkbenchEvidenceRef[] | null }

export interface UnitOutcome { unitId: string; serial: string | null; outcome: string; reason: string | null; detail: string | null; unblockingStep: string | null }
export interface CommandRefusal { rule: string; message: string; unblockingStep: string | null }
export interface CorrectionEventPreview { unitId: string; eventType: string; effectiveAt: string; summary: string | null }
export interface CorrectionEntitlementPreview { unitId: string; change: string; entitlementId: string | null; summary: string | null }
export interface CorrectionCertificatePreview { unitId: string; certificateType: string; action: string; reason: string }
export interface CorrectionPrintJobPreview { unitId: string; jobType: string; reason: string }
export interface CorrectionPreview {
  selectionId: string | null; command: string; unitCount: number; change: number; skip: number; block: number;
  units: UnitOutcome[]; unitEvents: CorrectionEventPreview[]; entitlementChanges: CorrectionEntitlementPreview[];
  certificateVersions: CorrectionCertificatePreview[]; printJobs: CorrectionPrintJobPreview[]; refusal: CommandRefusal | null;
}
export interface WorkbenchPreviewResult { choice: string; command: string; preview: CorrectionPreview; previewHash: string }

export interface WorkbenchApproval { userRef: string; roles: string[]; approvedAt: string; note: string | null }
export interface WorkbenchRequestRow {
  requestId: string; commandId: string; choice: string; command: string; target: WorkbenchTarget; answers: WorkbenchAnswers; reason: string; note: string | null;
  evidence: WorkbenchEvidenceRef[]; preview: CorrectionPreview; previewHash: string; status: string; initiatedBy: string; initiatorRoles: string[]; initiatedAt: string;
  requiredApproverRoles: string[]; approvals: WorkbenchApproval[]; rejectedBy: string | null; rejectedAt: string | null; decisionNote: string | null;
  committedBy: string | null; committedAt: string | null; result: unknown; unclassifiedDescription: string | null;
  classifiedBy: string | null; classifiedAt: string | null; escalatedBy: string | null; escalatedAt: string | null;
}
export interface WorkbenchRequestPage { items: WorkbenchRequestRow[]; total: number; skip: number; take: number }

export interface WorkbenchItemAssignment { assignmentId: string; itemId: string; modelVersionId: string | null; validFrom: string; validTo: string | null; reason: string }
export interface WorkbenchEntitlement { entitlementId: string; coverageFamily: string; type: string; direction: string; status: string; startDate: string | null; endDate: string | null; supersedesEntitlementId: string | null; parentEntitlementId: string | null }
export interface WorkbenchAssemblyLink { linkId: string; parentUnitId: string; childUnitId: string | null; childItemId: string | null; relation: string; slot: string | null; installedAt: string; removedAt: string | null; perspective: string }
export interface WorkbenchCorrectionHistory { correctionId: string; command: string; reasonCode: string; initiatedVia: string; initiatingRole: string; actorRef: string; eventTime: string; recordedTime: string; correctsCorrectionId: string | null; note: string | null }
export interface WorkbenchUnitLineage {
  unitId: string; state: UnitDetail; identifiers: IdentifierRow[]; itemAssignments: WorkbenchItemAssignment[]; episodes: EpisodeRow[];
  entitlements: WorkbenchEntitlement[]; assembly: WorkbenchAssemblyLink[]; corrections: WorkbenchCorrectionHistory[]; lastEvents: HistoryRow[];
}

/** Request states the approver can still act on. */
export const OPEN_STATUSES = ["awaiting_approval", "approved"];

export const workbenchApi = {
  resolve: (entry: WorkbenchEntry, signal?: AbortSignal) => api<WorkbenchResolution>("/api/workbench/resolve", { method: "POST", body: { resolutionId: crypto.randomUUID(), entry, actorRef: "" }, signal }),
  lineage: (unitId: string, signal?: AbortSignal) => api<WorkbenchUnitLineage>(`/api/workbench/units/${unitId}/lineage`, { signal }),
  picker: (unitId: string | null, q: string, signal?: AbortSignal) => {
    const query = new URLSearchParams();
    if (unitId) query.set("unitId", unitId);
    if (q.trim()) query.set("q", q.trim());
    return api<WorkbenchPicker>(`/api/workbench/picker?${query}`, { signal });
  },
  choices: (signal?: AbortSignal) => api<WorkbenchChoice[]>("/api/workbench/choices", { signal }),
  reasons: (command: string, signal?: AbortSignal) => api<string[]>(`/api/workbench/routes/${encodeURIComponent(command)}/reasons`, { signal }),
  preview: (request: WorkbenchPreviewRequest, signal?: AbortSignal) => api<WorkbenchPreviewResult>("/api/workbench/preview", { method: "POST", body: request, signal }),
  initiate: (requestId: string, request: WorkbenchPreviewRequest) => api<WorkbenchRequestRow>(`/api/workbench/requests/${requestId}`, { method: "POST", body: request }),
  fileUnclassified: (requestId: string, target: WorkbenchTarget, description: string, evidence: WorkbenchEvidenceRef[]) =>
    api<WorkbenchRequestRow>(`/api/workbench/requests/${requestId}/unclassified`, { method: "POST", body: { target, description, evidence } }),
  request: (requestId: string, signal?: AbortSignal) => api<WorkbenchRequestRow>(`/api/workbench/requests/${requestId}`, { signal }),
  inbox: (skip: number, take: number, signal?: AbortSignal) => api<WorkbenchRequestPage>(`/api/workbench/inbox?skip=${skip}&take=${take}`, { signal }),
  approve: (requestId: string, note: string | null) => api<WorkbenchRequestRow>(`/api/workbench/requests/${requestId}/approve`, { method: "POST", body: { note } }),
  reject: (requestId: string, note: string) => api<WorkbenchRequestRow>(`/api/workbench/requests/${requestId}/reject`, { method: "POST", body: { note } }),
  commit: (requestId: string) => api<WorkbenchRequestRow>(`/api/workbench/requests/${requestId}/commit`, { method: "POST" }),
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

/** "swap_identifiers" → "SwapIdentifiers", the command's name as the ADR and the approver see it. */
export function commandName(command: string): string {
  return command.split(/[._]/).map((part) => part.charAt(0).toUpperCase() + part.slice(1)).join("");
}

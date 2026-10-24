// The Serialisation module's HTTP surface (P1-T19, P1-T20): read models as the server returns them, commands as the
// contracts define them. Every list is a page with a total. Shapes mirror Yojana.Serialisation.Contracts.
import { api } from "./client";
import type { PageResult } from "../components/types";

export interface Page<T> { items: T[]; total: number; skip: number; take: number }
export interface DocumentRef { type: string; id: string; line?: number | null }

export interface LookupHit { unitId: string; identifierId: string; identifierType: string; scheme: string; normalisedValue: string; rawValue: string; open: boolean; supersededByIdentifierId: string | null; resolvesTo: string | null; itemId: string | null; lifecycleStatus: string }
export interface LookupResult { query: string; resolution: "exact" | "superseded" | "ambiguous" | "none"; hits: LookupHit[] }

export interface UnitSearchRow { unitId: string; identifier: string; identifierType: string; scheme: string; open: boolean; resolvesTo: string | null; itemId: string; modelVersionId: string | null; lifecycleStatus: string; disposition: string; locationKind: string | null; locationId: string | null; ownerPartyRef: string | null; custodyPartyRef: string | null; lastEventType: string | null; lastEventTime: string; rawValue: string | null }
export interface UnitSearchResult { page: Page<UnitSearchRow>; identifiers: number; units: number; items: number; identifierTypes: number; superseded: number; servedInMilliseconds: number; hasMore: boolean; totalIsLowerBound: boolean; summaryIsSample: boolean }
export interface UnitSearchParams { q?: string | undefined; type?: string | undefined; status?: string | undefined; superseded?: boolean | undefined; document?: string | undefined; sort?: string | undefined; skip?: number | undefined; take?: number | undefined }

export interface IdentifierRow { identifierId: string; type: string; scheme: string; rawValue: string; normalisedValue: string; scopeKey: string; issuerPartyRef: string | null; qualifier: string | null; validFrom: string; validTo: string | null; supersededByIdentifierId: string | null; supersedesIdentifierId: string | null; correctionReason: string | null; actorRef: string }
export interface EpisodeRow { episodeId: string; kind: string; partyRef: string | null; partyType: string | null; locationId: string | null; locationKind: string | null; reason: string | null; source: string | null; confidence: string | null; validFrom: string; validTo: string | null; openingDocument: DocumentRef | null; closingDocument: DocumentRef | null; customerPoRef: string | null; customerItemRef: string | null; dueBack: string | null }
export interface AsBuiltRow { linkId: string; slot: string | null; relation: string; childUnitId: string | null; childSerial: string | null; childItemId: string | null; childLotId: string | null; quantity: number | null; releaseStatus: string | null; throughPhantomItemId: string | null; installedAt: string; removedAt: string | null }
export interface HistoryRow { eventId: string; seq: number; eventType: string; eventTime: string; recordedTime: string; bizStep: string | null; disposition: string | null; locationId: string | null; actorRef: string; document: DocumentRef | null; attributes: string | null }
export interface BuildRecordRow { buildRecordId: string; version: number; releasedAt: string; payloadHash: string; releaseSignatureRef: string | null }
export interface UnitDetail {
  unitId: string; lifecycleStatus: string; origin: string; serialisationPoint: string; retentionClass: string; historyDepth: string; lotId: string | null; expiryDate: string | null;
  derivedFromUnitId: string | null; mergedIntoUnitId: string | null; itemId: string; modelVersionId: string | null; disposition: string; conditionGrade: string | null; locationId: string | null; locationKind: string | null;
  custodyPartyRef: string | null; ownerPartyRef: string | null; openOwnershipEpisodeId: string | null; openCustodyEpisodeId: string | null; overlayFlags: string; lastEventSeq: number; lastEventTime: string;
  identifiers: IdentifierRow[]; episodes: EpisodeRow[]; asBuilt: AsBuiltRow[]; buildRecords: BuildRecordRow[]; parentUnitId: string | null; eventCount: number; ownerAndHolderDiffer: boolean;
}

export interface ScanField { key: string; value: string; description: string | null; errors: string[] }
export interface ScanResult { format: number | string; raw: string; cleaned: string; symbologyIdentifier: string | null; fields: ScanField[]; separatorsRecovered: number; alternatives: string[] }

export interface IdentifierInput { type: string; rawValue: string; issuerPartyRef?: string | null }
export interface CommissionLine { identifiers: IdentifierInput[]; clientReference?: string | null; lotId?: string | null; expiryDate?: string | null }
export interface CommissionUnits { commandId: string; itemId: string; origin: string; lines: CommissionLine[]; eventTime: string; legalEntityId?: string; document?: DocumentRef | null; locationId?: string | null; locationKind?: string | null; source?: string | null; skipInvalidLines?: boolean; brandIsOurs?: boolean | null; confirmedNotModelNumber?: boolean; modelNumberConfirmationReason?: string | null }
export interface CommissionLineResult { index: number; clientReference: string | null; unitId: string | null; outcome: "commissioned" | "pending" | "refused" | "duplicate" | string; reasons: string[] }
export interface CommissionUnitsResult { lines: CommissionLineResult[]; commissioned: number; pending: number; refused: number; duplicates: number }

export interface CorrectIdentifier { commandId: string; unitId: string; identifierType: string; newRawValue: string; reason: string; eventTime: string; legalEntityId?: string; approverRef?: string | null; note?: string | null; confirmedNotModelNumber?: boolean; modelNumberConfirmationReason?: string | null }
export interface CorrectIdentifierResult { unitId: string; supersededIdentifierId: string; newIdentifierId: string; oldNormalisedValue: string; newNormalisedValue: string }
export interface AssignIdentifier { commandId: string; unitId: string; identifier: IdentifierInput; eventTime: string; actorRef: string; legalEntityId?: string }
export interface UnitFlagCommand { commandId: string; unitId: string; flag: string; eventTime: string; actorRef?: string; legalEntityId?: string; reason?: string | null }
export interface UnitFlagResult { unitId: string; flags: string[]; changed: boolean; investigationRaised: boolean }

export interface SerialSchemeRow { code: string; identifierType: string; prefix: string; blockFormat: string | null; separator: string; counterDigits: number; example: string }
export interface SerialRangeRow { rangeId: string; scheme: string; startValue: string | null; endValue: string | null; count: number; commissionedCount: number; status: string; randomised: boolean; allocatedToDocumentType: string | null; allocatedToDocumentId: string | null; itemId: string | null; reservedAt: string; reservedBy: string; note: string | null }
export interface SerialRangeList { page: Page<SerialRangeRow>; scheme: string; ranges: number; serialsIssued: number; nextFree: string | null }
export interface AllocateSerialRange { commandId: string; scheme: string; quantity: number; allocatedTo: DocumentRef; eventTime: string; legalEntityId?: string; start?: string | null; randomised?: boolean; itemId?: string | null; note?: string | null; confirmedNotModelNumber?: boolean; modelNumberConfirmationReason?: string | null }
export interface AllocateSerialRangeResult { rangeId: string; scheme: string; startValue: string | null; endValue: string | null; count: number; randomised: boolean; serials: string[]; startMoved: boolean; [extra: string]: unknown }

export interface CompletenessQuery { serials?: string[] | null; selectionId?: string | null; document?: DocumentRef | null; effectiveAt?: string | null; skip?: number; take?: number }
export interface CompletenessSlot { slot: string; expectedItemCode: string; expectedItemId: string; outcome: string; capturedSerial: string | null; capturedUnitId: string | null; capturedItemId: string | null }
export interface CompletenessRow { unitId: string; serial: string | null; itemId: string; policy: string; expected: number; captured: number; missing: number; unexpected: number; complete: boolean; slots: CompletenessSlot[] }
export interface CompletenessResult { page: Page<CompletenessRow>; units: number; complete: number; missingUnits: number; missingSerials: number; unexpectedUnits: number; slots: string[]; unknown: string[] }

export interface SelectionSource { kind: string; document?: DocumentRef | null; partyRef?: string | null; itemId?: string | null; from?: string | null; to?: string | null; eventType?: string | null; serials?: string[] | null; scheme?: string | null; rangeStart?: string | null; rangeEnd?: string | null }
export interface ResolveSelectionSetResult { selectionId: string; resolved: number; unknown: string[]; rejected: string[]; source: SelectionSource }
export interface BulkOperationArguments { eventTime: string; actorRef: string; legalEntityId?: string; sourceDocument: DocumentRef; reason?: string | null; note?: string | null; extra?: Record<string, string> | null }
export interface UnitOutcome { unitId: string; serial: string | null; outcome: string; reason: string | null; detail: string | null }
export interface BulkPreviewResult { selectionId: string; operation: string; total: number; change: number; skip: number; block: number; page: UnitOutcome[]; pageSkip: number; pageTake: number }
export interface BulkExecutionResult { commandId: string; selectionId: string; operation: string; total: number; done: number; skipped: number; blocked: number; validationReportId: string; replayed: boolean }

export const RELEASE_OPERATION = "release_units";

export function newCommandId(): string { return crypto.randomUUID(); }
export function now(): string { return new Date().toISOString(); }

function query(params: Record<string, string | number | boolean | undefined | null>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
}

/** Server page → the grid's page result (page numbers from skip/take). */
export function toPage<T>(page: Page<T>): PageResult<T> {
  const size = page.take || 50;
  return { rows: page.items, total: page.total, page: Math.floor(page.skip / size) + 1, pageSize: size, continuation: null };
}

export const units = {
  lookup: (q: string, type?: string, signal?: AbortSignal) => api<LookupResult>(`/api/units/lookup${query({ q, type })}`, { signal }),
  search: (p: UnitSearchParams, signal?: AbortSignal) => api<UnitSearchResult>(`/api/units/search${query({ ...p })}`, { signal }),
  detail: (unitId: string, signal?: AbortSignal) => api<UnitDetail>(`/api/units/${unitId}`, { signal }),
  history: (unitId: string, skip: number, take: number, signal?: AbortSignal) => api<Page<HistoryRow>>(`/api/units/${unitId}/history${query({ skip, take })}`, { signal }),
  scan: (raw: string) => api<ScanResult>("/api/units/scan", { method: "POST", body: { raw } }),
  commission: (command: CommissionUnits) => api<CommissionUnitsResult>("/api/units/commission", { method: "POST", body: command }),
  correctIdentifier: (command: CorrectIdentifier) => api<CorrectIdentifierResult>("/api/units/correct-identifier", { method: "POST", body: command }),
  assignIdentifier: (command: AssignIdentifier) => api<{ unitId: string; identifierId: string; normalisedValue: string; lifecycleStatus: string }>("/api/units/assign-identifier", { method: "POST", body: command }),
  flag: (command: UnitFlagCommand) => api<UnitFlagResult>("/api/units/flag", { method: "POST", body: command }),
  unflag: (command: UnitFlagCommand) => api<UnitFlagResult>("/api/units/unflag", { method: "POST", body: command }),
  completeness: (q: CompletenessQuery, signal?: AbortSignal) => api<CompletenessResult>("/api/units/completeness", { method: "POST", body: q, signal }),
};

export const ranges = {
  schemes: (signal?: AbortSignal) => api<SerialSchemeRow[]>("/api/serial-ranges/schemes", { signal }),
  list: (scheme: string, status: string | null, skip: number, take: number, signal?: AbortSignal) => api<SerialRangeList>(`/api/serial-ranges/${query({ scheme, status, skip, take })}`, { signal }),
  allocate: (command: AllocateSerialRange) => api<AllocateSerialRangeResult>("/api/serial-ranges/", { method: "POST", body: command }),
};

export const selections = {
  resolve: (source: SelectionSource, actorRef: string) => api<ResolveSelectionSetResult>("/api/selections/", { method: "POST", body: { commandId: newCommandId(), source, actorRef } }),
  preview: (selectionId: string, operation: string, args: BulkOperationArguments, skip: number, take: number, find: string | null, outcomeFilter: string | null, signal?: AbortSignal) =>
    api<BulkPreviewResult>(`/api/selections/${selectionId}/preview`, { method: "POST", body: { selectionId, operation, arguments: args, skip, take, find, outcomeFilter }, signal }),
  execute: (selectionId: string, operation: string, args: BulkOperationArguments, approverRef: string | null) =>
    api<BulkExecutionResult>(`/api/selections/${selectionId}/execute`, { method: "POST", body: { commandId: newCommandId(), selectionId, operation, arguments: args, approval: { approverRef, required: false } } }),
};

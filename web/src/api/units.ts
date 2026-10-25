// The Serialisation module's HTTP surface (P1-T19, P1-T20): read models as the server returns them, commands as the
// contracts define them. Every list is a page with a total. Shapes mirror Yojana.Serialisation.Contracts.
import * as z from "zod/mini";
import { apiParsed } from "./client.ts";
import type { PageResult } from "../components/types";

// Schemas follow the C# records (ADR-0019): C# nullable → z.nullable; a C# parameter with a default may be left out of a
// request. Responses drop unknown fields; request bodies use looseObject so the declared fields are checked and any
// field a caller adds is passed on, never silently dropped.
const text = z.string();
const nullableText = z.nullable(z.string());
const count = z.number();
const nullableCount = z.nullable(z.number());
const flag = z.boolean();
const texts = z.array(text);
const maybe = <T extends z.ZodMiniType>(schema: T) => z.optional(z.nullable(schema));

export interface Page<T> { items: T[]; total: number; skip: number; take: number }
export const pageOf = <T extends z.ZodMiniType>(item: T) => z.object({ items: z.array(item), total: count, skip: count, take: count });
export const documentRef = z.object({ type: text, id: text, line: maybe(count) });
export type DocumentRef = z.infer<typeof documentRef>;
/** The same shape inside a request body: fields a caller adds are passed on. */
export const documentRefRequest = z.looseObject(documentRef.def.shape);

const lookupHit = z.object({
  unitId: text, identifierId: text, identifierType: text, scheme: text, normalisedValue: text, rawValue: text, open: flag,
  supersededByIdentifierId: nullableText, resolvesTo: nullableText, itemId: nullableText, lifecycleStatus: text,
});
const lookupResult = z.object({ query: text, resolution: z.enum(["exact", "superseded", "ambiguous", "none"]), hits: z.array(lookupHit) });
export type LookupHit = z.infer<typeof lookupHit>;
export type LookupResult = z.infer<typeof lookupResult>;

const unitSearchRow = z.object({
  unitId: text, identifier: text, identifierType: text, scheme: text, open: flag, resolvesTo: nullableText, itemId: text,
  modelVersionId: nullableText, lifecycleStatus: text, disposition: text, locationKind: nullableText, locationId: nullableText,
  ownerPartyRef: nullableText, custodyPartyRef: nullableText, lastEventType: nullableText, lastEventTime: text, rawValue: nullableText,
});
const unitSearchResult = z.object({
  page: pageOf(unitSearchRow), identifiers: count, units: count, items: count, identifierTypes: count, superseded: count,
  servedInMilliseconds: count, hasMore: flag, totalIsLowerBound: flag, summaryIsSample: flag,
});
export type UnitSearchRow = z.infer<typeof unitSearchRow>;
export type UnitSearchResult = z.infer<typeof unitSearchResult>;
export interface UnitSearchParams { q?: string | undefined; type?: string | undefined; status?: string | undefined; superseded?: boolean | undefined; document?: string | undefined; sort?: string | undefined; skip?: number | undefined; take?: number | undefined }

export const identifierRow = z.object({
  identifierId: text, type: text, scheme: text, rawValue: text, normalisedValue: text, scopeKey: text, issuerPartyRef: nullableText,
  qualifier: nullableText, validFrom: text, validTo: nullableText, supersededByIdentifierId: nullableText,
  supersedesIdentifierId: nullableText, correctionReason: nullableText, actorRef: text,
});
export const episodeRow = z.object({
  episodeId: text, kind: text, partyRef: nullableText, partyType: nullableText, locationId: nullableText, locationKind: nullableText,
  reason: nullableText, source: nullableText, confidence: nullableText, validFrom: text, validTo: nullableText,
  openingDocument: z.nullable(documentRef), closingDocument: z.nullable(documentRef), customerPoRef: nullableText,
  customerItemRef: nullableText, dueBack: nullableText,
});
export const asBuiltRow = z.object({
  linkId: text, slot: nullableText, relation: text, childUnitId: nullableText, childSerial: nullableText, childItemId: nullableText,
  childLotId: nullableText, quantity: nullableCount, releaseStatus: nullableText, throughPhantomItemId: nullableText,
  installedAt: text, removedAt: nullableText,
});
export const historyRow = z.object({
  eventId: text, seq: count, eventType: text, eventTime: text, recordedTime: text, bizStep: nullableText, disposition: nullableText,
  locationId: nullableText, actorRef: text, document: z.nullable(documentRef), attributes: nullableText,
});
export const buildRecordRow = z.object({ buildRecordId: text, version: count, releasedAt: text, payloadHash: text, releaseSignatureRef: nullableText });
export const unitDetail = z.object({
  unitId: text, lifecycleStatus: text, origin: text, serialisationPoint: text, retentionClass: text, historyDepth: text,
  lotId: nullableText, expiryDate: nullableText, derivedFromUnitId: nullableText, mergedIntoUnitId: nullableText, itemId: text,
  modelVersionId: nullableText, disposition: text, conditionGrade: nullableText, locationId: nullableText, locationKind: nullableText,
  custodyPartyRef: nullableText, ownerPartyRef: nullableText, openOwnershipEpisodeId: nullableText, openCustodyEpisodeId: nullableText,
  overlayFlags: text, lastEventSeq: count, lastEventTime: text, identifiers: z.array(identifierRow), episodes: z.array(episodeRow),
  asBuilt: z.array(asBuiltRow), buildRecords: z.array(buildRecordRow), parentUnitId: nullableText, eventCount: count,
  ownerAndHolderDiffer: flag,
});
export type IdentifierRow = z.infer<typeof identifierRow>;
export type EpisodeRow = z.infer<typeof episodeRow>;
export type AsBuiltRow = z.infer<typeof asBuiltRow>;
export type HistoryRow = z.infer<typeof historyRow>;
export type BuildRecordRow = z.infer<typeof buildRecordRow>;
export type UnitDetail = z.infer<typeof unitDetail>;

const scanField = z.object({ key: text, value: text, description: nullableText, errors: texts });
// ScanFormat is a C# enum the host writes as a number (no string-enum converter is registered).
const scanResult = z.object({
  format: z.union([count, text]), raw: text, cleaned: text, symbologyIdentifier: nullableText, fields: z.array(scanField),
  separatorsRecovered: count, alternatives: texts,
});
export type ScanField = z.infer<typeof scanField>;
export type ScanResult = z.infer<typeof scanResult>;

const identifierInput = z.looseObject({ type: text, rawValue: text, issuerPartyRef: maybe(text) });
const commissionLine = z.looseObject({ identifiers: z.array(identifierInput), clientReference: maybe(text), lotId: maybe(text), expiryDate: maybe(text) });
const commissionUnits = z.looseObject({
  commandId: text, itemId: text, origin: text, lines: z.array(commissionLine), eventTime: text, legalEntityId: z.optional(text),
  document: maybe(documentRefRequest), locationId: maybe(text), locationKind: maybe(text), source: maybe(text),
  skipInvalidLines: z.optional(flag), brandIsOurs: maybe(flag), confirmedNotModelNumber: z.optional(flag),
  modelNumberConfirmationReason: maybe(text),
});
const commissionLineResult = z.object({ index: count, clientReference: nullableText, unitId: nullableText, outcome: text, reasons: texts });
const commissionUnitsResult = z.object({ lines: z.array(commissionLineResult), commissioned: count, pending: count, refused: count, duplicates: count });
export type IdentifierInput = z.infer<typeof identifierInput>;
export type CommissionLine = z.infer<typeof commissionLine>;
export type CommissionUnits = z.infer<typeof commissionUnits>;
export type CommissionLineResult = z.infer<typeof commissionLineResult>;
export type CommissionUnitsResult = z.infer<typeof commissionUnitsResult>;

const correctIdentifier = z.looseObject({
  commandId: text, unitId: text, identifierType: text, newRawValue: text, reason: text, eventTime: text,
  legalEntityId: z.optional(text), approverRef: maybe(text), note: maybe(text), confirmedNotModelNumber: z.optional(flag),
  modelNumberConfirmationReason: maybe(text),
});
const correctIdentifierResult = z.object({ unitId: text, supersededIdentifierId: text, newIdentifierId: text, oldNormalisedValue: text, newNormalisedValue: text });
const assignIdentifier = z.looseObject({ commandId: text, unitId: text, identifier: identifierInput, eventTime: text, actorRef: text, legalEntityId: z.optional(text) });
const assignIdentifierResult = z.object({ unitId: text, identifierId: text, normalisedValue: text, lifecycleStatus: text });
const unitFlagCommand = z.looseObject({ commandId: text, unitId: text, flag: text, eventTime: text, actorRef: z.optional(text), legalEntityId: z.optional(text), reason: maybe(text) });
const unitFlagResult = z.object({ unitId: text, flags: texts, changed: flag, investigationRaised: flag });
export type CorrectIdentifier = z.infer<typeof correctIdentifier>;
export type CorrectIdentifierResult = z.infer<typeof correctIdentifierResult>;
export type AssignIdentifier = z.infer<typeof assignIdentifier>;
export type UnitFlagCommand = z.infer<typeof unitFlagCommand>;
export type UnitFlagResult = z.infer<typeof unitFlagResult>;

const serialSchemeRow = z.object({ code: text, identifierType: text, prefix: text, blockFormat: nullableText, separator: text, counterDigits: count, example: text });
const serialRangeRow = z.object({
  rangeId: text, scheme: text, startValue: nullableText, endValue: nullableText, count, commissionedCount: count, status: text,
  randomised: flag, allocatedToDocumentType: nullableText, allocatedToDocumentId: nullableText, itemId: nullableText,
  reservedAt: text, reservedBy: text, note: nullableText,
});
const serialRangeList = z.object({ page: pageOf(serialRangeRow), scheme: text, ranges: count, serialsIssued: count, nextFree: nullableText });
const allocateSerialRange = z.looseObject({
  commandId: text, scheme: text, quantity: count, allocatedTo: documentRefRequest, eventTime: text, legalEntityId: z.optional(text),
  start: maybe(text), randomised: z.optional(flag), itemId: maybe(text), note: maybe(text), confirmedNotModelNumber: z.optional(flag),
  modelNumberConfirmationReason: maybe(text),
});
const allocateSerialRangeResult = z.object({
  rangeId: text, scheme: text, startValue: nullableText, endValue: nullableText, count, randomised: flag, serials: texts,
  startMoved: flag, requestedStart: maybe(text),
});
export type SerialSchemeRow = z.infer<typeof serialSchemeRow>;
export type SerialRangeRow = z.infer<typeof serialRangeRow>;
export type SerialRangeList = z.infer<typeof serialRangeList>;
export type AllocateSerialRange = z.infer<typeof allocateSerialRange>;
export type AllocateSerialRangeResult = z.infer<typeof allocateSerialRangeResult>;

const completenessQuery = z.looseObject({ serials: maybe(texts), selectionId: maybe(text), document: maybe(documentRefRequest), effectiveAt: maybe(text), skip: z.optional(count), take: z.optional(count) });
const completenessSlot = z.object({
  slot: text, expectedItemCode: text, expectedItemId: text, outcome: text, capturedSerial: nullableText, capturedUnitId: nullableText,
  capturedItemId: nullableText,
});
const completenessRow = z.object({
  unitId: text, serial: nullableText, itemId: text, policy: text, expected: count, captured: count, missing: count,
  unexpected: count, complete: flag, slots: z.array(completenessSlot),
});
const completenessResult = z.object({
  page: pageOf(completenessRow), units: count, complete: count, missingUnits: count, missingSerials: count,
  unexpectedUnits: count, slots: texts, unknown: texts,
});
export type CompletenessQuery = z.infer<typeof completenessQuery>;
export type CompletenessSlot = z.infer<typeof completenessSlot>;
export type CompletenessRow = z.infer<typeof completenessRow>;
export type CompletenessResult = z.infer<typeof completenessResult>;

export const selectionSource = z.looseObject({
  kind: text, document: maybe(documentRefRequest), partyRef: maybe(text), itemId: maybe(text), from: maybe(text), to: maybe(text),
  eventType: maybe(text), serials: maybe(texts), scheme: maybe(text), rangeStart: maybe(text), rangeEnd: maybe(text),
});
const resolveSelectionSetResult = z.object({ selectionId: text, resolved: count, unknown: texts, rejected: texts, source: selectionSource });
const bulkOperationArguments = z.looseObject({
  eventTime: text, actorRef: text, legalEntityId: z.optional(text), sourceDocument: documentRefRequest, reason: maybe(text), note: maybe(text),
  extra: maybe(z.record(text, text)),
});
const unitOutcome = z.object({ unitId: text, serial: nullableText, outcome: text, reason: nullableText, detail: nullableText });
const bulkPreviewResult = z.object({
  selectionId: text, operation: text, total: count, change: count, skip: count, block: count, page: z.array(unitOutcome),
  pageSkip: count, pageTake: count,
});
const bulkExecutionResult = z.object({
  commandId: text, selectionId: text, operation: text, total: count, done: count, skipped: count, blocked: count,
  validationReportId: text, replayed: flag,
});
export type SelectionSource = z.infer<typeof selectionSource>;
export type ResolveSelectionSetResult = z.infer<typeof resolveSelectionSetResult>;
export type BulkOperationArguments = z.infer<typeof bulkOperationArguments>;
export type UnitOutcome = z.infer<typeof unitOutcome>;
export type BulkPreviewResult = z.infer<typeof bulkPreviewResult>;
export type BulkExecutionResult = z.infer<typeof bulkExecutionResult>;

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
  lookup: (q: string, type?: string, signal?: AbortSignal) => apiParsed(`/api/units/lookup${query({ q, type })}`, lookupResult, { signal }),
  search: (p: UnitSearchParams, signal?: AbortSignal) => apiParsed(`/api/units/search${query({ ...p })}`, unitSearchResult, { signal }),
  detail: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}`, unitDetail, { signal }),
  history: (unitId: string, skip: number, take: number, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}/history${query({ skip, take })}`, pageOf(historyRow), { signal }),
  scan: (raw: string) => apiParsed("/api/units/scan", scanResult, { method: "POST", body: { raw } }),
  commission: (command: CommissionUnits) => apiParsed("/api/units/commission", commissionUnitsResult, { method: "POST", body: z.parse(commissionUnits, command) }),
  correctIdentifier: (command: CorrectIdentifier) => apiParsed("/api/units/correct-identifier", correctIdentifierResult, { method: "POST", body: z.parse(correctIdentifier, command) }),
  assignIdentifier: (command: AssignIdentifier) => apiParsed("/api/units/assign-identifier", assignIdentifierResult, { method: "POST", body: z.parse(assignIdentifier, command) }),
  flag: (command: UnitFlagCommand) => apiParsed("/api/units/flag", unitFlagResult, { method: "POST", body: z.parse(unitFlagCommand, command) }),
  unflag: (command: UnitFlagCommand) => apiParsed("/api/units/unflag", unitFlagResult, { method: "POST", body: z.parse(unitFlagCommand, command) }),
  completeness: (q: CompletenessQuery, signal?: AbortSignal) => apiParsed("/api/units/completeness", completenessResult, { method: "POST", body: z.parse(completenessQuery, q), signal }),
};

export const ranges = {
  schemes: (signal?: AbortSignal) => apiParsed("/api/serial-ranges/schemes", z.array(serialSchemeRow), { signal }),
  list: (scheme: string, status: string | null, skip: number, take: number, signal?: AbortSignal) => apiParsed(`/api/serial-ranges/${query({ scheme, status, skip, take })}`, serialRangeList, { signal }),
  allocate: (command: AllocateSerialRange) => apiParsed("/api/serial-ranges/", allocateSerialRangeResult, { method: "POST", body: z.parse(allocateSerialRange, command) }),
};

export const selections = {
  resolve: (source: SelectionSource, actorRef: string) => apiParsed("/api/selections/", resolveSelectionSetResult, { method: "POST", body: { commandId: newCommandId(), source: z.parse(selectionSource, source), actorRef } }),
  preview: (selectionId: string, operation: string, args: BulkOperationArguments, skip: number, take: number, find: string | null, outcomeFilter: string | null, signal?: AbortSignal) =>
    apiParsed(`/api/selections/${selectionId}/preview`, bulkPreviewResult, { method: "POST", body: { selectionId, operation, arguments: z.parse(bulkOperationArguments, args), skip, take, find, outcomeFilter }, signal }),
  execute: (selectionId: string, operation: string, args: BulkOperationArguments, approverRef: string | null) =>
    apiParsed(`/api/selections/${selectionId}/execute`, bulkExecutionResult, { method: "POST", body: { commandId: newCommandId(), selectionId, operation, arguments: z.parse(bulkOperationArguments, args), approval: { approverRef, required: false } } }),
};

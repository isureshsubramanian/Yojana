import * as z from "zod/mini";

// Local copies of the Serialisation wire contracts used by correction screens. They deliberately do not import
// schemas from api/units.ts: this boundary can be changed and tested independently of that module's API conversion.
const text = z.string();
const number = z.number();
const nullableText = z.nullable(text);
const nullableNumber = z.nullable(number);

export const documentRef = z.object({ type: text, id: text, line: nullableNumber });
export const documentRefInput = z.object({ type: text, id: text, line: z.optional(nullableNumber) });
export const documentRefRequest = z.looseObject({ type: text, id: text, line: z.optional(nullableNumber) });

export const selectionSourceInput = z.object({
  kind: text,
  document: z.optional(z.nullable(documentRefInput)),
  partyRef: z.optional(nullableText),
  itemId: z.optional(nullableText),
  from: z.optional(nullableText),
  to: z.optional(nullableText),
  eventType: z.optional(nullableText),
  serials: z.optional(z.nullable(z.array(text))),
  scheme: z.optional(nullableText),
  rangeStart: z.optional(nullableText),
  rangeEnd: z.optional(nullableText),
  importJobId: z.optional(nullableText),
});
export const selectionSourceRequest = z.looseObject({
  kind: text,
  document: z.optional(z.nullable(documentRefRequest)),
  partyRef: z.optional(nullableText),
  itemId: z.optional(nullableText),
  from: z.optional(nullableText),
  to: z.optional(nullableText),
  eventType: z.optional(nullableText),
  serials: z.optional(z.nullable(z.array(text))),
  scheme: z.optional(nullableText),
  rangeStart: z.optional(nullableText),
  rangeEnd: z.optional(nullableText),
  importJobId: z.optional(nullableText),
});

export const identifierRow = z.object({
  identifierId: text,
  type: text,
  scheme: text,
  rawValue: text,
  normalisedValue: text,
  scopeKey: text,
  issuerPartyRef: nullableText,
  qualifier: nullableText,
  validFrom: text,
  validTo: nullableText,
  supersededByIdentifierId: nullableText,
  supersedesIdentifierId: nullableText,
  correctionReason: nullableText,
  actorRef: text,
});

export const episodeRow = z.object({
  episodeId: text,
  kind: text,
  partyRef: nullableText,
  partyType: nullableText,
  locationId: nullableText,
  locationKind: nullableText,
  reason: nullableText,
  source: nullableText,
  confidence: nullableText,
  validFrom: text,
  validTo: nullableText,
  openingDocument: z.nullable(documentRef),
  closingDocument: z.nullable(documentRef),
  customerPoRef: nullableText,
  customerItemRef: nullableText,
  dueBack: nullableText,
});

export const asBuiltRow = z.object({
  linkId: text,
  slot: nullableText,
  relation: text,
  childUnitId: nullableText,
  childSerial: nullableText,
  childItemId: nullableText,
  childLotId: nullableText,
  quantity: nullableNumber,
  releaseStatus: nullableText,
  throughPhantomItemId: nullableText,
  installedAt: text,
  removedAt: nullableText,
});

export const buildRecordRow = z.object({
  buildRecordId: text,
  version: number,
  releasedAt: text,
  payloadHash: text,
  releaseSignatureRef: nullableText,
});

export const historyRow = z.object({
  eventId: text,
  seq: number,
  eventType: text,
  eventTime: text,
  recordedTime: text,
  bizStep: nullableText,
  disposition: nullableText,
  locationId: nullableText,
  actorRef: text,
  document: z.nullable(documentRef),
  attributes: nullableText,
});

// Mirrors Contracts/Queries.cs UnitDetail in full; a partial state schema would let server/client drift hide here.
export const unitDetail = z.object({
  unitId: text,
  lifecycleStatus: text,
  origin: text,
  serialisationPoint: text,
  retentionClass: text,
  historyDepth: text,
  lotId: nullableText,
  expiryDate: nullableText,
  derivedFromUnitId: nullableText,
  mergedIntoUnitId: nullableText,
  itemId: text,
  modelVersionId: nullableText,
  disposition: text,
  conditionGrade: nullableText,
  locationId: nullableText,
  locationKind: nullableText,
  custodyPartyRef: nullableText,
  ownerPartyRef: nullableText,
  openOwnershipEpisodeId: nullableText,
  openCustodyEpisodeId: nullableText,
  overlayFlags: text,
  lastEventSeq: number,
  lastEventTime: text,
  identifiers: z.array(identifierRow),
  episodes: z.array(episodeRow),
  asBuilt: z.array(asBuiltRow),
  buildRecords: z.array(buildRecordRow),
  parentUnitId: nullableText,
  eventCount: number,
  ownerAndHolderDiffer: z.boolean(),
});

export const commandRefusal = z.object({ rule: text, message: text, unblockingStep: nullableText });
export const unitOutcome = z.object({
  unitId: text, serial: nullableText, outcome: text, reason: nullableText, detail: nullableText, unblockingStep: nullableText,
});
export const correctionEventPreview = z.object({ unitId: text, eventType: text, effectiveAt: text, summary: nullableText });
export const correctionEntitlementPreview = z.object({ unitId: text, change: text, entitlementId: nullableText, summary: nullableText });
export const correctionCertificatePreview = z.object({ unitId: text, certificateType: text, action: text, reason: text });
export const correctionPrintJobPreview = z.object({ unitId: text, jobType: text, reason: text });
export const correctionAllocationPreview = z.object({
  pair: number,
  fromSerial: text,
  exchangeSerial: nullableText,
  fromSerialRangeId: text,
  exchangeSerialRangeId: nullableText,
  fromProductionOrder: documentRef,
  toProductionOrder: documentRef,
  outcome: text,
  rule: nullableText,
  detail: nullableText,
  unblockingStep: nullableText,
});
export const correctionCommissionPreview = z.object({
  serial: text, locationId: nullableText, locationKind: nullableText, sourceDocument: documentRef, forKit: z.boolean(),
});
export const correctionExemptionPreview = z.object({ locationId: nullableText, locationKind: text, quantity: number, reason: text });
export const correctionAsBuiltRequirementPreview = z.object({
  parentUnitId: text, assemblyLinkId: text, quantity: number, capturedByCommand: z.boolean(),
});
export const correctionPreview = z.object({
  selectionId: nullableText,
  command: text,
  unitCount: number,
  change: number,
  skip: number,
  block: number,
  units: z.array(unitOutcome),
  unitEvents: z.array(correctionEventPreview),
  entitlementChanges: z.array(correctionEntitlementPreview),
  certificateVersions: z.array(correctionCertificatePreview),
  printJobs: z.array(correctionPrintJobPreview),
  refusal: z.nullable(commandRefusal),
  allocationReassignments: z.nullable(z.array(correctionAllocationPreview)),
  unitsToCommission: z.nullable(z.array(correctionCommissionPreview)),
  exemptions: z.nullable(z.array(correctionExemptionPreview)),
  kitsNeedingAsBuiltCapture: z.nullable(z.array(correctionAsBuiltRequirementPreview)),
});
